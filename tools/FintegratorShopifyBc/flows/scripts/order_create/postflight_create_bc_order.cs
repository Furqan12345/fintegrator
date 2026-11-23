// Turns the extension's answer into flow state and fails loudly when BC did not create or find a
// sales order, so the transaction and its dead letter carry the real reason.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var bcError = root["error"]?["message"]?.ToString();
if (!string.IsNullOrWhiteSpace(bcError))
{
    throw new InvalidOperationException("Business Central rejected the sales order: " + bcError);
}

var status = root["status"]?.ToString() ?? string.Empty;
var salesOrderNo = root["salesOrderNo"]?.ToString() ?? string.Empty;
var errorMessage = root["errorMessage"]?.ToString() ?? string.Empty;

if (string.IsNullOrWhiteSpace(salesOrderNo))
{
    throw new InvalidOperationException("Business Central did not return a sales order number (status '" + status + "'" + (string.IsNullOrWhiteSpace(errorMessage) ? string.Empty : ", " + errorMessage) + ").");
}

return new JObject
{
    ["salesOrderNo"] = salesOrderNo,
    ["status"] = status,
    ["created"] = FinValues.ReadBool(root["createdSalesOrder"], false),
    ["errorMessage"] = errorMessage
}.ToString(Newtonsoft.Json.Formatting.None);
