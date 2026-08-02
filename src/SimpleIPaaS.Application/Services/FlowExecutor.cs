using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text.Json;

namespace SimpleIPaaS.Application.Services;

public class FlowExecutor
{
    private readonly IIntegrationRepository _repository;
    private readonly ITransportEngine _transportEngine;
    private readonly IAdvancedCodeExecutionService _codeExecutionService;
    private readonly IExecutionRepository _executionRepository;
    private readonly ICrossReferenceRepository _crossReferenceRepository;
    private readonly ILogger<FlowExecutor> _logger;

    public FlowExecutor(
        IIntegrationRepository repository,
        ITransportEngine transportEngine,
        IAdvancedCodeExecutionService codeExecutionService,
        IExecutionRepository executionRepository,
        ICrossReferenceRepository crossReferenceRepository,
        ILogger<FlowExecutor> logger)
    {
        _repository = repository;
        _transportEngine = transportEngine;
        _codeExecutionService = codeExecutionService;
        _executionRepository = executionRepository;
        _crossReferenceRepository = crossReferenceRepository;
        _logger = logger;
    }

    public async Task<FlowExecution> ExecuteFlowAsync(Guid flowId, Guid executionId, string? triggerPayload, CancellationToken cancellationToken)
    {
        var flowExecution = await _executionRepository.GetFlowExecutionAsync(executionId)
            ?? throw new InvalidOperationException($"FlowExecution {executionId} not found.");

        flowExecution.Status = ExecutionStatus.InProgress;
        flowExecution.StartedAt = DateTime.UtcNow;
        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);

        _logger.LogInformation("Execution {ExecutionId} started for flow {FlowId} (trigger: {TriggerSource})",
            executionId, flowId, flowExecution.TriggerSource);

        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null)
        {
            flowExecution.Status = ExecutionStatus.Failed;
            flowExecution.ErrorMessage = "Flow not found.";
            flowExecution.CompletedAt = DateTime.UtcNow;
            await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
            _logger.LogWarning("Execution {ExecutionId} failed: flow {FlowId} not found", executionId, flowId);
            return flowExecution;
        }

        if (string.IsNullOrWhiteSpace(flowExecution.FlowName))
        {
            flowExecution.FlowName = flow.Name;
        }

        flowExecution.IntegrationId ??= flow.IntegrationId;

        if (!flow.Nodes.Any())
        {
            flowExecution.Status = ExecutionStatus.Success;
            flowExecution.CompletedAt = DateTime.UtcNow;
            await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
            return flowExecution;
        }

        try
        {
            var startNodes = flow.Nodes.Where(n => !flow.Edges.Any(e => e.TargetNodeId == n.Id)).ToList();
            if (!startNodes.Any()) throw new InvalidOperationException("Could not find a starting node.");

            var sortedNodes = TopologicalSort(flow);

            var flowStateContext = new JObject();
            if (!string.IsNullOrWhiteSpace(triggerPayload))
            {
                flowStateContext["trigger"] = ParseTokenOrString(triggerPayload);
            }

            var persistedStateContext = ParseObjectOrEmpty(flow.PersistedStateJson);
            var activeNodes = new HashSet<Guid>();

            foreach (var startNode in startNodes)
            {
                activeNodes.Add(startNode.Id);
            }

            foreach (var node in sortedNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!activeNodes.Contains(node.Id))
                {
                    continue;
                }

                var stepExecution = new StepExecution
                {
                    FlowExecutionId = flowExecution.Id,
                    StepId = node.Id,
                    NodeName = node.NodeName,
                    Status = ExecutionStatus.InProgress,
                    StartedAt = DateTime.UtcNow
                };
                await _executionRepository.AddStepExecutionAsync(stepExecution);
                flowExecution.TotalRecords++;

                string flowStateJson = flowStateContext.ToString(Newtonsoft.Json.Formatting.None);
                string persistedStateJson = persistedStateContext.ToString(Newtonsoft.Json.Formatting.None);
                string currentPayload = string.Empty;

                _logger.LogInformation("Execution {ExecutionId}: running node {NodeName} ({StepType})",
                    executionId, node.NodeName, node.StepType);

                try
                {
                    if (node.StepType == StepType.HttpAction)
                    {
                        var (statusCode, response, requestPayload) =
                            await ExecuteHttpNodeAsync(node, flowStateJson, persistedStateJson, cancellationToken);

                        stepExecution.RequestPayload = requestPayload;
                        stepExecution.HttpStatusCode = statusCode;
                        stepExecution.ResponsePayload = response;

                        if (statusCode < 200 || statusCode >= 300)
                        {
                            throw new Exception($"HTTP request failed with status code {statusCode}: {response}");
                        }

                        currentPayload = response;

                        if (!string.IsNullOrWhiteSpace(node.PostFlightCode))
                        {
                            currentPayload = await RunScriptAsync(node, "PostFlight",
                                () => _codeExecutionService.ExecutePostFlightAsync(node.PostFlightCode, flowStateJson, persistedStateJson, response));
                        }

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                    else if (node.StepType == StepType.Mapping)
                    {
                        if (!string.IsNullOrWhiteSpace(node.MappingCode))
                        {
                            currentPayload = await RunScriptAsync(node, "Mapping",
                                () => _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson, persistedStateJson));
                            stepExecution.ResponsePayload = currentPayload;
                        }

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                    else if (node.StepType == StepType.Branch)
                    {
                        bool branchResult = false;
                        if (!string.IsNullOrWhiteSpace(node.MappingCode))
                        {
                            branchResult = await RunScriptAsync(node, "Branch",
                                () => _codeExecutionService.ExecuteBranchAsync(node.MappingCode, flowStateJson, persistedStateJson));
                        }

                        stepExecution.ResponsePayload = $"{{\"branchResult\": {branchResult.ToString().ToLower()}}}";
                        currentPayload = stepExecution.ResponsePayload;

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            string port = (edge.SourcePortId ?? string.Empty).ToLower();
                            if ((branchResult && port == "true") || (!branchResult && port == "false"))
                            {
                                activeNodes.Add(edge.TargetNodeId);
                            }
                        }
                    }
                    else if (node.StepType == StepType.Debug)
                    {
                        var inspectedAtUtc = DateTime.UtcNow;
                        stepExecution.ResponsePayload = new JObject
                        {
                            ["flowState"] = flowStateContext.DeepClone(),
                            ["persistedState"] = persistedStateContext.DeepClone()
                        }.ToString(Newtonsoft.Json.Formatting.Indented);
                        currentPayload = new JObject
                        {
                            ["inspectedAtUtc"] = inspectedAtUtc
                        }.ToString(Newtonsoft.Json.Formatting.None);

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                    else if (node.StepType == StepType.PersistedState)
                    {
                        if (!string.IsNullOrWhiteSpace(node.MappingCode))
                        {
                            currentPayload = await RunScriptAsync(node, "PersistedState",
                                () => _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson, persistedStateJson));
                            persistedStateContext = ParseObjectOrEmpty(currentPayload);
                            await _repository.UpdatePersistedStateAsync(flow.Id, persistedStateContext.ToString(Newtonsoft.Json.Formatting.None));
                            stepExecution.ResponsePayload = currentPayload;
                        }

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                    else if (node.StepType == StepType.Schedule)
                    {
                        currentPayload = (flowStateContext["trigger"] ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
                        stepExecution.ResponsePayload = currentPayload;

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                    else if (node.StepType == StepType.CrossReferenceStore)
                    {
                        var config = ReadCrossReferenceConfig(node);
                        var input = ResolveNodeInput(flow, node, flowStateContext);
                        var records = CrossReferenceKeyBuilder.ToRecords(
                            ResolveRecordSource(input, flowStateContext, config.ArrayPath));

                        await _crossReferenceRepository.EnsureListAsync(config.ListName, string.Empty);

                        var entries = records
                            .Select(record => new
                            {
                                Record = record,
                                Key = CrossReferenceKeyBuilder.BuildKey(record, config.KeyPaths)
                            })
                            .Where(candidate => !string.IsNullOrEmpty(candidate.Key))
                            .Select(candidate => new CrossReferenceEntry
                            {
                                ListName = config.ListName,
                                KeyValue = candidate.Key,
                                ValueJson = CrossReferenceKeyBuilder.BuildValueJson(candidate.Record, config.ValuePaths),
                                FlowId = flow.Id
                            })
                            .ToList();

                        var stored = await _crossReferenceRepository.UpsertEntriesAsync(config.ListName, entries);

                        stepExecution.ResponsePayload = new JObject
                        {
                            ["listName"] = config.ListName,
                            ["recordsRead"] = records.Count,
                            ["keysStored"] = stored,
                            ["keysSkipped"] = entries.Count - stored,
                            ["recordsWithoutKey"] = records.Count - entries.Count
                        }.ToString(Newtonsoft.Json.Formatting.None);

                        currentPayload = input.ToString(Newtonsoft.Json.Formatting.None);

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                    else if (node.StepType == StepType.CrossReferenceFilter)
                    {
                        var config = ReadCrossReferenceConfig(node);
                        var input = ResolveNodeInput(flow, node, flowStateContext);
                        var records = CrossReferenceKeyBuilder.ToRecords(
                            ResolveRecordSource(input, flowStateContext, config.ArrayPath));

                        var keyed = records
                            .Select(record => (Record: record, Key: CrossReferenceKeyBuilder.BuildKey(record, config.KeyPaths)))
                            .ToList();

                        var lookupKeys = keyed
                            .Where(candidate => !string.IsNullOrEmpty(candidate.Key))
                            .Select(candidate => candidate.Key)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();

                        var knownKeys = await _crossReferenceRepository.GetExistingKeysAsync(config.ListName, lookupKeys);

                        var emitted = new JArray();
                        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

                        foreach (var candidate in keyed)
                        {
                            if (!string.IsNullOrEmpty(candidate.Key) &&
                                (knownKeys.Contains(candidate.Key) || !seenKeys.Add(candidate.Key)))
                            {
                                continue;
                            }

                            emitted.Add(candidate.Record.DeepClone());
                        }

                        currentPayload = emitted.ToString(Newtonsoft.Json.Formatting.None);
                        stepExecution.ResponsePayload = currentPayload;

                        _logger.LogInformation(
                            "Execution {ExecutionId}: node {NodeName} passed {Passed} of {Total} records from list {ListName}",
                            executionId, node.NodeName, emitted.Count, records.Count, config.ListName);

                        var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                        foreach (var edge in outgoing)
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(node.NodeName))
                    {
                        try
                        {
                            flowStateContext[node.NodeName] = JToken.Parse(string.IsNullOrWhiteSpace(currentPayload) ? "{}" : currentPayload);
                        }
                        catch
                        {
                            flowStateContext[node.NodeName] = currentPayload;
                        }
                    }

                    stepExecution.Status = ExecutionStatus.Success;
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    flowExecution.SuccessRecords++;
                }
                catch (OperationCanceledException)
                {
                    stepExecution.Status = ExecutionStatus.Cancelled;
                    stepExecution.ErrorMessage = "Execution was cancelled.";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await _executionRepository.UpdateStepExecutionAsync(stepExecution);
                    throw;
                }
                catch (Exception ex)
                {
                    stepExecution.Status = ExecutionStatus.Failed;
                    stepExecution.ErrorMessage = ex.Message;
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    flowExecution.FailedRecords++;

                    _logger.LogError(ex, "Execution {ExecutionId}: node {NodeName} failed", executionId, node.NodeName);

                    var deadLetter = new DeadLetterEntry
                    {
                        FlowExecutionId = flowExecution.Id,
                        StepId = node.Id,
                        FlowName = flow.Name,
                        IntegrationName = flowExecution.IntegrationName,
                        NodeName = string.IsNullOrWhiteSpace(node.NodeName) ? node.StepType.ToString() : node.NodeName,
                        Payload = stepExecution.RequestPayload,
                        FlowStateJson = flowStateJson,
                        ErrorMessage = ex.Message,
                        Status = "Pending"
                    };
                    await _executionRepository.AddDeadLetterEntryAsync(deadLetter);
                    await _executionRepository.UpdateStepExecutionAsync(stepExecution);
                    throw;
                }

                await _executionRepository.UpdateStepExecutionAsync(stepExecution);
            }

            flowExecution.Status = ExecutionStatus.Success;
            flowExecution.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Execution {ExecutionId} completed successfully ({TotalRecords} steps)",
                executionId, flowExecution.TotalRecords);
        }
        catch (OperationCanceledException)
        {
            flowExecution.Status = ExecutionStatus.Cancelled;
            flowExecution.ErrorMessage = "Execution was cancelled.";
            flowExecution.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning("Execution {ExecutionId} was cancelled", executionId);
        }
        catch (Exception ex)
        {
            flowExecution.Status = ExecutionStatus.Failed;
            flowExecution.ErrorMessage = ex.Message;
            flowExecution.CompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Execution {ExecutionId} failed", executionId);
        }

        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
        return flowExecution;
    }

    public async Task<(int StatusCode, string Response, string RequestPayload)> ExecuteHttpNodeAsync(
        IntegrationStep node, string flowStateJson, string persistedStateJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var url = node.EndpointUrl;
        if (!string.IsNullOrWhiteSpace(node.UrlCode))
        {
            url = await RunScriptAsync(node, "Url",
                () => _codeExecutionService.ExecuteUrlAsync(node.UrlCode, flowStateJson, persistedStateJson));
        }

        string requestPayload = string.Empty;
        if (!string.IsNullOrWhiteSpace(node.PreFlightCode))
        {
            requestPayload = await RunScriptAsync(node, "PreFlight",
                () => _codeExecutionService.ExecuteMappingAsync(node.PreFlightCode, flowStateJson, persistedStateJson));
        }

        var nodeToDispatch = new IntegrationStep
        {
            EndpointUrl = url,
            HttpMethod = node.HttpMethod,
            AuthType = node.AuthType,
            AuthToken = node.AuthToken,
            AuthUsername = node.AuthUsername,
            AuthPassword = node.AuthPassword,
            AuthConfigJson = node.AuthConfigJson,
            ConnectionId = node.ConnectionId
        };

        var (statusCode, response) = await _transportEngine.DispatchAsync(nodeToDispatch, requestPayload, node.ConnectionId, cancellationToken);
        return (statusCode, response, requestPayload);
    }

    private static async Task<TResult> RunScriptAsync<TResult>(IntegrationStep node, string stage, Func<Task<TResult>> scriptCall)
    {
        try
        {
            return await scriptCall();
        }
        catch (ScriptExecutionException ex)
        {
            throw new ScriptExecutionException(
                $"{stage} script failed on node {node.NodeName}: {ex.Message}", ex.TimedOut, ex);
        }
    }

    private static CrossReferenceStepConfig ReadCrossReferenceConfig(IntegrationStep node)
    {
        var config = CrossReferenceStepConfig.Parse(node.StepConfig);
        var label = string.IsNullOrWhiteSpace(node.NodeName) ? node.StepType.ToString() : node.NodeName;

        if (string.IsNullOrWhiteSpace(config.ListName))
        {
            throw new InvalidOperationException($"Cross-reference node {label} has no list name configured.");
        }

        if (config.KeyPaths.Count == 0)
        {
            throw new InvalidOperationException($"Cross-reference node {label} has no key paths configured.");
        }

        return config;
    }

    private static JToken ResolveNodeInput(IntegrationFlow flow, IntegrationStep node, JObject flowStateContext)
    {
        var upstream = flow.Edges
            .Where(edge => edge.TargetNodeId == node.Id)
            .Select(edge => flow.Nodes.FirstOrDefault(candidate => candidate.Id == edge.SourceNodeId))
            .Where(source => source != null && !string.IsNullOrWhiteSpace(source.NodeName))
            .Select(source => flowStateContext[source!.NodeName])
            .FirstOrDefault(token => token != null && token.Type != JTokenType.Null);

        return upstream ?? flowStateContext["trigger"] ?? flowStateContext;
    }

    private static JToken? ResolveRecordSource(JToken input, JObject flowStateContext, string arrayPath)
    {
        if (string.IsNullOrWhiteSpace(arrayPath))
        {
            return input;
        }

        // A path prefixed with "flowState." resolves against the whole flow-state
        // tree (the map of node name -> output) - the same shape exposed in the
        // Debug / Sample Flow State JSON view - so a cross-reference can reference
        // any upstream node's output by name instead of only the directly
        // connected upstream node's output.
        if (arrayPath.StartsWith("flowState.", StringComparison.Ordinal))
        {
            var treePath = arrayPath.Substring("flowState.".Length);
            return CrossReferenceKeyBuilder.ResolvePath(flowStateContext, treePath)
                ?? CrossReferenceKeyBuilder.ResolvePath(input, treePath);
        }

        return CrossReferenceKeyBuilder.ResolvePath(input, arrayPath)
            ?? CrossReferenceKeyBuilder.ResolvePath(flowStateContext, arrayPath);
    }

    private List<IntegrationStep> TopologicalSort(IntegrationFlow flow)
    {
        var result = new List<IntegrationStep>();
        var visited = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();

        void Visit(IntegrationStep node)
        {
            if (visiting.Contains(node.Id)) throw new InvalidOperationException("Cycle detected in the flow DAG.");
            if (!visited.Contains(node.Id))
            {
                visiting.Add(node.Id);
                var outgoingEdges = flow.Edges.Where(e => e.SourceNodeId == node.Id).ToList();
                foreach (var edge in outgoingEdges)
                {
                    var targetNode = flow.Nodes.First(n => n.Id == edge.TargetNodeId);
                    Visit(targetNode);
                }
                visiting.Remove(node.Id);
                visited.Add(node.Id);
                result.Insert(0, node);
            }
        }

        var startNodes = flow.Nodes.Where(n => !flow.Edges.Any(e => e.TargetNodeId == n.Id)).ToList();
        foreach (var startNode in startNodes)
        {
            Visit(startNode);
        }

        return result;
    }

    private static JToken ParseTokenOrString(string payload)
    {
        try
        {
            return JToken.Parse(payload);
        }
        catch
        {
            return payload;
        }
    }

    private static JObject ParseObjectOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JObject();
        }

        try
        {
            var token = JToken.Parse(json);
            if (token is JObject obj)
            {
                return obj;
            }

            return new JObject
            {
                ["value"] = token
            };
        }
        catch
        {
            return new JObject
            {
                ["value"] = json ?? string.Empty
            };
        }
    }

    private static string GetApiPacketRequestBody(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(stepConfig);
            if (!document.RootElement.TryGetProperty("apiPacket", out var packet) ||
                !packet.TryGetProperty("requestBody", out var body) ||
                body.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return body.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
