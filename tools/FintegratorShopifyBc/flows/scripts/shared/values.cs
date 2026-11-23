// Shared helpers prepended to generated scripts by scripts/build_flows.py (only when a script uses
// them, to keep the compiled script small - cold Roslyn compilation counts against the engine's
// Scripting:TimeoutSeconds).
//
// Why this exists: Json.NET parses ISO-8601 looking strings into DateTime tokens while reading flow
// state, and DateTime.ToString()/double.ToString() follow the *server's* culture, so a stored
// watermark can come back as "15/10/2026 8:00:00 am" and silently stop matching. Every date or
// number read out of state goes through here. A class keeps unused members warning-free.
static class FinValues
{
    public static string Iso(DateTime value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Date(DateTime value)
    {
        return value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ReadString(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return string.Empty;
        if (token.Type == JTokenType.Date) return Iso(token.Value<DateTime>());
        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<decimal>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return token.ToString();
    }

    public static decimal ReadDecimal(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return 0m;
        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<decimal>();
        var parsed = 0m;
        decimal.TryParse(token.ToString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out parsed);
        return parsed;
    }

    public static long ReadLong(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return 0L;
        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<long>();
        var parsed = 0L;
        long.TryParse(token.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed);
        return parsed;
    }

    public static int ReadInt(JToken token)
    {
        var value = ReadLong(token);
        if (value > int.MaxValue) return int.MaxValue;
        if (value < int.MinValue) return int.MinValue;
        return (int)value;
    }

    public static bool ReadBool(JToken token, bool fallback)
    {
        if (token == null || token.Type == JTokenType.Null) return fallback;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        var parsed = false;
        return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
    }

    // Shopify reports rate limiting as HTTP 200 plus errors ("Throttled") or as exhausted cost, so it
    // never reaches the transport retry policy and has to be explained at this level.
    public static string ShopifyGraphQlError(JObject root)
    {
        if (root == null) return string.Empty;

        var errors = root["errors"] as JArray;
        if (errors != null && errors.Count > 0)
        {
            var message = errors[0]?["message"]?.ToString() ?? "unknown error";
            if (message.IndexOf("throttl", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Shopify rate-limited this call (GraphQL throttling arrives as HTTP 200). Lower ordersPageSize / inventoryPageSize or the run frequency, wait for the bucket to refill, then retry from the dead letter queue. Original message: " + message;
            }

            return "Shopify GraphQL error: " + message;
        }

        var available = root["extensions"]?["cost"]?["throttleStatus"]?["currentlyAvailable"];
        if (available != null && ReadDecimal(available) <= 0m)
        {
            return "Shopify reported no remaining GraphQL cost (extensions.cost.throttleStatus.currentlyAvailable = 0). Lower the batch size or the run frequency, then retry from the dead letter queue.";
        }

        return string.Empty;
    }
}
