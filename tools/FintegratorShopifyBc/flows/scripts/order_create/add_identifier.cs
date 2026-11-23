// AddIdentifier card: "Shopify #1001 -> BC 101234" shows up in the transaction list.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var order = flow["order"] as JObject;
if (order == null)
{
    order = flow["FanOutOrders"] as JObject;
}
var result = flow["CreateBcSalesOrder"] as JObject ?? new JObject();

var orderName = FinValues.ReadString(order?["name"]);
var salesOrderNo = FinValues.ReadString(result["salesOrderNo"]);

return "Shopify " + orderName + " -> BC " + salesOrderNo;
