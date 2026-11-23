using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Services;

// Renders {{flowState.Node.path}} and {{persistedState.path}} placeholders in email
// templates. Path resolution is delegated to the same helper every other card uses.
public static class EmailTemplate
{
    public static string Render(
        string template,
        JObject flowState,
        JObject persistedState,
        bool htmlEncode,
        List<string> missing)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template ?? string.Empty;
        }

        var result = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            // \{{ is an escaped literal {{
            if (template[index] == '\\'
                && index + 2 < template.Length
                && template[index + 1] == '{'
                && template[index + 2] == '{')
            {
                result.Append("{{");
                index += 3;
                continue;
            }

            if (template[index] == '{' && index + 1 < template.Length && template[index + 1] == '{')
            {
                var close = template.IndexOf("}}", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    result.Append(template, index, template.Length - index);
                    break;
                }

                var expression = template.Substring(index + 2, close - index - 2).Trim();
                var value = Resolve(expression, flowState, persistedState, missing);
                result.Append(htmlEncode ? WebUtility.HtmlEncode(value) : value);
                index = close + 2;
                continue;
            }

            result.Append(template[index]);
            index++;
        }

        return result.ToString();
    }

    private static string Resolve(
        string expression,
        JObject flowState,
        JObject persistedState,
        List<string> missing)
    {
        JToken root;
        string path;
        if (expression.StartsWith("flowState.", StringComparison.Ordinal))
        {
            root = flowState;
            path = expression["flowState.".Length..];
        }
        else if (expression.StartsWith("persistedState.", StringComparison.Ordinal))
        {
            root = persistedState;
            path = expression["persistedState.".Length..];
        }
        else
        {
            missing.Add(expression);
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            missing.Add(expression);
            return string.Empty;
        }

        var tokens = CrossReferenceKeyBuilder.ResolvePaths(root, path).ToList();
        if (tokens.Count == 0)
        {
            missing.Add(expression);
            return string.Empty;
        }

        return string.Join(", ", tokens.Select(TokenText));
    }

    private static string TokenText(JToken token) => token.Type switch
    {
        JTokenType.Null or JTokenType.Undefined => string.Empty,
        JTokenType.String => token.Value<string>() ?? string.Empty,
        JTokenType.Object or JTokenType.Array => token.ToString(Formatting.None),
        _ => token is JValue value
            ? Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty
            : token.ToString()
    };
}
