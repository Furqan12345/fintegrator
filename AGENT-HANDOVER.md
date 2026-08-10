# SimpleIPaaS — Agent Handover

**For:** the next AI agent (or engineer) picking this up.
**State as of this document:** tests **142/142 passing**; latest client build passed with 0 errors and one pre-existing unused-variable warning in `Pages/Index.razor`.

**Session context (compact):** branch `newTT`. Uncommitted card UI, parallel ForEach, test-stub, CSS, and handover changes; untracked `$null`, `.commandcode/settings.json`, and `Shared/MinimizableNodeCard.razor`.

**Last changes (branch `newTT`, newest first):**
- `2e45da8` fix(engine): `ResolveFilterSource` node-name peel for flowState wildcard paths (`FlowExecutor.cs`).
- `aab5ee9` fix(cross-ref): resolve flowState-prefixed wildcard `arrayPath` against referenced node (sample + `CrossReferenceTests`).
- `820bb84` fix(engine): resolve `ForEach` `arrayPath` from central flow state (`FlowExecutor`, `ForEachCard`, `ForEachTests`).
- `8e0352b` fix(client): resilient integrations loading in `Index.razor`.
- `c758d30` feat(ForEach): route post-loop merge via a dedicated "completed" port.
- `d1e6620` feat: add `ForEach` node type for N+1 per-item API fan-out (Amazon SP-API).
- `e994926` feat: nested array wildcard support for cross-reference nodes.

Theme: `ForEach` node + cross-reference wildcard/flow-state resolution. Tests grew 122→141.

**This session (parallel ForEach):** added `_executionLock` to `FlowExecutor.cs`; locked counter increments (`TotalRecords`/`SuccessRecords`/`FailedRecords`) and all 9 `activeNodes.Add` call-sites; made `StubExecutionRepository` and `StubCrossReferenceRepository` thread-safe with internal locks; implemented parallel ForEach branch using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism` from config, collecting per-iteration results in an indexed array then merging sequentially; sequential path preserved as fallback. Build: 0 errors/0 warnings. Tests: 142/142 pass (added `ForEach_ParallelExecutionFansOutAndCombinesResults`).

**Latest:** Amazon SP-API now includes encrypted STS role settings, memory-only AssumeRole credentials, explicit RDT acquisition/cache, retry-safe transport response metadata, executor-owned AmazonNextToken/LinkHeader/Cursor/PageOffset pagination with max/repeat guards, pagination UI, sample/README/architecture updates, and tests. Build: 0 errors; tests: 146/146. Runtime: API healthy; sample seeded with AmazonNextToken pagination; client and Blazor boot HTTP 200.

Read this before touching code. Most of it is hard-won detail that is *not* obvious from reading the source, and several items will silently corrupt data or fail quietly if you get them wrong.

---

## 1. What this is

An enterprise iPaaS: users compose integration flows as a DAG in a visual designer, connect them to authenticated HTTP endpoints, run them manually / on a schedule / via webhook, and monitor every execution step-by-step with a dead-letter queue for failures.

| | |
|---|---|
| Backend | ASP.NET Core (.NET 10), `src/SimpleIPaaS.Api` — port **5000** |
| Frontend | Blazor WebAssembly, `src/SimpleIPaaS.Client` — port **5001** |
| Data | EF Core + SQLite, tenant-filtered |
| Tests | `tests/SimpleIPaaS.Tests` (xUnit), 122 tests |

**Authoritative documentation — keep these in sync with the code:**
- `architecture/solution-architecture.md` — the technical spec. **This is the contract.** Implementation is expected to match it; when you change behaviour, update it.
- `design/wireframes.md` — UI spec per screen.
- `presentation/client-presentation.md` — client-facing deck, demo script, **known limitations**, roadmap.
- `presentation/user-guide.md` — end-user walkthrough.
- `wbs/SimpleIPaaS-SOW-and-Project-Plan.docx` — SOW/WBS.

## 2. Running it

```bash
dotnet run --project src/SimpleIPaaS.Api    # :5000
dotnet run --project src/SimpleIPaaS.Client # :5001
```

Open **http://localhost:5001**. No login: in Development the API seeds an API key `dev-api-key` and the client sends it automatically (`src/SimpleIPaaS.Client/wwwroot/appsettings.json`). Every API call needs the `X-Api-Key` header except `/health` and `/api/webhooks/*`.

If using Claude Code, `.claude/launch.json` defines both servers for the preview tooling (not committed by default).

---

## 3. Traps that will bite you

These are real defects that were found the hard way. Each one failed *silently*.

### Enums are persisted as INTEGER — append only
`StepType` and `ExecutionStatus` in `src/SimpleIPaaS.Domain/Enums.cs` are stored by ordinal. **Inserting a value mid-enum silently corrupts every existing row.** Always append.

### There are TWO StepType enums
`Domain/Enums.cs` **and** a private duplicate inside `src/SimpleIPaaS.Client/Pages/FlowDesigner.razor` (bottom of the file). Adding a node type means editing both.

### Unknown step types silently become `Mapping`
`DtoMappings.ToEntity` does `Enum.TryParse<StepType>(...) ? stepType : StepType.Mapping`. Miss an enum entry and your node quietly turns into a Mapper instead of erroring.

### Every executor branch activates its own outgoing edges
In `FlowExecutor`, each node-type branch runs its own `activeNodes.Add(...)` loop. A node type with **no** branch still executes, produces empty output, and **never activates its downstream edges** — the rest of the flow just silently doesn't run. Always add the branch *and* its activation loop.

### `EnsureCreated()` is a no-op on an existing database
`DatabaseSchemaInitializer` is the **only** migration mechanism — there are no EF migrations.
- New **column** → `EnsureColumn(...)` (must be nullable or have a DEFAULT; SQLite requires it).
- New **table** → an explicit `CREATE TABLE IF NOT EXISTS` helper (see `EnsureCrossReferenceTables`). `EnsureCreated()` will **not** create it on an existing DB.
- Neither backfills existing rows — keep UI fallbacks for old data.

### No EF navigation properties
`IPaaSContext` wires every execution relationship as `HasOne<T>().WithMany()` with no navigation property, so **`Include` is impossible**. To show related data, denormalize it at write time (that's why `FlowExecution` carries `FlowName`/`IntegrationName` and `StepExecution` carries `NodeName`).

### Secrets use an "empty means keep" convention
All APIs redact secrets on read (`AuthConfigJson` etc. come back as `""`). On save, **empty means "keep what's stored"**. If a client blindly rebuilds a config object from redacted (blank) fields and sends it, it will **overwrite real credentials with blanks**. This bug has been introduced twice — once for Connections, once for step-level auth. Both are fixed; don't reintroduce it. UI shows "Stored — leave blank to keep".

### Roslyn only checks its CancellationToken *before* the script starts
`ScriptRunner` never observes the token again once running, so a `while(true)` cannot be cancelled. The services race the script against a wall-clock timeout instead. **Residual limitation:** a runaway script still occupies a thread until the process recycles — only an out-of-process sandbox fixes that (roadmap).

### Changing an interface or `FlowExecutor`'s constructor breaks the tests
`tests/SimpleIPaaS.Tests/TestDoubles/StubRepositories.cs` implements the repository interfaces, and `FlowExecutorDagTests` constructs `FlowExecutor` directly. Update both.

### Blazor: never let an effect throw
An unhandled exception in a Fluxor effect trips Blazor's error boundary and shows a permanent red bar until manual reload. **Use `SafeFetch.GetAsync` / `SafeFetch.SendAsync`** (`Store/SafeFetch.cs`) for all HTTP — they surface a toast instead.

---

## 4. Extension points

**Adding a node type** (~7 files, config in JSON — no schema change):
1. Append to `StepType` in `Domain/Enums.cs`
2. Append to the duplicate enum in `Pages/FlowDesigner.razor`
3. Palette button in `FlowDesigner.razor`
4. Ports in `Components/Nodes/IntegrationNodeModel.cs`
5. Dispatch in `Components/Nodes/IntegrationNodeWidget.razor`
6. New `Shared/<X>Card.razor` (copy `MappingCard.razor`)
7. Branch in `FlowExecutor` — **with its own edge activation**
8. Optional `.custom-node.<lowercase-type>` accent in `app.css`

**Per-node configuration** goes in `IntegrationStep.StepConfig`, a JSON blob already plumbed through DTOs and mappings. It currently holds `apiPacket` (HTTP), `schedule`, and cross-reference settings at the root. **Merge into the existing object — don't overwrite it.** Prefer this over new columns.

**Styling** is fully tokenized: `:root` in `wwwroot/css/app.css` (65 tokens) plus a `[data-theme="dark"]` block. Use `var(--…)`, the `--space-*` scale and `--z-*` scale. There are **zero** inline styles in the app — keep it that way. Icons are inline SVG via `Shared/AppIcon.razor`; no emoji.

---

## 5. Design decisions worth respecting

- **Recovery preserves history.** A resynced dead letter marks the step (and, once *all* siblings resolve, the execution) as `Recovered`. The original `ErrorMessage` is deliberately never cleared, and every attempt is appended to `AttemptHistoryJson`. Don't "tidy up" by clearing these — that was the original bug.
- **Cross-reference keys are declarative dotted paths**, not scripts — avoids Roslyn cost and sandbox caveats. The executor has **no loop construct**, so the filter processes a whole array in one pass with **one bulk existence query** per node. Don't make it per-record.
- **Schedule node owns the trigger.** If a Schedule node is on the canvas it is the source of truth; the settings modal shows a read-only summary so the two can't disagree. One-time schedules clear their marker *before* enqueuing (at-most-once) and a past date never resurrects.
- **Cron is always evaluated server-side** (`POST /api/integrationflow/cron-preview`, Cronos). A previous client-side parser silently rejected valid 6-field expressions. Don't reintroduce client-side cron parsing.
- **POST replay is opt-in per flow** (`AllowPostReplay`), because replaying a non-idempotent POST can duplicate its side effect.

---

## 6. Testing & verification

```bash
dotnet build SimpleIPaaS.slnx   # must stay 0 errors / 0 warnings
dotnet test SimpleIPaaS.slnx    # 122 tests
```

Coverage is concentrated where failure would be *silent*: script-failure semantics, tenant isolation, encryption round-trips, the auth-handler matrix, cross-reference idempotency, and recovery. Writing that suite uncovered three real production bugs — it earns its keep; extend it rather than working around it.

For UI work, verify in a real browser (light **and** dark, plus a narrow viewport) and check the console — several defects here were only visible at runtime.

---

## 7. Known limitations (deliberate, documented)

Do not treat these as bugs to fix casually; they are scoped decisions recorded in `architecture/solution-architecture.md` §12 and the presentation's limitations slide.

1. **Single-node deployment.** SQLite + an in-process queue. Scale-out needs PostgreSQL + distributed locks. The repository abstraction makes it a provider swap.
2. **In-process scripting.** Trust model is "authenticated tenant users are trusted script authors." Hostile-author isolation needs an out-of-process sandbox.
3. **No mid-flow wait node.** The engine is one synchronous topological pass with no suspend/resume; a durable-continuation redesign would be required.
4. **HTTP-family connectors only** — no queue/DB/SFTP node types yet.
5. **No response pagination** in HTTP actions (the domain reserves `PaginationStyle` for it).
6. **Cross-reference keys are field paths**, not expressions — no computed/derived keys.
7. **Old rows lack denormalized names** (schema upgrades don't backfill); UI falls back to truncated GUIDs.
8. **Cosmetic:** new nodes spawn under the designer toolbar and need dragging clear.

---

## 8. Suggested next steps

1. Automated **UI/E2E** tests (Playwright) — current UI verification is manual.
2. **PostgreSQL provider + EF migrations**, retiring the hand-rolled schema initializer.
3. **Out-of-process script sandbox** — the one genuine security gap for untrusted authors.
4. **Idempotency keys** on outbound requests, which would make POST replay safe by default.
5. Connector marketplace (queues, databases, SFTP) on top of the existing node abstraction.

**Ground rules:** keep the build at 0 warnings, keep tests green, update `architecture/solution-architecture.md` when behaviour changes, and never commit a real secret — the dev key in `appsettings.Development.json` is a Development-only placeholder that is already in git history and must never be reused in a deployed environment.
