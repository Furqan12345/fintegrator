// Flattens the (possibly multi-page) GraphQL answer into one orders array and reports the highest
// updatedAt seen, which becomes the next run's watermark.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var graphQlError = FinValues.ShopifyGraphQlError(root);
if (!string.IsNullOrWhiteSpace(graphQlError))
{
    throw new InvalidOperationException(graphQlError);
}

var orders = new JArray();
var highest = string.Empty;
foreach (var edge in (root["data"]?["orders"]?["edges"] as JArray) ?? new JArray())
{
    var node = edge?["node"];
    if (node == null)
    {
        continue;
    }

    if (FinValues.ReadBool(node["lineItems"]?["pageInfo"]?["hasNextPage"], false))
    {
        throw new InvalidOperationException("Shopify order " + (node["name"]?.ToString() ?? string.Empty) + " has more than 100 line items, so importing it would silently drop lines. Raise the line-items page size in the flow script before importing it.");
    }

    orders.Add(node.DeepClone());

    var updatedAt = FinValues.ReadString(node["updatedAt"]);
    if (!string.IsNullOrWhiteSpace(updatedAt) && string.CompareOrdinal(updatedAt, highest) > 0)
    {
        highest = updatedAt;
    }
}

var output = new JObject
{
    ["orders"] = orders,
    ["count"] = orders.Count
};
if (!string.IsNullOrWhiteSpace(highest))
{
    output["lastUpdatedAt"] = highest;
}

return output.ToString(Newtonsoft.Json.Formatting.None);
