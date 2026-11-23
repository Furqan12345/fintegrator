// OData answer -> flow state. The feed is ordered ascending, so the newest row drives the next
// watermark; the row's own timestamp is normalised to ISO-8601 UTC on the way out.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var bcError = root["error"]?["message"]?.ToString();
if (!string.IsNullOrWhiteSpace(bcError))
{
    throw new InvalidOperationException("Business Central error: " + bcError);
}

var shipments = root["value"] as JArray ?? new JArray();
var lastModified = string.Empty;
var lastEntryNo = 0L;

foreach (var shipment in shipments)
{
    var timestamp = FinValues.ReadString(shipment["lastModifiedDateTime"]);
    if (string.IsNullOrWhiteSpace(timestamp))
    {
        continue;
    }

    lastModified = timestamp;
    lastEntryNo = FinValues.ReadLong(shipment["entryNo"]);
}

var output = new JObject
{
    ["shipments"] = shipments,
    ["count"] = shipments.Count
};

if (!string.IsNullOrWhiteSpace(lastModified))
{
    output["lastModifiedDateTime"] = lastModified;
    output["lastEntryNo"] = lastEntryNo;
}

return output.ToString(Newtonsoft.Json.Formatting.None);
