# Sample: Amazon SP-API Orders N+1 Sync + Nested Dedup

This sample imports as a single `IntegrationFlowDto` and demonstrates the two
capability families implemented in this project:

1. **ForEach N+1 fan-out** — `getOrders` returns order headers, then a `ForEach`
   node fans out so `getOrderItems` is called once per order.
2. **Cross-reference filter with nested arrays** — a filter node de-duplicates
   line items by SKU using a wildcard path (`orders[*].items[*]`) while
   preserving the original nested order/item structure.

## Node map (left → right)

| # | Node | Type | Purpose |
|---|------|------|---------|
| 1 | `NightlySync` | Schedule | Cron trigger (`0 6 * * *` daily). Start node — also marks the flow as cron-triggered. |
| 2 | `FetchOrders` | HTTP Action | `GET /orders/v2/orders`. Post-flight unwraps `payload.Orders` → `{ "orders": [...] }`. |
| 3 | `PerOrderItems` | **ForEach** | `arrayPath: "orders"`, `itemVariable: "order"`. Runs its subgraph once per order. |
| 4 | `FetchLineItems` | HTTP Action | UrlCode builds `/orders/v2/orders/{order.AmazonOrderId}/orderItems` per iteration. Post-flight unwraps `payload.OrderItems`. |
| 5 | `TagLineItems` | Mapping | Tags each line item with its `AmazonOrderId`; last subgraph node → becomes the per-order element of the combined array. |
| 6 | `RegroupItems` | Mapping | **Merge node** (predecessor `FetchOrders` keeps it outside the ForEach subgraph). Nests the combined per-order items into `{ "orders": [{ "AmazonOrderId", "items": [...] }] }`. |
| 7 | `FilterNewSKUs` | CrossReferenceFilter | `orders[*].items[*]`, key `sku`. Emits only items whose SKU is **not** already stored, preserving the parent order structure. |
| 8 | `RememberSeenSKUs` | CrossReferenceStore | Same nested path. Records newly-seen SKUs so the next run skips them. |
| 9 | `ProcessNewItems` | HTTP Action | `POST` the de-duplicated payload to a downstream system. |

## Data flow

```
NightlySync ──► FetchOrders ──► PerOrderItems (ForEach)
                                  │
                                  ├──► FetchLineItems ──► TagLineItems  ◄── each order, once per iteration
                                  │
                                  └──► RegroupItems  ◄── merge (also fed by FetchOrders, runs once)
                                              │
                                              ▼
                                   FilterNewSKUs (orders[*].items[*])  ◄── drop already-seen SKUs
                                              │
                                              ▼
                                   RememberSeenSKUs  ◄── record the new SKUs
                                              │
                                              ▼
                                   ProcessNewItems (POST new-only items)
```

## How to use

1. Replace the placeholder tokens:
   - `FetchOrders` → Bearer **SP-API access token**
   - `FetchLineItems` → Bearer **SP-API access token**
   - `ProcessNewItems` → **your backend token** and endpoint URL.
2. Import via the API (returns the created flow with generated IDs):
   ```bash
   curl -X POST http://localhost:5000/api/integrationflows \
     -H 'Content-Type: application/json' \
     -d @samples/integrations/sp-api-orders-nplus1.json
   ```
   Or open the Flow Designer at `/flow-designer`, paste the JSON into a new flow, and Save & Run.

## How a user works with this

- **First run:** `FilterNewSKUs` finds an empty `processed-skus` list, so every
  line item passes. `RememberSeenSKUs` records each SKU.
- **Next run:** `FilterNewSKUs` drops any SKU already in `processed-skus`, so only
  genuinely new line items reach `ProcessNewItems`. The parent `orders` array and
  per-order `items` arrays keep their shape — orders with no new items simply get
  an empty `items: []`.

## Notes on the SP-API specifics

- The Amazon Selling Partner API wraps responses in `payload` (`Orders`, `OrderItems`)
  and uses AWS Sig V4 signing. The two HTTP nodes include Post-flight scripts that
  unwrap the `payload` envelope so downstream nodes see clean `orders`/`items`.
- Real SP-API calls need AWS Sig V4. The Bearer-token slots here are placeholders;
  swap in a **Custom** auth / connection that emits the `Authorization` + `X-Amz-*` headers.
