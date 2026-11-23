// Success card: a short, human readable summary on the child transaction.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var order = flow["order"] as JObject;
if (order == null)
{
    order = flow["FanOutOrders"] as JObject;
}
var result = flow["CreateBcSalesOrder"] as JObject ?? new JObject();

var orderName = FinValues.ReadString(order?["name"]);
var salesOrderNo = FinValues.ReadString(result["salesOrderNo"]);
var created = FinValues.ReadBool(result["created"], false);

if (created)
{
    return "Created Business Central sales order " + salesOrderNo + " from Shopify order " + orderName + ".";
}

return "Shopify order " + orderName + " was already imported as Business Central sales order " + salesOrderNo + ".";
