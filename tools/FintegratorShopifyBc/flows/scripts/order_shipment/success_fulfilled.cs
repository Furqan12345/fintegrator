// Success card for the "true" branch: the shipment was marked as shipped in Shopify.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}
var result = flow["CreateShopifyFulfillment"] as JObject ?? new JObject();

var shipmentNo = FinValues.ReadString(shipment?["salesShipmentNo"]);
var orderName = FinValues.ReadString(shipment?["shopifyOrderName"]);
var tracking = FinValues.ReadString(result["trackingNumber"]);
var trackingText = string.IsNullOrWhiteSpace(tracking) ? string.Empty : " with tracking " + tracking;

return "Marked Shopify order " + orderName + " as shipped from Business Central shipment " + shipmentNo + trackingText + ".";
