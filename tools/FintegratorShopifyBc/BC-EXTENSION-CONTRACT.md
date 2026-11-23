# Business Central extension API contract (frozen)

This is the contract between the **BC AL extension** (`Fintegrator BC Connector`) and the three
fintegrator.io flows (order create, order shipment, inventory update).

Both sides must match this document exactly. Change it here first, then on both sides.

## Base URL shape

```
{bcBaseUrl}/api/fintegrator/integration/v1.0/companies({companyId})/{entitySetName}
```

| piece | value |
|---|---|
| APIPublisher | `fintegrator` |
| APIGroup | `integration` |
| APIVersion | `v1.0` |
| Tenant/instance part | environment specific — lives in the fintegrator **connection** `baseUrl` |

So the fintegrator connection `baseUrl` is
`https://api.businesscentral.dynamics.com/v2.0/{tenantId}/{environment}/api/fintegrator/integration/v1.0`
(SaaS) or `http://{server}:{port}/{instance}/api/fintegrator/integration/v1.0` (on-prem / Docker)
and each flow node appends `companies({companyId})/{entitySet}`.

`companyId` is **not** hardcoded in any flow: it lives in the flow's persisted state under
`bcCompanyId` (editable from the designer's **State** modal). A missing value produces an
actionable failure message naming the key.

## Entity 1 — `fintegratorSalesOrders` (POST deep insert, GET)

Creates a BC sales order from a Shopify order. Staging table `FIN Sales Order Import`
(+ lines `FIN Sales Order Import Line`), so every inbound order is auditable in BC.

### Request (POST)

```json
{
  "shopifyOrderId": "gid://shopify/Order/5123456789",
  "shopifyOrderName": "#1001",
  "customerNo": "",
  "orderDate": "2026-10-15",
  "currencyCode": "USD",
  "note": "Shopify order #1001",
  "shippingName": "Jane Doe",
  "shippingAddress1": "1 Main Street",
  "shippingAddress2": "Apt 4",
  "shippingCity": "Springfield",
  "shippingPostCode": "12345",
  "shippingCountryCode": "US",
  "shippingPhone": "+15550100",
  "totalAmount": 129.90,
  "fintegratorSalesOrderLines": [
    { "lineNo": 10000, "sku": "TSHIRT-BLK-M", "description": "T-Shirt Black M", "quantity": 2, "unitPrice": 29.95 }
  ]
}
```

`customerNo` blank ⇒ the extension uses `FIN Integrator Setup."Default Customer No."`.

### Response (201)

The staging record, including the outcome fields the extension filled in:

```json
{
  "id": "0f1c...",                                  // SystemId of the staging record
  "shopifyOrderId": "gid://shopify/Order/5123456789",
  "shopifyOrderName": "#1001",
  "status": "Created",                              // Created | Duplicate | Failed
  "salesOrderNo": "101234",
  "errorMessage": "",
  "createdSalesOrder": true,
  ...
}
```

Semantics that the flows rely on:

* **Both outcome fields are always present.** `status` is `Created` (new BC order),
  `Duplicate` (a BC order with the same `shopifyOrderId` already existed — no second order is
  created) or `Failed` (creation rejected; `errorMessage` explains why).
* The whole request is one BC transaction: a line that cannot be created (unknown SKU) fails
  the POST with a BC error message naming the SKU, and no partial order is left behind.
* Idempotent: repeating the same POST never creates a second BC sales order.
* Line numbers: the extension assigns `lineNo` (10000/20000/...) when the caller omits it.

The order-create flow additionally filters orders it has already recorded in its
`shopify-orders-imported` cross-reference ledger, so a repeat POST is the *second* line of defence:
the ledger stops the call, this de-duplication guarantees correctness if the call happens anyway.

### Required AL mechanics (do not deviate)

The request is a **deep insert** (header + `fintegratorSalesOrderLines` part). Per Microsoft's
documented behaviour (and confirmed field experience) the **lines do not exist yet when the
header's insert trigger runs**, so:

1. header API page `OnInsertRecord` → resolve/create the BC **Sales Header** (idempotency
   lookup, number series, ship-to, `FIN Shopify Order Id`), store `Sales Order No.` + `status`
   on the staging record.
2. lines part page `OnInsertRecord` → resolve the header staging record through the SystemId
   `SubPageLink`, then create the BC **Sales Line** (item resolution by SKU, price, quantity).
   `Error(...)` on an unresolvable SKU so the POST fails with an actionable message.

## Entity 2 — `fintegratorShippedOrders` (GET, read-only + optional PATCH of `shopifyPushed`)

Posted sales shipments feed (table `FIN Shipment Feed`, written by an event subscriber on
`Sales Shipment Header` insert).

| JSON field | type | notes |
|---|---|---|
| `entryNo` | integer | PK, auto-increment — **the flow's cursor** |
| `lastModifiedDateTime` | datetime (ISO, UTC) | **own field**, stamped on insert, never rewritten; kept in the flow's state for humans |
| `salesShipmentNo` | string | BC posted shipment no |
| `salesOrderNo` | string | BC sales order no |
| `shopifyOrderId` | string | Shopify GID (copied from the sales order) |
| `shopifyOrderName` | string | `#1001` |
| `customerNo` | string | |
| `shipmentDate` | date | |
| `shippingAgentCode` / `shippingAgentName` | string | carrier |
| `trackingNo` | string | parcel tracking number |
| `trackingUrl` | string | **always empty from this extension** (no documented carrier URL rule); the flow only sends it when set |
| `shippedQuantity` | decimal | total shipped on this shipment |
| `fullyShipped` | boolean | true when nothing remains unshipped on the order |
| `shopifyPushed` | boolean | set to true by the shipment flow (PATCH) once Shopify accepted the fulfillment — this is what makes a replay safe |
| `fintegratorShippedOrderLines` | array (via `$expand`) | `lineNo`, `itemNo`, `sku`, `quantity` |

Query used by the flow:

```
?$orderby=entryNo asc&$top=200&$expand=fintegratorShippedOrderLines[&$filter=entryNo gt N]
```

`entryNo` (not the timestamp) is the cursor on purpose: an auto-increment integer cannot drift in
precision or format the way an ISO timestamp can — a truncated fraction of a second would make the
same rows eligible on every run.

## Entity 3 — `fintegratorInventoryChanges` (GET, read-only)

Inventory movement feed (table `FIN Inventory Change Feed`, written by an event subscriber on
`Item Ledger Entry` insert) — **only items that have a `FIN Shopify Item Mapping` row**.

| JSON field | type | notes |
|---|---|---|
| `entryNo` | integer | PK, auto-increment — **the flow's cursor** |
| `lastModifiedDateTime` | datetime (ISO, UTC) | own field, stamped on insert |
| `itemNo` | string | |
| `variantCode` | string | |
| `locationCode` | string | |
| `sku` | string | Shopify SKU from the mapping table (falls back to `itemNo`) |
| `inventory` | decimal | inventory **after** the change |
| `changeQuantity` | decimal | the ledger movement that caused it |

Query used by the flow:

```
?$orderby=entryNo asc&$top=500[&$filter=entryNo gt N]
```

## Setup + supporting objects

* `FIN Integrator Setup` (singleton): `Default Customer No.`, `Default Location Code`,
  `Default Shipping Agent Code`, `Send Shipment Feed` (bool), `Send Inventory Feed` (bool),
  `Inventory Safety Stock` (decimal, subtracted from pushed inventory).
* `FIN Shopify Item Mapping`: `Item No.` (PK), `Shopify SKU`, `Shopify Variant Id`,
  `Shopify Inventory Item Id`, `Send Inventory` (bool).
* `Sales Header` tableextension: `FIN Shopify Order Id`, `FIN Shopify Order Name`.
* `Sales Line` tableextension: `FIN Shopify SKU`.
* Human-facing list pages for the two feeds + the staging table, a Setup page and a
  permission set.

## What the extension deliberately does NOT do

* It never calls Shopify (fintegrator.io owns that direction).
* It never posts the sales order — orders stay open for review/shipment in BC.
* Feeds are append-only; the integration owns the cursor, BC never deletes rows.
* It does not derive a carrier tracking URL: `trackingUrl` stays empty and the flow only forwards it
  when something else fills it (a user, an extension, or a future enhancement).
