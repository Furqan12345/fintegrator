# PostgreSQL

PostgreSQL is the production database for Fusetor. SQLite stays the zero-setup development default and SQL Server
is still supported; both keep their existing single-table layout. Elasticsearch is optional and is not needed with
PostgreSQL: every dataset (logs, packet logs, cross references, executions) can stay in the database.

## Configuration

Set the same two values for the API and the Engine (they must share one database):

```json
"Database": { "Provider": "PostgreSql" },
"ConnectionStrings": {
  "DefaultConnection": "Host=db.internal;Port=5432;Database=fusetor;Username=fusetor;Password=..."
}
```

or as environment variables: `Database__Provider=PostgreSql` and `ConnectionStrings__DefaultConnection=...`.

Create the database first (`createdb fusetor` or `CREATE DATABASE fusetor;`). The application user needs to create
tables, sequences and indexes in its schema. On start-up each host builds or upgrades the schema; an advisory lock
makes the second host wait while the first one does it. Tested with PostgreSQL 17. Declarative partitioning and
`DETACH PARTITION ... CONCURRENTLY` need PostgreSQL 14 or later.

## Storage layout

**Hot tables** hold the metadata that lists, filters and dashboards read. They are narrow, and they store payload
sizes, so a list never reads a payload:

| Table | Holds |
|---|---|
| `FlowExecutions` | one row per run |
| `StepExecutions` | one row per card per run: status, timing, error, payload sizes |
| `StepPacketLogs` | one row per HTTP call or card packet: method, URL, status, duration, body sizes |

**Cold tables** hold what only the data inspector opens. They are read only when a step or packet is opened, and
the lookup carries the row's date, so it touches a single partition:

| Table | Holds |
|---|---|
| `StepExecutionPayloads` | a step's received input, request and response |
| `StepPacketPayloads` | an HTTP call's request and response headers and bodies |
| `FlowExecutionPayloads` | a run's trigger payload, test definition, Fan Out item and start state (not partitioned; deleted with the run) |
| `DeadLetterPayloads` | a dead letter's payload, flow state and persisted state (not partitioned; deleted with the entry) |

The run and dead-letter payloads are written once, when the row is created. A run's row is updated several times
(claimed, finished); before the split each update rewrote a webhook body of any size. A Fan Out keeps the shared
start state once in `FanOutSnapshots`, not in every child. A failed child's dead letter stores only what differs from
that snapshot (its item and what its own cards added) and the full state is rebuilt when the entry is opened or
replayed; an edited state is stored the same way. Measured: 1,000 failed children of a 0.9 MB state stored 1.7 MB of
dead-letter state instead of ~834 MB. This applies to every database provider.

PostgreSQL also moves values over about 2 KB into out-of-line TOAST storage.

**Daily partitions.** `StepPacketLogs`, `StepPacketPayloads`, `StepExecutionPayloads` and `AppLogEntries` are range
partitioned by day (UTC), named `<Table>_pYYYYMMDD`. The application keeps partitions from yesterday to seven days
ahead. It does this at start-up and on every retention pass (every 15 minutes), and a writer that meets a missing
day creates it. No pg_partman or extension is needed. There is no default partition, because
`DETACH ... CONCURRENTLY` is not allowed while one exists.

Creating a partition locks the parent table exclusively for a moment. The scheduled creation (start-up and every
pass) gives up on a table after 2 seconds if a long query holds it, so writers never queue behind it, and tries
again on the next pass; a writer whose day is missing waits for the lock instead. Old partitions are dropped by one
host at a time (an advisory try-lock; the other host skips that pass). Lookups by run or step id add that run's or
step's time window, so PostgreSQL reads only the days it ran on instead of probing every partition.

`FlowExecutions` and `StepExecutions` are not partitioned. PostgreSQL requires the partition key in every unique
key, and dead letters, steps and child runs reference a run by its id alone. Without their payloads these rows are
small, so the batched delete that expires them stays cheap.

## Ingestion

- **Packet logs** are handed to a bounded in-process channel (20,000 entries). One consumer per process drains
  it in batches of up to 500 and writes each batch with two binary `COPY` commands (hot and cold) in one
  transaction. A packet is readable at most about 200 ms after the call.
  - A full channel makes the producing step wait; packets are never dropped for lack of room.
  - A failed batch is retried three times before it is dropped and logged.
- **Debug logs** (`AppLogEntries`) use the existing batched log sink. On PostgreSQL each batch is one binary `COPY`.
- **Runs and steps** are written synchronously. The Engine reads a step back to resume, resync or evaluate a test,
  so it must be committed when the card finishes. Each step write puts the hot row and the cold payload in one
  `SaveChanges` (one round trip, one transaction).
- **Shutdown.** A graceful stop drains the packet channel. A hard kill (`kill -9`, a crashed container) can lose
  the packets of the last flush interval. It cannot lose runs or steps.

## Retention

Retention follows the windows on Settings → Storage. A new installation starts with logs 3 days (whole days, so 3 to 4 on
PostgreSQL), messages and payloads 14 days, runs, steps and dead letters 30 days, and cross references kept forever. An installation upgraded
from a build without these defaults keeps what it had (runs and payloads forever) until someone changes it; the
first start writes those values down so they are visible on the page.

| Dataset | On PostgreSQL |
|---|---|
| Packet logs | Whole days older than the window are detached concurrently and dropped, for both `StepPacketLogs` and `StepPacketPayloads`. The window is never longer than the runs window: a packet whose run is gone cannot be opened from anywhere. |
| Executions | `StepExecutionPayloads` days older than the window are dropped. Finished, non-test runs older than the window are deleted in batches; steps, dead letters, Fan Out snapshots and the run and dead-letter payload rows cascade. A Fan Out parent is kept while any of its child runs still exists, because the children and their dead letters rebuild their state from its snapshot. Test runs are not deleted by age (the latest 3 per test case are kept), but their card inputs and outputs go with their day; the UI then shows "Details expired". |
| Logs | `AppLogEntries` is split by level into `AppLogEntries_warnings` (Warning, Error, Critical) and `AppLogEntries_detail` (every other level), each partitioned by day. Each part has its own window (Settings → Storage: 3 days for other levels and 14 days for warnings and errors on a new install). Whole days older than a window are detached concurrently and dropped, so a 3-day window keeps 3 to 4 days. Logs are never removed because of how many there are: the log volume alert (1,000,000 stored lines by default, set next to the windows) emails the alert recipients at 80% and 100% and shows admins a banner. A database from before the split is converted once at start-up, its log rows moved. |
| Cross references | Batched delete, as on the other providers. |

Deleting a run does not delete its packets or step payloads row by row: they go with their day. A run deleted today
leaves them for at most about a day, unreachable from the UI. Deleting them with the run turned every expired run
into row-by-row deletes in the partitioned tables (runs cross the cutoff all day; their day can only be dropped up to
a day later), which is what the partitions are for. Deleting a flow, trimming test runs and the demo reseed still
delete them at once. Runs, steps and dead letters are deleted row by row; `IX_FlowExecutions_CompletedAt` lets each
batch find its runs without scanning the table.

Dropping a partition frees its disk space immediately, with no VACUUM needed.

## Server settings and WAL

Fusetor needs no server settings and turns nothing on. It only sets `lock_timeout` on its own sessions for
partition maintenance. It creates no replication slots, uses no archiving or logical decoding, and changes no
logging settings. Run PostgreSQL with its defaults unless you have a reason not to.

The write-ahead log (WAL) cannot be switched off. PostgreSQL writes every change to it first, which is how it
survives a crash or power loss without corruption. With the defaults it is bounded:

| | Default | Meaning |
|---|---|---|
| Memory | `wal_buffers` 4 MB (1/32 of `shared_buffers`, at most 16 MB) | The only memory WAL uses |
| Disk | `pg_wal` recycled at each checkpoint, about `max_wal_size` (1 GB) | Measured: 592 MB after the volume suite (1,000-child Fan Out, 50,000-run cleanup) |
| Unbounded growth | `archive_mode=off`, no replication slots, `wal_keep_size=0` | Only a failing `archive_command`, a forgotten replication slot or `wal_keep_size` make `pg_wal` grow without limit |
| Server log | `logging_collector=off`, `log_statement=none`, `log_min_duration_statement=-1` | No statement logging |

**Keep the server log files off** (`logging_collector = off`, the default; errors still reach the service's console
output). Do not turn on `log_statement` or a low `log_min_duration_statement` either: the Engine polls and writes
continuously, so the server log would grow quickly, and PostgreSQL does not delete old log files unless rotation is
configured. Fusetor never changes these settings; at start-up it logs a warning (`PostgreSQL: logging_collector is
on ...`) if any of them is on. When PostgreSQL is added to a compose file, pass `-c logging_collector=off`
explicitly. If you enable archiving or a replica, monitor it:
`SELECT slot_name, active, pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) FROM pg_replication_slots;`
and `SELECT failed_count, last_failed_time FROM pg_stat_archiver;`.

Optional, if disk or write volume matters more than the features they give up:

| Setting | Effect | Cost |
|---|---|---|
| `max_wal_size = 256MB` | Smaller `pg_wal` | More frequent checkpoints (slightly more WAL written) |
| `wal_compression = lz4` | Smaller WAL records | Some CPU |
| `wal_level = minimal`, `max_wal_senders = 0` | Less WAL for bulk loads | No replicas, no point-in-time recovery (`pg_dump` still works) |

## Search

Run and log search use `ILIKE`. PostgreSQL's `LIKE` is case-sensitive, and on SQLite the same search is not.

## Upgrades

At start-up every table and column the model maps but the database lacks is added: tables with EF's own CREATE
TABLE and CREATE INDEX statements (a new append-only table is then partitioned), columns with a type default for
NOT NULL. This is the PostgreSQL counterpart of the additive lists for SQLite and SQL Server. Tables and columns
are never dropped or altered automatically.

## Not covered

- There is no tool to move an existing SQLite or SQL Server database into PostgreSQL. A new PostgreSQL deployment
  starts empty, apart from the demo flow and the first-time setup.
- Backups: use `pg_dump` or your platform's managed backups. `ops/backup.ps1` and `ops/restore.ps1` are for SQLite.

## Tests

`tests/SimpleIPaaS.Tests/Postgres` runs against a real server when `FUSETOR_TEST_POSTGRES` holds a connection string
to a database the tests may wipe, and is skipped otherwise:

```
FUSETOR_TEST_POSTGRES="Host=localhost;Port=5432;Database=fusetor_test;Username=postgres;Password=..." dotnet test
```

## Measured end to end (2026-11-24)

These numbers come from one Windows dev machine, running the real API and Engine processes in Release against a
local PostgreSQL 17.7. Each run of a three-card flow made one HTTP call to a local mock upstream:

| Measure | Result |
|---|---|
| 1,000 queued runs drained by one Engine | 47.8 s (21 runs/s), all successful |
| Packets written | 2,000 packets in 309 `COPY` batches (instead of 2,000 INSERTs) |
| Debug logs written | about 23,000 rows in 171 `COPY` batches |
| A backlog of 1,200 packets (integration test) | written in 3 batches |
| API reads after the burst | run list 80 ms, step summaries 95 ms, payload or body from the cold table 64-66 ms |
| Dead letters | 3 failing calls became dead letters; replay resolved them after the upstream recovered |
| Retention | expired day partitions detached and dropped in about 230 ms; recent rows untouched |
