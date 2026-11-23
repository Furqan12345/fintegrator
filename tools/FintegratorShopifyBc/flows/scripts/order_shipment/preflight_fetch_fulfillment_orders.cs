// Asks Shopify which fulfillment orders are still open for the Shopify order that Business
// Central shipped, so the fulfillment can be created against the right line items.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}
if (shipment == null)
{
    throw new InvalidOperationException("The fan-out item is missing from flow state, so there is no shipment to push.");
}

var orderId = shipment["shopifyOrderId"]?.ToString();
if (string.IsNullOrWhiteSpace(orderId))
{
    throw new InvalidOperationException("Business Central shipment " + (shipment["salesShipmentNo"]?.ToString() ?? string.Empty) + " has no Shopify order id. Import the order through the order-create flow so Business Central keeps the Shopify order id.");
}

var query = "query FintegratorFulfillmentOrders($id: ID!) { order(id: $id) { id name displayFulfillmentStatus fulfillmentOrders(first: 10) { nodes { id status requestStatus lineItems(first: 100) { nodes { id remainingQuantity lineItem { id sku } } } } } } }";

return new JObject
{
    ["query"] = query,
    ["variables"] = new JObject { ["id"] = orderId }
}.ToString(Newtonsoft.Json.Formatting.None);
