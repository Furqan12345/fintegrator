# Performance notes

How Fusetor was measured and tuned, what the numbers were, and what was deliberately left alone.
Measured 2026-11-23 on one Windows 11 dev machine (8 cores). Absolute times vary by about 2x between runs on a busy
machine; the before/after ratios were stable across repeated runs.

## Reproducing the measurements

`tools/FusetorBench` (not part of `SimpleIPaaS.slnx`) builds its own scratch SQLite database, seeds 60,000 runs
(180,000 steps, 20,000 dead letters, 100,000 log rows) and times the queries the Engine and the UI depend on. It never
touches `ipaas.db`.

```
dotnet run -c Release --project tools/FusetorBench -- <label> [dbPath] [executionCount]
```

It also times an end-to-end 3-card flow through the real `FlowExecutor`, and Roslyn script compilation.
To compare against an older commit, check that commit out as a git worktree, copy `tools/FusetorBench` into it and
run the same command.

## Results (60,000-run database, identical conditions, run back to back)

| Measure | Before | After |
|---|---|---|
| Idle Engine claim poll (runs every poll interval) | 207 ms | 0.9 ms |
| Claim with a queue present | 202 ms | 2.6 ms |
| 4 workers draining a queue | 5 claims/s | 353 claims/s |
| Run list, first page | 290 ms | 3.2 ms |
| Run list, filtered by flow / by status | 42 / 252 ms | 3.3 / 3.6 ms |
| Dead-letter pending scan | 144 ms | 7.8 ms |
| Dead-letter replay scan | 150 ms | 5.1 ms |
| Card persistence (add + 2 updates) | 12.3 ms | 2.6 ms |
| Log sink | 4.1k rows/s | 5.8k rows/s |
| Log filter lists | 271 ms | 50 ms |
| 3-card flow, sequential, end to end | 15 runs/s (66.6 ms) | 53 runs/s (18.9 ms) |
| 3-card flow, 4 in parallel | 15.8 runs/s | 63 runs/s |
| `/api/integrations` payload (4 integrations) | 441 KB | 37.7 KB with `?lite=true` |
| Dashboard data (cold start, request waterfall) | 4.9 s to 7.3 s | 4.7 s to 5.3 s |
| Client download, cold load (65 static files), served by the API | 9.5 MB uncompressed | 3.2 MB with Brotli |
| `ipaas.db` on the dev machine | 110 MB (87% free pages) | 13 MB after `VACUUM` |

## What changed and why

- **Indexes** on `FlowExecutions (Status, StartedAt)`, `(StartedAt)`, `(FlowId, StartedAt)` and
  `DeadLetterEntries (Status, CreatedAt)` plus a partial index on pending manual replays. The claim poll and every run
  list were full scans plus a sort. Redundant indexes were dropped (`IX_FlowExecutions_FlowId`, the duplicate
  `StepPacketLogs` index, and four low-cardinality `AppLogEntries` indexes that cost an extra b-tree update on every log
  line). Applied to existing SQLite and SQL Server databases at start-up by `DatabaseSchemaInitializer`
  (the SQL Server statements are generated but were not run against a SQL Server in this pass).
- **SQLite per-connection settings** (`SqliteTuning`): `synchronous=NORMAL` (the documented safe setting for WAL:
  a power loss can drop the last transactions but cannot corrupt the file), `temp_store=MEMORY`, a bounded page cache and
  WAL size limit. Applied through an EF connection interceptor and to the log writer.
- **Garbage collector**: the Web SDK defaults the API to Server GC (a heap and a thread per core). Both hosts now use
  workstation concurrent GC (`ServerGarbageCollection=false`). Idle memory did not change measurably (API about
  38 MB private / 125 MB working set either way); the benefit is on a multi-core host under load and in container
  memory limits. Judge memory by private bytes: the working set also counts shared, mapped assemblies.
- **Engine polling**: one poller gated by a free-slot semaphore instead of one polling loop per worker. An idle Engine
  runs one claim query per interval instead of `MaxConcurrency`; bursts still drain immediately.
- **Scripts**: LRU-bounded compiled-script caches (the old cache wiped itself completely at 512 entries, the other grew
  without limit), one shared `ScriptOptions`, and `ScriptWarmupService` in the **Engine only**, which compiles one script
  of each shape in the background after start (the first Roslyn compile in a process costs 0.4 to 1.3 s, and the Engine
  would pay it on its first flow run anyway). It was first registered in the API too and measured: the API's private
  memory went from 38 MB to 55 MB settled and peaked near 190 MB while compiling, for a benefit only Debug/Test runs
  see, so the API does not run it.
- **Memory**: HTTP response bodies are decoded once (they were decoded twice) and capped at `Outbound:MaxResponseBytes`
  (default 64 MB); the run list does not load the large JSON columns and does not track entities.
- **API**: Brotli/gzip for JSON, the publish-time `.br`/`.gz` files are served for Blazor assets, EF SQL and HttpClient
  logging are at Warning (the API used to write every SQL statement to the console).
- **UI**: list pages use `?lite=true` for integrations and flows; the dashboard loads its four data sets in parallel;
  polling (health, run list, live log tail) pauses in hidden tabs; the Error-link marker observes the diagram canvas
  only and is released on leave.

## Tried and not kept, or deliberately skipped

- **Skipping widget re-renders** (`ShouldRender` with a content fingerprint) on the flow designer's cards: no measurable
  change (zoom step 145-199 ms with it, 151-196 ms without, on a 60-card flow), so it was reverted.
  Designer cost is about 1.8 ms per card per zoom step with almost no DOM change; reducing it further needs
  diagram-library-level work (culling), not component tweaks.
- **Batching the per-card commits**: after `synchronous=NORMAL` the three commits per card cost about 2.6 ms in total, and
  the first insert must be visible immediately (the card shows as in progress), so it was left as is.
- **DbContext pooling, ICU trimming, AOT / `wasm-tools`**: pooling gains microseconds; ICU saves about 140-217 KB
  of brotli download but changes culture formatting; AOT needs a workload install and enlarges the download.
- **ForEach / Fan Out state cloning**: every iteration serializes the whole flow state into the child run row. That is
  part of the persistence design (each child run is replayable), so it was not changed without a design decision.
- Roslyn script assemblies are not collectible, so a script that is compiled and then evicted stays loaded until
  the process restarts. The LRU bound limits compile churn, not that residue.

## Operating notes

- `Outbound:MaxResponseBytes` raises or lowers the response cap. A response over the cap fails the HTTP card with a
  transport error that names the buffer limit.
- Reclaim file space with `VACUUM` while the API and Engine are stopped. Retention deletes rows but SQLite keeps the
  freed pages in the file.
- The Docker/Caddy deployment already compresses (`encode zstd gzip`); the precompressed-file serving and
  response compression matter for the App Service deployment where Caddy is not in front.
