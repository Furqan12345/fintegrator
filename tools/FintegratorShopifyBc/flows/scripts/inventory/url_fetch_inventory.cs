// Reads the Business Central inventory change feed through the AL extension, delta-filtered by the
// entry number kept in persisted state (see the shipment flow for why entryNo, not the timestamp).
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var companyId = state["bcCompanyId"]?.ToString();
if (string.IsNullOrWhiteSpace(companyId))
{
    throw new InvalidOperationException("Persisted state key 'bcCompanyId' is not set. Open this flow, click State and set it to the Business Central company GUID, for example {\"bcCompanyId\":\"00000000-0000-0000-0000-000000000000\"}.");
}

var pageSize = FinValues.ReadInt(state["inventoryPageSize"]);
if (pageSize < 1)
{
    pageSize = 500;
}

var url = "companies(" + companyId + ")/fintegratorInventoryChanges"
    + "?$orderby=" + Uri.EscapeDataString("entryNo asc")
    + "&$top=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

var lastEntryNo = FinValues.ReadLong(state["inventoryEntryNo"]);
if (lastEntryNo > 0L)
{
    url = url + "&$filter=" + Uri.EscapeDataString("entryNo gt " + lastEntryNo.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

return url;
