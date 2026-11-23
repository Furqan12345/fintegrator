# SimpleIPaaS — Solution Architecture

**Document type:** Solution Architecture & Technical Implementation Specification
**Product:** SimpleIPaaS — Enterprise Integration Platform as a Service
**Audience:** Engineering, architecture review board, client technical stakeholders

---

## 1. Executive Overview

SimpleIPaaS is an enterprise integration platform that lets teams design, execute, and monitor API-to-API integration flows without writing deployable code. Users compose flows visually as a directed acyclic graph (DAG) of nodes (HTTP actions, mappers, branches, persisted state, debug probes), connect them to reusable authenticated Connections, and run them on demand, on a schedule, or in response to inbound webhooks. Every execution is recorded step-by-step with full request/response capture, failures are captured in a dead-letter queue with managed replay. Fusetor is a single-deployment, self-hosted platform: one deployment serves one customer, and all data belongs to that deployment.

The platform is delivered as three deployable units:

| Unit | Technology | Responsibility |
|---|---|---|
| **SimpleIPaaS.Api** | ASP.NET Core (.NET 10) | REST API, validation, trigger/webhook ingestion that *persists runs*, connection CRUD/testing, persistence |
| **SimpleIPaaS.Engine** | .NET Worker host (.NET 10) | The only place flows execute: claims queued runs from the database, cron/one-time scheduling, dead-letter replay, cancellation watching |
| **SimpleIPaaS.Client** | Blazor WebAssembly | Flow designer, connection management, monitoring UI |

The Api and Engine communicate exclusively through the shared SQLite database — no HTTP between them, no in-memory queue. A flow run is handed off by writing a `Queued` `FlowExecutions` row; the Engine claims it atomically. Because `dotnet run` makes each project directory its working directory, hosts without an explicit connection string resolve the shared file via `DefaultDatabasePath` (anchored at the `SimpleIPaaS.slnx` root) rather than relative to their own CWD.


---

## 2. Architecture Diagram

```mermaid
flowchart TB
    subgraph ClientTier["Client Tier — Blazor WebAssembly"]
        UI[Flow Designer / Connections / Activity Log / DLQ Console]
        FX[Fluxor State Stores<br/>FlowState · ConnectionState · ExecutionState · DeadLetterState]
        UI --> FX
    end

    subgraph ApiTier["API Tier — SimpleIPaaS.Api (ASP.NET Core)"]
        AUTH[Authentication Middleware<br/>JWT validation and account checks]
        CTRL[Controllers<br/>IntegrationFlow · Connection · Execution · DeadLetter · Webhook · Integration]
        VAL[Model Validation + ProblemDetails error handling]
        AUTH --> CTRL --> VAL
    end

    subgraph DataTier["Shared Data Tier — the transport between Api and Engine"]
        DB[(EF Core + SQLite/SQL Server<br/>host-wide DbContext)]
        ENC[EncryptionService<br/>AES-GCM, key from configuration]
    end

    subgraph EngineTier["Execution Tier — SimpleIPaaS.Engine (worker host)"]
        CLAIM[DB queue poller<br/>claims Status=Queued rows]
        WORKER[FlowExecutionWorker<br/>MaxConcurrency + per-flow lock]
        SCHED[CronTriggerScheduler<br/>Cronos-based]
        DLQW[DeadLetterWorker<br/>managed replay via ReplayRequestedAt]
        EXEC[FlowExecutor<br/>topological DAG runner]
        SCRIPT[ScriptExecutionService<br/>Roslyn, timeout-guarded]
        TRANSPORT[TransportEngine<br/>Polly resilience]
        AUTHF[AuthenticationHandlerFactory<br/>9 outbound auth types + token cache]
        WATCH[DbCancellationWatcher]
        SCHED --> CLAIM
        CLAIM --> WORKER --> EXEC
        WATCH --> WORKER
        DLQW --> TRANSPORT
        EXEC --> SCRIPT
        EXEC --> TRANSPORT --> AUTHF
    end

    FX -- "HTTPS + Bearer JWT" --> AUTH
    EXT[External SaaS APIs] <--> TRANSPORT
    HOOK[Inbound Webhooks] --> CTRL
    CTRL -- "writes Queued FlowExecutions" --> DB
    CTRL -- "flags ReplayRequestedAt" --> DB
    CTRL -- "writes Status=Cancelled" --> DB
    EXEC --> DB
    ENC --- DB
```


---

## 3. Technology Stack

| Concern | Technology | Notes |
|---|---|---|
| Runtime | .NET 10 | LTS-track |
| API | ASP.NET Core Web API | Controllers + middleware pipeline |
| Frontend | Blazor WebAssembly | SPA, no server rendering dependency |
| Execution tier | .NET Worker host (`SimpleIPaaS.Engine`) | Standalone process; couples to the API exclusively through the shared database |
| State management | Fluxor | Redux-style stores/effects |

| Flow canvas | Z.Blazor.Diagrams | Node/port/link model |
| ORM | Entity Framework Core (SQLite and SQL Server providers) | Host-wide data; no tenant filters |
| Resilience | Polly v8 `ResiliencePipeline` | Retry + timeout on outbound HTTP |
| Scheduling | Cronos | Cron expression evaluation |
| Scripting | Microsoft.CodeAnalysis.CSharp.Scripting (Roslyn) | Timeout-guarded, curated references |
| Secrets at rest | AES-GCM (`EncryptionService`) | Key injected via configuration/environment |
| Logging | `ILogger` structured logging throughout engine + workers | Correlation by ExecutionId |
| Health | ASP.NET Core Health Checks | `/health` endpoint incl. DB probe |

**Database note:** SQLite is the packaged default for single-node deployments. The data layer is confined behind repository interfaces (`IIntegrationRepository`, `IConnectionRepository`, `IExecutionRepository`), so promoting to PostgreSQL/SQL Server is a provider swap plus migration generation, with no engine changes.

---

## 4. Layered Solution Structure

```
src/
  SimpleIPaaS.Domain          → Entities, enums, no dependencies
  SimpleIPaaS.Application     → FlowExecutor, DeadLetterService, script services, repository interfaces
  SimpleIPaaS.Infrastructure  → EF Core context, repositories, TransportEngine, auth handlers, encryption, DefaultDatabasePath
  SimpleIPaaS.Api             → Controllers, middleware, composition root (no execution code)
  SimpleIPaaS.Engine          → Worker host: FlowExecutionWorker, CronTriggerScheduler, DeadLetterWorker, DbCancellationWatcher
  SimpleIPaaS.Client          → Blazor WASM UI, Fluxor stores
  SimpleIPaaS.Shared          → DTOs shared between Api and Client
```

Dependency rule: `Api → Application → Domain` and `Engine → Application → Domain`; `Infrastructure` implements `Application` interfaces and is referenced by both `Api` and `Engine`; `Client` references only `Shared`. The Engine and Api are separate processes that never call each other — the only coupling is the shared database.

---

## 5. Database Design

Fusetor is single-deployment: one deployment serves one customer, so tables carry no `TenantId` and there are no tenant query filters. Every row belongs to the deployment. `AlertSettings` and `StorageSettings` are host-wide single-row tables (`Id = 1`).

```mermaid
erDiagram
    Integration ||--o{ IntegrationFlow : contains
    IntegrationFlow ||--o{ IntegrationStep : nodes
    IntegrationFlow ||--o{ IntegrationEdge : edges
    IntegrationFlow ||--o{ FlowExecution : runs
    FlowExecution ||--o{ StepExecution : steps
    StepExecution ||--o{ DeadLetterEntry : failures
    Connection ||--o{ IntegrationStep : "used by"
    CrossReferenceList ||--o{ CrossReferenceEntry : contains
```

### 5.1 Core tables

| Table | Key columns | Purpose |
|---|---|---|
| `Integrations` | Id, Name, Description | Logical grouping of flows |
| `IntegrationFlows` | Id, IntegrationId, Name, Status, **TriggerType**, **CronExpression**, **RunAt**, **NextRunAt**, **WebhookSecret**, AllowPostReplay, PersistedStateJson, **HelperCode** | Flow definition + trigger configuration (`RunAt` drives one-time schedules); `HelperCode` is the flow-scoped C# prelude shared by every script |
| `IntegrationSteps` | Id, FlowId, StepType, NodeName, EndpointUrl, HttpMethod, AuthType, AuthConfigJson *(encrypted)*, MappingCode, UrlCode, PreFlightCode, PostFlightCode, ConnectionId, **StepConfig**, PositionX/Y, IsArchived | Current DAG nodes; removed nodes referenced by execution or dead-letter history are archived so audit/replay foreign keys remain valid |
| `IntegrationEdges` | Id, FlowId, SourceNodeId, TargetNodeId, SourcePortId, TargetPortId | DAG edges (Branch uses `true`/`false` source ports) |
| `Connections` | Id, Name, BaseUrl, AuthType, AuthConfigJson *(AES-GCM encrypted)*, Status | Reusable authenticated endpoints |
| `FlowExecutions` | Id, FlowId, **FlowName**, **IntegrationId**, **IntegrationName**, Status, StartedAt, CompletedAt, **RecoveredAt**, TotalRecords, SuccessRecords, FailedRecords, TriggerSource | Run header. Names are denormalized at creation so history survives renames and lists need no joins |
| `StepExecutions` | Id, ExecutionId, StepId, NodeName, Status, HttpStatusCode, RequestPayload, ResponsePayload, ErrorMessage, **RecoveredAt**, **RecoveredByDeadLetterId**, StartedAt, CompletedAt | Per-node audit trail. `ErrorMessage` is never cleared — it remains the historical record even after recovery |
| `DeadLetterEntries` | Id, FlowExecutionId, StepId, **FlowName**, **IntegrationName**, **NodeName**, Payload, FlowStateJson, ErrorMessage, **AttemptHistoryJson**, Status (Pending/Retrying/Resolved/Discarded), RetryCount, LastRetriedAt, **ResolvedAt** | Failure capture + managed replay. `AttemptHistoryJson` is an append-only log of every attempt |
| `CrossReferenceLists` | Id, Name, Description, CreatedAt | Named deduplication lists, shareable across flows |
| `CrossReferenceEntries` | Id, ListName, KeyValue, ValueJson, FlowId, CreatedAt — **unique (ListName, KeyValue)** | Stored keys. The unique index makes the membership test indexed and re-runs idempotent |
| `ApiKeys` | Id, Name, KeyHash (SHA-256), CreatedAt, RevokedAt | Legacy table retained for upgrade compatibility; no longer used for authentication |
| `AiBuildJobs` | Id, ProviderId, ProviderName, Model, Instruction, CurrentFlowJson, Status (Queued/Running/Completed/Failed), ProgressNote, ToolCallsUsed, WarningsJson, SourcesJson, ResultFlowJson, Error, CreatedAt, StartedAt, CompletedAt | Background AI flow-build jobs (DB-as-bus). String status deliberately avoids the persisted-enum ordinal trap |

Upgrading a pre-removal database runs `DatabaseSchemaInitializer.RemoveTenancy` in place: it drops the `TenantId` columns and their indexes, refuses to merge data if any table contains more than one distinct `TenantId`, and on SQLite writes `ipaas.db.bak-pre-remove-tenancy-<yyyyMMdd-HHmmss>` before changing anything. SQL Server takes no automatic backup; back up the database before deploying this version.

### 5.2 Schema management

Schema is created/upgraded at startup through `DatabaseSchemaInitializer`, which is idempotent and additive (creates missing tables, adds missing columns). All schema changes must go through this initializer so single-file deployments upgrade in place. The tenancy-removal step is idempotent and safe for both hosts to run at startup concurrently. Editing a flow archives removed steps that are referenced by `StepExecutions` or `DeadLetterEntries`; archived rows are excluded from the current graph, while their IDs and snapshots remain available to history and rollback.

---

## 6. API Design

Base path `/api`. All endpoints except the auth endpoints, `POST /api/webhooks/{flowId}/{secret}`, `/health`, and the client shell require a valid bearer JWT. Accounts are app-owned (ASP.NET Core Identity), every account must enroll TOTP 2FA, and roles are `Admin`, `User`, and `Viewer` (Viewer is read-only). JSON errors follow RFC 7807 `ProblemDetails`. Invalid input returns `400`, unknown ids `404`, missing/expired/revoked sessions `401`, insufficient role `403`.

### 6.1 Endpoint catalogue

| Method + Route | Purpose |
|---|---|
| `GET /api/integrations` / `POST` / `PUT /{id}` / `DELETE /{id}` | Integration CRUD |
| `GET /api/integrationflow` | List flows (summary DTO) |
| `GET /api/integrationflow/{id}` | Flow detail incl. nodes/edges/trigger config |
| `POST /api/integrationflow` | Create flow (validated: node names unique, edges reference nodes, no cycles) |
| `PUT /api/integrationflow/{id}` | Update flow |
| `DELETE /api/integrationflow/{id}` | Delete flow + dependent steps/edges |
| `POST /api/integrationflow/{id}/run` | Enqueue execution; returns `202 Accepted` + `executionId` immediately |
| `GET /api/executions?flowId=&status=&page=&pageSize=` | Execution history, paged |
| `GET /api/executions/{id}` | Execution header |
| `GET /api/executions/{id}/steps` | Step timeline |
| `POST /api/executions/{id}/cancel` | Cooperative cancellation of a queued/running execution |
| `GET /api/deadletters?status=` | DLQ listing |
| `POST /api/deadletters/{id}/retry` | Managed replay of a dead letter |
| `POST /api/deadletters/{id}/discard` | Mark discarded |
| `POST /api/integrationflow/{id}/webhook-secret` | Server-side regeneration of the flow's webhook secret |
| `POST /api/integrationflow/cron-preview` | Returns the next N occurrences of a cron expression (Cronos, 5- or 6-field) plus a human description; `400` ProblemDetails on an invalid expression |
| `POST /api/webhooks/{flowId}/{secret}` | Inbound webhook trigger; secret must match flow's `WebhookSecret` (constant-time compare); body becomes trigger payload; returns `202` + executionId |
| `GET/POST/DELETE /api/crossreferences` | Cross-reference list management |
| `GET /api/crossreferences/{list}/entries?search=&page=` | Paged entry inspection; delete an entry or clear the list |
| `GET /api/connections` (secrets always redacted) / `GET /{id}` (secrets redacted) / `POST` / `PUT /{id}` / `DELETE /{id}` / `POST /{id}/test` | Connection CRUD + connectivity test |
| `GET /health` | Liveness + DB readiness |

**Removed by design:** the former `GET /api/executions/dev/seed` endpoint does not exist in the production build.

### 6.2 Authentication & data scope contract

- Client signs in with email/password plus TOTP, then sends `Authorization: Bearer <short-lived JWT>`; the refresh token is rotated on use and stored hashed server-side.
- The first visit to a fresh deployment completes `/setup` and creates the first `Admin`; there is no public registration and no shared operator credential.
- Admins invite Users/Viewers, disable accounts, reset passwords and reset 2FA. Viewer requests are restricted to GET/HEAD/OPTIONS.
- The JWT carries the user id, role and security stamp. `AuthContextMiddleware` re-loads the user and rejects disabled/locked accounts and stale security stamps. **The client can never assert its own data scope or role.**
- Accounts belong to the deployment; all users share the same data. The legacy `ApiKeys` table is retained for upgrade compatibility but is no longer used for authentication.
- Disabling an account, changing its role, resetting its password, or resetting its 2FA rotates the security stamp and revokes refresh tokens, so existing sessions stop working.

### 6.3 AI flow builder (background agent)

Flow generation runs as a background job, not inside the browser request:

| Method + Route | Purpose |
|---|---|
| `POST /api/ai/flow/jobs` | Queue a build; returns `202 Accepted` + job id immediately |
| `GET /api/ai/flow/jobs` | Recent AI build jobs (so a build can be re-found after navigating away) |
| `GET /api/ai/flow/jobs/{id}` | Status, live `ProgressNote`, tool-call count, and the completed draft |

`AiBuildJobWorker` (a hosted service in the API process) claims `Queued` rows and runs an agent loop against the configured AI provider. The worker is DB-as-bus like the Engine's run queue, so it can move to the Engine later without an API change.

The agent receives a full canvas/card/database reference (`src/SimpleIPaaS.Api/AI/flow-builder-skill.md`) and a **read-only** tool surface: `search_web`, `fetch_official_docs`, `list_cross_reference_lists`, `read_cross_reference_entries`, `list_connections`, `list_flows`, `read_flow`, `read_test_payloads`, `validate_draft`. Security boundaries: tools never return credentials (connection auth is encrypted and excluded; node auth fields are blanked before a flow snapshot is stored), every read is scoped to the deployment, and the agent has no write/publish/execute capability — a draft is always reviewed and saved manually in the designer.

Budgets: 32 tool calls, 40 chat rounds, 3 draft-repair rounds, 20 minutes wall clock, and in-place context compaction (older tool outputs and stale draft attempts are shortened while preserving `tool_calls`/tool-result pairing) once the conversation exceeds ~60k characters.

---

## 7. Execution Engine

### 7.1 Asynchronous, queued execution

`POST /run`, webhook hits, and cron firings all funnel into one path. There is deliberately **no in-memory channel**: the shared database IS the queue, which is what allows the UI-serving API and the executing Engine to be separate processes.

1. The API (or the Engine's own scheduler) writes a `FlowExecution` row with `Status=Queued` and persists any trigger payload in `TriggerPayloadJson`; the id is returned to the caller (`202 Accepted`). This row **is** the queue entry.
2. `SimpleIPaaS.Engine`'s `FlowExecutionWorker` polls every `Execution:PollIntervalSeconds` (default 2), picks the oldest `Queued` row ordered by enqueue time, and claims it with a guarded atomic `UPDATE … WHERE Status = Queued` (flipping it to `InProgress`). Concurrent Engines can never claim the same row, though per-flow locking is per-process — run exactly one Engine per database.
3. Inside the Engine, configurable parallelism (`Execution:MaxConcurrency`, default 4) and a keyed `SemaphoreSlim` guard execution; `FlowExecutor.ExecuteFlowAsync` runs the DAG exactly as before, rewriting `StartedAt` on claim and finalising terminal states.
4. **Per-flow serialization:** the keyed `SemaphoreSlim` ensures at most one concurrent execution per flow, protecting `PersistedStateJson` from lost updates.
5. **Cancellation:** `POST /executions/{id}/cancel` writes `Status=Cancelled` into the row. Rows cancelled before their claim are skipped at claim time; running executions are picked up within ~2s by the Engine's `DbCancellationWatcher`, which cancels the local token so `FlowExecutor` observes it between nodes and inside transport calls and marks the run `Cancelled` — identical end state to the previous in-process registry behaviour.

6. **Flow-level timeout:** `Execution:MaxFlowDurationSeconds` (default 600) linked into the token.

### 7.2 DAG semantics (unchanged, proven)

- Topological sort with cycle detection; unreachable branch arms are pruned via active-node gating.
- Cumulative named-node state: each node's output is appended to the shared `FlowStateContext` JSON under its `NodeName`; downstream scripts address prior outputs by name.
- Node types: `HttpAction`, `Mapping`, `Branch` (boolean script → `true`/`false` port), `PersistedState`, `Debug`, `Schedule`, `CrossReferenceStore`, `CrossReferenceFilter`. Every node type owns its outgoing-edge activation, so an unhandled type would silently strand its downstream branch — new types must always be added to the executor as well as the palette.
- Webhook trigger payloads are injected into `FlowStateContext` under the reserved key `trigger` before the first node runs.
- The executor sets `TotalRecords` (= executed step count) alongside `SuccessCount`/`FailedCount`.

### 7.3 Scripting (Roslyn) — guarded execution

User scripts (mapping, dynamic URL, pre/post-flight, branch predicates) run through `AdvancedCodeExecutionService` with these mandatory guards:

- **Wall-clock timeout** per script (`Scripting:TimeoutSeconds`, default 10). The script runs as a separate task raced against a timeout (`Task.WhenAny`), because Roslyn's `ScriptRunner` observes its `CancellationToken` only before the submission begins and cannot interrupt code already executing. On timeout the step **fails** with an explicit message, the flow aborts, and the worker slot is released immediately.
- **Residual limitation (documented constraint):** a script stuck in a tight non-yielding loop keeps running on a background thread after its step has failed; .NET cannot forcibly abort it in-process. The execution pipeline is protected (the step fails, the worker continues), but the thread is not reclaimed until the process recycles. Reclaiming it requires the roadmap out-of-process sandbox (§12).
- **Curated reference set** — only the platform-supplied assemblies/imports are added (System core, LINQ, `System.Text.Json`, Newtonsoft.Json); scripts are compiled per-node and cached by content hash (SHA-256 of code + globals/result types).
- **Flow helper prelude** — `IntegrationFlows.HelperCode` holds flow-scoped C# declarations that are prepended to every Mapping, URL, Pre-Flight, Post-Flight, Branch and Persisted State script. Leading `using` directives from both the helper and the node script are hoisted so the combined unit compiles. Non-empty helper code is compiled (cached by hash) when the flow is created or updated; a compile error rejects the save with line/column details, so one typo cannot break every node. Helpers are declarations-only (top-level statements would run before every card) and are included in published flow snapshots/rollback. The AI builder preserves the existing helper when it produces a draft.
- Script failures raise `ScriptExecutionException`, which fails the step, records the error, writes a dead-letter entry, and aborts downstream nodes — a failing script is never recorded as a successful step. Branch predicate errors fail the step rather than defaulting to `false`. Error text is JSON-escaped; raw exception text is never emitted as unescaped JSON.
- **Trust model:** Roslyn scripting is in-process; the platform assumes *authenticated users are trusted script authors*. Deployments serving untrusted authors must enable the roadmap out-of-process sandbox (§12).

### 7.4 Resilience & retry budget

- Transport-level: Polly pipeline — 3 retries, exponential backoff, 30s attempt timeout, retry on 5xx/408/429 only (**not** on other 4xx, which are non-retryable client errors).
- Flow-level: a step that exhausts transport retries fails the step, dead-letters it, and aborts downstream nodes.
- DLQ-level: `DeadLetterWorker` polls every 60s and replays only `HttpAction` steps flagged replay-safe (GET/PUT/DELETE, or POST where the flow sets `AllowPostReplay`), max 5 attempts, then `Discarded`. Replays re-run the node through the full pipeline (UrlCode/PreFlight/auth) using the **flow state captured at failure time**, not a raw payload re-send.

### 7.5 Recovery semantics

A failed run is history, not something to overwrite. When a dead letter is replayed successfully:

- The originating `StepExecution` moves to **`Recovered`** with `RecoveredAt` and the originating dead-letter id. Its original `ErrorMessage` is deliberately **left intact** as the historical record.
- The parent `FlowExecution` moves to `Recovered` **only once no sibling dead letter is still Pending or Retrying**, and its failed/success record counts are rebalanced so the totals stay coherent.
- Every replay attempt — success or failure — appends `{attempt, at, statusCode, error}` to the dead letter's `AttemptHistoryJson`. Nothing is ever cleared, so the full error history remains reviewable in the UI after recovery.

The UI therefore reads a recovered run as a success while still disclosing that it failed initially, when it was resynced, and every error encountered along the way.

A resync can start from the failed node, any previously completed upstream node, or — when the failure is inside a `ForEach` body — only that loop's outermost card. Nodes inside a loop body are never offered or accepted as a start node because they only run per iteration. The operator may edit the captured flow state before queuing the replay; the edited JSON is persisted atomically with the replay request and is the state the worker executes. At replay start the executor clears the flow-state values of the selected node and every downstream node, so stale outputs cannot leak into the rerun while upstream state is preserved.

---

## 8. Trigger Architecture

| Trigger | Mechanism |
|---|---|
| **Manual** | `POST /api/integrationflow/{id}/run` → queue |
| **Webhook** | `POST /api/webhooks/{flowId}/{secret}` — anonymous route, authenticated by per-flow secret (generated server-side, constant-time compared); payload passed to the flow |
| **Cron (recurring)** | `CronTriggerScheduler` (hosted inside SimpleIPaaS.Engine): every 30s scans Active flows with `TriggerType=Cron`, evaluates `CronExpression` via Cronos (UTC, 5- or 6-field), enqueues when due; per-flow `NextRunAt` bookkeeping prevents double-fires |

| **One-time** | A flow with `RunAt` set fires exactly once at that instant. The scheduler clears `NextRunAt` **before** enqueuing, giving at-most-once delivery; a `RunAt` in the past is never resurrected on re-save, and `RunAt` is retained as the historical record of what was scheduled |
| **Polling** | Modeled as Cron + an initial HttpAction node (documented pattern); no separate infrastructure |

Trigger configuration is edited either in the flow-settings panel or, preferably, via a **Schedule node** on the canvas. When a Schedule node is present it is the single source of truth for the flow's trigger and the settings panel shows a read-only summary, so the two cannot silently disagree. Next-fire times are always computed **server-side** through `POST /api/integrationflow/cron-preview` (Cronos), so the designer and scheduler can never diverge on cron semantics.

### 8.1 Cross-reference (deduplication)

Two node types provide idempotent record deduplication against named lists that multiple flows can share:

- **CrossReferenceStore** — resolves an optional `arrayPath`, computes a composite key per record from dotted `keyPaths` (e.g. `order.id`), and bulk-upserts keys into the named list. The payload passes through unchanged.
- **CrossReferenceFilter** — computes the same keys and emits only records whose key is **absent** from the list.

Because the executor runs each node exactly once (there is no loop construct), both nodes process the entire array in a single pass and use **one bulk existence query per node**, never a per-record query. Keys are declarative field paths rather than scripts, which avoids compilation cost and the in-process scripting constraints in §7.3. Lists are managed from the Cross-References console (inspect, search, delete entries, clear or delete a list).

---

## 9. Outbound Connection Authentication

`AuthenticationHandlerFactory` provides one handler per `AuthType`; every type is configurable from the Connection Wizard and usable by HTTP action cards, either via a referenced Connection or inline step auth:

| AuthType | Behaviour |
|---|---|
| `None` | No decoration |
| `Basic` | `Authorization: Basic base64(user:pass)` |
| `ApiKey` | Configurable header name + value (or query parameter) |
| `Bearer` | Static bearer token |
| `OAuth2ClientCredentials` | **Real client-credentials grant**: POST to token endpoint with client id/secret/scope, cache token per connection until expiry (with 60s skew), refresh on 401 once |
| `OAuth2RefreshToken` | Refresh-token grant against token endpoint; rotates stored refresh token when the server returns a new one |
| `OAuth2AuthCode` | Operator completes the code exchange out-of-band; the platform stores access + refresh tokens and thereafter behaves as `OAuth2RefreshToken` (auto-refresh on expiry/401) |
| `AmazonSpApi` | LWA refresh-token exchange plus AWS Signature Version 4 signing; optionally assumes an STS role for temporary credentials, adds `x-amz-access-token` or an explicitly requested RDT, caches short-lived tokens in memory, and re-signs every retry/page attempt. Supports Amazon NextToken, link-header, cursor, and page/offset pagination with max-page/repeated-token guards; STS role assumption and RDT resources are explicit connection/node configuration and are never persisted as short-lived tokens |
| `Custom` | Arbitrary static header set (name/value pairs) defined in config JSON |

Token cache: in-memory `ConcurrentDictionary` keyed by ConnectionId; invalidated on connection update.

---

## 10. Security Architecture

| Control | Implementation |
|---|---|
| Platform authn | App-owned accounts (ASP.NET Core Identity, PBKDF2 password hashing), mandatory TOTP 2FA with one-time recovery codes, 15-minute JWT access tokens, hashed rotating refresh tokens (30 days), 5-attempt/15-minute lockout, first-run `/setup`, Admin/User/Viewer policies, auth audit trail |
| Data scope | Single-deployment: no `TenantId` columns or filters; one customer per deployment; the client cannot assert a different scope |
| Secrets at rest | Connection `AuthConfigJson` AES-GCM encrypted; key supplied via `Encryption:Key` configuration/environment (32 bytes); startup fails fast if missing outside Development |
| Secrets in transit | API responses never include decrypted auth config — all reads redact; decryption happens only inside the transport layer |
| Webhooks | Per-flow high-entropy secret, constant-time comparison, no caller-asserted data scope |
| Script safety | Timeout + curated references + structured error capture (§7.3); trust model documented |
| CORS | Configured allow-list (`Cors:AllowedOrigins`); wide-open CORS only in Development |
| Input validation | DTO validation + safe enum parsing (`TryParse`) → 400, never 500 |
| Error hygiene | ProblemDetails responses; internal exception details logged, not returned |
| API edge | HTTPS/HSTS behind the proxy, security headers, exact CORS allow-list, 2 MB Kestrel body cap, named rate limits for auth/webhooks/test-mapping, separate `X-Metrics-Key` for `/metrics` |
| Outbound egress | `OutboundUrlPolicy` blocks loopback, private, link-local, metadata and multicast addresses (override only with `Outbound:AllowPrivateNetworks`), disables automatic redirects, and requires Link-header pagination to stay on the same host |
| Logging hygiene | Auth config, API keys, JWT/refresh/setup tokens, recovery codes, and password-reset tokens are never written to logs; the development email fallback is Development-only |

---

## 11. Data Flow (end-to-end)

1. **Design time** — user builds the DAG in the designer; client validates (unique node names, connected graph, no cycles) before `POST/PUT`; server re-validates.
2. **Trigger** — manual/webhook/cron produces a `Queued` `FlowExecution` row in the shared database; caller immediately receives `executionId`.

3. **Execution** — worker acquires the per-flow lock, loads the flow, topologically orders nodes, and walks the DAG. Per node: UrlCode → PreFlight script → auth decoration → transport (Polly) → PostFlight script → state append → `StepExecution` persisted.
4. **Failure** — step failure writes a `DeadLetterEntry`, marks the execution `Failed`, halts downstream.
5. **Observation** — Activity Log lists executions (paged); Execution Detail shows the step timeline with payloads; DLQ Console lists failures with retry/discard actions.
6. **Replay** — DLQ worker or manual retry re-runs the node through the full pipeline; success resolves the entry.

---

## 12. Roadmap (post-delivery hardening)

- Out-of-process script sandbox (isolated worker process / WASM) for untrusted-author deployments.
- PostgreSQL provider + EF migrations for multi-node scale-out; distributed execution locks (e.g., Redis).
- OAuth2 interactive authorization-code UI flow with PKCE.
- Connector marketplace (queue, database, file/SFTP, SaaS-specific connectors) atop the existing `HttpAction` abstraction.
- OpenTelemetry traces/metrics export; per-deployment rate limiting; pagination-aware HttpAction (LinkHeader/Cursor/PageOffset are already modeled in the domain).
