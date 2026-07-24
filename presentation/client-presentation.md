# SimpleIPaaS — Client Presentation Pack

**Contents:** slide deck outline with speaker notes · live demo flow · testing summary · known limitations · future roadmap · AI prompts used during delivery

---

## Part 1 — Presentation Deck (with speaker notes)

### Slide 1 · Title
**SimpleIPaaS — Your Integration Platform, In-House**
Design, run, and monitor API integrations across your business systems from one governed platform.

> *Speaker notes:* Open with the business problem: every new SaaS tool your teams adopt creates another point-to-point integration, usually as untracked scripts. SimpleIPaaS replaces that sprawl with one platform your team owns — visual design, centralized credentials, full audit of every run.

### Slide 2 · The Problem
- Point-to-point integrations multiply: N systems → N² connections
- Credentials scattered across scripts, CI variables, and inboxes
- No shared visibility: "did last night's sync run?" has no answer
- Every failure is a manual investigation

> *Speaker notes:* Ask the audience how many system-to-system syncs they run today and who gets paged when one fails. Anchor the value in the audit trail and the dead-letter queue — failures become work items, not mysteries.

### Slide 3 · The Solution
One platform, four capabilities:
1. **Design** — drag-and-drop flow designer (HTTP actions, mappers, branches, state)
2. **Connect** — reusable Connections with 8 authentication schemes, secrets encrypted at rest
3. **Run** — manual, scheduled (cron), or webhook-triggered; queued and concurrency-controlled
4. **Observe** — per-step execution timeline, payload capture, dead-letter replay

> *Speaker notes:* Emphasize that flows are data, not code deployments — a change to a mapping goes live on save, with the previous runs still fully auditable.

### Slide 4 · Architecture at a Glance
Blazor WebAssembly client → ASP.NET Core API (API-key auth, tenant-isolated) → queued execution engine (DAG runner, Polly resilience, Roslyn transformations) → EF Core persistence with per-tenant filters and AES-GCM-encrypted credentials.

> *Speaker notes:* Keep to one minute. Land three points: (1) every query is tenant-filtered at the ORM level, (2) credentials are encrypted with a key held outside the codebase, (3) execution is asynchronous and per-flow serialized, so a slow endpoint never blocks the platform. Refer deep-divers to `architecture/solution-architecture.md`.

### Slide 5 · Transformations Without Deployments
C# transformation scripts at four extension points per node — dynamic URL, pre-flight request shaping, post-flight response mapping, branch predicates — with named-node state so any step can reference any prior step's output.

> *Speaker notes:* This is the differentiator versus rigid field-mapping tools. Demo a script referencing two upstream nodes by name. Mention the guardrails: per-script timeout and structured error capture.

### Slide 6 · Enterprise Controls
- Platform access by hashed API keys; tenant derived from the key, never from the caller
- Connection secrets AES-GCM encrypted; never returned by any API
- Webhook triggers authenticated by per-flow secrets
- Health endpoint, structured logs, full request/response audit per step

### Slide 7 · What You Saw Today / Next Steps
Delivered platform + documentation set (architecture, design, project plan). Proposed next phase: roadmap items (Part 5) prioritized with your team.

---

## Part 2 — Live Demo Flow (12–15 min)

| # | Action | What to say |
|---|---|---|
| 1 | Open **Integrations** home | "Everything is grouped by business integration; each card is a runnable flow." |
| 2 | Open **Connections**, create one with OAuth2 Client Credentials | "Credentials are entered once, encrypted at rest, and never shown again — watch the field placeholder when I re-open it." |
| 3 | Open a flow in the **Designer**; add an HTTP Action + Mapper | "Node names are the contract — the mapper reads `getOrders.body` by name." |
| 4 | Open a Branch node's predicate script | "Routing logic is one C# expression; true/false ports are visible on the canvas." |
| 5 | Open **Flow Settings** → set a cron schedule; show the webhook URL + secret | "Three trigger types — manual, schedule, webhook — configured here, no redeploys." |
| 6 | Click **Run**, jump to **Activity Log** | "The run is queued instantly; the API never blocks on the target systems." |
| 7 | Open **Execution Detail** on a finished run | "Every step: status, latency, request and response payloads. This is the audit trail." |
| 8 | Show a failed run → follow link to **Dead Letters** → click **Retry** | "Failures become managed work items. Retry replays through the full pipeline — auth, scripts, everything." |
| 9 | Call the webhook URL from a terminal (`curl -X POST …/api/webhooks/{flow}/{secret}`) | "External systems trigger flows with a scoped secret — no platform credentials shared." |

**Demo environment:** run API and client locally, seed one integration with two flows (one healthy, one pointing at a 502-returning endpoint to produce the dead-letter scenario) before the session.

---

## Part 3 — Testing Summary

| Area | Approach | Status |
|---|---|---|
| Build integrity | Full solution build on every change wave | Passing |
| API contract | Endpoint-by-endpoint verification of client Fluxor effects against controller routes (list, detail, run, executions, steps, dead letters, webhooks) | Verified consistent |
| Execution engine | Manual flows exercising all five node types, branch true/false paths, cumulative state, cancellation, and failure → dead-letter → retry path | Verified |
| Outbound authentication | Each auth scheme exercised against a token-echo test endpoint (grant request shape, header decoration, token cache expiry, refresh rotation) | Verified |
| Security controls | Negative tests: missing/invalid API key → 401; cross-tenant id probing → 404; secrets absent from every API response; script timeout enforced | Verified |
| Static review | Multi-pass agent-assisted review across engine, security, and frontend, findings driven to closure via the verification loop (Part 6 prompts) | Complete |

*Automated unit/integration test suites are part of the immediate roadmap (Part 5) — current coverage is build verification plus the structured manual passes above.*

---

## Part 4 — Known Limitations

1. **Single-node deployment.** SQLite + in-process queue target one API instance; horizontal scale-out requires the PostgreSQL/distributed-lock roadmap item.
2. **In-process script execution.** Transformation scripts run inside the API process with timeout guards. The trust model assumes authenticated tenant users are trusted script authors; hostile-author isolation (out-of-process sandbox) is a roadmap item.
3. **OAuth2 authorization-code flow is operator-assisted.** Initial code exchange happens out-of-band; the platform then manages refresh automatically. Interactive PKCE flow is on the roadmap.
4. **HTTP-family connectors only.** Queues, databases, and file transfer (SFTP) are not yet native node types.
5. **No response pagination in HTTP actions.** Large collection endpoints must be windowed by the flow author (the domain model already reserves pagination styles for the roadmap item).
6. **Retry replays require idempotent targets.** Dead-letter replay of non-idempotent POST endpoints can duplicate side effects; per-request idempotency keys are on the roadmap.

---

## Part 5 — Future Roadmap

| Horizon | Item | Value |
|---|---|---|
| Near | Automated test suite (unit + API integration) wired into CI | Regression safety for every release |
| Near | PostgreSQL provider + EF migrations; container images + compose/Helm | Multi-node scale, managed environments |
| Near | DLQ notifications (email/Teams/Slack webhook on new dead letters) | Failures reach operators, not dashboards |
| Mid | Out-of-process script sandbox (isolated worker / WASM) | Hostile-author safety, per-script resource caps |
| Mid | Interactive OAuth2 authorization-code + PKCE consent flow | Self-service connection of user-consented SaaS APIs |
| Mid | Pagination-aware HTTP actions (link-header, cursor, page/offset) | First-class large-dataset sync |
| Mid | Flow versioning with draft/publish and rollback | Safe iteration on production flows |
| Far | Connector marketplace (queues, DBs, SFTP, SaaS-specific) | Faster time-to-integration |
| Far | OpenTelemetry traces/metrics, per-tenant rate limits, usage analytics | Enterprise observability & governance |

---

## Part 6 — AI Prompts Used During Delivery

AI agents were used as accelerators under engineering supervision; every change was reviewed against the architecture specification. Representative prompts:

1. **Gap analysis (assessment phase):**
   - "Assess the backend execution engine and API of this iPaaS for what is missing or wrong for production: flow execution, retries, idempotency, DLQ, concurrency, cancellation, trigger mechanisms, connectors, and whether frontend-expected endpoints exist."
   - "Assess security and multi-tenancy: authentication on the API, tenant isolation and spoofability, credential storage, the Roslyn scripting engine as an RCE surface, CORS/HTTPS/input validation."
   - "Assess the frontend and operational readiness: screens, designer UX gaps, observability, migrations, tests, deployment configuration, and recurring errors in the server logs."
2. **Specification-driven implementation:**
   - "Read architecture/solution-architecture.md §9 and implement every outbound authentication type for HTTP action cards — real OAuth2 client-credentials grant, refresh-token rotation, auth-code continuation, custom headers — with token caching, then update the Connection Wizard to expose them all."
   - "Implement §6.2/§10 platform security: hashed API-key authentication with tenant resolution from the key, removal of client-asserted tenancy, configuration-sourced encryption key, secret redaction on all reads, input validation, CORS allow-list, health checks, and script timeouts."
   - "Implement §7/§8: queued asynchronous execution with per-flow serialization and cancellation, the cron trigger scheduler, and the webhook trigger endpoint with constant-time secret comparison."
   - "Implement dead-letter management endpoints and console, flow deletion, designer-side flow validation, and trigger configuration UI."
3. **Verification loop:**
   - "Act as an independent verifier: compare the implementation against every requirement in architecture/solution-architecture.md, build the solution, and report each gap with file-level evidence. Do not fix anything — report."
   - Iterated: each verifier report was fed to implementation agents until the verifier reported no remaining specification gaps.
