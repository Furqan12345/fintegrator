# SimpleIPaaS — User Guide

**Running now:** API at http://localhost:5000 · Client at http://localhost:5001
**Sign-in:** none needed in this local environment — the client is pre-configured with a development API key (`dev-api-key`), which the platform accepts as tenant `11111111-1111-1111-1111-111111111111`.

Open **http://localhost:5001** in your browser to begin.

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

- **Palette (left):** drag on five node types — **HTTP Action**, **Mapper**, **Branch**, **Persisted State**, **Debug**.
- **Canvas (center):** connect nodes by dragging from one node's output port to another's input. A **Branch** node exposes two ports, `true` and `false`, for conditional routing.
- **Inspector (right):** click any node to configure it — every node needs a unique **Node Name** (this is how later nodes reference its output). HTTP Action nodes let you pick a Connection, method, URL, and optional transformation scripts (dynamic URL, pre-flight request shaping, post-flight response mapping) written in C#.
- **Flow Settings** (button in the toolbar): configure how the flow starts —
  - **Manual** — only runs when you click Run.
  - **Cron** — enter a cron expression; you'll see a plain-English description and the next few scheduled run times.
  - **Webhook** — the platform generates a secret URL you can copy; POST to that URL from any external system to trigger the flow. Use **Regenerate Secret** if the URL is ever compromised.
- **Save** validates the flow first (no empty flows, no duplicate node names, no dangling edges, no cycles) and shows an inline error banner if something's wrong — fix it and save again.
- **Delete Node** button removes the selected node and its connections.

## 4. Activity Log (`/activity-log`)

Every run of every flow shows up here, newest first.

- Filter by **flow** or **status**.
- See the **trigger source** (Manual / Webhook / Cron) for each run.
- Rows for runs that are still **Queued** or **Running** get a **Cancel** button, and the page auto-refreshes every 10 seconds while anything is active.
- Click into a run to see its step-by-step timeline with request/response detail.

## 5. Dead Letters (`/deadletters`)

When a step in a flow fails, it lands here instead of vanishing into a log.

- Filter by status: **Pending**, **Retrying**, **Resolved**, **Discarded**.
- **Retry** replays the failed step through the full pipeline (its scripts and auth run again, not just a raw resend).
- **Discard** if you don't want it retried again (asks for confirmation).
- Entries that have hit the retry limit show a warning badge.

---

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
