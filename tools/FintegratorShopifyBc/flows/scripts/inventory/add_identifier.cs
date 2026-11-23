// AddIdentifier card: "BC MUG-WHITE @ WH1 -> Shopify SKU MUG-WHITE".
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var change = flow["change"] as JObject;
if (change == null)
{
    change = flow["FanOutInventoryChanges"] as JObject;
}

var itemNo = FinValues.ReadString(change?["itemNo"]);
var locationCode = FinValues.ReadString(change?["locationCode"]);
var sku = FinValues.ReadString(change?["sku"]);

return "BC " + itemNo + " @ " + locationCode + " -> Shopify SKU " + sku;
