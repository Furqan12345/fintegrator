// OData answer -> flow state, keeping the newest row's timestamp and entry number for the next
// watermark.
var root = JObject.Parse(string.IsNullOrWhiteSpace(HttpResponseJson) ? "{}" : HttpResponseJson);

var bcError = root["error"]?["message"]?.ToString();
if (!string.IsNullOrWhiteSpace(bcError))
{
    throw new InvalidOperationException("Business Central error: " + bcError);
}

var changes = root["value"] as JArray ?? new JArray();
var lastModified = string.Empty;
var lastEntryNo = 0L;

foreach (var change in changes)
{
    var timestamp = FinValues.ReadString(change["lastModifiedDateTime"]);
    if (string.IsNullOrWhiteSpace(timestamp))
    {
        continue;
    }

    lastModified = timestamp;
    lastEntryNo = FinValues.ReadLong(change["entryNo"]);
}

var output = new JObject
{
    ["changes"] = changes,
    ["count"] = changes.Count
};

if (!string.IsNullOrWhiteSpace(lastModified))
{
    output["lastModifiedDateTime"] = lastModified;
    output["lastEntryNo"] = lastEntryNo;
}

return output.ToString(Newtonsoft.Json.Formatting.None);
