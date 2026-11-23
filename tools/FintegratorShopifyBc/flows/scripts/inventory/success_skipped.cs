// Success card for the "false" branch: the change was understood but nothing could be pushed, and
// the reason is recorded instead of failing the transaction.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var change = flow["change"] as JObject;
if (change == null)
{
    change = flow["FanOutInventoryChanges"] as JObject;
}
var lookup = flow["LookupShopifyVariant"] as JObject ?? new JObject();

var itemNo = FinValues.ReadString(change?["itemNo"]);
var reason = FinValues.ReadString(lookup["reason"]);
if (string.IsNullOrWhiteSpace(reason))
{
    reason = "no Shopify variant could be resolved";
}

return "Skipped Business Central item " + itemNo + " (" + reason + ").";
