# Mock vendors for fintegrator.io E2E runs

One dependency-free Python 3 server (stdlib only) that mocks the two vendors the
**Shopify → Business Central** flows talk to, so flows can be exercised end to end without
real credentials:

| vendor | path prefix | what is implemented |
|---|---|---|
| Shopify Admin GraphQL | `/admin/api/{version}/graphql.json` | `orders`, `fulfillmentOrders`, `fulfillmentCreateV2`, `productVariants`, `locations`, `inventorySetQuantities` |
| Business Central OData/API v2 | `/api/fintegrator/integration/…` | `fintegratorSalesOrders`, `fintegratorShippedOrders`, `fintegratorInventoryChanges` — exactly the shapes in [`../BC-EXTENSION-CONTRACT.md`](../BC-EXTENSION-CONTRACT.md) |
| test harness control | `/__control/…` | reset, seeds, failure injection, state + request inspection, clean shutdown |

Everything else answers `404` with a JSON body naming the path. State is in memory only
(`/__control/reset` restores the seed data); the only file written is the request log.

Files:

```
tools/FintegratorShopifyBc/mock-vendors/
  server.py                  the mock server
  selftest.py                starts the server and exercises every endpoint (49 checks)
  README.md                  this file
  logs/requests.jsonl        request log (created on startup, gitignored via logs/)
```

## Start it

```powershell
python tools/FintegratorShopifyBc/mock-vendors/server.py --port 5099
```

```
LISTENING 5099
LOG C:\...\tools\FintegratorShopifyBc\mock-vendors\logs\requests.jsonl
```

* stdout prints `LISTENING <port>` (flushed) as soon as the socket is listening — wait for
  that line before firing the first request. `--port 0` binds a free port and prints it.
* `ThreadingHTTPServer`, so fan-out child executions are served concurrently.
* Stop with `Ctrl+C` (SIGINT) or `POST /__control/shutdown`; both shut down cleanly and print
  `STOPPED`.

| flag | default | meaning |
|---|---|---|
| `--port` | `5099` | listen port |
| `--host` | `127.0.0.1` | bind address |
| `--log-dir` | `<script dir>/logs` | directory that receives `requests.jsonl` |

Run the self-test (starts its own server on a free port, ~5 s):

```powershell
python tools/FintegratorShopifyBc/mock-vendors/selftest.py
```

### Wiring fintegrator to it

* Shopify connection: base URL `http://127.0.0.1:5099`, endpoint
  `/admin/api/2026-10/graphql.json` (any version token works; auth headers are ignored).
* Business Central connection: base URL
  `http://127.0.0.1:5099/api/fintegrator/integration/v1.0`, and make sure the flow's persisted
  state has `bcCompanyId` (the mock accepts any value, e.g. `bcCompanyId = "abc-123"`), so
  each node appends `companies({companyId})/{entitySet}`.
* Reset between scenarios with `POST /__control/reset`.

## Shopify GraphQL

`POST /admin/api/{version}/graphql.json`, body `{"query": "...", "variables": {...}}`.
The operation is chosen by inspecting the query text (case-insensitive), so a real flow
payload works as-is. `data` envelopes on success; on a simulated problem the response is
HTTP **200** with `{"errors":[{"message":"..."}]}` (GraphQL semantics — the flow must be able
to detect it). Unknown operations answer the same way.

### `orders` (cursor paginated)

* Page size = `variables.first` (default 25, capped at 250), cursor = `variables.after`
  (or `variables.cursor`). Cursors are `cursor-<offset>`; the last page reports
  `hasNextPage: false` and a non-null `endCursor`.
* Delta filter from `variables.query` and/or an inline `query:` argument:
  `updated_at:>'2026-10-15T09:00:00Z'` (also matched without quotes, and as `updatedAt`).
  Only orders with `updatedAt` **strictly greater** than the value are returned; no filter
  returns everything.
* Node shape is the contractual one (id/name/updatedAt/createdAt/currencyCode/
  displayFinancialStatus/displayFulfillmentStatus/email/note/tags/currentTotalPriceSet/
  shippingAddress/customer/lineItems.edges[].node{id,name,sku,quantity,currentQuantity,
  variant{id},originalUnitPriceSet}).

```jsonc
// request
{"query":"query($first:Int,$after:String,$query:String){orders(first:$first,after:$after,query:$query){pageInfo{hasNextPage endCursor} edges{node{id name updatedAt}}}}",
 "variables":{"first":2,"query":"updated_at:>'2026-10-15T09:00:00Z'"}}
// response
{"data":{"orders":{"pageInfo":{"hasNextPage":true,"endCursor":"cursor-2"},
 "edges":[{"node":{"id":"gid://shopify/Order/5100000001","name":"#1001","updatedAt":"2026-10-15T09:10:00Z"}},
          {"node":{"id":"gid://shopify/Order/5100000002","name":"#1002","updatedAt":"2026-10-15T09:20:00Z"}}]}}}
```

### `fulfillmentOrders`

Parameterised by `variables.id` (also accepts `orderId`, or an inline `order(id: "...")`).
Unknown order id → `{"data":{"order":null}}` (the flow must handle a null order).

```jsonc
// response for gid://shopify/Order/5100000001
{"data":{"order":{"id":"gid://shopify/Order/5100000001","name":"#1001","fulfillmentOrders":{"nodes":[
  {"id":"gid://shopify/FulfillmentOrder/6001","status":"OPEN","lineItems":{"nodes":[
    {"id":"gid://shopify/FulfillmentOrderLineItem/6501","remainingQuantity":2,
     "lineItem":{"id":"gid://shopify/LineItem/9001","sku":"TSHIRT-BLK-M"}}]}}]}}}}
```

### `fulfillmentCreateV2` (mutation)

Reads `variables.fulfillment` (or `variables.input`). Fulfillment order ids must belong to an
order in the current order set (they are (re)generated by `/__control/reset` and
`/__control/orders`, so fetch `fulfillmentOrders` first — exactly like the flow does).

```jsonc
// response
{"data":{"fulfillmentCreateV2":{"fulfillment":{"id":"gid://shopify/Fulfillment/7001","status":"SUCCESS",
 "trackingInfo":[{"number":"1Z999AA10123456784","company":"UPS","url":null}]},"userErrors":[]}}}
```

Unknown fulfillment order **or** an empty/missing `lineItemsByFulfillmentOrder` →
`userErrors:[{"field":"lineItemsByFulfillmentOrder","message":"…"}]` (never a crash, always
HTTP 200). Successful fulfillments are recorded and visible in `/__control/state.fulfillments`.

### `productVariants`

SKU comes from `variables.query` (typical: `query: "sku:TSHIRT-BLK-M"`) or inline. Known SKU →
one node with `inventoryItem`; unknown SKU → `"nodes": []`. The known-SKU list is the **item
map** — see `/__control/items`.

```jsonc
{"data":{"productVariants":{"nodes":[{"id":"gid://shopify/ProductVariant/8001","sku":"TSHIRT-BLK-M",
  "inventoryItem":{"id":"gid://shopify/InventoryItem/8101","tracked":true}}]}}}
```

### `locations`

```jsonc
{"data":{"locations":{"nodes":[{"id":"gid://shopify/Location/9001","name":"Main Warehouse","isActive":true}]}}}
```

### `inventorySetQuantities` (mutation)

Reads `variables.input.quantities[] = {inventoryItemId, locationId, quantity, name?}`.
Records `{inventoryItemId, locationId, quantity, name, reason}` (see
`/__control/state.inventorySets`); `delta` is against the last set quantity for that
item/location. Missing `inventoryItemId`/`locationId`/`quantities` →
`userErrors` with a message (no state change).

```jsonc
{"data":{"inventorySetQuantities":{"inventoryAdjustmentGroup":{"reason":"correction",
  "changes":[{"name":"available","delta":40,"quantityAfterChange":40}]},"userErrors":[]}}}
```

## Business Central

Base path: `/api/fintegrator/integration/{version}/companies({companyId})/{entitySet}`
(versions like `v1.0` are accepted; any company id). Responses carry
`@odata.context` on collections and `@odata.etag` on entities. Errors are OData shaped:

```jsonc
{"error":{"code":"Application_DialogException","message":"Shopify SKU X does not match any Business Central item."}}
```

`code` is `Application_DialogException` for business rejections (HTTP 400), `NotFound` for
unknown entity sets / keys (404), `BadRequest` for unsupported verbs (405).

### `fintegratorSalesOrders`

| method | path | behaviour |
|---|---|---|
| POST | `…/fintegratorSalesOrders` | deep insert (header + `fintegratorSalesOrderLines`), HTTP 201 |
| GET | `…/fintegratorSalesOrders` | OData envelope `{"@odata.context":…, "value":[…]}` (supports `$filter` on `shopifyOrderId`/`salesOrderNo`/`status`/`customerNo`/`id` with `eq`, plus `$top`) |
| GET | `…/fintegratorSalesOrders({id})` | single entity (key = `id` GUID, `salesOrderNo` or `shopifyOrderId`) |
| PATCH | `…/fintegratorSalesOrders({id})` | field merge, returns the updated entity |

POST semantics:

* `shopifyOrderId` empty → 400; any line without `sku` or `quantity` → 400.
* SKU not in the item map → **400** `Application_DialogException` with
  `"Shopify SKU <sku> does not match any Business Central item."` (the message is what the
  flow surfaces) — nothing is created.
* New order → `status:"Created"`, `createdSalesOrder:true`, `salesOrderNo` from a counter
  starting at `101001`, generated `id` (GUID), lines with `lineNo` 10000/20000/… when absent,
  every header field echoed, `customerNo` defaulted, `totalAmount` computed when omitted.
* Same `shopifyOrderId` again → HTTP **201** with `status:"Duplicate"`,
  `createdSalesOrder:false` and the **same** `salesOrderNo`; no second order is stored.

```jsonc
// POST body (contract example)
{"shopifyOrderId":"gid://shopify/Order/5100000001","shopifyOrderName":"#1001","customerNo":"","orderDate":"2026-10-15",
 "currencyCode":"USD","note":"Shopify order #1001","shippingName":"Jane Doe","shippingAddress1":"1 Main Street",
 "shippingAddress2":"Apt 4","shippingCity":"Springfield","shippingPostCode":"12345","shippingCountryCode":"US",
 "shippingPhone":"+15550100","totalAmount":129.90,
 "fintegratorSalesOrderLines":[{"sku":"TSHIRT-BLK-M","description":"T-Shirt Black M","quantity":2,"unitPrice":29.95}]}
// 201 response (fields elided)
{"id":"7a80bff6-…","shopifyOrderId":"gid://shopify/Order/5100000001","shopifyOrderName":"#1001","customerNo":"C00010",
 "orderDate":"2026-10-15","currencyCode":"USD","status":"Created","salesOrderNo":"101001","errorMessage":"",
 "createdSalesOrder":true,"fintegratorSalesOrderLines":[{"lineNo":10000,"sku":"TSHIRT-BLK-M","quantity":2,
 "unitPrice":29.95,"description":"T-Shirt Black M"}],"@odata.etag":"W/\"fintegratorSalesOrders=7a80bff6-…\""}
```

### `fintegratorShippedOrders` (read-only feed + PATCH)

`GET …/fintegratorShippedOrders` supports the flow's OData query:

```
?$filter=lastModifiedDateTime gt {watermark}&$orderby=lastModifiedDateTime asc,entryNo asc&$top=200&$expand=fintegratorShippedOrderLines
```

* `$filter`: `lastModifiedDateTime gt <iso>`, `ge <iso>`, `eq <iso>`, `entryNo gt N`, and the
  compound `lastModifiedDateTime gt X or (lastModifiedDateTime eq X and entryNo gt N)`
  (watermark tie-breaker). Timestamps are compared as real datetimes, so mixed second/fraction
  precision is safe. An unparsable filter filters nothing (tolerant).
* `$orderby`: any of the entity fields, `asc`/`desc` per field; default is
  `lastModifiedDateTime asc,entryNo asc`.
* `$top`: default 100, clamped to max 1000.
* `$expand=fintegratorShippedOrderLines`: the nested `fintegratorShippedOrderLines` array
  (`lineNo`, `itemNo`, `sku`, `quantity`) is attached **only** when expanded.
* `GET …({entryNo})` returns one row (404 when missing); `PATCH …({entryNo})` merges fields
  (e.g. `{"shopifyPushed":true}`) and returns the updated entity.

```jsonc
{"@odata.context":"http://127.0.0.1:5099/api/fintegrator/integration/v1.0/$metadata#companies(abc-123)/fintegratorShippedOrders",
 "value":[{"entryNo":2,"lastModifiedDateTime":"2026-10-15T10:05:00Z","salesShipmentNo":"102002","salesOrderNo":"101002",
 "shopifyOrderId":"gid://shopify/Order/5100000002","shopifyOrderName":"#1002","customerNo":"C00011","shipmentDate":"2026-10-15",
 "shippingAgentCode":"DHL","shippingAgentName":"DHL Express","trackingNo":"JD0146000034567890","trackingUrl":"https://www.dhl.com/track?id=…",
 "shippedQuantity":1,"fullyShipped":true,"shopifyPushed":false,
 "fintegratorShippedOrderLines":[{"lineNo":10000,"itemNo":"MUG-WHITE","sku":"MUG-WHITE","quantity":1}],
 "@odata.etag":"W/\"fintegratorShippedOrders=2\""}]}
```

### `fintegratorInventoryChanges` (read-only feed)

Same OData behaviour (`lastModifiedDateTime`, `entryNo`, `$orderby`, `$top`; no `$expand`
property), fields `entryNo`, `lastModifiedDateTime`, `itemNo`, `variantCode`, `locationCode`,
`sku`, `inventory` (inventory **after** the change), `changeQuantity`.

## Control API

All control responses are JSON; `count`/`ok` fields are as shown. Failure injection never
applies to `/__control/*` itself.

| method + path | body | effect |
|---|---|---|
| `POST /__control/reset` | – | wipes vendor state, restores seed data, resets counters + failure rules → `{"ok":true}` (the request log is kept) |
| `POST /__control/orders` | `{"orders":[…]}` | **replaces** the Shopify order set (full nodes or the minimal shape below) → `{"ok":true,"count":N}` |
| `POST /__control/bc-shipments` | `{"shipments":[…]}` | appends shipment feed rows (`entryNo` assigned when missing, `lastModifiedDateTime` defaults to now-UTC) → `{"ok":true,"count":N}` |
| `POST /__control/bc-inventory` | `{"changes":[…]}` | appends inventory change rows (same defaults) → `{"ok":true,"count":N}` |
| `POST /__control/items` | `{"items":[{"sku":"TSHIRT-BLK-M"}]}` | replaces the known-SKU item map (BC validity + `productVariants` hits) → `{"ok":true,"count":N,"items":[…]}` |
| `POST /__control/fail` | `{"path":"/admin/api/…","status":500,"times":1}` or `{"path":"…","graphqlError":"message","times":1}` | makes the next N matching requests fail (transport status, or GraphQL `errors` with HTTP 200) → `{"ok":true,"failure":{…},"failures":[…]}` |
| `POST /__control/shutdown` | – | clean shutdown (process exits 0, prints `STOPPED`) |
| `GET /__control/state` | – | `{"orders":[…],"salesOrders":[…],"fulfillments":[…],"inventorySets":[…],"shipmentFeed":[…],"inventoryFeed":[…],"items":[…],"counters":{…},"requests":<count>}` |
| `GET /__control/requests?limit=200` | – | **bare JSON array** of the recorded requests, newest last, each `{ts,method,path,query,body,responseStatus}` (body truncated to 8000 chars) |
| `GET /__control/health` | – | `{"ok":true,"listening":true}` — readiness probe |

`/__control/fail` matching: the rule path must equal the request path, be a prefix of it
(e.g. `/admin/api/` matches every version), or end with `*`. `times` defaults to `1`; `0` or a
negative value means "unlimited" (clear with `/__control/reset`). Transport-status injection
answers with `{"error":"Mock: injected transport failure for <path>","status":N}`.

## Seed data (restored by `/__control/reset`)

* 3 Shopify orders — `#1001` @ `2026-10-15T09:10:00Z` (TSHIRT-BLK-M ×2), `#1002` @ `09:20:00Z`
  (MUG-WHITE ×1), `#1003` @ `09:30:00Z` (TSHIRT-BLK-M ×1, MUG-WHITE ×2); fulfilment order ids
  `gid://shopify/FulfillmentOrder/6001..6003`, line item ids `…/9001..`, FO line ids `…/6501..`.
* Item map: `TSHIRT-BLK-M` → variant `…/ProductVariant/8001` + `…/InventoryItem/8101`;
  `MUG-WHITE` → `8002`/`8102`.
* 2 shipment feed rows (`entryNo` 1, 2 — `2026-10-15T09:40:00Z` and `10:05:00Z`, each with one
  nested `fintegratorShippedOrderLines` row).
* 2 inventory change rows (`entryNo` 1, 2 — `09:45:00Z` and `10:10:00Z`).
* No sales orders, fulfillments or inventory sets.

## Seed-data format

`POST /__control/orders` accepts either the full contractual order node or the minimal shape:

```jsonc
{"orders":[{"id":"5100000099","name":"#1099","updatedAt":"2026-11-01T12:00:00Z","currencyCode":"EUR",
            "lineItems":[{"sku":"MUG-WHITE","quantity":3,"unitPrice":12.5}]}]}
```

`id` may be a bare number, a GID or `#name`; missing ids/names/addresses/prices/totals are
generated (line item ids `gid://shopify/LineItem/9xxx`, variant ids from the item map, total =
sum of quantity × unit price when `totalAmount`/`currentTotalPriceSet` is absent). Orders are
always returned in the full contractual shape, sorted by `updatedAt`.

`POST /__control/bc-shipments` row fields (all optional except what your assertions need):
`entryNo`, `lastModifiedDateTime`, `salesShipmentNo`, `salesOrderNo`, `shopifyOrderId`,
`shopifyOrderName`, `customerNo`, `shipmentDate`, `shippingAgentCode`, `shippingAgentName`,
`trackingNo`, `trackingUrl`, `shippedQuantity`, `fullyShipped`, `shopifyPushed`,
`fintegratorShippedOrderLines:[{lineNo,itemNo,sku,quantity}]`.

`POST /__control/bc-inventory` row fields: `entryNo`, `lastModifiedDateTime`, `itemNo`,
`variantCode`, `locationCode`, `sku`, `inventory`, `changeQuantity`.

## Request log

Every request (including `/__control/*`, injected failures and 404s) is appended **after** the
response is produced to `<log dir>/requests.jsonl` — one JSON object per line, flushed per
request:

```json
{"ts":"2026-10-15T19:15:11.089Z","method":"POST","path":"/admin/api/2026-10/graphql.json","query":"","body":"{\"query\":\"…\"}","responseStatus":200}
```

The same entries are available in memory through `GET /__control/requests?limit=N` (last 5000
kept) and counted as `requests` in `/__control/state`. Reset does not clear the log — use it to
assert retry/dead-letter behaviour across scenarios.

## Deliberate simplifications

* **Duplicate POSTs are not staged twice.** A repeat `fintegratorSalesOrders` POST returns the
  existing record's `id` with `status:"Duplicate"`; a real BC staging table would add an audit
  row for each inbound request.
* **The item map is the only source of truth for SKU validity** (BC unknown-SKU errors and
  `productVariants` hits). SKUs seen in mock orders do not implicitly become valid, so tests
  stay deterministic; extend it with `POST /__control/items`.
* **Shopify paging/limits**: `first` is capped at 250 (Shopify's own cap), cursors are the
  literal `cursor-<offset>` strings, and `endCursor` is non-null on non-empty pages. For
  `orders` the whole eligible set is filtered by index, not by a real Shopify filter language —
  only `updated_at` is understood.
* **Shopify mutation results are simplified**: `fulfillmentCreateV2` always reports
  `status:"SUCCESS"` with a single tracking entry, and `inventorySetQuantities` returns one
  `changes` entry per requested quantity (no availability/quantity-name validation beyond the
  required fields).
* **No auth, no rate limits, no ETag concurrency**: `Authorization`/`X-Shopify-Access-Token`
  headers are ignored, and PATCH does not verify `If-Match`.
* **Request bodies are parsed with a `{}` fallback**, so malformed JSON is answered like an
  unknown operation instead of a parse error.
* `$select`, `$count`, `$skiptoken`, OData `$batch` and `PATCH` on inventory changes are not
  implemented (unknown `$…` parameters are ignored, unknown verbs answer 405).
