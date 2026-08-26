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

    public static IEnumerable<JToken> ResolvePaths(JToken? root, string? path)
    {
        if (root == null || root.Type == JTokenType.Null) yield break;
        if (string.IsNullOrWhiteSpace(path)) { yield return root; yield break; }

        var segments = SplitPath(path).ToList();
        foreach (var token in ResolvePathsRecursive(root, segments, 0))
            yield return token;
    }

    private static IEnumerable<JToken> ResolvePathsRecursive(JToken current, List<string> segments, int index)
    {
        if (current == null || current.Type == JTokenType.Null) yield break;

        if (index >= segments.Count)
        {
            yield return current;
            yield break;
        }

        var segment = segments[index];

        if (current is JObject obj)
        {
            if (obj.TryGetValue(segment, out var child))
            {
                foreach (var token in ResolvePathsRecursive(child, segments, index + 1))
                    yield return token;
            }
        }
        else if (current is JArray array)
        {
            if (segment == "*" || segment.Length == 0)
            {
                foreach (var element in array)
                {
                    foreach (var token in ResolvePathsRecursive(element, segments, index + 1))
                        yield return token;
                }
            }
            else if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arrIndex))
            {
                if (arrIndex >= 0 && arrIndex < array.Count)
                {
                    foreach (var token in ResolvePathsRecursive(array[arrIndex], segments, index + 1))
                        yield return token;
                }
            }
        }
    }

    public static bool ContainsWildcard(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return SplitPath(path).Any(segment => segment == "*" || segment.Length == 0);
    }

    public static JToken? FilterArrayPreservingStructure(JToken? input, string? path, ISet<string> knownKeys, IReadOnlyList<string> keyPaths)
    {
        return FilterArrayPreservingStructure(input, path, knownKeys, keyPaths, filterKnown: false);
    }

    public static JToken? FilterArrayPreservingStructure(
        JToken? input,
        string? path,
        ISet<string> knownKeys,
        IReadOnlyList<string> keyPaths,
        bool filterKnown)
    {
        if (input == null) return null;
        if (string.IsNullOrWhiteSpace(path))
        {
            var key = BuildKey(input, keyPaths);
            return ShouldPass(key, knownKeys, new HashSet<string>(StringComparer.Ordinal), filterKnown) ? input.DeepClone() : null;
        }

        var segments = SplitPath(path).ToList();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        return FilterRecursive(input, segments, 0, knownKeys, seenKeys, keyPaths, filterKnown);
    }

    private static bool ShouldPass(string key, ISet<string> knownKeys, ISet<string> seenKeys, bool filterKnown)
    {
        if (string.IsNullOrEmpty(key)) return true;
        var isKnown = knownKeys.Contains(key);
        if (filterKnown) return isKnown && seenKeys.Add(key);
        if (isKnown) return false;
        return seenKeys.Add(key);
    }

    private static JToken? FilterRecursive(JToken current, List<string> segments, int index, ISet<string> knownKeys, ISet<string> seenKeys, IReadOnlyList<string> keyPaths, bool filterKnown)
    {
        if (current == null || current.Type == JTokenType.Null) return null;

        if (index >= segments.Count)
        {
            var key = BuildKey(current, keyPaths);
            return ShouldPass(key, knownKeys, seenKeys, filterKnown) ? current.DeepClone() : null;
        }

        var segment = segments[index];

        if (current is JObject obj)
        {
            if (obj.TryGetValue(segment, out var child))
            {
                var newObj = (JObject)obj.DeepClone();
                var filteredChild = FilterRecursive(child, segments, index + 1, knownKeys, seenKeys, keyPaths, filterKnown);

                if (filteredChild == null)
                {
                    if (child is JArray)
                        newObj[segment] = new JArray();
                    else
                        newObj.Remove(segment);
                }
                else
                {
                    newObj[segment] = filteredChild;
                }

                return newObj;
            }
            return (JObject)obj.DeepClone();
        }

        if (current is JArray array)
        {
            if (segment == "*" || segment.Length == 0)
            {
                var newItems = new JArray();
                foreach (var item in array)
                {
                    var filtered = FilterRecursive(item, segments, index + 1, knownKeys, seenKeys, keyPaths, filterKnown);
                    if (filtered != null)
                        newItems.Add(filtered);
                }
                return newItems;
            }

            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arrIndex))
            {
                if (arrIndex >= 0 && arrIndex < array.Count)
                {
                    var result = (JArray)array.DeepClone();
                    var filtered = FilterRecursive(array[arrIndex], segments, index + 1, knownKeys, seenKeys, keyPaths, filterKnown);
                    if (filtered != null)
                        result[arrIndex] = filtered;
                    return result;
                }
                return (JArray)array.DeepClone();
            }
        }

        return current.DeepClone();
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

    public static IReadOnlyList<JToken> ToRecords(IEnumerable<JToken> tokens)
    {
        var records = new List<JToken>();
        foreach (var token in tokens)
        {
            if (token == null || token.Type == JTokenType.Null) continue;
            if (token is JArray array)
                records.AddRange(array);
            else
                records.Add(token);
        }
        return records;
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
