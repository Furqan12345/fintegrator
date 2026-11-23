// Maps one Shopify order (the fan-out item) onto the Business Central extension's sales order
// contract: header fields plus the fintegratorSalesOrderLines collection.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var order = flow["order"] as JObject;
if (order == null)
{
    order = flow["FanOutOrders"] as JObject;
}
if (order == null)
{
    throw new InvalidOperationException("The fan-out item is missing from flow state, so there is no Shopify order to send.");
}

var orderName = order["name"]?.ToString() ?? string.Empty;
if (string.IsNullOrWhiteSpace(order["id"]?.ToString()))
{
    throw new InvalidOperationException("Shopify order " + orderName + " has no id, so Business Central cannot de-duplicate it.");
}

var address = order["shippingAddress"] as JObject ?? new JObject();

var orderDate = FinValues.Date(DateTime.UtcNow);
var createdAt = FinValues.ReadString(order["createdAt"]);
if (createdAt.Length >= 10)
{
    orderDate = createdAt.Substring(0, 10);
}

var lines = new JArray();
var lineNo = 10000;
foreach (var edge in (order["lineItems"]?["edges"] as JArray) ?? new JArray())
{
    var item = edge?["node"] as JObject;
    if (item == null)
    {
        continue;
    }

    var sku = item["sku"]?.ToString() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(sku))
    {
        throw new InvalidOperationException("Shopify order " + orderName + " line " + ((lines.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) + " has no SKU, so Business Central cannot resolve an item for it. Give the line a SKU in Shopify (the order is re-imported automatically the next time it is updated).");
    }

    var description = item["name"]?.ToString();
    if (string.IsNullOrWhiteSpace(description))
    {
        description = sku;
    }
    if (description != null && description.Length > 100)
    {
        description = description.Substring(0, 100);
    }

    lines.Add(new JObject
    {
        ["lineNo"] = lineNo,
        ["sku"] = sku,
        ["description"] = description ?? sku,
        ["quantity"] = FinValues.ReadDecimal(item["quantity"]),
        ["unitPrice"] = FinValues.ReadDecimal(item["originalUnitPriceSet"]?["shopMoney"]?["amount"])
    });
    lineNo += 10000;
}

if (lines.Count == 0)
{
    throw new InvalidOperationException("Shopify order " + orderName + " has no line items, so it cannot be created in Business Central.");
}

var payload = new JObject
{
    ["shopifyOrderId"] = order["id"]?.ToString() ?? string.Empty,
    ["shopifyOrderName"] = orderName,
    ["customerNo"] = string.Empty,
    ["orderDate"] = orderDate,
    ["currencyCode"] = order["currencyCode"]?.ToString() ?? string.Empty,
    ["note"] = ("Shopify order " + orderName).Trim(),
    ["shippingName"] = address["name"]?.ToString() ?? string.Empty,
    ["shippingAddress1"] = address["address1"]?.ToString() ?? string.Empty,
    ["shippingAddress2"] = address["address2"]?.ToString() ?? string.Empty,
    ["shippingCity"] = address["city"]?.ToString() ?? string.Empty,
    ["shippingPostCode"] = address["zip"]?.ToString() ?? string.Empty,
    ["shippingCountryCode"] = address["countryCodeV2"]?.ToString() ?? string.Empty,
    ["shippingPhone"] = address["phone"]?.ToString() ?? string.Empty,
    ["totalAmount"] = FinValues.ReadDecimal(order["currentTotalPriceSet"]?["shopMoney"]?["amount"]),
    ["fintegratorSalesOrderLines"] = lines
};

return payload.ToString(Newtonsoft.Json.Formatting.None);
