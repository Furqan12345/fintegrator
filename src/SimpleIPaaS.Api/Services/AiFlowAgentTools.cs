using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Services;

// Read-only capability surface for the AI flow-building agent. Every tool is
// side-effect free: the agent can inspect vendor docs and deployment data and check
// its own draft, but it can never write, publish, or execute anything.
public sealed class AiFlowAgentTools
{
    internal static readonly HashSet<string> DocumentationHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "shopify.dev", "developer-docs.amazon.com", "docs.stripe.com", "developers.google.com",
        "learn.microsoft.com", "developer.paypal.com", "developer.squareup.com", "woocommerce.com",
        "developer.zendesk.com", "docs.github.com", "api.slack.com", "developers.facebook.com",
        "developers.hubspot.com", "docs.mulesoft.com", "developer.ebay.com", "docs.oracle.com",
        "developer.intuit.com", "developer.atlassian.com", "developers.tiktok.com"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICrossReferenceRepository _crossReferences;
    private readonly IConnectionRepository _connections;
    private readonly IIntegrationRepository _integrations;
    private readonly IFlowTestRepository _flowTests;
    private readonly IExecutionRepository _executions;
    private readonly IIntegrationCatalogRepository _catalog;
    private readonly ILogRepository _logs;
    private readonly ILogger<AiFlowAgentTools> _logger;
    private int _validateDraftCalls;

    public AiFlowAgentTools(IHttpClientFactory httpClientFactory, ICrossReferenceRepository crossReferences,
        IConnectionRepository connections, IIntegrationRepository integrations, IFlowTestRepository flowTests,
        IExecutionRepository executions, IIntegrationCatalogRepository catalog, ILogRepository logs,
        ILogger<AiFlowAgentTools> logger)
    {
        _httpClientFactory = httpClientFactory;
        _crossReferences = crossReferences;
        _connections = connections;
        _integrations = integrations;
        _flowTests = flowTests;
        _executions = executions;
        _catalog = catalog;
        _logs = logs;
        _logger = logger;
    }

    public static JsonArray CreateToolSchemas() => new()
    {
        Schema("search_web", "Search public web results for official vendor API documentation. Returns supported official documentation links only.",
            new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["maxLength"] = 240 } }, new JsonArray("query")),
        Schema("fetch_official_docs", "Fetch readable text from an official supported vendor API documentation page.",
            new JsonObject { ["url"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1500 } }, new JsonArray("url")),
        Schema("list_cross_reference_lists", "List available cross-reference dedupe lists with entry counts. Use before choosing a listName.", null, null),
        Schema("read_cross_reference_entries", "Read sample key/value rows from a cross-reference list so array and key paths match real data.",
            new JsonObject
            {
                ["listName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 120 },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50 }
            }, new JsonArray("listName")),
        Schema("list_connections", "List saved connections (name, base URL, auth type only — credentials are never returned).", null, null),
        Schema("list_integrations", "List integrations (the groups flows belong to) with their descriptions, for organizational context.", null, null),
        Schema("list_flows", "List existing flows with their node types, to reuse proven shapes.", null, null),
        Schema("read_flow", "Read one existing flow's nodes, edges and stepConfig as a worked example. Returns at most 25 nodes.",
            new JsonObject { ["flowId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 } }, new JsonArray("flowId")),
        Schema("read_test_payloads", "Read stored sample inputs and HTTP response bodies from flow test cases — the authoritative shape of vendor payloads.",
            new JsonObject
            {
                ["flowId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10 }
            }, null),
        Schema("list_recent_executions", "List recent flow runs with status, timings and error summary. Use when the user asks why a flow failed or how it behaves in production.",
            new JsonObject
            {
                ["flowId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20 }
            }, null),
        Schema("read_execution", "Read one run's per-node results (status, HTTP code, error) to pinpoint exactly which step failed and why.",
            new JsonObject { ["executionId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 } }, new JsonArray("executionId")),
        Schema("list_dead_letters", "List dead-letter failures (failed node, error, retry state) so troubleshooting covers failed payloads awaiting retry.",
            new JsonObject { ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20 } }, null),
        Schema("read_step_packets", "Read the actual HTTP request and response bodies a node sent and received during a run (truncated, headers omitted). Use when a step fails and you must see what was really sent — for example pagination cursors going to the wrong place.",
            new JsonObject
            {
                ["executionId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 },
                ["nodeName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 8 }
            }, new JsonArray("executionId")),
        Schema("read_step_payload", "Read one node's actual input and output JSON from a run — the authoritative shape for arrayPath/keyPaths later in the flow.",
            new JsonObject
            {
                ["executionId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 },
                ["nodeName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 }
            }, new JsonArray("executionId")),
        Schema("read_dead_letter", "Read one dead-letter entry in full: the failed payload, captured flow state, and retry history — the exact data that failed.",
            new JsonObject { ["deadLetterId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 } }, new JsonArray("deadLetterId")),
        Schema("search_logs", "Search the application log (errors, warnings, events) by text, level, flow or run. Best source for failures outside a single run — scheduler, engine, webhooks.",
            new JsonObject
            {
                ["search"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 },
                ["level"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                ["flowId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 },
                ["flowExecutionId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 },
                ["hours"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 168 },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50 }
            }, null),
        Schema("list_flow_tests", "List a flow's stored test cases (name, purpose, required-for-publish) with each one's latest run result. New drafts should keep these passing.",
            new JsonObject { ["flowId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 } }, new JsonArray("flowId")),
        Schema("list_flow_versions", "List a flow's published version history (version number, date, change note, rollback marker). Use when the user references or compares versions.",
            new JsonObject { ["flowId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 40 } }, new JsonArray("flowId")),
        Schema("validate_draft", "Validate a candidate flow draft JSON before returning it. Returns graph, port, stepConfig field and C# script errors. Fix every error and call again until valid.",
            new JsonObject { ["draftJson"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200_000 } }, new JsonArray("draftJson"))
    };

    public async Task<string> RunAsync(string toolName, string argumentsJson, List<string> sources, CancellationToken cancellationToken)
    {
        JsonDocument arguments;
        try { arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson); }
        catch (JsonException) { return "Tool arguments must be valid JSON."; }
        using (arguments)
        {
            var root = arguments.RootElement;
            switch (toolName)
            {
                case "search_web":
                {
                    var query = GetString(root, "query", 240);
                    return query.Length == 0 ? "A search query is required." : await SearchWebAsync(query, sources, cancellationToken);
                }
                case "fetch_official_docs":
                {
                    var address = GetString(root, "url", 1500);
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IsAllowedDocumentationUri(uri))
                        return "Only HTTPS pages on supported official vendor documentation domains can be fetched.";
                    return await FetchDocumentationAsync(uri, sources, cancellationToken);
                }
                case "list_cross_reference_lists":
                    return await ListCrossReferenceListsAsync();
                case "read_cross_reference_entries":
                    return await ReadCrossReferenceEntriesAsync(GetString(root, "listName", 120), GetInt(root, "limit", 20, 1, 50));
                case "list_connections":
                    return await ListConnectionsAsync();
                case "list_integrations":
                    return await ListIntegrationsAsync();
                case "list_flows":
                    return await ListFlowsAsync();
                case "read_flow":
                    return await ReadFlowAsync(GetString(root, "flowId", 40));
                case "read_test_payloads":
                    return await ReadTestPayloadsAsync(GetString(root, "flowId", 40), GetInt(root, "limit", 5, 1, 10));
                case "list_recent_executions":
                    return await ListRecentExecutionsAsync(GetString(root, "flowId", 40), GetInt(root, "limit", 10, 1, 20));
                case "read_execution":
                    return await ReadExecutionAsync(GetString(root, "executionId", 40));
                case "list_dead_letters":
                    return await ListDeadLettersAsync(GetInt(root, "limit", 10, 1, 20));
                case "read_step_packets":
                    return await ReadStepPacketsAsync(GetString(root, "executionId", 40), GetString(root, "nodeName", 200), GetInt(root, "limit", 4, 1, 8));
                case "read_step_payload":
                    return await ReadStepPayloadAsync(GetString(root, "executionId", 40), GetString(root, "nodeName", 200));
                case "read_dead_letter":
                    return await ReadDeadLetterAsync(GetString(root, "deadLetterId", 40));
                case "search_logs":
                    return await SearchLogsAsync(GetString(root, "search", 200), GetString(root, "level", 20),
                        GetString(root, "flowId", 40), GetString(root, "flowExecutionId", 40),
                        GetInt(root, "hours", 24, 1, 168), GetInt(root, "limit", 20, 1, 50));
                case "list_flow_tests":
                    return await ListFlowTestsAsync(GetString(root, "flowId", 40));
                case "list_flow_versions":
                    return await ListFlowVersionsAsync(GetString(root, "flowId", 40));
                case "validate_draft":
                    if (++_validateDraftCalls > 6)
                        return "Validation keeps failing. Stop calling this tool: return your best draft now and list the remaining known issues in warnings instead.";
                    return ValidateDraft(GetString(root, "draftJson", 200_000));
                default:
                    return "Unknown tool.";
            }
        }
    }

    private async Task<string> ListCrossReferenceListsAsync()
    {
        var lists = await _crossReferences.GetListsAsync();
        if (lists.Count == 0) return "No cross-reference lists exist yet; the draft may create one via a CrossReferenceStore node.";
        return Json(new
        {
            lists = lists.Select(item => new { name = item.List.Name, description = item.List.Description, entryCount = item.EntryCount })
        });
    }

    private async Task<string> ReadCrossReferenceEntriesAsync(string listName, int limit)
    {
        if (string.IsNullOrWhiteSpace(listName)) return "listName is required.";
        var page = await _crossReferences.GetEntriesAsync(listName, null, 1, limit);
        return Json(new
        {
            listName,
            totalCount = page.TotalCount,
            entries = page.Entries.Select(entry => new { keyValue = entry.KeyValue, valueJson = Truncate(entry.ValueJson, 1200) })
        });
    }

    private async Task<string> ListConnectionsAsync()
    {
        var connections = (await _connections.GetAllAsync()).ToList();
        if (connections.Count == 0) return "No saved connections. The draft must use urlMode Manual and explain the auth the user has to configure.";
        return Json(new
        {
            connections = connections.Select(connection => new
            {
                id = connection.Id,
                name = connection.Name,
                baseUrl = connection.BaseUrl,
                authType = connection.AuthType.ToString(),
                status = connection.Status.ToString()
            }),
            note = "Credentials are encrypted and never returned. Set urlMode Connection and connectionId only when the user asks for a specific connection."
        });
    }

    private async Task<string> ListFlowsAsync()
    {
        var flows = (await _integrations.GetAllAsync()).ToList();
        if (flows.Count == 0) return "No existing flows.";
        return Json(new
        {
            flows = flows.Take(40).Select(flow => new
            {
                flowId = flow.Id,
                name = flow.Name,
                status = flow.Status.ToString(),
                triggerType = flow.TriggerType.ToString(),
                nodeTypes = flow.Nodes.Select(node => node.StepType.ToString()).ToList()
            })
        });
    }

    private async Task<string> ReadFlowAsync(string flowId)
    {
        if (!Guid.TryParse(flowId, out var id)) return "flowId must be a GUID.";
        var flow = await _integrations.GetByIdAsync(id);
        if (flow == null) return "No flow with that id.";
        return Json(new
        {
            flow = new
            {
                flowId = flow.Id,
                name = flow.Name,
                description = flow.Description,
                triggerType = flow.TriggerType.ToString(),
                cronExpression = flow.CronExpression,
                allowPostReplay = flow.AllowPostReplay,
                nodes = flow.Nodes.Take(25).Select(node => new
                {
                    node.Id,
                    node.NodeName,
                    stepType = node.StepType.ToString(),
                    node.EndpointUrl,
                    node.HttpMethod,
                    urlMode = node.UrlMode.ToString(),
                    authType = node.AuthType.ToString(),
                    connectionId = node.ConnectionId,
                    mappingCode = Truncate(node.MappingCode, 1200),
                    urlCode = Truncate(node.UrlCode, 800),
                    preFlightCode = Truncate(node.PreFlightCode, 800),
                    postFlightCode = Truncate(node.PostFlightCode, 800),
                    stepConfig = Truncate(node.StepConfig, 3000)
                }),
                edges = flow.Edges.Select(edge => new { edge.SourceNodeId, edge.TargetNodeId, edge.SourcePortId, edge.TargetPortId, edge.Condition, edge.Order })
            },
            note = flow.Nodes.Count > 25 ? "Truncated to the first 25 nodes." : null
        });
    }

    private async Task<string> ReadTestPayloadsAsync(string flowId, int limit)
    {
        IReadOnlyList<FlowTestCase> cases;
        if (!string.IsNullOrWhiteSpace(flowId))
        {
            if (!Guid.TryParse(flowId, out var id)) return "flowId must be a GUID.";
            cases = await _flowTests.GetByFlowIdAsync(id);
        }
        else
        {
            var flows = (await _integrations.GetAllAsync()).Take(10).ToList();
            var collected = new List<FlowTestCase>();
            foreach (var flow in flows) collected.AddRange(await _flowTests.GetByFlowIdAsync(flow.Id));
            cases = collected;
        }

        var samples = new List<object>();
        foreach (var testCase in cases.Take(limit))
        {
            FlowTestDefinition? definition = null;
            try { definition = JsonSerializer.Deserialize<FlowTestDefinition>(testCase.DefinitionJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { }
            samples.Add(new
            {
                testCase = testCase.Name,
                flowId = testCase.FlowId,
                triggerPayloadJson = Truncate(definition?.TriggerPayloadJson ?? string.Empty, 3000),
                httpResponses = (definition?.HttpResponses ?? new Dictionary<string, List<FlowHttpResponseOverride>>())
                    .Take(4)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Take(2).Select(response => new
                    {
                        statusCode = response.StatusCode,
                        responseFormat = response.ResponseFormat,
                        responseBody = Truncate(response.ResponseBody, 3000)
                    }).ToList()),
                crossReferenceRows = (definition?.CrossReferenceRows ?? new List<FlowCrossReferenceFixture>()).Take(5)
                    .Select(row => new { row.ListName, row.KeyValue, valueJson = Truncate(row.ValueJson, 800) })
            });
        }
        if (samples.Count == 0) return "No stored test payloads. Use fetch_official_docs to learn the response shape and state the assumption as a warning.";
        return Json(new { samples });
    }

    private async Task<string> ListRecentExecutionsAsync(string flowIdText, int limit)
    {
        Guid? flowId = null;
        if (!string.IsNullOrWhiteSpace(flowIdText))
        {
            if (!Guid.TryParse(flowIdText, out var parsed)) return "flowId must be a GUID.";
            flowId = parsed;
        }
        var executions = await _executions.GetFlowExecutionsAsync(new ExecutionQuery { FlowId = flowId, PageSize = limit });
        var items = executions.Take(limit).Select(execution => new
        {
            executionId = execution.Id,
            flowId = execution.FlowId,
            flowName = execution.FlowName,
            status = execution.Status.ToString(),
            startedAt = execution.StartedAt,
            completedAt = execution.CompletedAt,
            error = Truncate(execution.ErrorMessage, 300)
        }).ToList();
        if (items.Count == 0) return "No runs recorded yet.";
        return Json(new { executions = items, note = "Call read_execution with an executionId for per-node detail." });
    }

    private async Task<string> ReadExecutionAsync(string executionIdText)
    {
        if (!Guid.TryParse(executionIdText, out var executionId)) return "executionId must be a GUID.";
        var execution = await _executions.GetFlowExecutionAsync(executionId);
        if (execution == null) return "No run exists with that executionId.";
        var steps = await _executions.GetStepExecutionSummariesAsync(executionId);
        return Json(new
        {
            execution = new
            {
                executionId = execution.Id,
                flowId = execution.FlowId,
                flowName = execution.FlowName,
                status = execution.Status.ToString(),
                startedAt = execution.StartedAt,
                completedAt = execution.CompletedAt,
                error = Truncate(execution.ErrorMessage, 500)
            },
            steps = steps.Select(step => new
            {
                nodeName = step.NodeName,
                status = step.Status.ToString(),
                httpStatusCode = step.HttpStatusCode,
                error = Truncate(step.ErrorMessage, 300),
                startedAt = step.StartedAt,
                completedAt = step.CompletedAt
            }).ToList(),
            note = "Fix the failing node's config or script in a new draft. Use list_dead_letters for retry and replay state."
        });
    }

    private async Task<string> ListDeadLettersAsync(int limit)
    {
        var entries = await _executions.GetAllDeadLettersAsync(null);
        var items = entries
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(limit)
            .Select(entry => new
            {
                deadLetterId = entry.Id,
                flowName = entry.FlowName,
                nodeName = entry.NodeName,
                status = entry.Status,
                retryCount = entry.RetryCount,
                error = Truncate(entry.ErrorMessage, 300),
                createdAt = entry.CreatedAt
            })
            .ToList();
        if (items.Count == 0) return "No dead letters — nothing is awaiting retry.";
        return Json(new { deadLetters = items });
    }

    private async Task<string> ReadStepPacketsAsync(string executionIdText, string nodeName, int limit)
    {
        if (!Guid.TryParse(executionIdText, out var executionId)) return "executionId must be a GUID.";
        var summaries = await _executions.GetStepExecutionSummariesAsync(executionId);
        var matches = string.IsNullOrWhiteSpace(nodeName)
            ? summaries.ToList()
            : summaries.Where(step => step.NodeName.Equals(nodeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return "No matching step found in that run.";

        var target = matches.FirstOrDefault(step => !string.IsNullOrWhiteSpace(step.ErrorMessage)) ?? matches[0];
        var packets = await _executions.GetStepPacketLogSummariesAsync(executionId);
        var mine = packets
            .Where(packet => packet.StepExecutionId == target.Id)
            .OrderBy(packet => packet.Sequence)
            .Take(limit)
            .ToList();
        if (mine.Count == 0) return $"Step {target.NodeName} recorded no HTTP requests.";

        var items = new List<object>();
        foreach (var packet in mine)
        {
            var body = await _executions.GetStepPacketBodyAsync(executionId, packet.Id);
            items.Add(new
            {
                page = packet.PageNumber,
                method = packet.HttpMethod,
                url = packet.RequestUrl,
                statusCode = packet.StatusCode,
                requestBody = Truncate(body?.RequestBody, 1500),
                responseBody = Truncate(body?.ResponseBody, 2500)
            });
        }
        return Json(new
        {
            node = target.NodeName,
            status = target.Status.ToString(),
            error = Truncate(target.ErrorMessage, 300),
            packets = items
        });
    }

    private async Task<string> ListIntegrationsAsync()
    {
        var integrations = await _catalog.GetAllAsync();
        var items = integrations.Select(integration => new
        {
            integrationId = integration.Id,
            name = integration.Name,
            description = Truncate(integration.Description, 300)
        }).ToList();
        if (items.Count == 0) return "No integrations exist yet.";
        return Json(new { integrations = items, note = "Flows reference their group with integrationId." });
    }

    private async Task<string> ReadStepPayloadAsync(string executionIdText, string nodeName)
    {
        if (!Guid.TryParse(executionIdText, out var executionId)) return "executionId must be a GUID.";
        if (string.IsNullOrWhiteSpace(nodeName)) return "nodeName is required.";
        var summaries = await _executions.GetStepExecutionSummariesAsync(executionId);
        var target = summaries.FirstOrDefault(step => step.NodeName.Equals(nodeName, StringComparison.OrdinalIgnoreCase));
        if (target == null) return $"No step named '{nodeName}' ran in that execution.";
        var payload = await _executions.GetStepPayloadAsync(executionId, target.Id);
        if (payload == null) return $"Step {nodeName} recorded no payload.";
        return Json(new
        {
            node = target.NodeName,
            status = target.Status.ToString(),
            receivedInput = Truncate(payload.ReceivedInput, 2500),
            requestPayload = Truncate(payload.RequestPayload, 1500),
            responsePayload = Truncate(payload.ResponsePayload, 3000)
        });
    }

    private async Task<string> ReadDeadLetterAsync(string deadLetterIdText)
    {
        if (!Guid.TryParse(deadLetterIdText, out var deadLetterId)) return "deadLetterId must be a GUID.";
        var entry = await _executions.GetDeadLetterAsync(deadLetterId);
        if (entry == null) return "No dead letter exists with that id.";
        return Json(new
        {
            deadLetterId = entry.Id,
            flowName = entry.FlowName,
            nodeName = entry.NodeName,
            status = entry.Status,
            retryCount = entry.RetryCount,
            error = Truncate(entry.ErrorMessage, 500),
            createdAt = entry.CreatedAt,
            payload = Truncate(entry.Payload, 2500),
            flowStateJson = Truncate(entry.FlowStateJson, 2000),
            attemptHistoryJson = Truncate(entry.AttemptHistoryJson, 1500),
            note = "Credentials are excluded from these snapshots."
        });
    }

    private async Task<string> SearchLogsAsync(string search, string level, string flowIdText, string flowExecutionIdText, int hours, int limit)
    {
        Guid? flowId = null;
        if (!string.IsNullOrWhiteSpace(flowIdText))
        {
            if (!Guid.TryParse(flowIdText, out var parsedFlowId)) return "flowId must be a GUID.";
            flowId = parsedFlowId;
        }
        Guid? flowExecutionId = null;
        if (!string.IsNullOrWhiteSpace(flowExecutionIdText))
        {
            if (!Guid.TryParse(flowExecutionIdText, out var parsedExecutionId)) return "flowExecutionId must be a GUID.";
            flowExecutionId = parsedExecutionId;
        }

        var query = new SimpleIPaaS.Application.Models.LogQuery
        {
            MinimumLevel = string.IsNullOrWhiteSpace(level) ? null : level,
            Search = string.IsNullOrWhiteSpace(search) ? null : search,
            FlowId = flowId,
            FlowExecutionId = flowExecutionId,
            SinceUtc = DateTime.UtcNow.AddHours(-hours)
        };
        var page = await _logs.GetPageAsync(query, 1, limit, CancellationToken.None);
        var items = page.Items.Select(entry => new
        {
            timestamp = entry.Timestamp,
            level = entry.Level,
            eventName = entry.EventName,
            message = Truncate(entry.Message, 400),
            exception = Truncate(entry.Exception, 500)
        }).ToList();
        if (items.Count == 0) return "No matching log entries in the selected window.";
        return Json(new { total = page.Total, entries = items, note = $"Showing the latest {items.Count}." });
    }

    private async Task<string> ListFlowTestsAsync(string flowIdText)
    {
        if (!Guid.TryParse(flowIdText, out var flowId)) return "flowId must be a GUID.";
        var cases = await _flowTests.GetByFlowIdAsync(flowId);
        if (cases.Count == 0) return "This flow has no stored test cases.";
        var items = new List<object>();
        foreach (var testCase in cases)
        {
            var latest = await _flowTests.GetLatestRunAsync(testCase.Id);
            items.Add(new
            {
                testCaseId = testCase.Id,
                name = testCase.Name,
                description = Truncate(testCase.Description, 300),
                enabled = testCase.Enabled,
                requiredForPublish = testCase.RequiredForPublish,
                lastRunAt = testCase.LastRunAt,
                lastPassedAt = testCase.LastPassedAt,
                latestRun = latest == null ? null : new
                {
                    status = latest.Status,
                    startedAt = latest.StartedAt,
                    completedAt = latest.CompletedAt,
                    result = Truncate(latest.ResultJson, 1200)
                }
            });
        }
        return Json(new { tests = items, note = "Drafts must keep required-for-publish tests passing." });
    }

    private async Task<string> ListFlowVersionsAsync(string flowIdText)
    {
        if (!Guid.TryParse(flowIdText, out var flowId)) return "flowId must be a GUID.";
        var flow = await _integrations.GetByIdAsync(flowId);
        if (flow == null) return "No flow exists with that id.";
        var versions = await _integrations.GetVersionsAsync(flowId);
        if (versions.Count == 0) return "This flow has never been published.";
        var items = versions
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new
            {
                versionNumber = version.VersionNumber,
                createdAt = version.CreatedAt,
                changeNote = Truncate(version.ChangeNote, 300),
                isCurrent = flow.PublishedVersion == version.VersionNumber,
                rolledBackFromVersion = version.RolledBackFromVersion
            })
            .ToList();
        return Json(new { versions = items, publishedVersion = flow.PublishedVersion });
    }

    private static string ValidateDraft(string draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftJson)) return Json(new { valid = false, errors = new[] { "draftJson is required." } });
        var json = ExtractJson(draftJson);
        if (json == null) return Json(new { valid = false, errors = new[] { "The draft must be one JSON object (optionally wrapped in a code fence)." } });

        IntegrationFlowDto? flow;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var flowElement = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("flow", out var nested) ? nested : root;
            flow = JsonSerializer.Deserialize<IntegrationFlowDto>(flowElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return Json(new { valid = false, errors = new[] { "The draft is not valid JSON." } }); }

        if (flow == null) return Json(new { valid = false, errors = new[] { "The draft did not contain a flow." } });
        flow.Nodes ??= new List<IntegrationStepDto>();
        flow.Edges ??= new List<IntegrationEdgeDto>();

        var errors = FlowValidator.Validate(flow);
        errors.AddRange(FlowDraftChecks.ValidateCanvasEdges(flow));
        var (configErrors, configWarnings) = StepConfigLinter.Lint(flow);
        errors.AddRange(configErrors);
        List<string> warnings;
        try { warnings = FlowDraftChecks.CheckGeneratedScripts(flow); }
        catch (AiFlowBuilderException ex) { return Json(new { valid = false, errors = new[] { ex.Message } }); }
        warnings.InsertRange(0, configWarnings);

        return Json(new { valid = errors.Count == 0, errors = errors.Take(12), warnings });
    }

    private static string? ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && end > firstLine) trimmed = trimmed[(firstLine + 1)..end].Trim();
        }
        var start = trimmed.IndexOf('{');
        var finish = trimmed.LastIndexOf('}');
        return start < 0 || finish < start ? null : trimmed[start..(finish + 1)];
    }

    private async Task<string> SearchWebAsync(string query, List<string> sources, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("AiResearch");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query));
            request.Headers.UserAgent.ParseAdd("fusetor.com-FlowBuilder/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return "Web search is temporarily unavailable.";
            var html = await ReadLimitedAsync(response.Content, 1_000_000, cancellationToken);
            var matches = Regex.Matches(html, "<a[^>]*class=\"[^\"]*result__a[^\"]*\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var results = new List<string>();
            foreach (Match match in matches.Cast<Match>().Take(6))
            {
                var href = WebUtility.HtmlDecode(match.Groups[1].Value);
                var title = CleanHtml(match.Groups[2].Value);
                var address = UnwrapSearchUrl(href);
                if (Uri.TryCreate(address, UriKind.Absolute, out var resultUri) && IsAllowedDocumentationUri(resultUri))
                {
                    var source = $"{title} — {resultUri}";
                    if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase)) sources.Add(source);
                    results.Add(source);
                }
            }
            return results.Count == 0
                ? "No supported official documentation links were found; try a vendor-specific search phrase."
                : string.Join("\n", results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogInformation("Web research search failed ({FailureType}).", ex.GetType().Name);
            return "Web search is temporarily unavailable. Use the built-in guide and mark uncertain API details as warnings.";
        }
    }

    private async Task<string> FetchDocumentationAsync(Uri initialUri, List<string> sources, CancellationToken cancellationToken)
    {
        try
        {
            var uri = initialUri;
            using var client = _httpClientFactory.CreateClient("AiResearch");
            for (var redirects = 0; redirects <= 2; redirects++)
            {
                if (!IsAllowedDocumentationUri(uri)) return "Blocked redirect outside supported official documentation domains.";
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("fusetor.com-FlowBuilder/1.0");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json;q=0.8");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return $"Documentation fetch returned HTTP {(int)response.StatusCode}.";
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!(contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || contentType.Contains("text", StringComparison.OrdinalIgnoreCase) || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)))
                    return "The documentation response was not text or JSON.";
                var text = CleanHtml(await ReadLimitedAsync(response.Content, 1_000_000, cancellationToken));
                if (text.Length > 12_000) text = text[..12_000];
                var source = $"Official documentation — {uri}";
                if (!sources.Contains(source, StringComparer.OrdinalIgnoreCase)) sources.Add(source);
                return $"Source: {uri}\n{text}";
            }
            return "Documentation page redirected too many times.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogInformation("Documentation fetch failed ({FailureType}).", ex.GetType().Name);
            return "Documentation fetch is temporarily unavailable.";
        }
    }

    internal static bool IsAllowedDocumentationUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        DocumentationHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                                       uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    internal static async Task<string> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes) throw new AiFlowBuilderException("Remote response exceeded the allowed size.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > maxBytes) throw new AiFlowBuilderException("Remote response exceeded the allowed size.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    internal static string GetString(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value)) return string.Empty;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
        text = text.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    internal static int GetInt(JsonElement element, string property, int fallback, int min, int max)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt32(out var parsed)) return fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static string UnwrapSearchUrl(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return string.Empty;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == "uddg" && parts.Length == 2)
                return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
        }
        return href;
    }

    private static string CleanHtml(string html)
    {
        html = Regex.Replace(html, "<(script|style|noscript)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(html), @"\s+", " ").Trim();
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "…[truncated]";
    }

    private static string Json(object payload) => JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static JsonObject Schema(string name, string description, JsonObject? properties, JsonArray? required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties ?? new JsonObject(),
                ["required"] = required ?? new JsonArray(),
                ["additionalProperties"] = false
            }
        }
    };
}
