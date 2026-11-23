// Business Central extension endpoint for sales order creation.
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var companyId = state["bcCompanyId"]?.ToString();
if (string.IsNullOrWhiteSpace(companyId))
{
    throw new InvalidOperationException("Persisted state key 'bcCompanyId' is not set. Open this flow, click State and set it to the Business Central company GUID, for example {\"bcCompanyId\":\"00000000-0000-0000-0000-000000000000\"}.");
}

return "companies(" + companyId + ")/fintegratorSalesOrders";
