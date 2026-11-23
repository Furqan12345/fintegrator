using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Small helpers that emit Elasticsearch query DSL. Every filter the relational stores apply
// through LINQ has an exact counterpart here, so switching a dataset over cannot silently change
// what a page shows.
public static class ElasticsearchQueryBuilder
{
    public static JsonObject MatchAll() => new() { ["match_all"] = new JsonObject() };

    public static JsonObject Term(string field, string value) =>
        new() { ["term"] = new JsonObject { [field] = value } };

    public static JsonObject Terms(string field, IEnumerable<string> values) =>
        new() { ["terms"] = new JsonObject { [field] = new JsonArray(values.Select(value => (JsonNode)value!).ToArray()) } };

    public static JsonObject Range(string field, string? gte = null, string? gt = null, string? lte = null, string? lt = null)
    {
        var range = new JsonObject();
        if (gte != null) range["gte"] = gte;
        if (gt != null) range["gt"] = gt;
        if (lte != null) range["lte"] = lte;
        if (lt != null) range["lt"] = lt;
        return new JsonObject { ["range"] = new JsonObject { [field] = range } };
    }

    public static JsonObject Exists(string field) =>
        new() { ["exists"] = new JsonObject { ["field"] = field } };

    // Substring search across several fields, mirroring the SQL "LIKE %term%" filters.
    public static JsonObject SearchAcross(IReadOnlyList<string> fields, string term)
    {
        var pattern = term.Contains('*') || term.Contains('?') ? term : $"*{term}*";
        return new JsonObject
        {
            ["query_string"] = new JsonObject
            {
                ["query"] = pattern,
                ["fields"] = new JsonArray(fields.Select(field => (JsonNode)field!).ToArray()),
                ["analyze_wildcard"] = true,
                ["lenient"] = true,
                ["default_operator"] = "and"
            }
        };
    }

    public static JsonObject Bool(
        IEnumerable<JsonObject>? must = null,
        IEnumerable<JsonObject>? filter = null,
        IEnumerable<JsonObject>? should = null,
        IEnumerable<JsonObject>? mustNot = null,
        int? minimumShouldMatch = null)
    {
        var clauses = new JsonObject();
        AddClause(clauses, "must", must);
        AddClause(clauses, "filter", filter);
        AddClause(clauses, "should", should);
        AddClause(clauses, "must_not", mustNot);

        if (minimumShouldMatch.HasValue)
        {
            clauses["minimum_should_match"] = minimumShouldMatch.Value;
        }

        return new JsonObject { ["bool"] = clauses };
    }

    private static void AddClause(JsonObject target, string name, IEnumerable<JsonObject>? clauses)
    {
        if (clauses == null)
        {
            return;
        }

        var items = clauses.Where(clause => clause != null).ToList();
        if (items.Count == 0)
        {
            return;
        }

        target[name] = new JsonArray(items.Select(item => (JsonNode)item.DeepClone()).ToArray());
    }

    // A single sort entry, e.g. { "sequence": { "order": "desc" } }.
    public static JsonObject SortDescending(string field) =>
        new() { [field] = new JsonObject { ["order"] = "desc", ["missing"] = "_last" } };

    public static JsonObject SortAscending(string field) =>
        new() { [field] = new JsonObject { ["order"] = "asc", ["missing"] = "_last" } };

    public static JsonArray Sorts(params JsonObject[] entries) =>
        new(entries.Select(entry => (JsonNode)entry).ToArray());
}
