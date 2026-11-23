// Picks the variant and the location to write, and explains itself when it cannot.
//
// The variant must match the SKU exactly: Shopify allows duplicate SKUs and its search is not an
// exact-match guarantee, and writing stock to the wrong variant is worse than skipping the change.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);

var graphQlError = FinValues.ShopifyGraphQlError(root);
if (!string.IsNullOrWhiteSpace(graphQlError))
{
    throw new InvalidOperationException(graphQlError);
}

var change = flow["change"] as JObject;
if (change == null)
{
    change = flow["FanOutInventoryChanges"] as JObject;
}
var sku = change?["sku"]?.ToString() ?? string.Empty;
var bcLocationCode = change?["locationCode"]?.ToString() ?? string.Empty;

var variantId = string.Empty;
var inventoryItemId = string.Empty;
var tracked = false;
var nearMisses = new JArray();

foreach (var variant in (root["data"]?["productVariants"]?["nodes"] as JArray) ?? new JArray())
{
    var candidateSku = variant["sku"]?.ToString() ?? string.Empty;
    if (!candidateSku.Equals(sku, StringComparison.OrdinalIgnoreCase))
    {
        if (!string.IsNullOrWhiteSpace(candidateSku))
        {
            nearMisses.Add(new JValue(candidateSku));
        }
        continue;
    }

    variantId = variant["id"]?.ToString() ?? string.Empty;
    inventoryItemId = variant["inventoryItem"]?["id"]?.ToString() ?? string.Empty;
    tracked = FinValues.ReadBool(variant["inventoryItem"]?["tracked"], false);
    break;
}

// Location resolution order: an explicit map for this Business Central location, then a globally
// pinned location, then the first active Shopify location.
var locations = root["data"]?["locations"]?["nodes"] as JArray ?? new JArray();
var locationId = string.Empty;
var locationName = string.Empty;

var locationMap = state["shopifyLocationByBcLocation"] as JObject;
if (locationMap != null && !string.IsNullOrWhiteSpace(bcLocationCode))
{
    locationId = FinValues.ReadString(locationMap[bcLocationCode]);
}

if (string.IsNullOrWhiteSpace(locationId))
{
    locationId = state["shopifyLocationId"]?.ToString() ?? string.Empty;
}

foreach (var location in locations)
{
    if (!(location["id"]?.ToString() ?? string.Empty).Equals(locationId, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    locationName = location["name"]?.ToString() ?? string.Empty;
    break;
}

if (string.IsNullOrWhiteSpace(locationId))
{
    foreach (var location in locations)
    {
        if (!FinValues.ReadBool(location["isActive"], true))
        {
            continue;
        }

        locationId = location["id"]?.ToString() ?? string.Empty;
        locationName = location["name"]?.ToString() ?? string.Empty;
        break;
    }
}

var reason = string.Empty;
if (variantId.Length == 0)
{
    reason = "no Shopify variant matches SKU " + sku
        + (nearMisses.Count > 0 ? " (search returned " + nearMisses.ToString(Newtonsoft.Json.Formatting.None) + ")" : string.Empty);
}
else if (inventoryItemId.Length == 0)
{
    reason = "the Shopify variant for SKU " + sku + " has no inventory item";
}
else if (!tracked)
{
    reason = "Shopify does not track inventory for SKU " + sku;
}
else if (string.IsNullOrWhiteSpace(locationId))
{
    reason = "no active Shopify location could be resolved for Business Central location '" + bcLocationCode + "'";
}

return new JObject
{
    ["sku"] = sku,
    ["variantId"] = variantId,
    ["inventoryItemId"] = inventoryItemId,
    ["tracked"] = tracked,
    ["locationId"] = locationId,
    ["locationName"] = locationName,
    ["resolved"] = reason.Length == 0,
    ["reason"] = reason
}.ToString(Newtonsoft.Json.Formatting.None);
