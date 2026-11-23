// Confirms the flag was stored. A failure here fails the transaction, which is intentional: the
// operator should know that Shopify holds the fulfillment while Business Central does not show the
// flag, because that is the one state in which a replay could duplicate the fulfillment.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var bcError = root["error"]?["message"]?.ToString();
if (!string.IsNullOrWhiteSpace(bcError))
{
    throw new InvalidOperationException("Business Central refused to flag the shipment as pushed: " + bcError);
}

var pushed = FinValues.ReadBool(root["shopifyPushed"], false);
if (!pushed)
{
    throw new InvalidOperationException("Business Central accepted the PATCH but the shopifyPushed flag is still false on the feed row.");
}

return new JObject
{
    ["shopifyPushed"] = true,
    ["entryNo"] = FinValues.ReadLong(root["entryNo"])
}.ToString(Newtonsoft.Json.Formatting.None);
