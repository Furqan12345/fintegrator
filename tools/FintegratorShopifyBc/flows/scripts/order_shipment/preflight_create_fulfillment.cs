// Builds the fulfillmentCreateV2 mutation. Business Central's shipped SKUs and quantities are
// matched against Shopify's open fulfillment-order lines, and quantities are consumed as they are
// allocated so a second fulfillment order can never be over-shipped.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);

var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}
if (shipment == null)
{
    throw new InvalidOperationException("The fan-out item is missing from flow state, so there is no shipment to fulfil.");
}

var lookup = flow["FetchShopifyFulfillmentOrders"] as JObject ?? new JObject();

var shippedBySku = new JObject();
foreach (var line in (shipment["fintegratorShippedOrderLines"] as JArray) ?? new JArray())
{
    var sku = line["sku"]?.ToString();
    if (string.IsNullOrWhiteSpace(sku))
    {
        continue;
    }

    var already = FinValues.ReadDecimal(shippedBySku[sku]);
    shippedBySku[sku] = already + FinValues.ReadDecimal(line["quantity"]);
}

var allocated = new JObject();
foreach (var item in (lookup["openLineItems"] as JArray) ?? new JArray())
{
    var sku = item["sku"]?.ToString() ?? string.Empty;
    var fulfillmentOrderId = item["fulfillmentOrderId"]?.ToString() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(fulfillmentOrderId))
    {
        continue;
    }

    var available = shippedBySku[sku];
    if (available == null)
    {
        continue;
    }

    var shipped = FinValues.ReadDecimal(available);
    var remaining = FinValues.ReadDecimal(item["remainingQuantity"]);
    var quantity = shipped < remaining ? shipped : remaining;
    if (quantity <= 0m)
    {
        continue;
    }

    var lines = allocated[fulfillmentOrderId] as JArray;
    if (lines == null)
    {
        lines = new JArray();
        allocated[fulfillmentOrderId] = lines;
    }

    lines.Add(new JObject
    {
        ["id"] = item["fulfillmentOrderLineItemId"]?.ToString() ?? string.Empty,
        ["quantity"] = quantity
    });

    shippedBySku[sku] = shipped - quantity;
}

var lineItemsByFulfillmentOrder = new JArray();
foreach (var property in allocated.Properties())
{
    lineItemsByFulfillmentOrder.Add(new JObject
    {
        ["fulfillmentOrderId"] = property.Name,
        ["fulfillmentOrderLineItems"] = property.Value
    });
}

if (lineItemsByFulfillmentOrder.Count == 0)
{
    throw new InvalidOperationException("Business Central shipment " + (shipment["salesShipmentNo"]?.ToString() ?? string.Empty) + " contains no SKU that matches an open Shopify fulfillment order line, so there is nothing to mark as shipped.");
}

var trackingInfo = new JObject();
var trackingNumber = shipment["trackingNo"]?.ToString();
if (!string.IsNullOrWhiteSpace(trackingNumber))
{
    trackingInfo["number"] = trackingNumber;
}

var carrier = shipment["shippingAgentName"]?.ToString();
if (string.IsNullOrWhiteSpace(carrier))
{
    carrier = shipment["shippingAgentCode"]?.ToString();
}
if (!string.IsNullOrWhiteSpace(carrier))
{
    trackingInfo["company"] = carrier;
}

var trackingUrl = shipment["trackingUrl"]?.ToString();
if (!string.IsNullOrWhiteSpace(trackingUrl))
{
    trackingInfo["url"] = trackingUrl;
}

var notifyCustomer = FinValues.ReadBool(state["notifyCustomerOnFulfillment"], false);

var fulfillment = new JObject
{
    ["lineItemsByFulfillmentOrder"] = lineItemsByFulfillmentOrder,
    ["notifyCustomer"] = notifyCustomer
};
if (trackingInfo.Count > 0)
{
    fulfillment["trackingInfo"] = trackingInfo;
}

var mutation = "mutation FintegratorCreateFulfillment($fulfillment: FulfillmentV2Input!) { fulfillmentCreateV2(fulfillment: $fulfillment) { fulfillment { id status trackingInfo { number company url } } userErrors { field message } } }";

return new JObject
{
    ["query"] = mutation,
    ["variables"] = new JObject { ["fulfillment"] = fulfillment }
}.ToString(Newtonsoft.Json.Formatting.None);
