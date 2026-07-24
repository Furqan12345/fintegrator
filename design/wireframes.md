# SimpleIPaaS — UI Design & Wireframes

**Document type:** UX design specification with wireframes and build instructions
**Product:** SimpleIPaaS — Enterprise Integration Platform
**Applies to:** SimpleIPaaS.Client (Blazor WebAssembly)

---

## 1. Design Principles

1. **Flow-first navigation.** The integration flow is the primary object; every screen is at most two clicks from a flow.
2. **Show the run, not the log.** Monitoring surfaces executions as timelines with payloads inline, never raw log text.
3. **Progressive disclosure.** Node cards show name + type at rest; configuration (scripts, auth) opens in a side panel only on selection.
4. **No dead ends on failure.** Every failed step links to its dead-letter entry with retry/discard actions in place.
5. **Tenant-silent UI.** Tenancy is resolved by the platform API key; the UI never asks the user to pick or type a tenant.

### Visual language

| Token | Value / rule |
|---|---|
| Layout | Fixed left navigation rail (72–220 px), fluid content area, 16 px base grid |
| Type | System font stack; page title 20 px semibold; body 14 px; monospace for payloads/scripts |
| Status colors | Success `#16a34a` · Failed `#dc2626` · Running `#2563eb` · Queued `#9ca3af` · Cancelled `#a16207` · Discarded `#6b7280` |
| Node accents | HTTP Action blue · Mapper purple · Branch amber · Persisted State teal · Debug gray |
| Feedback | Toasts for save/run/retry actions; inline field validation; empty states with a primary CTA |

---

## 2. Application Shell

```
┌──────┬────────────────────────────────────────────────────────────┐
│      │  {Page title}                              {Primary CTA}   │
│  ◧   ├────────────────────────────────────────────────────────────┤
│ Nav  │                                                            │
│      │                    Page content area                       │
│ Home │                                                            │
│ Conn │                                                            │
│ Act  │                                                            │
│ DLQ  │                                                            │
│      │                                                            │
└──────┴────────────────────────────────────────────────────────────┘
```

**Navigation items:** Integrations (home `/`), Connections `/connections`, Activity Log `/activity`, Dead Letters `/deadletters`. Active item gets a filled accent bar. The shell lives in `MainLayout.razor`.

---

## 3. Screen: Integrations (Home, `/`)

```
┌────────────────────────────────────────────────────────────────────┐
│  Integrations                                   [+ New Integration]│
├────────────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────────────────┐   │
│ │ ▸ Order Sync                                    3 flows      │   │
│ │   ┌─────────────────────────────────────────────────────┐    │   │
│ │   │ Flow: Shopify → ERP        ● Active   Trigger: Cron │    │   │
│ │   │ Last run: ✓ 12 min ago     [Open] [Run] [⋯ Delete]  │    │   │
│ │   └─────────────────────────────────────────────────────┘    │   │
│ │   ┌─────────────────────────────────────────────────────┐    │   │
│ │   │ Flow: Refund Webhook       ● Active  Trigger: Webhook│   │   │
│ │   └─────────────────────────────────────────────────────┘    │   │
│ └──────────────────────────────────────────────────────────────┘   │
│  (empty state: "No integrations yet — create your first one")      │
└────────────────────────────────────────────────────────────────────┘
```

**Instructions**
- Integrations are collapsible groups; flows are cards inside them.
- Flow card shows: name, status pill, trigger type badge, last-run outcome + relative time.
- `Run` enqueues (`POST /run`), shows a toast with a link to the new execution.
- `Delete` (flow or integration) requires a typed-name confirm dialog; calls the DELETE endpoints.
- `Open` navigates to the Flow Designer.

---

## 4. Screen: Flow Designer (`/flow/{id}`)

```
┌────────────────────────────────────────────────────────────────────┐
│ ← Flows   Shopify → ERP        [Flow Settings] [Save] [Run ▶]      │
├─────────────┬──────────────────────────────────────┬───────────────┤
│ PALETTE     │            CANVAS                    │ INSPECTOR     │
│             │   ┌──────────┐      ┌──────────┐     │ (on select)   │
│ ⬡ HTTP      │   │ HTTP     │─────▶│ Mapper   │     │ Node name     │
│ ⬡ Mapper    │   │ getOrders│      │ toErp    │     │ [getOrders ]  │
│ ⬡ Branch    │   └──────────┘      └────┬─────┘     │ Connection ▾  │
│ ⬡ State     │                     true │ false     │ Method  URL   │
│ ⬡ Debug     │                  ┌───────┴──┐        │ Auth ▾        │
│             │                  │ Branch   │        │ ─ Scripts ─   │
│             │                  │ hasItems │        │ URL code      │
│             │                  └──────────┘        │ Pre-flight    │
│             │                                      │ Post-flight   │
│             │                                      │ [Delete node] │
└─────────────┴──────────────────────────────────────┴───────────────┘
```

**Instructions**
- Palette: drag (or click-to-add) the five node types onto the Z.Blazor.Diagrams canvas.
- Every node requires a unique **Node Name** — it is the key under which the node's output is stored in flow state; the inspector validates uniqueness live.
- HTTP Action inspector: Connection dropdown (from Connections), method, URL (or dynamic URL script), inline auth override (all auth types), pre/post-flight script editors (monospace, expandable modal).
- Branch node exposes `true`/`false` source ports; edges from them are labeled on the canvas.
- **Save validation (client-side, before PUT):** at least one node; unique node names; all edges reference existing nodes; no cycles; branch nodes have both ports handled or a warning is shown. Server re-validates.
- **Flow Settings panel** (modal): trigger type (Manual / Webhook / Cron), cron expression with human-readable preview and next-3-fires, webhook URL display with copy button + "Regenerate secret", flow status (Draft/Active).
- `Run ▶` is disabled while the flow has unsaved changes.

---

## 5. Screen: Connections (`/connections`) + Connection Wizard

```
┌────────────────────────────────────────────────────────────────────┐
│ Connections                                      [+ New Connection]│
├────────────────────────────────────────────────────────────────────┤
│ Name          Base URL                  Auth            Status     │
│ Shopify Prod  https://x.myshopify.com   OAuth2 (CC)     ● Active   │
│ ERP API       https://erp.internal      API Key         ● Active   │
└────────────────────────────────────────────────────────────────────┘

Wizard (modal, 3 steps):
┌─────────────────────────────────────────────┐
│ 1 Basics   2 Authentication   3 Test        │
│ ─────────────────────────────────────────── │
│ Auth type ▾  [OAuth2 Client Credentials]    │
│ Token endpoint [___________________]        │
│ Client ID [________]  Client secret [•••]   │
│ Scope [________]                            │
│                     [Back] [Test] [Save]    │
└─────────────────────────────────────────────┘
```

**Instructions**
- Auth type selector drives the visible fields: None (nothing) · Basic (user/pass) · API Key (header name, value, placement header/query) · Bearer (token) · OAuth2 Client Credentials (token endpoint, client id/secret, scope) · OAuth2 Refresh Token (token endpoint, client id/secret, refresh token) · OAuth2 Auth Code (token endpoint, client id/secret, access token, refresh token) · Custom (repeatable header name/value rows).
- Secrets render as password fields; **existing secrets are never echoed back** — editing shows a "value stored — replace?" placeholder.
- Step 3 "Test" calls `POST /api/connections/{id}/test` and shows the round-trip result.

---

## 6. Screen: Activity Log (`/activity`)

```
┌────────────────────────────────────────────────────────────────────┐
│ Activity Log        Flow ▾  Status ▾            ◀ 1 2 3 ▶          │
├────────────────────────────────────────────────────────────────────┤
│ Status   Flow             Trigger   Started        Duration  Steps │
│ ✓ Done   Shopify → ERP    Cron      10:32:14       4.2 s     6/6   │
│ ✗ Failed Refund Webhook   Webhook   10:30:02       1.1 s     2/4   │
│ ● Running Inventory Sync  Manual    10:29:55       …         3/…   │
└────────────────────────────────────────────────────────────────────┘
```

**Instructions** — paged server-side (`page`, `pageSize`, `flowId`, `status` filters); rows link to Execution Detail; Running rows show a Cancel action (`POST /executions/{id}/cancel`); auto-refresh every 10 s while any row is Queued/Running.

---

## 7. Screen: Execution Detail (`/execution/{id}`)

```
┌────────────────────────────────────────────────────────────────────┐
│ ← Activity   Execution 8f3a…   ✗ Failed   Started 10:30:02  1.1 s  │
├────────────────────────────────────────────────────────────────────┤
│ ✓ getOrder      HTTP 200   320 ms   ▸ request ▸ response           │
│ ✓ mapRefund     —          12 ms    ▸ output                       │
│ ✗ postToErp     HTTP 502   760 ms   ▾ error                        │
│    ┌────────────────────────────────────────────────┐              │
│    │ Bad Gateway — upstream timeout                 │              │
│    │ [View dead letter →]                           │              │
│    └────────────────────────────────────────────────┘              │
│ ○ notifyOps     skipped                                            │
└────────────────────────────────────────────────────────────────────┘
```

**Instructions** — vertical step timeline in execution order; payload accordions render pretty-printed JSON in monospace; failed steps deep-link to their DLQ entry; skipped downstream steps shown hollow.

---

## 8. Screen: Dead Letters (`/deadletters`)

```
┌────────────────────────────────────────────────────────────────────┐
│ Dead Letters                 Status ▾ [Pending]                    │
├────────────────────────────────────────────────────────────────────┤
│ Flow            Node       Error              Retries  Actions     │
│ Refund Webhook  postToErp  502 Bad Gateway    2/5      [Retry][Discard] │
│ Inventory Sync  pushStock  401 Unauthorized   5/5 ⚠    [Retry][Discard] │
└────────────────────────────────────────────────────────────────────┘
```

**Instructions** — status filter (Pending / Retrying / Resolved / Discarded); Retry calls `POST /api/deadletters/{id}/retry` (full-pipeline replay); Discard requires confirm; rows link back to the source execution; entries at max retries show a warning badge.

---

## 9. Cross-cutting Behaviors

- **Errors:** all API errors arrive as ProblemDetails → shown as toast (transient) or inline banner (blocking); 401 shows a "platform key missing/invalid" full-page state.
- **Loading:** skeleton rows for tables, spinner overlay for the canvas; optimistic UI only for node position drags.
- **Accessibility:** all actions keyboard-reachable; status conveyed by icon + text, never color alone; focus trap in modals.
- **State:** each screen maps to a Fluxor store (FlowState, ConnectionState, ExecutionState, DeadLetterState); effects own all HTTP; reducers stay pure.
