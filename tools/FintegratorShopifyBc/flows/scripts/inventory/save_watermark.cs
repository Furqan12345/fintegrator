// PersistedState card: advances the inventory watermark only, preserving all other keys.
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var fetched = flow["FetchBcInventoryChanges"] as JObject ?? new JObject();

var lastModified = FinValues.ReadString(fetched["lastModifiedDateTime"]);
if (!string.IsNullOrWhiteSpace(lastModified))
{
    state["inventoryWatermark"] = lastModified;
    state["inventoryEntryNo"] = FinValues.ReadLong(fetched["lastEntryNo"]);
}

state["inventoryLastCount"] = FinValues.ReadInt(fetched["count"]);
state["inventoryLastRunAtUtc"] = FinValues.Iso(DateTime.UtcNow);

return state.ToString(Newtonsoft.Json.Formatting.None);
