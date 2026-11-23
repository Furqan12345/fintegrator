// Branch card: only push inventory when the variant, its inventory item, the tracking flag and a
// location all resolved.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var lookup = flow["LookupShopifyVariant"] as JObject ?? new JObject();

return FinValues.ReadBool(lookup["resolved"], false);
