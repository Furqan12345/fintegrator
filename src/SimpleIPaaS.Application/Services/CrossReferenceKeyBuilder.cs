using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Services;

public static class CrossReferenceKeyBuilder
{
    public const string KeySeparator = "|";

    public static JToken? ResolvePath(JToken? root, string? path)
    {
        if (root == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return root;
        }

        var current = root;

        foreach (var segment in SplitPath(path))
        {
            if (current == null || current.Type == JTokenType.Null)
            {
                return null;
            }

            if (current is JObject obj)
            {
                current = obj[segment];
                continue;
            }

            if (current is JArray array && int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                current = index >= 0 && index < array.Count ? array[index] : null;
                continue;
            }

            return null;
        }

        return current == null || current.Type == JTokenType.Null ? null : current;
    }

    public static IReadOnlyList<JToken> ToRecords(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return Array.Empty<JToken>();
        }

        if (token is JArray array)
        {
            return array.Cast<JToken>().ToList();
        }

        return new[] { token };
    }

    public static string BuildKey(JToken? record, IReadOnlyList<string> keyPaths)
    {
        if (keyPaths == null || keyPaths.Count == 0)
        {
            return string.Empty;
        }

        var segments = keyPaths
            .Select(path => FormatValue(ResolvePath(record, path)))
            .ToList();

        if (segments.All(string.IsNullOrEmpty))
        {
            return string.Empty;
        }

        return string.Join(KeySeparator, segments.Select(Escape));
    }

    public static string BuildValueJson(JToken? record, IReadOnlyList<string> valuePaths)
    {
        if (valuePaths == null || valuePaths.Count == 0)
        {
            return record == null ? "{}" : record.ToString(Formatting.None);
        }

        var payload = new JObject();

        foreach (var path in valuePaths)
        {
            var name = LeafName(path);
            if (payload.ContainsKey(name))
            {
                name = path;
            }

            payload[name] = ResolvePath(record, path)?.DeepClone() ?? JValue.CreateNull();
        }

        return payload.ToString(Formatting.None);
    }

    public static string FormatValue(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
        {
            return string.Empty;
        }

        if (token is JValue value)
        {
            return value.Value switch
            {
                null => string.Empty,
                bool flag => flag ? "true" : "false",
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var other => other.ToString() ?? string.Empty
            };
        }

        return token.ToString(Formatting.None);
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        var buffer = new StringBuilder();

        foreach (var character in path)
        {
            if (character == '.' || character == '[')
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                continue;
            }

            if (character == ']')
            {
                continue;
            }

            buffer.Append(character);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private static string LeafName(string path)
    {
        var segments = SplitPath(path).ToList();
        return segments.Count == 0 ? path : segments[^1];
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(KeySeparator, "\\" + KeySeparator);
}
