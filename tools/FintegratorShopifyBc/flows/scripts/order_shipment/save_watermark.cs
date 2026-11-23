// PersistedState card: advances the shipment watermark and leaves every other state key alone.
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var fetched = flow["FetchBcShipments"] as JObject ?? new JObject();

var lastModified = FinValues.ReadString(fetched["lastModifiedDateTime"]);
if (!string.IsNullOrWhiteSpace(lastModified))
{
    state["shipmentsWatermark"] = lastModified;
    state["shipmentsEntryNo"] = FinValues.ReadLong(fetched["lastEntryNo"]);
}

state["shipmentsLastCount"] = FinValues.ReadInt(fetched["count"]);
state["shipmentsLastRunAtUtc"] = FinValues.Iso(DateTime.UtcNow);

return state.ToString(Newtonsoft.Json.Formatting.None);
