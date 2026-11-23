// Collapses the inventory feed to one row per SKU + location before the fan-out.
//
// Why: the fan-out queues one durable child transaction per row, and the engine runs children
// concurrently, so their completion order is not the feed order. Because inventorySetQuantities
// writes an ABSOLUTE quantity, a reordered pair of movements for the same SKU would leave Shopify
// holding the older value. Keeping only the newest row per SKU + location (the feed is ordered by
// entryNo ascending) makes "latest wins" true by construction.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var fetched = flow["FetchBcInventoryChanges"] as JObject ?? new JObject();
var changes = fetched["changes"] as JArray ?? new JArray();

var latestByKey = new JObject();
var keys = new JArray();
foreach (var row in changes)
{
    var sku = FinValues.ReadString(row["sku"]);
    var locationCode = FinValues.ReadString(row["locationCode"]);
    var key = sku + "|" + locationCode;

    if (latestByKey[key] == null)
    {
        keys.Add(key);
    }

    // Rows arrive ordered by entryNo ascending, so the last write per key is the newest movement.
    latestByKey[key] = row.DeepClone();
}

var collapsed = new JArray();
foreach (var key in keys)
{
    collapsed.Add(latestByKey[key.ToString()]);
}

return new JObject
{
    ["changes"] = collapsed,
    ["count"] = collapsed.Count,
    ["sourceCount"] = changes.Count
}.ToString(Newtonsoft.Json.Formatting.None);
