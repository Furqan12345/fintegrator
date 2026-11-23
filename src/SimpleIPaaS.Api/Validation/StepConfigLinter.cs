using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Validation;

// Field-level lint for flow drafts. The engine silently falls back to defaults when
// stepConfig is malformed, which turns hallucinated keys into mystery bugs at runtime;
// this linter makes those explicit so validate_draft can force fixes before a draft is
// returned. Rules mirror the executor's stepConfig parsers (SimpleIPaaS.Application.Models:
// ApiPacket/Pagination/ForEach/FanOut/CrossReferenceStepConfig) and the designer cards.
public static class StepConfigLinter
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };
    private static readonly HashSet<string> UrlModes = new(StringComparer.OrdinalIgnoreCase) { "Manual", "Connection" };
    private static readonly HashSet<string> AuthTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "None", "Basic", "Bearer", "ApiKey", "OAuth2ClientCredentials", "OAuth2AuthCode", "OAuth2RefreshToken", "Custom", "AmazonSpApi", "Shopify"
    };
    private static readonly HashSet<string> PaginationStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "None", "AmazonNextToken", "LinkHeader", "Cursor", "PageOffset"
    };
    private static readonly HashSet<string> ScheduleModes = new(StringComparer.OrdinalIgnoreCase) { "Cron", "OneTime" };
    private static readonly HashSet<string> FilterModes = new(StringComparer.OrdinalIgnoreCase) { "SkipExisting", "KeepExisting" };
    private static readonly HashSet<string> StepTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mapping", "Branch", "HttpAction", "Debug", "PersistedState", "Schedule",
        "CrossReferenceStore", "CrossReferenceFilter", "ForEach", "FanOut",
        "AddIdentifier", "Error", "Success", "Email"
    };

    private static readonly string[] CredentialMarkers =
    {
        "shpat_", "shpss_", "shpca_", "sk-", "sk_", "ghp_", "gho_", "AKIA", "xox", "Bearer "
    };

    public static (List<string> Errors, List<string> Warnings) Lint(IntegrationFlowDto? flow)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (flow == null) return (errors, warnings);

        if (string.IsNullOrWhiteSpace(flow.Name))
            errors.Add("flow.name is required.");

        for (var index = 0; index < flow.Nodes.Count; index++)
        {
            var node = flow.Nodes[index];
            if (node == null) continue;
            var label = string.IsNullOrWhiteSpace(node.NodeName) ? $"node #{index + 1}" : node.NodeName;

            if (string.IsNullOrWhiteSpace(node.NodeName))
                errors.Add($"node #{index + 1}: nodeName is required.");
            if (string.IsNullOrWhiteSpace(node.StepType) || !StepTypes.Contains(node.StepType))
            {
                errors.Add($"{label}: stepType '{node.StepType}' is not supported. Use one of: {string.Join(", ", StepTypes)}.");
                continue;
            }

            var type = StepTypes.First(value => string.Equals(value, node.StepType, StringComparison.OrdinalIgnoreCase));
            switch (type)
            {
                case "HttpAction": LintHttpAction(label, node, errors, warnings); break;
                case "Schedule": LintSchedule(label, node, errors, warnings); break;
                case "CrossReferenceStore": LintCrossReference(label, node, errors, warnings); break;
                case "CrossReferenceFilter": LintCrossReference(label, node, errors, warnings); break;
                case "ForEach": LintForEach(label, node, errors, warnings); break;
                case "FanOut": LintFanOut(label, node, errors, warnings); break;
                case "Email": LintEmail(label, node, flow, errors, warnings); break;
                case "AddIdentifier":
                case "Error":
                case "Success":
                    if (string.IsNullOrWhiteSpace(node.MappingCode))
                        warnings.Add($"{label}: mappingCode is empty, so the {(type == "AddIdentifier" ? "identifier" : "message")} will not be set. Provide a C# snippet that returns the string.");
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(node.StepConfig))
                        warnings.Add($"{label}: stepConfig is not used by {type} and will be ignored.");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(node.StepConfig) &&
                CredentialMarkers.Any(marker => node.StepConfig.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"{label}: stepConfig appears to hardcode a credential. Move it to the auth fields or a connection — those are encrypted and never exposed to the AI or its tools.");
        }

        var scheduleNodes = flow.Nodes.Count(node => string.Equals(node.StepType, "Schedule", StringComparison.OrdinalIgnoreCase));
        if (scheduleNodes > 1)
            errors.Add("A flow can have at most one Schedule trigger node.");

        return (errors, warnings);
    }

    private static void LintHttpAction(string label, IntegrationStepDto node, List<string> errors, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(node.HttpMethod) && !HttpMethods.Contains(node.HttpMethod))
            errors.Add($"{label}: httpMethod '{node.HttpMethod}' is not supported. Use GET, POST, PUT, PATCH or DELETE.");
        if (!string.IsNullOrWhiteSpace(node.UrlMode) && !UrlModes.Contains(node.UrlMode))
            errors.Add($"{label}: urlMode '{node.UrlMode}' is not supported. Use Manual or Connection.");
        if (!string.IsNullOrWhiteSpace(node.AuthType) && !AuthTypes.Contains(node.AuthType))
            errors.Add($"{label}: authType '{node.AuthType}' is not supported. Use one of: {string.Join(", ", AuthTypes)}.");

        if (string.Equals(node.UrlMode, "Connection", StringComparison.OrdinalIgnoreCase) && node.ConnectionId == null)
            errors.Add($"{label}: urlMode Connection requires connectionId (use an id returned by list_connections, or use urlMode Manual with endpointUrl).");
        if (string.IsNullOrWhiteSpace(node.UrlMode) || string.Equals(node.UrlMode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(node.EndpointUrl) && string.IsNullOrWhiteSpace(node.UrlCode))
                errors.Add($"{label}: urlMode Manual requires endpointUrl (or urlCode that returns the URL at runtime).");
        }

        if (string.IsNullOrWhiteSpace(node.StepConfig)) return;
        if (!TryParseObject(label, node.StepConfig, errors, out var config)) return;

        WarnUnknownKeys(label, "stepConfig", config, new[] { "apiPacket", "pagination" }, warnings);

        if (config.TryGetProperty("apiPacket", out var packet))
        {
            if (packet.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{label}: stepConfig.apiPacket must be an object.");
            }
            else
            {
                WarnUnknownKeys(label, "stepConfig.apiPacket", packet, new[] { "headersJson", "queryParamsJson", "contentType", "requestBody" }, warnings);
                foreach (var key in new[] { "headersJson", "queryParamsJson" })
                {
                    if (!packet.TryGetProperty(key, out var value)) continue;
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"{label}: stepConfig.apiPacket.{key} must be a JSON string.");
                        continue;
                    }
                    var json = value.GetString();
                    if (!string.IsNullOrWhiteSpace(json) && !IsValidJson(json))
                        errors.Add($"{label}: stepConfig.apiPacket.{key} is not valid JSON.");
                }
                foreach (var key in new[] { "contentType", "requestBody" })
                {
                    if (packet.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.String)
                        errors.Add($"{label}: stepConfig.apiPacket.{key} must be a string.");
                }
            }
        }

        if (config.TryGetProperty("pagination", out var pagination))
        {
            if (pagination.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{label}: stepConfig.pagination must be an object.");
            }
            else
            {
                WarnUnknownKeys(label, "stepConfig.pagination", pagination,
                    new[]
                    {
                        "style", "maxPages", "delayMs", "aggregatePath", "nextTokenPath", "nextTokenParameter",
                        "nextLinkHeader", "cursorResponsePath", "cursorRequestParameter", "offsetRequestParameter",
                        "pageSizeParameter", "pageSize", "hasMoreResponsePath"
                    }, warnings);
                if (pagination.TryGetProperty("style", out var style))
                {
                    if (style.ValueKind != JsonValueKind.String || !PaginationStyles.Contains(style.GetString() ?? string.Empty))
                        errors.Add($"{label}: stepConfig.pagination.style '{style}' is not supported. Use one of: {string.Join(", ", PaginationStyles)}.");
                }
                CheckIntegerRange(label, pagination, "maxPages", 1, 1000, warnings);
                CheckIntegerRange(label, pagination, "delayMs", 0, 60000, warnings);
                CheckIntegerRange(label, pagination, "pageSize", 1, 10000, warnings);
                LintPaginationStyleFields(label, pagination, errors);
            }
        }
    }

    private static void LintPaginationStyleFields(string label, JsonElement pagination, List<string> errors)
    {
        var style = pagination.TryGetProperty("style", out var styleValue) ? styleValue.GetString() ?? "None" : "None";
        switch (style.ToLowerInvariant())
        {
            case "cursor":
                RequirePaginationField(label, pagination, "cursorResponsePath", errors);
                RequirePaginationField(label, pagination, "cursorRequestParameter", errors);
                break;
            case "amazonnexttoken":
                RequirePaginationField(label, pagination, "nextTokenPath", errors);
                RequirePaginationField(label, pagination, "nextTokenParameter", errors);
                break;
            case "linkheader":
                RequirePaginationField(label, pagination, "nextLinkHeader", errors);
                break;
            case "pageoffset":
                RequirePaginationField(label, pagination, "offsetRequestParameter", errors);
                break;
        }
    }

    private static void RequirePaginationField(string label, JsonElement pagination, string key, List<string> errors)
    {
        if (!pagination.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            errors.Add($"{label}: stepConfig.pagination.{key} is required for this pagination style, or pagination will repeat the first page.");
    }

    private static void LintSchedule(string label, IntegrationStepDto node, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(node.StepConfig) || !TryParseObject(label, node.StepConfig, errors, out var config))
        {
            if (string.IsNullOrWhiteSpace(node.StepConfig))
                errors.Add(label + ": stepConfig is required for Schedule, for example {\"schedule\":{\"mode\":\"Cron\",\"cronExpression\":\"0 9 * * *\"}}.");
            return;
        }

        WarnUnknownKeys(label, "stepConfig", config, new[] { "schedule" }, warnings);
        if (!config.TryGetProperty("schedule", out var schedule) || schedule.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{label}: stepConfig.schedule object is required.");
            return;
        }

        WarnUnknownKeys(label, "stepConfig.schedule", schedule, new[] { "mode", "cronExpression", "runAtUtc" }, warnings);
        var mode = schedule.TryGetProperty("mode", out var modeValue) ? modeValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(mode) || !ScheduleModes.Contains(mode))
        {
            errors.Add($"{label}: stepConfig.schedule.mode must be Cron or OneTime.");
            return;
        }
        if (string.Equals(mode, "Cron", StringComparison.OrdinalIgnoreCase))
        {
            var cron = schedule.TryGetProperty("cronExpression", out var cronValue) ? cronValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(cron))
                errors.Add($"{label}: schedule mode Cron requires cronExpression.");
            else if (cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
                errors.Add($"{label}: cronExpression must have exactly 5 fields (minute hour day month weekday).");
        }
        else
        {
            var runAt = schedule.TryGetProperty("runAtUtc", out var runAtValue) ? runAtValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(runAt) || !DateTime.TryParse(runAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out _))
                errors.Add($"{label}: schedule mode OneTime requires runAtUtc as an ISO-8601 timestamp.");
        }
    }

    private static void LintCrossReference(string label, IntegrationStepDto node, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(node.StepConfig) || !TryParseObject(label, node.StepConfig, errors, out var config))
        {
            if (string.IsNullOrWhiteSpace(node.StepConfig))
                errors.Add($"{label}: stepConfig is required for {node.StepType}. Cross-reference settings are root-level keys.");
            return;
        }

        if (config.TryGetProperty("crossReference", out _))
            errors.Add($"{label}: cross-reference settings must be root-level keys of stepConfig, not nested in a wrapper object.");

        WarnUnknownKeys(label, "stepConfig", config, new[] { "listName", "arrayPath", "keyPaths", "valuePaths", "filterMode" }, warnings);

        if (!config.TryGetProperty("listName", out var listName) || string.IsNullOrWhiteSpace(listName.GetString()))
            errors.Add($"{label}: listName is required (use list_cross_reference_lists to reuse an existing list).");
        if (!config.TryGetProperty("arrayPath", out var arrayPath) || string.IsNullOrWhiteSpace(arrayPath.GetString()))
            errors.Add($"{label}: arrayPath is required.");
        if (!config.TryGetProperty("keyPaths", out var keyPaths) || keyPaths.ValueKind != JsonValueKind.Array || keyPaths.GetArrayLength() == 0)
            errors.Add($"{label}: keyPaths must be a non-empty array of path strings.");
        else if (keyPaths.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            errors.Add($"{label}: keyPaths entries must be non-empty strings.");

        if (config.TryGetProperty("filterMode", out var filterMode) &&
            (filterMode.ValueKind != JsonValueKind.String || !FilterModes.Contains(filterMode.GetString() ?? string.Empty)))
            errors.Add($"{label}: filterMode '{filterMode}' is not supported. Use SkipExisting or KeepExisting.");
    }

    private static void LintForEach(string label, IntegrationStepDto node, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(node.StepConfig) || !TryParseObject(label, node.StepConfig, errors, out var config))
        {
            if (string.IsNullOrWhiteSpace(node.StepConfig))
                errors.Add(label + ": stepConfig is required for ForEach, for example {\"forEach\":{\"arrayPath\":\"flowState.Fetch.orders\",\"itemVariable\":\"item\"}}.");
            return;
        }

        WarnUnknownKeys(label, "stepConfig", config, new[] { "forEach", "arrayPath", "itemVariable", "parallel", "maxDegreeOfParallelism" }, warnings);
        var body = config.TryGetProperty("forEach", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : config;

        if (!body.TryGetProperty("arrayPath", out var arrayPath) || string.IsNullOrWhiteSpace(arrayPath.GetString()))
            errors.Add($"{label}: forEach.arrayPath is required.");
        if (!body.TryGetProperty("itemVariable", out var itemVariable) || string.IsNullOrWhiteSpace(itemVariable.GetString()))
            errors.Add($"{label}: forEach.itemVariable is required (for example \"item\").");
        CheckIntegerRange(label, body, "maxDegreeOfParallelism", 1, 64, warnings);
    }

    private static void LintFanOut(string label, IntegrationStepDto node, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(node.StepConfig) || !TryParseObject(label, node.StepConfig, errors, out var config))
        {
            if (string.IsNullOrWhiteSpace(node.StepConfig))
                errors.Add(label + ": stepConfig is required for FanOut, for example {\"fanOut\":{\"arrayPath\":\"flowState.Fetch.orders\",\"itemVariable\":\"item\",\"maxItems\":1000}}.");
            return;
        }

        WarnUnknownKeys(label, "stepConfig", config, new[] { "fanOut", "arrayPath", "itemVariable", "maxItems" }, warnings);
        var body = config.TryGetProperty("fanOut", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : config;

        if (!body.TryGetProperty("arrayPath", out var arrayPath) || string.IsNullOrWhiteSpace(arrayPath.GetString()))
            errors.Add($"{label}: fanOut.arrayPath is required.");
        if (!body.TryGetProperty("itemVariable", out var itemVariable) || string.IsNullOrWhiteSpace(itemVariable.GetString()))
            errors.Add($"{label}: fanOut.itemVariable is required (for example \"item\").");
        CheckIntegerRange(label, body, "maxItems", 1, 10000, warnings);
    }

    private static void LintEmail(
        string label,
        IntegrationStepDto node,
        IntegrationFlowDto flow,
        List<string> errors,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(node.StepConfig))
        {
            if (!TryParseObject(label, node.StepConfig, errors, out var configJson))
            {
                return;
            }

            WarnUnknownKeys(label, "stepConfig", configJson,
                new[] { "to", "cc", "bcc", "replyTo", "subject", "body", "isHtml", "useScript", "continueOnError" },
                warnings);
        }

        var config = EmailStepConfig.Parse(node.StepConfig);
        if (config.UseScript)
        {
            if (string.IsNullOrWhiteSpace(node.MappingCode))
            {
                errors.Add($"{label}: mappingCode is required when stepConfig.useScript is true. Return an object with to, cc, subject, body and isHtml.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.To))
            {
                errors.Add($"{label}: stepConfig.to is required unless useScript is enabled.");
            }

            if (string.IsNullOrWhiteSpace(config.Subject))
            {
                errors.Add($"{label}: stepConfig.subject is required unless useScript is enabled.");
            }
        }

        foreach (var raw in $"{config.To};{config.Cc};{config.Bcc}".Split(new[] { ',', ';' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailAddress.TryCreate(raw, out _))
            {
                warnings.Add($"{label}: '{raw}' does not look like a valid email address.");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ReplyTo) && !MailAddress.TryCreate(config.ReplyTo, out _))
        {
            warnings.Add($"{label}: stepConfig.replyTo does not look like a valid email address.");
        }

        if (config.Subject.Length > 255)
        {
            warnings.Add($"{label}: stepConfig.subject is longer than 255 characters and will be rejected at runtime.");
        }

        if (Encoding.UTF8.GetByteCount(config.Body) > 256 * 1024)
        {
            warnings.Add($"{label}: stepConfig.body is larger than 256 KB and will be rejected at runtime.");
        }

        var nodeNames = new HashSet<string>(
            flow.Nodes.Where(candidate => !string.IsNullOrWhiteSpace(candidate.NodeName))
                .Select(candidate => candidate.NodeName),
            StringComparer.OrdinalIgnoreCase);
        foreach (var template in new[] { config.To, config.Cc, config.Bcc, config.ReplyTo, config.Subject, config.Body })
        {
            LintEmailPlaceholders(label, template, nodeNames, errors, warnings);
        }
    }

    private static void LintEmailPlaceholders(
        string label,
        string? template,
        HashSet<string> nodeNames,
        List<string> errors,
        List<string> warnings)
    {
        if (string.IsNullOrEmpty(template))
        {
            return;
        }

        var index = 0;
        while (index < template.Length)
        {
            if (template[index] == '\\'
                && index + 2 < template.Length
                && template[index + 1] == '{'
                && template[index + 2] == '{')
            {
                index += 3;
                continue;
            }

            if (template[index] == '{' && index + 1 < template.Length && template[index + 1] == '{')
            {
                var close = template.IndexOf("}}", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    errors.Add($"{label}: unbalanced placeholder braces in stepConfig.");
                    return;
                }

                var expression = template.Substring(index + 2, close - index - 2).Trim();
                if (expression.StartsWith("flowState.", StringComparison.Ordinal))
                {
                    var path = expression["flowState.".Length..];
                    var nodeName = path.Split('.')[0].Split('[')[0];
                    if (string.IsNullOrWhiteSpace(nodeName))
                    {
                        errors.Add($"{label}: flowState placeholder '{expression}' needs a node name.");
                    }
                    else if (!nodeNames.Contains(nodeName))
                    {
                        warnings.Add($"{label}: placeholder references unknown node '{nodeName}'.");
                    }
                }
                else if (!expression.StartsWith("persistedState.", StringComparison.Ordinal))
                {
                    errors.Add($"{label}: placeholder '{expression}' must start with flowState. or persistedState.");
                }

                index = close + 2;
                continue;
            }

            if (template[index] == '}' && index + 1 < template.Length && template[index + 1] == '}')
            {
                errors.Add($"{label}: unbalanced placeholder braces in stepConfig.");
                index += 2;
                continue;
            }

            index++;
        }
    }

    private static bool TryParseObject(string label, string json, List<string> errors, out JsonElement config)
    {
        config = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{label}: stepConfig must be a JSON object.");
                return false;
            }
            config = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            errors.Add($"{label}: stepConfig is not valid JSON.");
            return false;
        }
    }

    private static void WarnUnknownKeys(string label, string scope, JsonElement config, IReadOnlyCollection<string> allowed, List<string> warnings)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (var property in config.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
                warnings.Add($"{label}: {scope}.{property.Name} is not a recognized field and will be ignored.");
        }
    }

    private static void CheckIntegerRange(string label, JsonElement config, string key, int min, int max, List<string> warnings)
    {
        if (!config.TryGetProperty(key, out var value)) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            warnings.Add($"{label}: {key} should be an integer between {min} and {max}.");
            return;
        }
        if (number < min || number > max)
            warnings.Add($"{label}: {key} should be between {min} and {max}; the engine will clamp it.");
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
