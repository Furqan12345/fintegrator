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
start state once in `FanOutSnapshots`, not in every child.

PostgreSQL also moves values over about 2 KB into out-of-line TOAST storage.

**Daily partitions.** `StepPacketLogs`, `StepPacketPayloads`, `StepExecutionPayloads` and `AppLogEntries` are range
partitioned by day (UTC), named `<Table>_pYYYYMMDD`. The application keeps partitions from yesterday to seven days
ahead. It does this at start-up and on every retention pass (every 15 minutes), and a writer that meets a missing
day creates it. No pg_partman or extension is needed. There is no default partition, because
`DETACH ... CONCURRENTLY` is not allowed while one exists.

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

Retention follows the windows on Settings → Storage. A new installation starts with logs 3 days, messages and
payloads 14 days, runs, steps and dead letters 30 days, and cross references kept forever. An installation upgraded
from a build without these defaults keeps what it had (runs and payloads forever) until someone changes it; the
first start writes those values down so they are visible on the page.

| Dataset | On PostgreSQL |
|---|---|
| Packet logs | Whole days older than the window are detached concurrently and dropped, for both `StepPacketLogs` and `StepPacketPayloads`. |
| Executions | `StepExecutionPayloads` days older than the window are dropped first. Then finished, non-test runs older than the window are deleted in batches: their HTTP call logs and payloads are deleted explicitly (they have no foreign key); steps, dead letters, Fan Out snapshots and the run and dead-letter payload rows cascade. |
| Logs | The log sink drops whole days older than the longer of its two windows (detail, warnings). It deletes only what the shorter window still covers, then applies the row cap. |
| Cross references | Batched delete, as on the other providers. |

Dropping a partition frees its disk space immediately, with no VACUUM needed.

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
