// Normalises the open fulfillment-order lines into a flat list the next card can consume, and
// reports whether this Business Central shipment was already pushed to Shopify.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var graphQlError = FinValues.ShopifyGraphQlError(root);
if (!string.IsNullOrWhiteSpace(graphQlError))
{
    throw new InvalidOperationException(graphQlError);
}

var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}

var order = root["data"]?["order"] as JObject;
if (order == null)
{
    throw new InvalidOperationException("Shopify returned no order for the id stored on the Business Central shipment. The order may have been deleted in Shopify; discard the dead letter once you have checked it.");
}

var fulfillmentOrders = new JArray();
var openLineItems = new JArray();
var hasOpen = false;

foreach (var fulfillmentOrder in (order["fulfillmentOrders"]?["nodes"] as JArray) ?? new JArray())
{
    fulfillmentOrders.Add(fulfillmentOrder.DeepClone());

    var status = fulfillmentOrder["status"]?.ToString() ?? string.Empty;
    if (!status.Equals("OPEN", StringComparison.OrdinalIgnoreCase) && !status.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    foreach (var lineItem in (fulfillmentOrder["lineItems"]?["nodes"] as JArray) ?? new JArray())
    {
        var remaining = FinValues.ReadDecimal(lineItem["remainingQuantity"]);
        if (remaining <= 0m)
        {
            continue;
        }

        hasOpen = true;
        openLineItems.Add(new JObject
        {
            ["fulfillmentOrderId"] = fulfillmentOrder["id"]?.ToString() ?? string.Empty,
            ["fulfillmentOrderLineItemId"] = lineItem["id"]?.ToString() ?? string.Empty,
            ["sku"] = lineItem["lineItem"]?["sku"]?.ToString() ?? string.Empty,
            ["remainingQuantity"] = remaining
        });
    }
}

return new JObject
{
    ["orderId"] = order["id"]?.ToString() ?? string.Empty,
    ["orderName"] = order["name"]?.ToString() ?? string.Empty,
    ["orderStatus"] = order["displayFulfillmentStatus"]?.ToString() ?? string.Empty,
    ["fulfillmentOrders"] = fulfillmentOrders,
    ["openLineItems"] = openLineItems,
    ["hasOpenFulfillmentOrders"] = hasOpen,
    ["alreadyPushed"] = FinValues.ReadBool(shipment?["shopifyPushed"], false)
}.ToString(Newtonsoft.Json.Formatting.None);
