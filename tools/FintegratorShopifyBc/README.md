# Shopify ⇄ Business Central integration (fintegrator.io)

Three production-shaped integration flows plus the Business Central AL extension they talk to.

| piece | where |
|---|---|
| BC extension (AL) | `integrations/business-central-extension/` — also mirrored to `C:\Users\FurqanIrfan\Desktop\PersonalWorkFolder\Fintegrator-BC` |
| frozen API contract | [`BC-EXTENSION-CONTRACT.md`](BC-EXTENSION-CONTRACT.md) — the single source of truth both sides obey |
| flow templates + scripts | `flows/*.flow.json`, `flows/scripts/**` |
| generated (deployable) flows | `flows/build/*.json` — produced by `scripts/build_flows.py`, never edited by hand |
| deploy tool | `scripts/deploy_flows.py` |
| engine-level tests | `tests/SimpleIPaaS.Tests/ShopifyBusinessCentral/ShopifyBusinessCentralFlowTests.cs` |
| local E2E harness | `scripts/run_e2e.py` + `mock-vendors/` (Python mock of Shopify + BC) |

## Data flow

```
                       ┌──────────────────────── fintegrator.io ────────────────────────┐
Shopify Admin GraphQL  │ Order Create:   Schedule → FetchShopifyOrders (updated_at > + paid)       │
   orders(first,after, │                 → SaveOrderWatermark (persisted state)                   │  BC extension
   query,sortKey)      │                 → FilterAlreadyImportedOrders (cross-reference ledger)  │
                       │                 → FanOut ─┬─ child: CreateBcSalesOrder ────────────────┼─► POST
                       │                          └─ child: MergeImportResult →                 │    fintegratorSalesOrders
                       │                             RecordImportedOrder → Tag → Success       │    (deep insert, idempotent)
                       ├────────────────────────────────────────────────────────────────┤
                       │ Order Shipment: FetchBcShipments (lastModified/entryNo >)       │  BC extension
                       │                 → SaveShipmentWatermark → FanOut ─┬─ child:      │◄─ GET
                       │                     FetchShopifyFulfillmentOrders │   Branch      │    fintegratorShippedOrders
                       │                     → CreateShopifyFulfillment ◄──┴─ or skip    │    (?$expand=…Lines)
                       ├────────────────────────────────────────────────────────────────┤
                       │ Inventory:      FetchBcInventoryChanges (lastModified/entryNo >)│  BC extension
                       │                 → SaveInventoryWatermark → FanOut ─┬─ child:     │◄─ GET
                       │                     LookupShopifyVariant → Branch   │             │    fintegratorInventoryChanges
                       │                     → SetShopifyInventory ◄─────────┴─ or skip   │
                       └────────────────────────────────────────────────────────────────┘
```

* Shopify calls are Admin GraphQL on the Shopify connection; BC calls are OData-style API calls on the
  Business Central connection. Credentials live only in the connections (encrypted) — never in a flow.
* Each flow keeps its cursor in the flow's **persisted state** (designer → *State*), so a run continues
  exactly where the previous run stopped. `FanOut` cards then process one record per child transaction.

## Persisted state keys (per flow, editable in the designer's State modal)

| key | flow | meaning |
|---|---|---|
| `bcCompanyId` | all three | Business Central company GUID. **Required** — calls fail with an actionable message when it is missing. |
| `shopifyApiVersion` | order create, inventory | Shopify Admin API version used by `urlCode` (default `2026-07`). Shopify supports each stable version for ~12 months; bump this key when yours sunsets. |
| `ordersWatermark` | order create | newest Shopify `updatedAt` of the last batch. The next run asks for `updated_at:>'<watermark − ordersOverlapSeconds>'`. |
| `ordersOverlapSeconds` | order create | safety overlap in seconds (default **900** = one cron interval). It closes the same-second gap and gives a failed import another chance while its order is still in the window — free of Business Central calls, because orders already in the ledger are filtered out. Set `0` to disable. |
| `ordersFinancialStatusFilter` | order create | Shopify search fragment added to the pull (default `financial_status:paid`, so unpaid/voided orders never reach BC; an order paid later gets a new `updatedAt` and is picked up by a following run). Set it to `""` to pull every financial status. |
| `ordersStartFromUtc` | order create | optional "first run" floor instead of "now − 7 days". |
| `ordersPageSize` | order create | page size for the GraphQL pull (default 25, `maxPages` 20). |
| `shipmentsEntryNo` / `inventoryEntryNo` | shipment, inventory | **the cursor**: the highest BC feed `entryNo` already processed (`$filter=entryNo gt N`). An auto-increment integer cannot drift in precision or format the way a timestamp can. |
| `shipmentsWatermark` / `inventoryWatermark` | shipment, inventory | newest `lastModifiedDateTime` seen — informational, not used for filtering. |
| `shipmentsPageSize` / `inventoryPageSize` | shipment, inventory | `$top` per run (default 200 / 500). |
| `notifyCustomerOnFulfillment` | order shipment | `true` sends Shopify's shipping notification (default `false`). |
| `shopifyLocationId` | inventory | pin the Shopify location for every BC location. |
| `shopifyLocationByBcLocation` | inventory | per-location map, e.g. `{"WH1":"gid://shopify/Location/123"}`; wins over `shopifyLocationId`. When neither is set the first active location is used. |
| `*LastRunAtUtc`, `*LastCount` | all | operational breadcrumbs for troubleshooting. |

## Deploying

```powershell
# 1. Business Central: publish the extension (VS Code + AL Language extension)
#    open integrations/business-central-extension, download symbols, publish, then
#    grant the "FIN Integrator" permission set, open "FIN Integrator Setup" and set the
#    default customer/location/shipping agent, and add the "FIN Shopify Item Mappings".

# 2. fintegrator.io: create/refresh the integration, connections, flows and persisted state
python tools\FintegratorShopifyBc\scripts\deploy_flows.py `
    --base-url http://localhost:5000 --api-key dev-api-key `
    --shopify-connection "shopify ate clothdiaper" `
    --bc-connection "business central" `
    --bc-base-url "https://api.businesscentral.dynamics.com/v2.0/<tenantId>/<environment>/api/fintegrator/integration/v1.0" `
    --bc-username "<user>" --bc-password "<web service access key>" `
    --bc-company-id "<company GUID>" --publish
```

The deploy script resolves connections **by name** (never printing secrets), creates the integration
grouping, creates or updates the three flows, seeds any missing persisted-state key and optionally
publishes (activates) them. Re-running it is safe: node/edge ids are deterministic, so an update keeps
node identity and execution history. `--reset-state` re-seeds state from scratch (use with care: it
throws the cursors away).

> **Trap — updating a flow wipes its cursors.** `PUT /api/integrationflow/{id}` replaces the whole
> flow and `persistedStateJson` defaults to `{}`, so any update (a redeploy, a hand edit, the AI
> builder) clears the flow's state unless it is sent back. `deploy_flows.py` reads the state *before*
> the update and restores it afterwards; if you edit a flow another way, check the designer's **State**
> modal afterwards — otherwise the next run silently falls back to the default start window
> ("now − 7 days" for orders, the whole feed for the BC cursors).

* The end-to-end harness deploys its own copies named `<flow> (e2e mock)` so it never disturbs a real
  deployment while it runs against the mock vendors.

**Canvas layout:** node `positionX`/`positionY` follow one policy so the designer draws the flow
without overlapping cards: a **440px column step** (cards are 300-340px wide) and **760px row steps**
between the main path, the per-item fan-out branch and any alternate branch. `order-create` is a single
main row (y 0) with its fan-out tail one row down (y 760); `order-shipment` and `inventory-update` add
the failed/skipped branch on a third row (y 1520). Keep new nodes on that grid, or use the designer's
**Tidy** button and save.

Without `--publish` the flows stay Draft and only run manually (or from a test case); the cron
expressions on the Schedule nodes take effect once a flow is Active.

## Running and monitoring

* Manual run: **Run** in the designer, or `POST /api/integrationflow/{flowId}/run`.
* The Engine process must be running (`dotnet run --project src/SimpleIPaaS.Engine`) — the API only
  queues runs; the Engine claims them from the database.
* Transactions page: parent runs plus one child row per fan-out item (`triggerSource = FanOut`), with
  the `Identifier`/`Message` the flows set (`Shopify #1001 -> BC 101234`, `BC shipment 102301 -> …`).
* Packet Logs page: every HTTP call with request/response bodies — the first place to look when
  Shopify or BC answers something unexpected.
* Dead letters: a failed child carries the payload, the flow/persisted state and an attempt history.
  Publishing replay of an HTTP POST is blocked unless the flow has **Allow POST replay** (default off)
  — that guard is what stops a replay from creating a duplicate fulfillment or inventory set.

### Cold-start note

Flow scripts are compiled with Roslyn on first use. With the default `Scripting:TimeoutSeconds = 10`
a cold compile under heavy load can time out (the run fails with *"… script failed … Script execution
timed out after 10 seconds"*). Either run each flow once after a restart, or raise
`Scripting:TimeoutSeconds` (for example to 30) in the Engine's configuration.

## Verifying

```powershell
# authoring gates + real engine execution of the generated flows (23 tests)
dotnet test tests\SimpleIPaaS.Tests\SimpleIPaaS.Tests.csproj --filter "FullyQualifiedName~ShopifyBusinessCentral"

# mock vendor self-test (49 checks)
python tools\FintegratorShopifyBc\mock-vendors\selftest.py

# end-to-end: deploy against the mock and drive real runs (40 checks)
python tools\FintegratorShopifyBc\scripts\run_e2e.py
```

`run_e2e.py` writes `artifacts/e2e-report.json` and the mock logs every request to
`mock-vendors/logs/requests.jsonl`, so each assertion is traceable to the bytes that crossed the wire.

What the E2E proves: the updatedAt watermark advances and the second run asks only for newer orders
(rewound by the overlap); one child transaction per order; BC sales orders carry the Shopify ids and
lines; unpaid orders are never pulled while paid ones are; orders already recorded in the ledger are
filtered out before Business Central is called (a no-op run creates zero children and zero BC calls);
replaying a rewound window cannot duplicate a sales order; shipments become Shopify fulfillments with
the BC tracking number and are flagged back in BC; inventory changes set the absolute quantity;
unmapped SKUs are reported as skipped instead of failing.

What it cannot prove: the real BC extension's compiled behaviour (no AL toolchain here), real
Shopify/BC API contracts under rate limits, partial shipments against partially fulfilled orders,
multi-location inventory, and Shopify version drift.

## Known limitations / trade-offs

* **The watermark advances before the fan-out children run.** If a child then fails, that record is not
  re-fetched automatically — it waits in the dead-letter queue. All three flows publish POST replay
  (`allowPostReplay: true`) so a dead letter *can* be replayed, and each direction is protected:
  the BC extension de-duplicates orders on the Shopify order id, the inventory mutation carries a
  deterministic `@idempotent` key derived from the feed row, and the shipment flow only pushes when
  the feed row is not already flagged `shopifyPushed`. Recovery by replay is therefore safe **except**
  for the narrow window where Shopify accepted a fulfillment but the follow-up PATCH of the BC flag
  failed — that case fails the child on purpose and its dead letter says so; check Shopify's order
  before replaying it manually.
* **Bounded batches.** Each run processes at most `$top` feed rows (200 shipments / 500 inventory
  changes) and at most 500 fan-out items; the next scheduled run continues from the cursor. The BC
  flows collapse their batch to the newest movement per key before fanning out (one child per Shopify
  order / per SKU + location) because fan-out children run concurrently and inventory writes are
  absolute — without the collapse, a reordered pair of movements could leave the older quantity live.
* **The order pull is paid-only and de-duplicated by a cross-reference ledger.** The Shopify search is
  `updated_at:>'<watermark − overlap>' financial_status:paid`, and `FilterAlreadyImportedOrders`
  (`SkipExisting` on the `shopify-orders-imported` list, keyed by the Shopify order id) drops anything
  already imported *before* the fan-out. Consequence: the overlap window and any replay cost Shopify
  reads but **zero** Business Central calls, and a failed import is retried automatically for as long
  as its order stays inside the window. The deliberate trade-off is **strict once-ever semantics**:
  an order that is edited in Shopify after being imported is *not* re-pushed to BC (remove the filter
  card, or key the list on `id` + `updatedAt`, if you later want change-syncing instead).
* **Shopify API version** is pinned in persisted state because mutation and pagination shapes are
  versioned (for example `inventorySetQuantities` requires an `@idempotent` key from 2026-04 onwards —
  the scripts add it only when the configured version is 2026-01 or newer).
* **Rate limiting is reported, not absorbed.** Shopify signals GraphQL throttling with HTTP 200 plus
  `errors`, which the transport-level retry policy cannot see, so the step fails with an explicit
  "rate-limited" message pointing at the batch-size knobs; the parent-path fetches retry naturally on
  the next run because the cursor only advances after a successful fetch.
* **Partial shipments**: BC shipped SKUs/quantities are matched to Shopify fulfillment-order lines and
  never over-shipped, but a shipment that ships only part of an order creates a fulfillment for
  exactly that part, and several shipments of one order inside a single run are merged into the newest
  tracking number (quantities summed).
* **Fan-out children snapshot the parent's persisted state.** Nothing writes durable state downstream of
  a FanOut card (the test suite asserts this) — cursors therefore stay on the parent path.
* **SKU mapping is a BC-side responsibility.** Items without a `FIN Shopify Item Mapping` row (and no
  matching item number) are reported as skipped for inventory and reject the order with an actionable
  message on the order-create side. Inventory requires an *exact* SKU match — a near miss is skipped
  and reported rather than written to the wrong variant.
