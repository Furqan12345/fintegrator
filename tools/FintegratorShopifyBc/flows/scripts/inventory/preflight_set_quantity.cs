// Builds the inventorySetQuantities mutation. Business Central is the source of truth, so the
// absolute quantity is pushed with ignoreCompareQuantity. The idempotency key is derived from the
// feed row itself: replaying the same row cannot be applied twice, and two different rows never
// share a key. The @idempotent directive only exists from Shopify 2026-01 onwards, so it is added
// only when the configured API version supports it.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);

var change = flow["change"] as JObject;
if (change == null)
{
    change = flow["FanOutInventoryChanges"] as JObject;
}
if (change == null)
{
    throw new InvalidOperationException("The fan-out item is missing from flow state, so there is no inventory change to push.");
}

var lookup = flow["LookupShopifyVariant"] as JObject ?? new JObject();
var inventoryItemId = lookup["inventoryItemId"]?.ToString() ?? string.Empty;
var locationId = lookup["locationId"]?.ToString() ?? string.Empty;
var sku = lookup["sku"]?.ToString() ?? string.Empty;

if (string.IsNullOrWhiteSpace(inventoryItemId) || string.IsNullOrWhiteSpace(locationId))
{
    throw new InvalidOperationException("The Shopify inventory item or location for SKU " + sku + " could not be resolved, so the quantity cannot be set.");
}

var quantity = FinValues.ReadDecimal(change["inventory"]);
if (quantity < 0m)
{
    quantity = 0m;
}

var entryNo = FinValues.ReadString(change["entryNo"]);
var itemNo = change["itemNo"]?.ToString() ?? string.Empty;
var invariantQuantity = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
var seed = "fintegrator:" + entryNo + ":" + itemNo + ":" + sku + ":" + invariantQuantity + ":" + locationId;

var hashA = 14695981039346656037UL;
foreach (var character in seed)
{
    hashA = hashA ^ (byte)character;
    hashA = hashA * 1099511628211UL;
}

var hashB = 14695981039346656037UL ^ 0x9E3779B97F4A7C15UL;
foreach (var character in seed)
{
    hashB = hashB ^ (byte)character;
    hashB = hashB * 1099511628211UL;
}

var hexA = hashA.ToString("x16");
var hexB = hashB.ToString("x16");
var idempotencyKey = hexA.Substring(0, 8) + "-" + hexA.Substring(8, 4) + "-4" + hexB.Substring(0, 3) + "-a" + hexB.Substring(4, 3) + "-" + hexB.Substring(8, 8) + hexA.Substring(0, 4);

var version = state["shopifyApiVersion"]?.ToString();
if (string.IsNullOrWhiteSpace(version))
{
    version = "2026-07";
}
var supportsIdempotency = string.CompareOrdinal(version, "2026-01") >= 0;

var input = new JObject
{
    ["name"] = "available",
    ["reason"] = "correction",
    ["referenceDocumentUri"] = "fintegrator://business-central/item/" + itemNo + "/entry/" + entryNo,
    ["ignoreCompareQuantity"] = true,
    ["quantities"] = new JArray
    {
        new JObject
        {
            ["inventoryItemId"] = inventoryItemId,
            ["locationId"] = locationId,
            ["quantity"] = quantity
        }
    }
};

var selection = "inventoryAdjustmentGroup { reason changes { name delta } } userErrors { code field message }";
var mutation = supportsIdempotency
    ? "mutation FintegratorSetInventory($input: InventorySetQuantitiesInput!, $idempotencyKey: String!) { inventorySetQuantities(input: $input) @idempotent(key: $idempotencyKey) { " + selection + " } }"
    : "mutation FintegratorSetInventory($input: InventorySetQuantitiesInput!) { inventorySetQuantities(input: $input) { " + selection + " } }";

var variables = new JObject { ["input"] = input };
if (supportsIdempotency)
{
    variables["idempotencyKey"] = idempotencyKey;
}

return new JObject
{
    ["query"] = mutation,
    ["variables"] = variables
}.ToString(Newtonsoft.Json.Formatting.None);
