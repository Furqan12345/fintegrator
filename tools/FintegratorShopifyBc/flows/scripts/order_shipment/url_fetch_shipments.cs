// Reads the Business Central shipment feed through the AL extension, delta-filtered by the entry
// number kept in persisted state.
//
// entryNo (not the timestamp) is the cursor on purpose: Entry No. is an auto-increment integer, so
// it can never drift in precision or format the way an ISO timestamp can (a truncated fraction of a
// second would re-fetch the same rows forever). lastModifiedDateTime is still persisted for humans.
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var companyId = state["bcCompanyId"]?.ToString();
if (string.IsNullOrWhiteSpace(companyId))
{
    throw new InvalidOperationException("Persisted state key 'bcCompanyId' is not set. Open this flow, click State and set it to the Business Central company GUID, for example {\"bcCompanyId\":\"00000000-0000-0000-0000-000000000000\"}.");
}

var pageSize = FinValues.ReadInt(state["shipmentsPageSize"]);
if (pageSize < 1)
{
    pageSize = 200;
}

var url = "companies(" + companyId + ")/fintegratorShippedOrders?$expand=fintegratorShippedOrderLines"
    + "&$orderby=" + Uri.EscapeDataString("entryNo asc")
    + "&$top=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

var lastEntryNo = FinValues.ReadLong(state["shipmentsEntryNo"]);
if (lastEntryNo > 0L)
{
    url = url + "&$filter=" + Uri.EscapeDataString("entryNo gt " + lastEntryNo.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

return url;
