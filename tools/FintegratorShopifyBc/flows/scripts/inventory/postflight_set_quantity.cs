// Shopify's answer: GraphQL errors and userErrors fail the step, a clean answer is summarised.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var graphQlError = FinValues.ShopifyGraphQlError(root);
if (!string.IsNullOrWhiteSpace(graphQlError))
{
    throw new InvalidOperationException(graphQlError);
}

var payload = root["data"]?["inventorySetQuantities"];
if (payload == null)
{
    throw new InvalidOperationException("Shopify returned no inventorySetQuantities payload: " + (string.IsNullOrWhiteSpace(HttpResponseJson) ? "empty response" : HttpResponseJson));
}

var userErrors = payload["userErrors"] as JArray;
if (userErrors != null && userErrors.Count > 0)
{
    throw new InvalidOperationException("Shopify refused the inventory update: " + (userErrors[0]?["message"]?.ToString() ?? "unknown error"));
}

var delta = 0m;
var changes = payload["inventoryAdjustmentGroup"]?["changes"] as JArray;
if (changes != null)
{
    foreach (var change in changes)
    {
        if ((change["name"]?.ToString() ?? string.Empty).Equals("available", StringComparison.OrdinalIgnoreCase))
        {
            delta = FinValues.ReadDecimal(change["delta"]);
            break;
        }
    }
}

return new JObject
{
    ["availableDelta"] = delta,
    ["applied"] = true
}.ToString(Newtonsoft.Json.Formatting.None);
