// Surfaces Shopify's answer: transport errors already failed the step, GraphQL errors and
// userErrors have to be raised here so the transaction (and its dead letter) tells the story.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var graphQlError = FinValues.ShopifyGraphQlError(root);
if (!string.IsNullOrWhiteSpace(graphQlError))
{
    throw new InvalidOperationException(graphQlError);
}

var payload = root["data"]?["fulfillmentCreateV2"];
if (payload == null)
{
    throw new InvalidOperationException("Shopify returned no fulfillmentCreateV2 payload: " + (string.IsNullOrWhiteSpace(HttpResponseJson) ? "empty response" : HttpResponseJson));
}

var userErrors = payload["userErrors"] as JArray;
if (userErrors != null && userErrors.Count > 0)
{
    throw new InvalidOperationException("Shopify refused the fulfillment: " + (userErrors[0]?["message"]?.ToString() ?? "unknown error"));
}

var fulfillment = payload["fulfillment"];
if (fulfillment == null)
{
    throw new InvalidOperationException("Shopify created no fulfillment. The order may already be fulfilled in Shopify.");
}

var trackingNumber = string.Empty;
var trackingInfo = fulfillment["trackingInfo"] as JArray;
if (trackingInfo != null && trackingInfo.Count > 0)
{
    trackingNumber = trackingInfo[0]?["number"]?.ToString() ?? string.Empty;
}

return new JObject
{
    ["fulfillmentId"] = fulfillment["id"]?.ToString() ?? string.Empty,
    ["fulfillmentStatus"] = fulfillment["status"]?.ToString() ?? string.Empty,
    ["trackingNumber"] = trackingNumber
}.ToString(Newtonsoft.Json.Formatting.None);
