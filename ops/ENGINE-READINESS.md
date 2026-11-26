# Engine production readiness

Tested 2026-11-24. The real API and Engine processes (Release builds) were driven against PostgreSQL 17.7 and SQLite.
A local mock upstream and an SMTP sink stood in for external systems, so no real Shopify, Amazon or Business Central
calls were made. Every scenario below passes on the current code.

## What was tested

| Area | Scenarios | Result |
|---|---|---|
| Card coverage | Amazon N+1 demo through a Connection (HTTP, For Each, scripts, mapping, cross-reference filter and store, debug). Paged HTTP, Fan Out (12 child runs), Persisted State, Add Identifier, Branch, Success, Error, Email. Webhook trigger (draft rejected, published accepted, wrong secret rejected). | 16/16 on PostgreSQL and on SQLite |
| Failure handling | HTTP timeout, connection refused, HTTP 500 (Stop and Continue), response over `Outbound:MaxResponseBytes`, malformed JSON in a post-flight script, runaway script, cancel running, cancel queued, maximum run time | all Failed or Cancelled with a readable error; failures dead-lettered |
| Lifecycle | Hard kill mid-run; graceful stop with a short run and with a long run; packets across a graceful stop; 20 s database outage | 20/20 PostgreSQL, 16/16 SQLite (no outage test on SQLite) |
| Two Engines on one PostgreSQL | 500 runs over 5 flows; cross-reference dedupe under 20 concurrent runs; Persisted State counter under 60 runs; every-minute cron for 4 minutes | 8/8 |
| Operations | Failure-alert digest (upstream error and timeout in one email); Production start without `Encryption:Key`; start against an unreachable database | 5/5 |
| Soak | 10 minutes of mixed load (8 simple flows, the N+1 demo, Fan Out kitchen sink): 3,026 runs on one Engine | all succeeded; see below |

Soak, sampled every 30 s on one Engine:

| Measure | Result |
|---|---|
| Private memory | 62–74 MB, flat (middle third 69.6 MB, last third 66.5 MB) |
| Handles | 611–651 |
| Threads | 21–30 |
| PostgreSQL connections | 11–13 |
| Average CPU | 0.17 cores |
| Engine errors | none |
| Queue | drained 2.3 s after the last enqueue |

Two Engines ran 500 runs in 34 s, each run executed exactly once.

### Data volume (added 2026-11-25)

The suites above used small payloads, so they could not show what each run writes or how cleanup behaves at
scale. A volume suite on PostgreSQL 17.7 measured that (real API and Engine):

| Scenario | Result |
|---|---|
| UPDATE that re-sends an unchanged 875 KB value | 961-1,049 KB of WAL; a status-only UPDATE: 0 KB (why run and dead-letter payloads live in their own tables) |
| 10 webhooks of ~0.9 MB | all succeeded; each body written once (0 updates on its payload row, 50 on the run rows) |
| Fan Out of 1,000 items, 0.9 MB state | all 1,000 children succeeded in 86 s; 1 snapshot (851 KB); children store 1.7 MB in total (a copy per child would be ~834 MB) |
| The same Fan Out with every child failing | 1,000 dead letters, each referencing the snapshot: 1.7 MB of stored flow state in total (largest 1.8 KB) instead of ~834 MB; the API shows the full state; a replay with an edited state resolved |
| Cleanup of 50,000 runs aged 40 days (200,000 steps and HTTP calls) | one pass, 5.2 s, 49 MB WAL; all runs and steps gone; the expired day partitions dropped; 0 rows deleted from the HTTP-call tables |

Webhook bodies are limited to 1,000,000 bytes (`WebhooksController`).

## Defects found and fixed

| Defect | Effect before | Fix |
|---|---|---|
| Runs of a flow serialised per process only | With two Engines, a dedupe flow created every order twice; Persisted State lost updates | `FlowRunGate`: a PostgreSQL advisory lock per flow for the whole run; replays wait for it |
| Persisted State computed from the run's start snapshot | 12 Fan Out children counted to 1; a replay could roll a watermark back | The card reads the stored value right before it runs |
| Script timeout only stopped waiting | `while (true) {}` kept a CPU core at 100% until restart | Loops and gotos get a cooperative cancellation check (`ScriptLoopGuard`) |
| Script timeout counted compilation | Trivial scripts timed out on the first runs after a start | The limit covers execution only |
| Max run time recorded as Cancelled | No dead letter, so no alert | Failed with the reason, plus a dead letter |
| Graceful stop cancelled in-flight runs | Every deploy broke running transactions | Stop claiming, drain, then fail what is left; unstarted claims return to the queue |
| No recovery after a crash | Runs stayed InProgress forever | `StaleExecutionRecoveryWorker` |
| A claimed run waited on its flow while holding a slot | Starvation; a "running" run that had not started | The claim skips flows already running |
| Cron enqueue without a claim | Two Engines could enqueue the same firing | Compare-and-set on `NextRunAt` before enqueueing |
| Database down at start-up | The host exited | Retries for `Database:StartupWaitSeconds` |
| Packet ingestion during an outage | Batches dropped after 3 attempts | Waits for the database (backpressure) |
| PostgreSQL log sink at shutdown | Last log lines lost | The sink owns its connection pool |
| Flow loads joined cards with links | cards × links rows on every run | Split queries |
| A finished run did not wake the claim loop | Runs of one flow (a Fan Out's children) waited up to a poll interval each: 1,000 children were not done after 15 minutes | The poller wakes when a run finishes: 86 s |
| Fan Out copied the whole flow state into every child | 1,000 children of a 0.9 MB state: ~834 MB written | One shared snapshot per Fan Out: 1.7 MB |
| A failed Fan Out child's dead letter copied the whole flow state | 1,000 failing children (upstream down) would write ~834 MB again | The entry stores its difference from the snapshot (edits too); the parent run is kept while its children exist |
| PostgreSQL log retention deleted rows | The age cutoff moves every 5 minutes, so the oldest day was emptied row by row before it could be dropped; the 1,000,000-row cap deleted by id | Drop-only: whole days for the window. Later the cap was removed altogether: warnings and errors have their own window and partitions, and a log volume alert emails instead of deleting |

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Execution:MaxConcurrency` | 4 | Runs at once per Engine (one run per flow at a time, across Engines on PostgreSQL) |
| `Execution:MaxFlowDurationSeconds` | 600 | Longer runs are stopped, Failed and dead-lettered |
| `Execution:ShutdownDrainSeconds` | 25 | Grace for in-flight runs on a graceful stop |
| `Execution:StaleRunGraceSeconds` | 300 | Runs InProgress longer than max duration + this are failed by recovery |
| `Database:StartupWaitSeconds` | 120 | How long start-up retries an unreachable database |
| `Scripting:TimeoutSeconds` | 10 | Per script execution (compilation excluded) |

## Deployment rules

- **Stop timeout.** Give the Engine at least `ShutdownDrainSeconds` plus 20 s to stop. Docker kills a container
  10 s after SIGTERM unless told otherwise, which would fail every in-flight run on deploy. `docker-compose.yml` sets
  `stop_grace_period: 50s`. Kubernetes: `terminationGracePeriodSeconds: 50`.
- **Several Engines need PostgreSQL.** On SQLite and SQL Server, runs of a flow are serialised inside one process
  only. Run exactly one Engine on those providers.
- **Crash recovery is slow by design.** A run orphaned by a crash is failed after `MaxFlowDurationSeconds` +
  `StaleRunGraceSeconds` (15 minutes by default). It is not retried automatically, because its cards may already
  have had side effects.
- **Alerts come from dead letters.** Failures and timeouts alert. A run recovered after a crash also gets a dead
  letter, so it appears in the alert digest and can be replayed by hand. Its flow state is rebuilt from the saved
  card outputs; the entry says when that is approximate (an HTTP card with a post-flight script, For Each bodies).
  It is marked manual-replay-only: `DeadLetter:MaxAutoRetryAttempts` never replays it, because the card that was
  running may already have sent its request. Event 1250 (`flow.execution.recovered`) is still logged.

## Known limits

- The loop guard cannot stop a single framework call that never returns (an endless LINQ sequence materialised in
  one call) or unbounded recursion, which ends the process with a stack overflow.
- A hard kill (`kill -9`, OOM) can lose the packet logs of the last 200 ms. Runs, steps and dead letters are written
  synchronously and are not lost.
- The test harness lives outside the repository; the regression tests that matter are in
  `tests/SimpleIPaaS.Tests/Engine/EngineReadinessTests.cs` and `tests/SimpleIPaaS.Tests/Postgres`.
