# SimpleIPaaS — User Guide

**Running now:** API at http://localhost:5000 · Client at http://localhost:5001
**Sign-in:** open **http://localhost:5001** and sign in with your account. On a brand-new database, the first visit shows **Create the first administrator**; that account becomes the Admin and must enroll an authenticator app before it gets a session. There is no shared operator key.

---

## 0. Sign in, accounts, and roles

- **First run:** open `/setup`, create the first Admin (12+ character password), scan the QR code in your authenticator app, and save the ten one-time recovery codes. The setup page closes permanently after the first account exists.
- **Normal sign-in:** email + password, then the six-digit authenticator code. If you lose the authenticator, use one recovery code; an Admin can reset 2FA if the codes are also lost.
- **Roles:**
  - **Admin** — everything, including **Users**, **Storage**, and **AI Providers**.
  - **User** — build, edit, run, publish, and use AI features; no user or storage administration.
  - **Viewer** — read-only; every mutating request is rejected.
- **Users page (`/admin/users`):** Admins invite people by email (or copy the invite link), change roles, disable/enable accounts, generate password-reset links, reset 2FA, and review the recent sign-in/audit trail.
- **Password reset:** **Forgot password?** emails a one-hour link. Without SMTP configured, an Admin can copy the reset link from the Users page.
- **Sessions:** the access token is short-lived and silently refreshed; **Sign out** in the top-right clears the refresh token. Disabling an account, changing its role, or resetting its password/2FA ends its existing sessions.
- **Account lockout:** five failed attempts lock the account for 15 minutes; authentication endpoints are also rate-limited.

---

## 1. Integrations (home screen, `/`)

This is where every integration and its flows live.

- **Create an integration:** click **+ New Integration**, give it a name (e.g. "Order Sync"), save.
- Each integration expands to show its **flows** as cards: name, status (Draft/Active), trigger type, and last-run outcome.
- **Open** a flow → goes to the Flow Designer.
- **Run** a flow → queues it immediately and shows a toast linking to the new execution (you don't wait for it to finish — the call returns right away).
- **Delete** a flow or integration → asks you to type the name to confirm, then removes it.

## 2. Connections (`/connections`)

Connections are reusable, authenticated endpoints your flows call out to.

- Click **+ New Connection** to open the wizard: name, base URL, and an auth type.
- **All 8 auth types are supported:** None, Basic, Bearer Token, API Key (header or query), OAuth2 Client Credentials, OAuth2 Refresh Token, OAuth2 Authorization Code, and Custom Headers.
- Credentials are encrypted before they're stored and are **never shown again** after saving. When you re-open a connection to edit it, secret fields show a **"Stored — leave blank to keep"** placeholder — leave them empty to keep the existing credentials, or type new values to replace them. Non-secret edits (like renaming) will no longer wipe your credentials.
- Click **Test** on a saved connection to fire a live request through it and see whether it's reachable.

## 3. Flow Designer (`/flow-designer/{id}`)

This is where you build the integration logic as a diagram.

- **Palette (left):** add nodes from the toolbar — **HTTP Action**, **Mapper**, **Branch**, **Persisted State**, **Debug**, **Cross-Reference Store/Filter**, **For Each**, **Fan Out**, **Email**, and the identifier/error/success status cards. The **Email** card sends through the server's SMTP settings and supports To/CC/BCC/Reply-To. Put upstream values in any text field with `{{flowState.NodeName.field}}` (for example, `Order {{flowState.MapOrder.orderNo}} synced`); the card lists the nodes immediately upstream so you can insert their names. Test runs show the resolved preview without sending; Debug runs send.
- **Canvas (center):** connect nodes by dragging from one node's output port to another's input. A **Branch** node exposes two ports, `true` and `false`, for conditional routing.
- **Inspector (right):** click any node to configure it — every node needs a unique **Node Name** (this is how later nodes reference its output). HTTP Action nodes let you pick a Connection, method, URL, and optional transformation scripts (dynamic URL, pre-flight request shaping, post-flight response mapping) written in C#.
- **Helpers** (button in the toolbar): write C# declarations (static classes/methods) once and use them from every script in the flow. The helper prelude is prepended to Mapping, URL, Pre-flight, Post-flight, Branch and Persisted State scripts. **Validate** compiles it and reports line/column errors; **Apply to draft** updates the flow, and the toolbar **Save** persists it. Keep helpers to declarations only — top-level statements would run before every card. For example, add a `FinValues` class with invariant date/number readers instead of repeating that code in each script. Test Run in a node's script editor uses the same helpers.
- **Flow Settings** (button in the toolbar): configure how the flow starts —
  - **Manual** — only runs when you click Run.
  - **Cron** — enter a cron expression; you'll see a plain-English description and the next few scheduled run times.
  - **Webhook** — the platform generates a secret URL you can copy; POST to that URL from any external system to trigger the flow. Use **Regenerate Secret** if the URL is ever compromised.
- **Save** validates the flow first (no empty flows, no duplicate node names, no dangling edges, no cycles) and shows an inline error banner if something's wrong — fix it and save again.
- **Delete Node** button removes the selected node and its connections.
- **Tests** enters test mode on the same canvas. The structure is read-only (pan, zoom and fit still work), and the toolbar switches to scenarios: **New**, **Duplicate**, **Inputs**, **Save**, **Run scenario**, **Run all** and **Exit**. A scenario can supply a card's output, queue HTTP response fixtures, set trigger payload/variables/cross-reference rows, and add checks. Before a run, cards show `Supplied`, `Fixture ×N`, `Needs response` or `N checks`; during a run they show live progress, and after it the path taken is highlighted, the failed card is ringed and centred, and not-reached cards are dimmed. The results drawer shows the headline, card timeline, checks, input/output/error and the last 20 runs. **Run all** shows suite totals and each scenario's result.
- **Create from transaction** in a scenario's … menu builds a draft from a real recent run, with recorded HTTP fixtures (auth headers redacted), fan-out supplied outputs and checks matching the recorded statuses.
- **Save changes before testing** and **discard unsaved scenario** guards protect scenario edits when switching scenarios, exiting test mode or leaving the page.
- **Required before publish** scenarios appear in the toolbar badge and the publish dialog. If a required scenario is Stale, Failed or NeverRun, publish offers **Run required tests and publish**; nothing is published unless every required scenario passes against the current draft.

## 4. Transactions (`/transaction-history`)

Every run of every flow shows up here, newest first. This is the single place for run history — the old separate Activity Log page has been merged into it (`/activity` and `/activity-log` redirect here).

- **Filters:** integration, flow, status, a date range (**From** / **To**), free-text **Search** across the identifier, message, flow name, error text and a full run id, and **Include test runs** (off by default). Test rows carry a **Test** badge. **Clear** resets everything.
- **Columns:** transaction id, flow (with fan-out item info), **Identifier** and **Message** (written by the Add Identifier / Error / Success cards), status and run time. Drag any column edge to resize it; double-click a header to reset the widths.
- **Pagination:** 50 runs per page with **Previous** / **Next**.
- Rows for runs that are still **Queued** or **Running** get a **Cancel** button, and the page auto-refreshes every 10 seconds while anything on the page is active.
- **Inspect** opens the run's step-by-step timeline with the request/response payload of every card.

## 5. Dead Letters (`/deadletters`)

When a step in a production flow fails, it lands here instead of vanishing into a log. Isolated test runs never create dead letters, and entries owned by test executions are excluded from the list, counts and bulk replay.

- Filter by status: **Pending**, **Retrying**, **Resolved**, **Discarded**.
- **Inspect** opens the diagnostics panel: root cause, attempt history, the payloads the step received/sent/received back, HTTP packets, and the captured flow state.
- **Resync from node** lets you choose where the replay starts: the failed node, the loop card containing it, or a previously completed upstream node. Nodes inside a `ForEach` body are deliberately not selectable — pick the loop card so every iteration re-runs safely.
- The **Flow state before resync** editor lets you correct the captured state before queueing. It must be a JSON object, and the edit is saved atomically with the resync request.
- When the replay starts, the output of the selected node and every downstream node is cleared from flow state and overwritten as those nodes re-run; upstream state is preserved.
- **Discard** if you don't want it retried again (asks for confirmation).
- Entries that have hit the retry limit show a warning badge.

---

## 6. AI Providers (`/ai-providers`) and Build with AI (`/ai-builder`)

Add an OpenAI-compatible or Azure OpenAI provider on **AI Providers** (name, endpoint, model, API key). The key is stored encrypted and never shown again; leave the field blank when editing to keep it, or use **Clear API key** to remove it. **Test connection** verifies the endpoint. Exactly one provider is the default.

Each provider speaks one of two OpenAI API shapes, chosen with **API mode**:

- **Responses** — OpenAI's newer API (posted to `/responses`). Pick this for `api.openai.com`; it is the default there. Optional **Reasoning effort** (`minimal`/`low`/`medium`/`high`) applies only to reasoning models, and **Enable web search** adds the provider's built-in web-search tool so the agent can research live web results (returns are cited in the draft's sources).
- **Chat Completions** — posted to `/chat/completions`. This is the safe default for every self-hosted OpenAI-compatible server (Ollama, LM Studio, vLLM, LiteLLM) and for Azure OpenAI; Azure can be switched to Responses if your deployment and API version support it.

Enter the **API root**, not the full endpoint — for example `https://api.openai.com/v1` (or `https://api.openai.com` for the plain host, `https://<resource>.openai.azure.com/openai/deployments/<deployment>?api-version=…` for Azure). The app appends `/responses` or `/chat/completions` itself, and the wizard shows the exact URL requests will use ("Requests go to …"). Pasted values that already end in `/chat/completions` or `/responses` are reduced to the same root, so switching API mode later never needs the endpoint re-entered. Self-hosted endpoints (Ollama, LM Studio, vLLM, LiteLLM) work on any port over HTTP on loopback or private/LAN addresses; public hosts must use HTTPS.

**Build with AI** turns a description into a flow draft: pick a provider, describe the integration, and follow the job progress. The finished draft opens in the Flow Designer for review before you save it. With no provider configured the page links you to AI Providers.

A rotated or lost `Encryption:Key` cannot decrypt stored provider keys — the API answers with a clear "re-enter the API key" error instead of failing silently.

## 7. Storage & retention (`/settings`)

Flows, integrations, connections and these settings always stay in the relational database. The datasets that grow — application logs, packet logs (request/response messages) and cross-reference entries — can be served by the database or by Elasticsearch, chosen per dataset. Enter the cluster URL, authentication (username/password or API key), index prefix and TLS options, then **Test connection** and **Create templates, policies & indices** (idempotent). Each dataset has an index period (daily / weekly / monthly) and a retention window in days; **0 keeps data forever**. With ILM enabled the cluster deletes expired indices through the policy; without it the app deletes whole expired period indices on its 15-minute retention pass. Runs, steps and dead letters stay in the database because the engine claims them transactionally. Switching a dataset back to the database is the rollback path — data already written to Elasticsearch is left there to expire through retention.

## Suggested first walkthrough

1. Go to **Connections**, create one pointing at any test API you have (or `https://httpbin.org` for a quick check), pick an auth type, save, then hit **Test**.
2. Go to **Integrations**, create a new integration and a flow inside it.
3. Open the flow in the **Designer**, drag on an **HTTP Action** node, name it, point it at your Connection, and set a URL/method.
4. Click **Save**, then **Run**.
5. Jump to **Activity Log** to watch the run complete, then click in to see the step detail.
6. If you want to see the failure path, point a second flow at an endpoint that returns an error, run it, and check **Dead Letters** for the resulting entry — try **Retry**.
7. Open **Flow Settings** on a flow and try setting a **Cron** schedule or generating a **Webhook** URL.

---

## Stopping the servers

Both the API and the client are running as local dev servers for this session. When you're done testing, let me know and I'll shut them down — or if you're running this yourself later, `Ctrl+C` in each terminal (or re-run `dotnet run` from `src/SimpleIPaaS.Api` and `src/SimpleIPaaS.Client`) works the same way.
