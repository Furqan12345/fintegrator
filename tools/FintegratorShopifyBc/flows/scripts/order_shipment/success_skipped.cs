// Success card for the "false" branch: Shopify has nothing open left, so the shipment needs no push.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}
var lookup = flow["FetchShopifyFulfillmentOrders"] as JObject ?? new JObject();

var shipmentNo = FinValues.ReadString(shipment?["salesShipmentNo"]);
var orderName = FinValues.ReadString(lookup["orderName"]);
if (string.IsNullOrWhiteSpace(orderName))
{
    orderName = FinValues.ReadString(shipment?["shopifyOrderName"]);
}
var status = FinValues.ReadString(lookup["orderStatus"]);

return "No open fulfillment order left on Shopify order " + orderName + (string.IsNullOrWhiteSpace(status) ? string.Empty : " (status " + status + ")") + "; Business Central shipment " + shipmentNo + " needs no push.";
