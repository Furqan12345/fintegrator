// Combines the Shopify order with the Business Central result so the transaction's
// cross-reference ledger records what was imported and where it landed.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var order = flow["order"] as JObject;
if (order == null)
{
    order = flow["FanOutOrders"] as JObject;
}
var result = flow["CreateBcSalesOrder"] as JObject ?? new JObject();

return new JObject
{
    ["id"] = FinValues.ReadString(order?["id"]),
    ["name"] = FinValues.ReadString(order?["name"]),
    ["updatedAt"] = FinValues.ReadString(order?["updatedAt"]),
    ["bcSalesOrderNo"] = FinValues.ReadString(result["salesOrderNo"]),
    ["bcStatus"] = FinValues.ReadString(result["status"])
}.ToString(Newtonsoft.Json.Formatting.None);
