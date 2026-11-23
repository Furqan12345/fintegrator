// Builds the Shopify GraphQL request body. The watermark comes from the flow's persisted state, so
// every run continues where the previous run stopped.
//
// Two deliberate safety margins:
//  * ordersOverlapSeconds (default 900 = one cron interval) rewinds the watermark. Shopify's
//    updated_at sort is coarse, so an order updated in the same second as the previous batch's newest
//    order would otherwise be skipped silently. Re-reading that window is cheap: orders already
//    imported are dropped by the cross-reference filter and never reach Business Central, and a run
//    that failed earlier gets another chance while it is still inside the window.
//  * financial_status:paid keeps unpaid/voided orders out of Business Central (an order that is paid
//    later has its updatedAt moved by Shopify and is picked up by a following run). Set
//    ordersFinancialStatusFilter in the persisted state to "" to pull every financial status.
//  * lineItems asks for hasNextPage so the post-flight can refuse to import a truncated order.
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);

var overlapSeconds = 900;
var configuredOverlap = state["ordersOverlapSeconds"];
if (configuredOverlap != null)
{
    overlapSeconds = FinValues.ReadInt(configuredOverlap);
    if (overlapSeconds < 0)
    {
        overlapSeconds = 0;
    }
}

var watermark = FinValues.ReadString(state["ordersWatermark"]);
if (!string.IsNullOrWhiteSpace(watermark))
{
    var parsed = DateTime.MinValue;
    if (DateTime.TryParse(watermark, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out parsed))
    {
        watermark = FinValues.Iso(parsed.AddSeconds(-overlapSeconds));
    }
}
else
{
    watermark = FinValues.ReadString(state["ordersStartFromUtc"]);
    if (string.IsNullOrWhiteSpace(watermark))
    {
        watermark = FinValues.Iso(DateTime.UtcNow.AddDays(-7));
    }
}

var statusFilter = "financial_status:paid";
var configuredStatus = state["ordersFinancialStatusFilter"];
if (configuredStatus != null)
{
    statusFilter = FinValues.ReadString(configuredStatus);
}

var search = "updated_at:>'" + watermark + "'";
if (!string.IsNullOrWhiteSpace(statusFilter))
{
    search = search + " " + statusFilter;
}

var pageSize = FinValues.ReadInt(state["ordersPageSize"]);
if (pageSize < 1)
{
    pageSize = 25;
}

var query = "query FintegratorOrders($first: Int!, $after: String, $search: String!) { orders(first: $first, after: $after, query: $search, sortKey: UPDATED_AT) { pageInfo { hasNextPage endCursor } edges { node { id name updatedAt createdAt currencyCode displayFinancialStatus displayFulfillmentStatus email note tags currentTotalPriceSet { shopMoney { amount currencyCode } } shippingAddress { name address1 address2 city provinceCode countryCodeV2 zip phone } customer { id email firstName lastName } lineItems(first: 100) { pageInfo { hasNextPage } edges { node { id name sku quantity currentQuantity variant { id } originalUnitPriceSet { shopMoney { amount currencyCode } } } } } } } } }";

var variables = new JObject
{
    ["first"] = pageSize,
    ["after"] = null,
    ["search"] = search
};

return new JObject { ["query"] = query, ["variables"] = variables }.ToString(Newtonsoft.Json.Formatting.None);
