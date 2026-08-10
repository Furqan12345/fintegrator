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
| 2 | `FetchOrders` | HTTP Action | Connection-based `GET /orders/v0/orders`. Post-flight unwraps `payload.Orders` → `{ "orders": [...] }`. |
| 3 | `PerOrderItems` | **ForEach** | `arrayPath: "flowState.FetchOrders.orders"`, `itemVariable: "order"`. Runs its subgraph once per order. |
| 4 | `FetchLineItems` | HTTP Action | Connection-based `GET /orders/v0/orders/{order.AmazonOrderId}/orderItems` per iteration. Post-flight unwraps `payload.OrderItems`. |
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

1. Create a connection at `/connection-wizard` with:
   - Base URL: `https://sellingpartnerapi-na.amazon.com`
   - Auth type: **Amazon SP-API (LWA + AWS SigV4)**
   - LWA client ID, client secret, refresh token, and token URL
   - AWS access key ID, secret access key, optional session token
   - Region (normally `us-east-1`) and service (`execute-api`)
   - Optional STS role ARN, role session name, external ID, STS region/endpoint, and duration. When a role ARN is configured, the base IAM credentials are used only to call STS; temporary role credentials are kept in memory.
2. Import the sample through the Flow Designer or API.
3. Open the imported flow and select the Amazon connection on both `FetchOrders` and `FetchLineItems`; the sample intentionally leaves `connectionId` empty because each tenant has a different connection ID.
4. Configure `ProcessNewItems` with your own downstream connection/endpoint. The sample placeholder endpoint is not runnable as-is.
5. Set the flow to **Active**, then run it manually first. The cron schedule is `0 6 * * *` UTC.

The connection stores credentials encrypted; API reads redact them. Do not put credentials in the sample JSON or commit them.

## How a user works with this

- **First run:** `FilterNewSKUs` finds an empty `processed-skus` list, so every
  line item passes. `RememberSeenSKUs` records each SKU.
- **Next run:** `FilterNewSKUs` drops any SKU already in `processed-skus`, so only
  genuinely new line items reach `ProcessNewItems`. The parent `orders` array and
  per-order `items` arrays keep their shape — orders with no new items simply get
  an empty `items: []`.

## Notes on the SP-API specifics

- The Amazon Selling Partner API wraps responses in `payload` (`Orders`, `OrderItems`) and uses AWS Sig V4 signing. The two HTTP nodes include post-flight scripts that unwrap the envelope so downstream nodes see clean `orders`/`items`.
- This implementation supports LWA refresh-token exchange, optional STS role assumption, direct IAM/temporary-credential SigV4 signing, and Amazon `NextToken` pagination. `FetchOrders` follows `payload.NextToken` for at most 100 pages while preserving the configured marketplace query parameters.
- Restricted Data Token (RDT) support is opt-in per HTTP node through `StepConfig.amazonSpApi.restrictedData`, `restrictedDataElements`, and exact `restrictedResources`. RDTs are short-lived, scoped to the resource set, cached only in memory, and never persisted.
- Amazon marketplace permissions, seller authorization, endpoint region, IAM policy, and STS trust policy must be valid in your Amazon account.
