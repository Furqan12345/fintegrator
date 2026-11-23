// Branch card: push only when Shopify still has open fulfillment orders AND the Business Central
// feed row does not already carry the "pushed" flag (which is what keeps a replay from creating a
// second fulfillment for a shipment that was already sent).
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var lookup = flow["FetchShopifyFulfillmentOrders"] as JObject ?? new JObject();

var hasOpen = FinValues.ReadBool(lookup["hasOpenFulfillmentOrders"], false);
var alreadyPushed = FinValues.ReadBool(lookup["alreadyPushed"], false);

return hasOpen && !alreadyPushed;
