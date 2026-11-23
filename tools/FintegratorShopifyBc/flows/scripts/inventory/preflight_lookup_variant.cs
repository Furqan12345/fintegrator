// Resolves the Shopify variant belonging to a Business Central item's Shopify SKU, together
// with the location whose inventory we are allowed to write.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var change = flow["change"] as JObject;
if (change == null)
{
    change = flow["FanOutInventoryChanges"] as JObject;
}
if (change == null)
{
    throw new InvalidOperationException("The fan-out item is missing from flow state, so there is no inventory change to push.");
}

var sku = change["sku"]?.ToString();
if (string.IsNullOrWhiteSpace(sku))
{
    throw new InvalidOperationException("Business Central inventory change " + (change["entryNo"]?.ToString() ?? string.Empty) + " for item '" + (change["itemNo"]?.ToString() ?? string.Empty) + "' has no Shopify SKU. Add a FIN Shopify Item Mapping row for that item in Business Central.");
}

var search = "sku:" + sku;
if (sku.Contains(" "))
{
    search = "sku:\"" + sku + "\"";
}

var query = "query FintegratorInventoryLookup($search: String!) { productVariants(first: 5, query: $search) { nodes { id sku inventoryItem { id tracked } } } locations(first: 10) { nodes { id name isActive } } }";

return new JObject
{
    ["query"] = query,
    ["variables"] = new JObject { ["search"] = search }
}.ToString(Newtonsoft.Json.Formatting.None);
