using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Application.Interfaces;
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
    private readonly ILogger<FlowExecutor> _logger;

    public FlowExecutor(
        IIntegrationRepository repository,
        ITransportEngine transportEngine,
        IAdvancedCodeExecutionService codeExecutionService,
        IExecutionRepository executionRepository,
        ILogger<FlowExecutor> logger)
    {
        _repository = repository;
        _transportEngine = transportEngine;
        _codeExecutionService = codeExecutionService;
        _executionRepository = executionRepository;
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
                            currentPayload = await _codeExecutionService.ExecutePostFlightAsync(node.PostFlightCode, flowStateJson, persistedStateJson, response);
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
                            currentPayload = await _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson, persistedStateJson);
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
                            branchResult = await _codeExecutionService.ExecuteBranchAsync(node.MappingCode, flowStateJson, persistedStateJson);
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
                            currentPayload = await _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson, persistedStateJson);
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
                        Payload = stepExecution.RequestPayload,
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
            url = await _codeExecutionService.ExecuteUrlAsync(node.UrlCode, flowStateJson, persistedStateJson);
        }

        string requestPayload = string.Empty;
        if (!string.IsNullOrWhiteSpace(node.PreFlightCode))
        {
            requestPayload = await _codeExecutionService.ExecuteMappingAsync(node.PreFlightCode, flowStateJson, persistedStateJson);
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

        var (statusCode, response) = await _transportEngine.DispatchAsync(nodeToDispatch, requestPayload, node.ConnectionId);
        return (statusCode, response, requestPayload);
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
