// AddIdentifier card: "BC shipment 102345 -> Shopify #1001".
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}

var shipmentNo = FinValues.ReadString(shipment?["salesShipmentNo"]);
var orderName = FinValues.ReadString(shipment?["shopifyOrderName"]);
if (string.IsNullOrWhiteSpace(orderName))
{
    orderName = FinValues.ReadString(shipment?["salesOrderNo"]);
}

return "BC shipment " + shipmentNo + " -> Shopify " + orderName;
