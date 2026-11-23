// Endpoint for flagging a Business Central shipment feed row as pushed to Shopify.
// The API entity is keyed on SystemId, which the feed row exposes as "id".
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);

var shipment = flow["shipment"] as JObject;
if (shipment == null)
{
    shipment = flow["FanOutShipments"] as JObject;
}

var companyId = state["bcCompanyId"]?.ToString();
if (string.IsNullOrWhiteSpace(companyId))
{
    throw new InvalidOperationException("Persisted state key 'bcCompanyId' is not set. Open this flow, click State and set it to the Business Central company GUID.");
}

var shipmentId = shipment?["id"]?.ToString();
if (string.IsNullOrWhiteSpace(shipmentId))
{
    throw new InvalidOperationException("Business Central shipment " + (shipment?["salesShipmentNo"]?.ToString() ?? string.Empty) + " has no feed row id, so it cannot be flagged as pushed. Re-read the shipment feed so the row carries its id.");
}

return "companies(" + companyId + ")/fintegratorShippedOrders(" + shipmentId + ")";
