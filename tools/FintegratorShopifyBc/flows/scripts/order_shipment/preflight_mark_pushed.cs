// PATCH body that marks the Business Central shipment feed row as delivered to Shopify.
// lastModifiedDateTime is not touched, so the flow's cursor is unaffected.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}
var result = flow["CreateShopifyFulfillment"] as JObject ?? new JObject();

return new JObject
{
    ["shopifyPushed"] = true,
    ["note"] = "Pushed to Shopify as fulfillment " + (result["fulfillmentId"]?.ToString() ?? string.Empty)
}.ToString(Newtonsoft.Json.Formatting.None);
