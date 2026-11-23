// Shopify GraphQL endpoint for the order pull. The API version is configurable through
// the flow's persisted state so it can be moved forward without editing the flow.
var state = JObject.Parse(string.IsNullOrWhiteSpace(PersistedStateJson) ? "{}" : PersistedStateJson);
var version = state["shopifyApiVersion"]?.ToString();
if (string.IsNullOrWhiteSpace(version))
{
    version = "2026-07";
}

return "admin/api/" + version + "/graphql.json";
