// Success card for the "true" branch: the Shopify inventory level now matches Business Central.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var change = flow["change"] as JObject;
if (change == null)
{
    change = flow["FanOutInventoryChanges"] as JObject;
}
var lookup = flow["LookupShopifyVariant"] as JObject ?? new JObject();

var itemNo = FinValues.ReadString(change?["itemNo"]);
var locationCode = FinValues.ReadString(change?["locationCode"]);
var sku = FinValues.ReadString(lookup["sku"]);
if (string.IsNullOrWhiteSpace(sku))
{
    sku = FinValues.ReadString(change?["sku"]);
}
var locationName = FinValues.ReadString(lookup["locationName"]);
var quantity = FinValues.ReadString(change?["inventory"]);

var locationText = string.IsNullOrWhiteSpace(locationName) ? string.Empty : " (" + locationName + ")";

return "Shopify inventory for SKU " + sku + " set to " + quantity + locationText + " from Business Central item " + itemNo + " at " + locationCode + ".";
