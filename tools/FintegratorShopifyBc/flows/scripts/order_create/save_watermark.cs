// PersistedState card: merges the new watermark into the durable flow state. Every other key
// (bcCompanyId, configuration toggles, the other flows' watermarks) is preserved.
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var fetched = flow["FetchShopifyOrders"] as JObject ?? new JObject();

var lastUpdatedAt = FinValues.ReadString(fetched["lastUpdatedAt"]);
if (!string.IsNullOrWhiteSpace(lastUpdatedAt))
{
    state["ordersWatermark"] = lastUpdatedAt;
}

state["ordersLastCount"] = FinValues.ReadInt(fetched["count"]);
state["ordersLastRunAtUtc"] = FinValues.Iso(DateTime.UtcNow);

return state.ToString(Newtonsoft.Json.Formatting.None);
