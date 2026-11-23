// Collapses the shipment feed to one entry per Shopify order before the fan-out.
//
// Why: the fan-out queues one durable child transaction per row and the engine runs children
// concurrently, so two shipments for the same order in one run could race into two Shopify
// fulfillments (the "no open fulfillment order" guard only helps once the first one lands).
// Quantities are summed per SKU (the fulfillment builder never ships more than Shopify's remaining
// quantity) and the newest row's metadata - tracking number, carrier, shipment number - wins.
var flow = JObject.Parse(string.IsNullOrWhiteSpace(FlowStateJson) ? "{}" : FlowStateJson);
var fetched = flow["FetchBcShipments"] as JObject ?? new JObject();
var shipments = fetched["shipments"] as JArray ?? new JArray();

var mergedByOrder = new JObject();
var lineTotals = new JObject();
var orderKeys = new JArray();
var sequence = 0;

foreach (var row in shipments)
{
    var orderId = FinValues.ReadString(row["shopifyOrderId"]);
    if (string.IsNullOrWhiteSpace(orderId))
    {
        // Keep unlinked rows as their own entry so the child fails with a clear, actionable message
        // instead of the row silently disappearing.
        sequence++;
        orderId = "missing-shopify-order-id-" + (string.IsNullOrWhiteSpace(FinValues.ReadString(row["entryNo"])) ? sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : FinValues.ReadString(row["entryNo"]));
    }

    var target = mergedByOrder[orderId] as JObject;
    if (target == null)
    {
        target = (JObject)row.DeepClone();
        mergedByOrder[orderId] = target;
        lineTotals[orderId] = new JObject();
        orderKeys.Add(orderId);
    }
    else
    {
        // Newer row (feed is ordered by entryNo ascending): its metadata wins.
        foreach (var property in row.Children<JProperty>())
        {
            if (!property.Name.Equals("fintegratorShippedOrderLines", StringComparison.OrdinalIgnoreCase))
            {
                target[property.Name] = property.Value.DeepClone();
            }
        }
    }

    var totals = lineTotals[orderId] as JObject;
    foreach (var line in (row["fintegratorShippedOrderLines"] as JArray) ?? new JArray())
    {
        var sku = FinValues.ReadString(line["sku"]);
        if (string.IsNullOrWhiteSpace(sku))
        {
            continue;
        }

        totals[sku] = FinValues.ReadDecimal(totals[sku]) + FinValues.ReadDecimal(line["quantity"]);
    }
}

var collapsed = new JArray();
foreach (var orderKey in orderKeys)
{
    var orderId = orderKey.ToString();
    var entry = (JObject)mergedByOrder[orderId];
    var totals = lineTotals[orderId] as JObject ?? new JObject();

    var items = new JObject();
    foreach (var line in (entry["fintegratorShippedOrderLines"] as JArray) ?? new JArray())
    {
        var sku = FinValues.ReadString(line["sku"]);
        if (string.IsNullOrWhiteSpace(sku) || items[sku] != null)
        {
            continue;
        }

        items[sku] = line["itemNo"]?.DeepClone() ?? new JValue(sku);
    }

    var lines = new JArray();
    var lineNo = 10000;
    foreach (var property in totals.Properties())
    {
        var quantity = FinValues.ReadDecimal(property.Value);
        if (quantity <= 0m)
        {
            continue;
        }

        lines.Add(new JObject
        {
            ["lineNo"] = lineNo,
            ["itemNo"] = items[property.Name]?.ToString() ?? property.Name,
            ["sku"] = property.Name,
            ["quantity"] = quantity
        });
        lineNo += 10000;
    }

    entry["fintegratorShippedOrderLines"] = lines;
    entry["shippedQuantity"] = FinValues.ReadDecimal(entry["shippedQuantity"]);
    collapsed.Add(entry);
}

return new JObject
{
    ["shipments"] = collapsed,
    ["count"] = collapsed.Count,
    ["sourceCount"] = shipments.Count
}.ToString(Newtonsoft.Json.Formatting.None);
