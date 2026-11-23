using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;
using Newtonsoft.Json;
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
    private readonly IEmailSender _emailSender;
    private readonly ILogger<FlowExecutor> _logger;
    private static readonly object _executionLock = new();

    public FlowExecutor(
        IIntegrationRepository repository,
        ITransportEngine transportEngine,
        IAdvancedCodeExecutionService codeExecutionService,
        IExecutionRepository executionRepository,
        ICrossReferenceRepository crossReferenceRepository,
        IEmailSender emailSender,
        ILogger<FlowExecutor> logger)
    {
        _repository = repository;
        _transportEngine = transportEngine;
        _codeExecutionService = codeExecutionService;
        _executionRepository = executionRepository;
        _crossReferenceRepository = crossReferenceRepository;
        _emailSender = emailSender;
        _logger = logger;
    }

    private sealed record IterationResult(
        string LastPayload,
        Dictionary<string, JToken> PerNodeOutputs,
        JToken? IterationOutput);

    public sealed record DebugNodeResult(
        Guid ExecutionId,
        Guid StepId,
        Guid? StepExecutionId,
        ExecutionStatus Status,
        long DurationMs,
        string? Input,
        string? Output,
        string? Error,
        bool BranchResult,
        int HttpStatusCode,
        bool ErrorPort);

    // Debug Mode runs exactly one card from the Flow Designer. It deliberately reuses RunStepAsync
    // so the card behaves like it does in a production run, but it differs in two ways: a failure is
    // returned to the caller instead of being dead-lettered (a human is watching), and Persisted State
    // cards do not write their durable value, so exploring a flow never advances its cursor.
    public async Task<DebugNodeResult> ExecuteDebugNodeAsync(
        Guid flowId,
        Guid stepId,
        JObject flowStateContext,
        string? persistedStateJson,
        CancellationToken cancellationToken)
    {
        var flow = await _repository.GetByIdAsync(flowId)
            ?? throw new InvalidOperationException("Flow not found.");
        var node = flow.Nodes.FirstOrDefault(candidate => candidate.Id == stepId)
            ?? throw new InvalidOperationException("The selected node no longer exists in this flow.");

        var persistedStateBox = new PersistedStateRef(ParseObjectOrEmpty(
            string.IsNullOrWhiteSpace(persistedStateJson) ? flow.PersistedStateJson : persistedStateJson));

        var flowExecution = new FlowExecution
        {
            FlowId = flow.Id,
            FlowName = flow.Name,
            IntegrationId = flow.IntegrationId,
            Status = ExecutionStatus.InProgress,
            StartedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
            TriggerSource = "Debug"
        };
        await _executionRepository.AddFlowExecutionAsync(flowExecution);

        var sortedNodes = TopologicalSort(flow);
        var activeNodes = new HashSet<Guid> { node.Id };
        var executedNodes = new HashSet<Guid>();
        var startedAt = Stopwatch.GetTimestamp();
        string? error = null;

        try
        {
            await RunStepAsync(node, flow, flowStateContext, persistedStateBox,
                flowExecution, flowExecution.Id, sortedNodes, activeNodes, executedNodes,
                cancellationToken, null, node.Id, suppressPersistedStateWrite: true, suppressDeadLetter: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var stepExecution = await _executionRepository.GetStepExecutionAsync(flowExecution.Id, node.Id);

        flowExecution.Status = error == null ? ExecutionStatus.Success : ExecutionStatus.Failed;
        flowExecution.ErrorMessage = error;
        flowExecution.CompletedAt = DateTime.UtcNow;
        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);

        return new DebugNodeResult(
            flowExecution.Id,
            node.Id,
            stepExecution?.Id,
            stepExecution?.Status ?? (error == null ? ExecutionStatus.Success : ExecutionStatus.Failed),
            durationMs,
            stepExecution?.ReceivedInput,
            stepExecution?.ResponsePayload,
            error ?? stepExecution?.ErrorMessage,
            ParseBranchResult(stepExecution?.ResponsePayload),
            stepExecution?.HttpStatusCode ?? 0,
            node.StepType == StepType.HttpAction
                && node.HttpErrorMode == HttpErrorMode.Continue
                && stepExecution?.HttpStatusCode is int status && (status < 200 || status >= 300));
    }

    private static bool ParseBranchResult(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            var token = JToken.Parse(payload);
            return token.Type == JTokenType.Boolean
                ? token.Value<bool>()
                : token["branchResult"]?.Value<bool>() ?? false;
        }
        catch
        {
            return string.Equals(payload.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<FlowExecution> ExecuteFlowAsync(Guid flowId, Guid executionId, string? triggerPayload, CancellationToken cancellationToken, FlowTestContext? testContext = null)
    {
        var flowExecution = await _executionRepository.GetFlowExecutionAsync(executionId)
            ?? throw new InvalidOperationException($"FlowExecution {executionId} not found.");

        triggerPayload = testContext?.ResolveTriggerPayload(triggerPayload) ?? triggerPayload;

        using var executionScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExecutionId"] = executionId,
            ["FlowId"] = flowId,
            ["FlowName"] = flowExecution.FlowName,
            ["IntegrationId"] = flowExecution.IntegrationId ?? Guid.Empty,
            ["IntegrationName"] = flowExecution.IntegrationName,
            ["IsTest"] = flowExecution.IsTest,
            ["TestCaseId"] = flowExecution.TestCaseId,
            ["TestRunId"] = flowExecution.TestRunId
        });

        flowExecution.Status = ExecutionStatus.InProgress;
        flowExecution.StartedAt = DateTime.UtcNow;
        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);

        _logger.LogInformation(new EventId(1300, "flow.execution.started"), "Execution {ExecutionId} started for flow {FlowId} (trigger: {TriggerSource})",
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
            _logger.LogInformation(
                "Execution {ExecutionId}: flow '{FlowName}' planned as {NodeCount} node(s) / {EdgeCount} edge(s), starting at [{StartNodes}]",
                executionId, flow.Name, sortedNodes.Count, flow.Edges.Count,
                string.Join(", ", startNodes.Select(n => n.NodeName)));

            var flowStateContext = new JObject();
            if (!string.IsNullOrWhiteSpace(triggerPayload))
            {
                flowStateContext["trigger"] = ParseTokenOrString(triggerPayload);
            }

            var persistedStateBox = new PersistedStateRef(ParseObjectOrEmpty(flow.PersistedStateJson));
            var activeNodes = new HashSet<Guid>();
            var executedNodes = new HashSet<Guid>();

            foreach (var startNode in startNodes)
            {
                activeNodes.Add(startNode.Id);
            }

            foreach (var node in sortedNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!activeNodes.Contains(node.Id) || executedNodes.Contains(node.Id))
                {
                    continue;
                }

                await RunStepAsync(node, flow, flowStateContext, persistedStateBox,
                    flowExecution, executionId, sortedNodes, activeNodes, executedNodes,
                    cancellationToken, testContext);
            }

            flowExecution.Status = ExecutionStatus.Success;
            flowExecution.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation(new EventId(1301, "flow.execution.completed"), "Execution {ExecutionId} completed successfully ({TotalRecords} steps)",
                executionId, flowExecution.TotalRecords);
        }
        catch (OperationCanceledException)
        {
            flowExecution.Status = ExecutionStatus.Cancelled;
            flowExecution.ErrorMessage = "Execution was cancelled.";
            flowExecution.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning(new EventId(1302, "flow.execution.cancelled"), "Execution {ExecutionId} was cancelled", executionId);
        }
        catch (Exception ex)
        {
            flowExecution.Status = ExecutionStatus.Failed;
            flowExecution.ErrorMessage = ex.Message;
            flowExecution.CompletedAt = DateTime.UtcNow;
            _logger.LogError(new EventId(1303, "flow.execution.failed"), ex, "Execution {ExecutionId} failed", executionId);
        }

        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
        return flowExecution;
    }

    public Task<FlowExecution> ExecuteFlowFromNodeAsync(
        Guid flowId,
        Guid executionId,
        Guid startStepId,
        string flowStateJson,
        string persistedStateJson,
        Guid failedStepId,
        CancellationToken cancellationToken) =>
        ExecuteFlowFromNodesCoreAsync(
            flowId, executionId, new[] { startStepId }, flowStateJson, persistedStateJson, failedStepId, cancellationToken);

    public Task<FlowExecution> ExecuteFlowFromNodesAsync(
        Guid flowId,
        Guid executionId,
        IReadOnlyCollection<Guid> startStepIds,
        string flowStateJson,
        string persistedStateJson,
        CancellationToken cancellationToken,
        FlowTestContext? testContext = null) =>
        ExecuteFlowFromNodesCoreAsync(
            flowId, executionId, startStepIds, flowStateJson, persistedStateJson, null, cancellationToken, testContext);

    private async Task<FlowExecution> ExecuteFlowFromNodesCoreAsync(
        Guid flowId,
        Guid executionId,
        IEnumerable<Guid> startStepIds,
        string flowStateJson,
        string persistedStateJson,
        Guid? failedStepId,
        CancellationToken cancellationToken,
        FlowTestContext? testContext = null)
    {
        var flowExecution = await _executionRepository.GetFlowExecutionAsync(executionId)
            ?? throw new InvalidOperationException($"FlowExecution {executionId} not found.");
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null)
        {
            flowExecution.Status = ExecutionStatus.Failed;
            flowExecution.ErrorMessage = "Flow not found.";
            flowExecution.CompletedAt = DateTime.UtcNow;
            await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
            return flowExecution;
        }

        var startNodeIds = startStepIds.Distinct().ToHashSet();
        var missingStartNode = startNodeIds.FirstOrDefault(id => flow.Nodes.All(node => node.Id != id));
        if (missingStartNode != Guid.Empty)
        {
            flowExecution.Status = ExecutionStatus.Failed;
            flowExecution.ErrorMessage = "A selected start node no longer exists in this flow.";
            flowExecution.CompletedAt = DateTime.UtcNow;
            await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
            return flowExecution;
        }

        flowExecution.Status = ExecutionStatus.InProgress;
        flowExecution.CompletedAt = null;
        flowExecution.ErrorMessage = null;
        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);

        try
        {
            var sortedNodes = TopologicalSort(flow);
            var flowStateContext = ParseObjectOrEmpty(flowStateJson);
            var rerunNodeIds = startNodeIds
                .SelectMany(startStepId => FlowGraphAnalysis.GetDownstreamNodeIds(flow, startStepId))
                .ToHashSet();
            foreach (var rerunNode in flow.Nodes.Where(node => rerunNodeIds.Contains(node.Id)))
            {
                if (!string.IsNullOrWhiteSpace(rerunNode.NodeName))
                {
                    flowStateContext.Remove(rerunNode.NodeName);
                }
            }

            var persistedStateBox = new PersistedStateRef(ParseObjectOrEmpty(
                string.IsNullOrWhiteSpace(persistedStateJson) ? flow.PersistedStateJson : persistedStateJson));
            var activeNodes = new HashSet<Guid>(startNodeIds);
            var executedNodes = new HashSet<Guid>();
            foreach (var node in sortedNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!activeNodes.Contains(node.Id) || executedNodes.Contains(node.Id))
                {
                    continue;
                }

                await RunStepAsync(node, flow, flowStateContext, persistedStateBox,
                    flowExecution, executionId, sortedNodes, activeNodes, executedNodes,
                    cancellationToken, testContext, failedStepId);
            }

            flowExecution.Status = ExecutionStatus.Success;
            flowExecution.CompletedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            flowExecution.Status = ExecutionStatus.Cancelled;
            flowExecution.ErrorMessage = "Execution was cancelled.";
            flowExecution.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            flowExecution.Status = ExecutionStatus.Failed;
            flowExecution.ErrorMessage = ex.Message;
            flowExecution.CompletedAt = DateTime.UtcNow;
        }

        await _executionRepository.UpdateFlowExecutionAsync(flowExecution);
        return flowExecution;
    }

    private async Task<string> RunStepAsync(
        IntegrationStep node,
        IntegrationFlow flow,
        JObject flowStateContext,
        PersistedStateRef persistedStateBox,
        FlowExecution flowExecution,
        Guid executionId,
        List<IntegrationStep> sortedNodes,
        HashSet<Guid> activeNodes,
        HashSet<Guid> executedNodes,
        CancellationToken cancellationToken,
        FlowTestContext? testContext,
        Guid? failedStepId = null,
        bool suppressPersistedStateWrite = false,
        bool suppressDeadLetter = false)
    {
        var receivedInput = ResolveNodeInput(flow, node, flowStateContext);
        var stepExecution = new StepExecution
        {
            FlowExecutionId = flowExecution.Id,
            StepId = node.Id,
            NodeName = node.NodeName,
            Status = ExecutionStatus.InProgress,
            StartedAt = DateTime.UtcNow,
            ReceivedInput = receivedInput.ToString(Newtonsoft.Json.Formatting.None)
        };
        await _executionRepository.AddStepExecutionAsync(stepExecution);
        lock (_executionLock) { flowExecution.TotalRecords++; }

        string flowStateJson = flowStateContext.ToString(Newtonsoft.Json.Formatting.None);
        string persistedStateJson = persistedStateBox.Value.ToString(Newtonsoft.Json.Formatting.None);
        string currentPayload = string.Empty;

        // Scope so every line emitted while this node runs (including transport/script
        // logging) carries the node correlation into the database log sink.
        using var nodeScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExecutionId"] = executionId,
            ["FlowId"] = flow.Id,
            ["CardId"] = node.Id,
            ["CardType"] = node.StepType.ToString(),
            ["StepExecutionId"] = stepExecution.Id,
            ["NodeName"] = node.NodeName ?? string.Empty,
            ["IsTest"] = flowExecution.IsTest,
            ["TestCaseId"] = flowExecution.TestCaseId,
            ["TestRunId"] = flowExecution.TestRunId
        });

        using var cardActivity = new Activity($"flow.card:{node.NodeName}");
        cardActivity.SetTag("flow.id", flow.Id);
        cardActivity.SetTag("flow.execution.id", executionId);
        cardActivity.SetTag("card.id", node.Id);
        cardActivity.SetTag("card.type", node.StepType.ToString());
        cardActivity.Start();

        var nodeStartedAt = Stopwatch.GetTimestamp();
        _logger.LogInformation(new EventId(1400, "flow.card.started"), "Execution {ExecutionId}: node {NodeName} ({StepType}) started",
            executionId, node.NodeName, node.StepType);

        try
        {
            if (testContext?.TryGetCardOverride(node.Id, out var cardOverride) == true && node.StepType != StepType.ForEach)
            {
                stepExecution.RequestPayload = receivedInput.ToString(Newtonsoft.Json.Formatting.None);
                stepExecution.HttpStatusCode = cardOverride.StatusCode ?? 200;
                currentPayload = testContext.Resolve(cardOverride.OutputJson);
                stepExecution.ResponsePayload = currentPayload;
                var suppliedError = !string.IsNullOrWhiteSpace(cardOverride.ErrorMessage)
                    || stepExecution.HttpStatusCode < 200 || stepExecution.HttpStatusCode >= 300;
                if (suppliedError && node.StepType == StepType.HttpAction && node.HttpErrorMode == HttpErrorMode.Continue)
                {
                    currentPayload = BuildHttpErrorEnvelope(
                        stepExecution.HttpStatusCode,
                        testContext.Resolve(cardOverride.OutputJson),
                        string.IsNullOrWhiteSpace(cardOverride.ErrorMessage)
                            ? $"Supplied output for card {node.NodeName} returned HTTP status {stepExecution.HttpStatusCode}."
                            : testContext.Resolve(cardOverride.ErrorMessage),
                        new Dictionary<string, string[]>());
                    stepExecution.ResponsePayload = currentPayload;
                    foreach (var edge in flow.Edges.Where(edge => edge.SourceNodeId == node.Id
                                 && (edge.SourcePortId ?? string.Empty).Equals("error", StringComparison.OrdinalIgnoreCase)))
                    {
                        activeNodes.Add(edge.TargetNodeId);
                    }
                }
                else
                {
                    if (suppliedError)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(cardOverride.ErrorMessage)
                            ? $"Supplied output for card {node.NodeName} returned HTTP status {stepExecution.HttpStatusCode}."
                            : testContext.Resolve(cardOverride.ErrorMessage));
                    }

                    if (node.StepType == StepType.Branch)
                    {
                        var branchValue = false;
                        try
                        {
                            var token = JToken.Parse(currentPayload);
                            branchValue = token.Type == JTokenType.Boolean
                                ? token.Value<bool>()
                                : token["branchResult"]?.Value<bool>() ?? false;
                        }
                        catch { branchValue = string.Equals(currentPayload.Trim(), "true", StringComparison.OrdinalIgnoreCase); }
                        foreach (var edge in flow.Edges.Where(e => e.SourceNodeId == node.Id))
                        {
                            var port = (edge.SourcePortId ?? string.Empty).ToLowerInvariant();
                            if ((branchValue && port == "true") || (!branchValue && port == "false")) activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                    else
                    {
                        foreach (var edge in flow.Edges.Where(e => e.SourceNodeId == node.Id
                                     && !(node.StepType == StepType.HttpAction
                                          && (e.SourcePortId ?? string.Empty).Equals("error", StringComparison.OrdinalIgnoreCase))))
                        {
                            activeNodes.Add(edge.TargetNodeId);
                        }
                    }
                }
            }
            else if (node.StepType == StepType.HttpAction)
            {
                var httpResult =
                    await ExecuteHttpNodeAsync(node, flowStateJson, persistedStateJson, cancellationToken, testContext, flow.HelperCode);

                stepExecution.RequestPayload = httpResult.RequestPayload;
                stepExecution.HttpStatusCode = httpResult.StatusCode;
                stepExecution.ResponsePayload = httpResult.Response;

                foreach (var packet in httpResult.Packets)
                {
                    packet.StepExecutionId = stepExecution.Id;
                    await AddStepPacketLogBestEffortAsync(packet);
                }

                if (httpResult.StatusCode < 200 || httpResult.StatusCode >= 300)
                {
                    var errorMessage = httpResult.StatusCode == 0
                        ? $"HTTP request failed before a response was received: {httpResult.Response}"
                        : $"HTTP request failed with status code {httpResult.StatusCode}";
                    if (node.HttpErrorMode == HttpErrorMode.Continue)
                    {
                        currentPayload = BuildHttpErrorEnvelope(
                            httpResult.StatusCode,
                            httpResult.Response,
                            errorMessage,
                            httpResult.ResponseHeaders);
                        stepExecution.ResponsePayload = currentPayload;
                        foreach (var edge in flow.Edges.Where(edge => edge.SourceNodeId == node.Id
                                     && (edge.SourcePortId ?? string.Empty).Equals("error", StringComparison.OrdinalIgnoreCase)))
                        {
                            lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                        }
                    }
                    else
                    {
                        throw new Exception(httpResult.StatusCode == 0
                            ? $"HTTP request failed before a response was received: {httpResult.Response}"
                            : $"HTTP request failed with status code {httpResult.StatusCode}: {httpResult.Response}");
                    }
                }
                else
                {
                    currentPayload = httpResult.Response;

                    if (!string.IsNullOrWhiteSpace(node.PostFlightCode))
                    {
                        currentPayload = await RunScriptAsync(node, "PostFlight",
                            () => _codeExecutionService.ExecutePostFlightAsync(node.PostFlightCode, flowStateJson, persistedStateJson, httpResult.Response, flow.HelperCode));
                    }

                    var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id
                        && !(e.SourcePortId ?? string.Empty).Equals("error", StringComparison.OrdinalIgnoreCase));
                    foreach (var edge in outgoing)
                    {
                        lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                    }
                }
            }
            else if (node.StepType == StepType.Mapping)
            {
                if (!string.IsNullOrWhiteSpace(node.MappingCode))
                {
                    currentPayload = await RunScriptAsync(node, "Mapping",
                        () => _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson, persistedStateJson, flow.HelperCode));
                    stepExecution.ResponsePayload = currentPayload;
                }

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType is StepType.AddIdentifier or StepType.Error or StepType.Success)
            {
                // Annotation cards: the script's string return lands in a transaction-page
                // column (Identifier for AddIdentifier, Message for Error/Success). The
                // last card that runs wins.
                if (!string.IsNullOrWhiteSpace(node.MappingCode))
                {
                    currentPayload = await RunScriptAsync(node, node.StepType.ToString(),
                        () => _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson, persistedStateJson, flow.HelperCode));
                    stepExecution.ResponsePayload = currentPayload;

                    var value = currentPayload?.Trim() ?? string.Empty;
                    if (node.StepType == StepType.AddIdentifier)
                        flowExecution.Identifier = value;
                    else
                        flowExecution.Message = value;
                }

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType == StepType.Branch)
            {
                bool branchResult = false;
                if (!string.IsNullOrWhiteSpace(node.MappingCode))
                {
                    branchResult = await RunScriptAsync(node, "Branch",
                        () => _codeExecutionService.ExecuteBranchAsync(node.MappingCode, flowStateJson, persistedStateJson, flow.HelperCode));
                }

                stepExecution.ResponsePayload = $"{{\"branchResult\": {branchResult.ToString().ToLower()}}}";
                currentPayload = stepExecution.ResponsePayload;

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    string port = (edge.SourcePortId ?? string.Empty).ToLower();
                    if ((branchResult && port == "true") || (!branchResult && port == "false"))
                    {
                        lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                    }
                }
            }
            else if (node.StepType == StepType.Debug)
            {
                var inspectedAtUtc = DateTime.UtcNow;
                stepExecution.ResponsePayload = new JObject
                {
                    ["flowState"] = flowStateContext.DeepClone(),
                    ["persistedState"] = persistedStateBox.Value.DeepClone()
                }.ToString(Newtonsoft.Json.Formatting.Indented);
                currentPayload = new JObject
                {
                    ["inspectedAtUtc"] = inspectedAtUtc
                }.ToString(Newtonsoft.Json.Formatting.None);

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType == StepType.Email)
            {
                // Email is a pass-through node: downstream nodes still receive the same payload.
                currentPayload = receivedInput.ToString(Newtonsoft.Json.Formatting.None);
                var config = EmailStepConfig.Parse(node.StepConfig);

                try
                {
                    var resolved = await ResolveEmailAsync(
                        node, config, flow, flowStateJson, persistedStateJson);

                    if (testContext != null)
                    {
                        // A flow test run must never send mail; it records the resolved preview.
                        var preview = new JObject
                        {
                            ["to"] = new JArray(resolved.To),
                            ["cc"] = new JArray(resolved.Cc),
                            ["bcc"] = new JArray(resolved.Bcc),
                            ["replyTo"] = resolved.ReplyTo ?? string.Empty,
                            ["subject"] = resolved.Subject,
                            ["body"] = resolved.Body,
                            ["isHtml"] = resolved.IsHtml
                        };
                        AddMissingPlaceholders(preview, resolved.MissingPlaceholders);
                        stepExecution.ResponsePayload = preview.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    else
                    {
                        var html = BuildEmailHtml(resolved.Body, resolved.IsHtml);
                        var text = BuildEmailText(resolved.Body, resolved.IsHtml);

                        try
                        {
                            // One real message: CC/BCC/Reply-To are headers, not extra copies.
                            await _emailSender.SendAsync(new EmailMessage(
                                resolved.To,
                                resolved.Cc,
                                resolved.Bcc,
                                resolved.ReplyTo,
                                resolved.Subject,
                                html,
                                text), cancellationToken);
                        }
                        catch (InvalidOperationException ex)
                            when (ex.Message.Contains("Email:Smtp:Host", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("SMTP is not configured (Email:Smtp:Host).", ex);
                        }

                        var summary = new JObject
                        {
                            ["sentTo"] = new JArray(resolved.To),
                            ["cc"] = new JArray(resolved.Cc),
                            ["bcc"] = resolved.Bcc.Count,
                            ["replyTo"] = resolved.ReplyTo ?? string.Empty,
                            ["subject"] = resolved.Subject,
                            ["sentAt"] = DateTime.UtcNow.ToString("O")
                        };
                        AddMissingPlaceholders(summary, resolved.MissingPlaceholders);
                        if (resolved.MissingPlaceholders.Count > 0)
                        {
                            _logger.LogWarning(
                                "Execution {ExecutionId}: email node {NodeName} sent with unresolved placeholders: {Placeholders}",
                                executionId, node.NodeName, string.Join(", ", resolved.MissingPlaceholders));
                        }

                        stepExecution.ResponsePayload = summary.ToString(Newtonsoft.Json.Formatting.None);
                    }
                }
                catch (Exception ex) when (config.ContinueOnError && ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Execution {ExecutionId}: email node {NodeName} failed; continueOnError is set, so the flow continues.",
                        executionId, node.NodeName);
                    stepExecution.ResponsePayload = new JObject
                    {
                        ["error"] = ex.Message
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType == StepType.PersistedState)
            {
                if (!string.IsNullOrWhiteSpace(node.MappingCode))
                {
                    currentPayload = await RunScriptAsync(node, "PersistedState",
                        () => _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson, persistedStateJson, flow.HelperCode));
                    persistedStateBox.Value = ParseObjectOrEmpty(currentPayload);
                    if (testContext == null && !suppressPersistedStateWrite)
                    {
                        await _repository.UpdatePersistedStateAsync(flow.Id, persistedStateBox.Value.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    stepExecution.ResponsePayload = currentPayload;
                }

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType == StepType.Schedule)
            {
                currentPayload = (flowStateContext["trigger"] ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
                stepExecution.ResponsePayload = currentPayload;

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType == StepType.CrossReferenceStore)
            {
                var config = ReadCrossReferenceConfig(node);
                var input = ResolveNodeInput(flow, node, flowStateContext);
                var records = CrossReferenceKeyBuilder.ToRecords(
                    ResolveRecordSourcePaths(input, flowStateContext, config.ArrayPath));

                if (testContext == null)
                {
                    await _crossReferenceRepository.EnsureListAsync(config.ListName, string.Empty);
                }

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

                var stored = testContext == null
                    ? await _crossReferenceRepository.UpsertEntriesAsync(config.ListName, entries)
                    : entries.Count;
                testContext?.StoreCrossReferenceKeys(config.ListName, entries.Select(entry => entry.KeyValue));

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
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType == StepType.CrossReferenceFilter)
            {
                var config = ReadCrossReferenceConfig(node);
                var input = ResolveNodeInput(flow, node, flowStateContext);
                var records = CrossReferenceKeyBuilder.ToRecords(
                    ResolveRecordSourcePaths(input, flowStateContext, config.ArrayPath));
                var filterKnown = config.FilterMode == CrossReferenceFilterMode.KeepExisting;

                var keyed = records
                    .Select(record => (Record: record, Key: CrossReferenceKeyBuilder.BuildKey(record, config.KeyPaths)))
                    .ToList();

                var lookupKeys = keyed
                    .Where(candidate => !string.IsNullOrEmpty(candidate.Key))
                    .Select(candidate => candidate.Key)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var knownKeys = testContext == null
                    ? await _crossReferenceRepository.GetExistingKeysAsync(config.ListName, lookupKeys)
                    : lookupKeys.Where(key => testContext.ContainsCrossReferenceKey(config.ListName, key)).ToArray();

                var knownKeySet = new HashSet<string>(knownKeys, StringComparer.Ordinal);
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);

                var emitted = new JArray();
                var passedCount = 0;

                foreach (var candidate in keyed)
                {
                    if (string.IsNullOrEmpty(candidate.Key))
                    {
                        continue;
                    }

                    var isKnown = knownKeySet.Contains(candidate.Key);
                    var isDuplicate = !seenKeys.Add(candidate.Key);

                    var passes = filterKnown ? isKnown : !isKnown;
                    if (!passes || isDuplicate)
                    {
                        continue;
                    }

                    emitted.Add(candidate.Record.DeepClone());
                    passedCount++;
                }

                if (CrossReferenceKeyBuilder.ContainsWildcard(config.ArrayPath))
                {
                    var (sourceRoot, actualPath) = ResolveFilterSource(input, flowStateContext, config.ArrayPath);
                    var filtered = CrossReferenceKeyBuilder.FilterArrayPreservingStructure(
                        sourceRoot, actualPath, knownKeySet, config.KeyPaths, filterKnown);
                    currentPayload = filtered?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
                }
                else
                {
                    currentPayload = emitted.ToString(Newtonsoft.Json.Formatting.None);
                }

                stepExecution.ResponsePayload = currentPayload;

                _logger.LogInformation(
                    "Execution {ExecutionId}: node {NodeName} ({FilterMode}) passed {Passed} of {Total} records from list {ListName}",
                    executionId, node.NodeName, config.FilterMode, passedCount, records.Count, config.ListName);

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
                }
            }
            else if (node.StepType == StepType.FanOut)
            {
                if (testContext != null)
                {
                    throw new InvalidOperationException(
                        "Fan Out creates durable child transactions and cannot enqueue them during an isolated test. Supply a test output for this card to test downstream nodes.");
                }

                var config = FanOutStepConfig.Parse(node.StepConfig);
                if (string.IsNullOrWhiteSpace(config.ArrayPath))
                {
                    throw new InvalidOperationException($"Fan Out node {node.NodeName} has no array path configured.");
                }

                var input = ResolveNodeInput(flow, node, flowStateContext);
                var (arrayRoot, arrayActualPath) = ResolveArraySource(input, flowStateContext, config.ArrayPath);
                var resolvedArray = CrossReferenceKeyBuilder.ResolvePath(arrayRoot, arrayActualPath);
                if (resolvedArray is not JArray arrayElements)
                {
                    throw new InvalidOperationException(
                        $"Fan Out node {node.NodeName} path '{config.ArrayPath}' did not resolve to a JSON array.");
                }

                if (arrayElements.Count > config.MaxItems)
                {
                    throw new InvalidOperationException(
                        $"Fan Out node {node.NodeName} found {arrayElements.Count} items, exceeding its limit of {config.MaxItems}.");
                }

                var startStepIds = flow.Edges
                    .Where(edge => edge.SourceNodeId == node.Id)
                    .OrderBy(edge => edge.Order)
                    .Select(edge => edge.TargetNodeId)
                    .Distinct()
                    .ToList();
                var queuedAt = DateTime.UtcNow;
                var children = new List<FlowExecution>(arrayElements.Count);
                for (var index = 0; index < arrayElements.Count; index++)
                {
                    var item = arrayElements[index].DeepClone();
                    var itemJson = item.ToString(Newtonsoft.Json.Formatting.None);
                    var itemState = (JObject)flowStateContext.DeepClone();
                    itemState[config.ItemVariable] = item.DeepClone();
                    if (!string.IsNullOrWhiteSpace(node.NodeName))
                    {
                        itemState[node.NodeName] = item.DeepClone();
                    }

                    children.Add(new FlowExecution
                    {
                        FlowId = flow.Id,
                        FlowName = flow.Name,
                        IntegrationId = flowExecution.IntegrationId ?? flow.IntegrationId,
                        IntegrationName = flowExecution.IntegrationName,
                        TraceParent = flowExecution.TraceParent,
                        Status = ExecutionStatus.Queued,
                        StartedAt = queuedAt,
                        QueuedAt = queuedAt,
                        TriggerSource = "FanOut",
                        TriggerPayloadJson = itemJson,
                        ParentExecutionId = flowExecution.Id,
                        FanOutNodeId = node.Id,
                        FanOutNodeName = node.NodeName ?? string.Empty,
                        FanOutItemIndex = index + 1,
                        FanOutItemCount = arrayElements.Count,
                        FanOutItemJson = itemJson,
                        StartStepIdsJson = JsonConvert.SerializeObject(startStepIds),
                        InitialFlowStateJson = itemState.ToString(Newtonsoft.Json.Formatting.None),
                        InitialPersistedStateJson = persistedStateBox.Value.ToString(Newtonsoft.Json.Formatting.None)
                    });
                }

                stepExecution.RequestPayload = input.ToString(Newtonsoft.Json.Formatting.None);
                await _executionRepository.AddFlowExecutionsAsync(children);
                currentPayload = new JObject
                {
                    ["itemCount"] = children.Count,
                    ["queuedTransactions"] = children.Count,
                    ["itemVariable"] = config.ItemVariable
                }.ToString(Newtonsoft.Json.Formatting.None);
                stepExecution.ResponsePayload = currentPayload;

                _logger.LogInformation(
                    "Execution {ExecutionId}: Fan Out node {NodeName} queued {ItemCount} item transaction(s)",
                    executionId, node.NodeName, children.Count);
            }
            else if (node.StepType == StepType.ForEach)
            {
                var config = ForEachStepConfig.Parse(node.StepConfig);

                if (string.IsNullOrWhiteSpace(config.ArrayPath))
                {
                    throw new InvalidOperationException(
                        $"ForEach node {node.NodeName} has no array path configured.");
                }

                var input = testContext != null && testContext.TryGetCardOverride(node.Id, out var foreachOverride)
                    ? ParseTokenOrString(testContext.Resolve(foreachOverride.OutputJson))
                    : ResolveNodeInput(flow, node, flowStateContext);
                var (arrayRoot, arrayActualPath) = ResolveArraySource(input, flowStateContext, config.ArrayPath);
                var arrayElements = CrossReferenceKeyBuilder.ResolvePath(arrayRoot, arrayActualPath)
                    ?.ToArray() ?? Array.Empty<JToken>();

                var subgraphNodeIds = FlowGraphAnalysis.GetForEachBodyNodeIds(flow, node.Id);
                var subgraphNodes = sortedNodes
                    .Where(candidate => subgraphNodeIds.Contains(candidate.Id))
                    .ToList();

                executedNodes.UnionWith(subgraphNodeIds);

                var perNodeAccumulators = new Dictionary<string, JArray>();
                var combinedOutput = new JArray();

                if (arrayElements.Length > 0 && subgraphNodes.Any())
                {
                    if (config.Parallel)
                    {
                        var parallelOptions = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = config.MaxDegreeOfParallelism,
                            CancellationToken = cancellationToken
                        };

                        var perIterationResults = new IterationResult[arrayElements.Length];

                        await Parallel.ForEachAsync(
                            arrayElements.Select((element, index) => (Element: element, Index: index)),
                            parallelOptions,
                            async (item, ct) =>
                            {
                                var iterationState = (JObject)flowStateContext.DeepClone();
                                iterationState[config.ItemVariable] = item.Element;

                                string lastPayload = string.Empty;
                                var perNodeOutputs = new Dictionary<string, JToken>();

                                foreach (var subNode in subgraphNodes)
                                {
                                    lastPayload = await RunStepAsync(
                                        subNode, flow, iterationState, persistedStateBox,
                                        flowExecution, executionId, sortedNodes,
                                        activeNodes, executedNodes, ct, testContext, failedStepId);

                                    if (!string.IsNullOrWhiteSpace(subNode.NodeName))
                                    {
                                        var perIter = iterationState[subNode.NodeName];
                                        if ((perIter == null || perIter.Type == JTokenType.Null) &&
                                            !string.IsNullOrWhiteSpace(lastPayload))
                                        {
                                            perIter = JToken.Parse(lastPayload);
                                        }

                                        if (perIter != null && perIter.Type != JTokenType.Null)
                                        {
                                            perNodeOutputs[subNode.NodeName] = perIter.DeepClone();
                                        }
                                    }
                                }

                                var lastSubNode = subgraphNodes.Last();
                                JToken? iterationOutput = null;
                                if (!string.IsNullOrWhiteSpace(lastSubNode.NodeName) &&
                                    perNodeOutputs.TryGetValue(lastSubNode.NodeName, out var stored))
                                {
                                    iterationOutput = stored.DeepClone();
                                }
                                else if (!string.IsNullOrWhiteSpace(lastPayload))
                                {
                                    iterationOutput = JToken.Parse(lastPayload);
                                }

                                perIterationResults[item.Index] = new IterationResult(lastPayload, perNodeOutputs, iterationOutput);
                            });

                        foreach (var result in perIterationResults)
                        {
                            foreach (var kv in result.PerNodeOutputs)
                            {
                                if (!perNodeAccumulators.TryGetValue(kv.Key, out var arr))
                                {
                                    perNodeAccumulators[kv.Key] = arr = new JArray();
                                }
                                arr.Add(kv.Value);
                            }

                            if (result.IterationOutput != null)
                            {
                                combinedOutput.Add(result.IterationOutput);
                            }
                        }
                    }
                    else
                    {
                        foreach (var element in arrayElements)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var iterationState = (JObject)flowStateContext.DeepClone();
                            iterationState[config.ItemVariable] = element;

                            string lastPayload = string.Empty;

                            foreach (var subNode in subgraphNodes)
                            {
                                lastPayload = await RunStepAsync(
                                    subNode, flow, iterationState, persistedStateBox,
                                    flowExecution, executionId, sortedNodes,
                                    activeNodes, executedNodes, cancellationToken, testContext, failedStepId);

                                if (!string.IsNullOrWhiteSpace(subNode.NodeName))
                                {
                                    var perIter = iterationState[subNode.NodeName];
                                    if ((perIter == null || perIter.Type == JTokenType.Null) &&
                                        !string.IsNullOrWhiteSpace(lastPayload))
                                    {
                                        perIter = JToken.Parse(lastPayload);
                                    }

                                    if (perIter != null && perIter.Type != JTokenType.Null)
                                    {
                                        if (!perNodeAccumulators.TryGetValue(subNode.NodeName, out var arr))
                                        {
                                            perNodeAccumulators[subNode.NodeName] = arr = new JArray();
                                        }

                                        arr.Add(perIter.DeepClone());
                                    }
                                }
                            }

                            var lastSubNode = subgraphNodes.Last();
                            if (!string.IsNullOrWhiteSpace(lastSubNode.NodeName) &&
                                iterationState.TryGetValue(lastSubNode.NodeName, out var stored))
                            {
                                combinedOutput.Add(stored.DeepClone());
                            }
                            else if (!string.IsNullOrWhiteSpace(lastPayload))
                            {
                                combinedOutput.Add(JToken.Parse(lastPayload));
                            }
                        }
                    }
                }
                else if (arrayElements.Length > 0 && !subgraphNodes.Any())
                {
                    foreach (var element in arrayElements)
                    {
                        combinedOutput.Add(element.DeepClone());
                    }
                }

                currentPayload = combinedOutput.ToString(Newtonsoft.Json.Formatting.None);

                foreach (var kv in perNodeAccumulators)
                {
                    flowStateContext[kv.Key] = kv.Value;
                }

                stepExecution.ResponsePayload = currentPayload;

                _logger.LogInformation(new EventId(1404, "flow.loop.completed"),
                    "Execution {ExecutionId}: ForEach node {NodeName} iterated {Count} items",
                    executionId, node.NodeName, arrayElements.Length);

                var outgoing = flow.Edges.Where(e => e.SourceNodeId == node.Id);
                foreach (var edge in outgoing)
                {
                    lock (_executionLock) { activeNodes.Add(edge.TargetNodeId); }
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
            lock (_executionLock) { flowExecution.SuccessRecords++; }

            _logger.LogInformation(new EventId(1401, "flow.card.completed"),
                "Execution {ExecutionId}: node {NodeName} finished with status {Status} in {DurationMs}ms (HTTP {HttpStatusCode}, {ResponseBytes} bytes out)",
                executionId, node.NodeName, ExecutionStatus.Success,
                (long)Stopwatch.GetElapsedTime(nodeStartedAt).TotalMilliseconds,
                stepExecution.HttpStatusCode, stepExecution.ResponsePayload?.Length ?? 0);
        }
        catch (OperationCanceledException)
        {
            stepExecution.Status = ExecutionStatus.Cancelled;
            stepExecution.ErrorMessage = "Execution was cancelled.";
            stepExecution.CompletedAt = DateTime.UtcNow;
            await _executionRepository.UpdateStepExecutionAsync(stepExecution);

            _logger.LogWarning(new EventId(1402, "flow.card.cancelled"),
                "Execution {ExecutionId}: node {NodeName} cancelled after {DurationMs}ms",
                executionId, node.NodeName, (long)Stopwatch.GetElapsedTime(nodeStartedAt).TotalMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            stepExecution.Status = ExecutionStatus.Failed;
            stepExecution.ErrorMessage = ex.Message;
            stepExecution.CompletedAt = DateTime.UtcNow;
            lock (_executionLock) { flowExecution.FailedRecords++; }

            _logger.LogError(new EventId(1403, "flow.card.failed"), ex,
                suppressDeadLetter
                    ? "Execution {ExecutionId}: node {NodeName} failed after {DurationMs}ms — not dead-lettering (debug run)"
                    : "Execution {ExecutionId}: node {NodeName} failed after {DurationMs}ms — dead-lettering",
                executionId, node.NodeName, (long)Stopwatch.GetElapsedTime(nodeStartedAt).TotalMilliseconds);

            if (node.Id != failedStepId && !suppressDeadLetter && testContext == null)
            {
                var deadLetter = new DeadLetterEntry
                {
                    FlowExecutionId = flowExecution.Id,
                    StepId = node.Id,
                    FlowName = flow.Name,
                    IntegrationName = flowExecution.IntegrationName,
                    NodeName = string.IsNullOrWhiteSpace(node.NodeName) ? node.StepType.ToString() : node.NodeName,
                    Payload = string.IsNullOrWhiteSpace(stepExecution.RequestPayload) ? stepExecution.ReceivedInput : stepExecution.RequestPayload,
                    FlowStateJson = flowStateJson,
                    PersistedStateJson = persistedStateJson,
                    ErrorMessage = ex.Message,
                    Status = "Pending"
                };
                // The original failure is attempt 0 so the root cause is never lost,
                // even after later replay attempts append their own errors.
                deadLetter.AttemptHistoryJson = System.Text.Json.JsonSerializer.Serialize(new List<DeadLetterAttempt>
                {
                    new()
                    {
                        Attempt = 0,
                        At = DateTime.UtcNow,
                        Error = ex.Message,
                        NodeName = deadLetter.NodeName,
                        Kind = "Failure"
                    }
                });
                try
                {
                    await _executionRepository.AddDeadLetterEntryAsync(deadLetter);
                }
                catch (Exception deadLetterException)
                {
                    // Never let a secondary persistence failure replace the operation
                    // failure that the run needs to report.
                    _logger.LogError(new EventId(1406, "flow.deadletter.failed"), deadLetterException,
                        "Execution {ExecutionId}: failed to persist dead letter for node {NodeName}",
                        executionId, deadLetter.NodeName);
                }
            }
            try
            {
                await _executionRepository.UpdateStepExecutionAsync(stepExecution);
            }
            catch (Exception updateException)
            {
                _logger.LogError(new EventId(1407, "flow.stepexecution.failed"), updateException,
                    "Execution {ExecutionId}: failed to update failed step {StepExecutionId}",
                    executionId, stepExecution.Id);
            }
            throw;
        }

        if (node.StepType != StepType.HttpAction)
        {
            await AddStepPacketLogBestEffortAsync(new StepPacketLog
            {
                StepExecutionId = stepExecution.Id,
                Sequence = 1,
                Kind = node.StepType.ToString(),
                RequestBody = stepExecution.ReceivedInput,
                // Email keeps the input as its pass-through output, so the audit packet uses the
                // small sent-summary/preview instead of the payload that flows downstream.
                ResponseBody = node.StepType == StepType.Email
                    ? stepExecution.ResponsePayload ?? string.Empty
                    : currentPayload ?? string.Empty,
                StatusCode = stepExecution.HttpStatusCode == 0 ? null : stepExecution.HttpStatusCode,
                StartedAt = stepExecution.StartedAt,
                CompletedAt = stepExecution.CompletedAt,
                DurationMs = stepExecution.CompletedAt.HasValue
                    ? (long)(stepExecution.CompletedAt.Value - stepExecution.StartedAt).TotalMilliseconds
                    : null
            });
        }
        await _executionRepository.UpdateStepExecutionAsync(stepExecution);
        return currentPayload ?? string.Empty;
    }

    // Packet logs are audit telemetry. They must never decide whether a flow step
    // succeeded, failed, or produced a dead letter; an Elasticsearch outage used to
    // turn an otherwise successful node into a failed execution with no dead letter.
    private async Task AddStepPacketLogBestEffortAsync(StepPacketLog packet)
    {
        try
        {
            await _executionRepository.AddStepPacketLogAsync(packet);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(new EventId(1405, "flow.packetlog.failed"), ex,
                "Failed to persist {Kind} packet log {Sequence} for step execution {StepExecutionId}; continuing without telemetry",
                packet.Kind, packet.Sequence, packet.StepExecutionId);
        }
    }

    public sealed record HttpExecutionResult(
        int StatusCode,
        string Response,
        string RequestPayload,
        IReadOnlyList<StepPacketLog> Packets,
        IReadOnlyDictionary<string, string[]> ResponseHeaders);

    public async Task<HttpExecutionResult> ExecuteHttpNodeAsync(
        IntegrationStep node, string flowStateJson, string persistedStateJson, CancellationToken cancellationToken,
        FlowTestContext? testContext = null, string? helperCode = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var url = node.EndpointUrl;
        if (!string.IsNullOrWhiteSpace(node.UrlCode))
        {
            url = await RunScriptAsync(node, "Url",
                () => _codeExecutionService.ExecuteUrlAsync(node.UrlCode, flowStateJson, persistedStateJson, helperCode));
        }

        string requestPayload = string.Empty;
        if (!string.IsNullOrWhiteSpace(node.PreFlightCode))
        {
            requestPayload = await RunScriptAsync(node, "PreFlight",
                () => _codeExecutionService.ExecuteMappingAsync(node.PreFlightCode, flowStateJson, persistedStateJson, helperCode));
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
            ConnectionId = node.ConnectionId,
            Id = node.Id,
            StepConfig = node.StepConfig
        };

        var pagination = PaginationStepConfig.Parse(node.StepConfig);
        var pageResponses = new List<string>();
        var packets = new List<StepPacketLog>();
        var nextUrl = url;
        var nextBody = requestPayload;
        var requestBodySeed = ReadApiPacketRequestBody(nodeToDispatch.StepConfig);
        var lastDispatchedBody = requestPayload;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var statusCode = 0;
        IReadOnlyDictionary<string, string[]> lastResponseHeaders = new Dictionary<string, string[]>();

        for (var page = 0; page < (pagination.Enabled ? pagination.MaxPages : 1); page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodeToDispatch.EndpointUrl = nextUrl;
            var pageBody = string.IsNullOrWhiteSpace(nextBody) ? requestBodySeed : nextBody;
            lastDispatchedBody = pageBody;
            var transportResponse = await _transportEngine.DispatchAsync(nodeToDispatch, pageBody, node.ConnectionId, cancellationToken, testContext);
            statusCode = transportResponse.StatusCode;
            lastResponseHeaders = transportResponse.Headers;
            foreach (var attempt in transportResponse.Attempts)
            {
                packets.Add(new StepPacketLog
                {
                    Sequence = packets.Count + 1,
                    Kind = "HTTP",
                    PageNumber = page + 1,
                    Attempt = attempt == transportResponse.Attempts[^1] ? 1 : 0,
                    HttpMethod = attempt.HttpMethod,
                    RequestUrl = attempt.RequestUrl,
                    StatusCode = attempt.StatusCode == 0 ? null : attempt.StatusCode,
                    RequestHeadersJson = JsonConvert.SerializeObject(attempt.RequestHeaders),
                    RequestBody = attempt.RequestBody,
                    ResponseHeadersJson = JsonConvert.SerializeObject(attempt.ResponseHeaders),
                    ResponseBody = attempt.Response,
                    Error = attempt.Error,
                    StartedAt = attempt.StartedAt,
                    CompletedAt = attempt.CompletedAt,
                    DurationMs = (long)(attempt.CompletedAt - attempt.StartedAt).TotalMilliseconds
                });
            }
            if (statusCode < 200 || statusCode >= 300)
            {
                return new HttpExecutionResult(statusCode, transportResponse.Response, lastDispatchedBody, packets, transportResponse.Headers);
            }

            pageResponses.Add(transportResponse.Response);

            if (!pagination.Enabled)
            {
                return new HttpExecutionResult(statusCode, transportResponse.Response, lastDispatchedBody, packets, transportResponse.Headers);
            }

            var pageJson = TryParseJson(transportResponse.Response);
            var next = ResolveNextPage(pagination, nextUrl, pageBody, pageJson, transportResponse.Headers, page);
            if (next == null)
            {
                return new HttpExecutionResult(statusCode, AggregatePages(pageResponses, pagination), lastDispatchedBody, packets, transportResponse.Headers);
            }

            if (!seenCursors.Add(next.Continuation))
            {
                throw new InvalidOperationException(
                    $"Pagination on node {node.NodeName} repeated the same continuation value '{Shorten(next.Continuation, 80)}'. " +
                    $"{pagination.Style} reads it from '{next.SourcePath}' and sends it as '{next.Target}'. " +
                    "Check that the source path points at the API's advancing token and that the target matches how the API expects the token — " +
                    "a query parameter name, or a JSON path inside the request body (for example variables.cursor for GraphQL).");
            }

            nextUrl = next.Url;
            nextBody = next.Body;
            if (pagination.DelayMs > 0)
            {
                await Task.Delay(pagination.DelayMs, cancellationToken);
            }
        }

        _logger.LogWarning(
            "Pagination on node {NodeName} stopped at the configured limit of {MaxPages} page(s); the API still had more pages available.",
            node.NodeName, pagination.MaxPages);

        return new HttpExecutionResult(statusCode, AggregatePages(pageResponses, pagination), lastDispatchedBody, packets, lastResponseHeaders);
    }

    private static string BuildHttpErrorEnvelope(
        int statusCode,
        string responseBody,
        string error,
        IReadOnlyDictionary<string, string[]> headers)
    {
        JToken body;
        try
        {
            body = string.IsNullOrWhiteSpace(responseBody) ? JValue.CreateNull() : JToken.Parse(responseBody);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            body = new JValue(responseBody);
        }

        var safeHeaders = new JObject();
        foreach (var header in headers)
        {
            if (IsSensitiveHeader(header.Key)) continue;
            safeHeaders[header.Key] = new JArray(header.Value);
        }

        return new JObject
        {
            ["ok"] = false,
            ["statusCode"] = statusCode,
            ["error"] = error,
            ["body"] = body,
            ["headers"] = safeHeaders
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static bool IsSensitiveHeader(string name) =>
        name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("api-key", StringComparison.OrdinalIgnoreCase);

    private static JObject? TryParseJson(string response)
    {
        try
        {
            return JObject.Parse(response);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record NextPage(string Continuation, string Url, string Body, string SourcePath, string Target);

    private static string ReadApiPacketRequestBody(string? stepConfig)
    {
        if (string.IsNullOrWhiteSpace(stepConfig)) return string.Empty;
        try
        {
            var config = JObject.Parse(stepConfig);
            return config.SelectToken("apiPacket.requestBody")?.Type == JTokenType.String
                ? config.SelectToken("apiPacket.requestBody")!.Value<string>() ?? string.Empty
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static NextPage? ResolveNextPage(
        PaginationStepConfig config,
        string currentUrl,
        string currentBody,
        JObject? page,
        IReadOnlyDictionary<string, string[]> headers,
        int pageNumber)
    {
        if (!string.IsNullOrWhiteSpace(config.HasMoreResponsePath) &&
            page?.SelectToken(config.HasMoreResponsePath)?.Type == JTokenType.Boolean &&
            page.SelectToken(config.HasMoreResponsePath)!.Value<bool>() == false)
        {
            return null;
        }

        if (string.Equals(config.Style, "LinkHeader", StringComparison.OrdinalIgnoreCase))
        {
            if (!headers.TryGetValue(config.NextLinkHeader, out var values)) return null;
            var link = values.SelectMany(value => value.Split(',')).FirstOrDefault(value => value.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase));
            if (link == null) return null;
            var start = link.IndexOf('<');
            var end = link.IndexOf('>');
            if (start < 0 || end <= start) return null;
            var nextUrl = link[(start + 1)..end];
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri) ||
                !Uri.TryCreate(nextUrl, UriKind.Absolute, out var nextUri) ||
                !string.Equals(currentUri.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Pagination on this node returned a Link header that points to a different host ('{nextUrl}'). " +
                    "Cross-host pagination is blocked to prevent server-side request forgery.");
            }

            return new NextPage(nextUrl, nextUrl, currentBody, $"response header '{config.NextLinkHeader}'", "the next URL");
        }

        if (string.Equals(config.Style, "PageOffset", StringComparison.OrdinalIgnoreCase))
        {
            var offset = ((pageNumber + 1) * config.PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new NextPage(offset, SetQueryParameter(currentUrl, config.OffsetRequestParameter, offset), currentBody,
                "page position", $"query parameter '{config.OffsetRequestParameter}'");
        }

        var isCursor = string.Equals(config.Style, "Cursor", StringComparison.OrdinalIgnoreCase);
        var tokenPath = isCursor ? config.CursorResponsePath : config.NextTokenPath;
        var parameterName = isCursor ? config.CursorRequestParameter : config.NextTokenParameter;
        var token = page?.SelectToken(tokenPath)?.ToString();
        if (string.IsNullOrWhiteSpace(token)) return null;

        // A parameter name that matches a JSON path inside the request body (for example
        // "variables.cursor" for GraphQL) is injected there; anything else is a query
        // parameter name, which keeps classic REST token pagination unchanged.
        if (TrySetBodyValue(currentBody, parameterName, token, out var updatedBody))
            return new NextPage(token, currentUrl, updatedBody, $"response path '{tokenPath}'", $"request-body path '{parameterName}'");

        return new NextPage(token, SetQueryParameter(currentUrl, parameterName, token), currentBody,
            $"response path '{tokenPath}'", $"query parameter '{parameterName}'");
    }

    private static bool TrySetBodyValue(string body, string path, string value, out string updatedBody)
    {
        updatedBody = body;
        if (string.IsNullOrWhiteSpace(path) || (!path.Contains('.') && !path.Contains('['))) return false;
        try
        {
            var json = JObject.Parse(body);
            var token = json.SelectToken(path);
            if (token == null || token.Type == JTokenType.Object || token.Type == JTokenType.Array) return false;
            token.Replace(new JValue(value));
            updatedBody = json.ToString(Newtonsoft.Json.Formatting.None);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Shorten(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";

    private static string SetQueryParameter(string url, string name, string value)
    {
        var parts = url.Split('?', 2);
        var query = parts.Length == 2
            ? parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.Split('=', 2)[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<string>();
        query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        return $"{parts[0]}?{string.Join("&", query)}";
    }

    private static string AggregatePages(IReadOnlyList<string> pages, PaginationStepConfig config)
    {
        if (pages.Count == 1 || string.IsNullOrWhiteSpace(config.AggregatePath)) return pages[^1];
        var first = JObject.Parse(pages[0]);
        var target = first.SelectToken(config.AggregatePath) as JArray;
        if (target == null) return pages[^1];
        foreach (var page in pages.Skip(1))
        {
            if (JObject.Parse(page).SelectToken(config.AggregatePath) is JArray items)
            {
                foreach (var item in items) target.Add(item.DeepClone());
            }
        }
        return first.ToString(Newtonsoft.Json.Formatting.None);
    }

    private sealed record ResolvedEmail(
        string Subject,
        string Body,
        bool IsHtml,
        IReadOnlyList<string> To,
        IReadOnlyList<string> Cc,
        IReadOnlyList<string> Bcc,
        string? ReplyTo,
        IReadOnlyList<string> MissingPlaceholders);

    private async Task<ResolvedEmail> ResolveEmailAsync(
        IntegrationStep node,
        EmailStepConfig config,
        IntegrationFlow flow,
        string flowStateJson,
        string persistedStateJson)
    {
        var flowState = ParseObjectOrEmpty(flowStateJson);
        var persistedState = ParseObjectOrEmpty(persistedStateJson);

        string? scriptTo = null;
        string? scriptCc = null;
        string? scriptBcc = null;
        string? scriptReplyTo = null;
        string? scriptSubject = null;
        string? scriptBody = null;
        bool? scriptIsHtml = null;

        if (config.UseScript)
        {
            if (string.IsNullOrWhiteSpace(node.MappingCode))
            {
                throw new InvalidOperationException($"Email node {EmailLabel(node)} has no script configured.");
            }

            var scriptJson = await RunScriptAsync(node, "Email",
                () => _codeExecutionService.ExecuteEmailAsync(
                    node.MappingCode, flowStateJson, persistedStateJson, flow.HelperCode));

            JObject scripted;
            try
            {
                scripted = JObject.Parse(scriptJson);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Email node {EmailLabel(node)} script must return a JSON object with to, cc, bcc, replyTo, subject, body and isHtml.", ex);
            }

            if (TryReadEmailField(scripted, "to", out var value)) scriptTo = value;
            if (TryReadEmailField(scripted, "cc", out value)) scriptCc = value;
            if (TryReadEmailField(scripted, "bcc", out value)) scriptBcc = value;
            if (TryReadEmailField(scripted, "replyTo", out value)) scriptReplyTo = value;
            if (TryReadEmailField(scripted, "subject", out value)) scriptSubject = value;
            if (TryReadEmailField(scripted, "body", out value)) scriptBody = value;
            if (scripted.TryGetValue("isHtml", out var isHtmlToken) && isHtmlToken.Type != JTokenType.Null)
            {
                scriptIsHtml = isHtmlToken.Value<bool>();
            }
        }

        var missing = new List<string>();
        var isHtml = scriptIsHtml ?? config.IsHtml;
        var toRaw = scriptTo ?? EmailTemplate.Render(config.To, flowState, persistedState, false, missing);
        var ccRaw = scriptCc ?? EmailTemplate.Render(config.Cc, flowState, persistedState, false, missing);
        var bccRaw = scriptBcc ?? EmailTemplate.Render(config.Bcc, flowState, persistedState, false, missing);
        var replyToRaw = scriptReplyTo ?? EmailTemplate.Render(config.ReplyTo, flowState, persistedState, false, missing);
        var subject = scriptSubject ?? EmailTemplate.Render(config.Subject, flowState, persistedState, false, missing);
        var body = scriptBody ?? EmailTemplate.Render(config.Body, flowState, persistedState, isHtml, missing);

        RejectHeaderBreaks(toRaw, node, "To");
        RejectHeaderBreaks(ccRaw, node, "Cc");
        RejectHeaderBreaks(bccRaw, node, "Bcc");
        RejectHeaderBreaks(replyToRaw, node, "Reply-To");
        RejectHeaderBreaks(subject, node, "Subject");

        var toList = ParseEmailAddresses(toRaw, node, "To");
        var ccList = ParseEmailAddresses(ccRaw, node, "Cc")
            .Where(address => !toList.Contains(address, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var bccList = ParseEmailAddresses(bccRaw, node, "Bcc")
            .Where(address => !toList.Contains(address, StringComparer.OrdinalIgnoreCase)
                && !ccList.Contains(address, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var replyTo = ParseSingleEmailAddress(replyToRaw, node);

        if (toList.Count == 0)
        {
            throw new InvalidOperationException($"Email node {EmailLabel(node)} has no To recipients configured.");
        }

        if (toList.Count + ccList.Count + bccList.Count > 20)
        {
            throw new InvalidOperationException(
                $"Email node {EmailLabel(node)} has {toList.Count + ccList.Count + bccList.Count} recipients; the maximum is 20.");
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException($"Email node {EmailLabel(node)} has no subject configured.");
        }

        if (subject.Length > 255)
        {
            throw new InvalidOperationException($"Email node {EmailLabel(node)} subject exceeds 255 characters.");
        }

        if (Encoding.UTF8.GetByteCount(body) > 256 * 1024)
        {
            throw new InvalidOperationException($"Email node {EmailLabel(node)} body exceeds 256 KB.");
        }

        return new ResolvedEmail(
            subject, body, isHtml, toList, ccList, bccList, replyTo, missing.Distinct(StringComparer.Ordinal).ToList());
    }

    private static void AddMissingPlaceholders(JObject summary, IReadOnlyList<string> missing)
    {
        if (missing.Count > 0)
        {
            summary["missingPlaceholders"] = new JArray(missing);
        }
    }

    private static void RejectHeaderBreaks(string value, IntegrationStep node, string field)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new InvalidOperationException(
                $"Email node {EmailLabel(node)} {field} contains a line break, which is not allowed in an email header.");
        }
    }

    private static string EmailLabel(IntegrationStep node) =>
        string.IsNullOrWhiteSpace(node.NodeName) ? "Email" : node.NodeName;

    private static bool TryReadEmailField(JObject source, string field, out string value)
    {
        if (source.TryGetValue(field, out var token) && token.Type != JTokenType.Null)
        {
            value = token is JArray array
                ? string.Join(", ", array.Values<string>().Where(item => !string.IsNullOrWhiteSpace(item)))
                : token.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static List<string> ParseEmailAddresses(string? value, IntegrationStep node, string field)
    {
        var addresses = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return addresses;
        }

        foreach (var raw in value.Split(new[] { ',', ';' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var address = new MailAddress(raw).Address;
                if (!addresses.Contains(address, StringComparer.OrdinalIgnoreCase))
                {
                    addresses.Add(address);
                }
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"Email node {EmailLabel(node)} has an invalid {field} address: {raw}");
            }
        }

        return addresses;
    }

    private static string? ParseSingleEmailAddress(string? value, IntegrationStep node)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains(',') || trimmed.Contains(';'))
        {
            throw new InvalidOperationException(
                $"Email node {EmailLabel(node)} Reply-To must be a single address.");
        }

        try
        {
            return new MailAddress(trimmed).Address;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"Email node {EmailLabel(node)} has an invalid Reply-To address: {trimmed}");
        }
    }

    private static string BuildEmailHtml(string body, bool isHtml)
    {
        if (isHtml)
        {
            return body;
        }

        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return WebUtility.HtmlEncode(normalized).Replace("\n", "<br>", StringComparison.Ordinal);
    }

    private static string BuildEmailText(string body, bool isHtml)
    {
        if (!isHtml)
        {
            return body;
        }

        var withBreaks = Regex.Replace(body, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        withBreaks = Regex.Replace(withBreaks, "</(p|div|h[1-6]|li|tr)>", "\n", RegexOptions.IgnoreCase);
        var stripped = Regex.Replace(withBreaks, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(stripped).Trim();
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

    private static (JToken Root, string ActualPath) ResolveArraySource(
        JToken input, JObject flowStateContext, string arrayPath)
    {
        // Array paths are resolved through the central flow state first, consistent
        // with the cross-reference nodes: a `flowState.<node>.<path>` prefix is
        // resolved against the shared flowStateContext (the single source of truth),
        // while a plain path resolves against the node's own upstream input.
        if (!string.IsNullOrWhiteSpace(arrayPath) && arrayPath.StartsWith("flowState.", StringComparison.Ordinal))
        {
            return (flowStateContext, arrayPath.Substring("flowState.".Length));
        }

        return (input, arrayPath);
    }

    private static IEnumerable<JToken> ResolveRecordSourcePaths(JToken input, JObject flowStateContext, string arrayPath)
    {
        if (string.IsNullOrWhiteSpace(arrayPath))
        {
            yield return input;
            yield break;
        }

        if (arrayPath.StartsWith("flowState.", StringComparison.Ordinal))
        {
            var treePath = arrayPath.Substring("flowState.".Length);
            var fromFlowState = CrossReferenceKeyBuilder.ResolvePaths(flowStateContext, treePath).ToList();
            if (fromFlowState.Any())
            {
                foreach (var token in fromFlowState) yield return token;
            }
            else
            {
                foreach (var token in CrossReferenceKeyBuilder.ResolvePaths(input, treePath)) yield return token;
            }
            yield break;
        }

        var fromInput = CrossReferenceKeyBuilder.ResolvePaths(input, arrayPath).ToList();
        if (fromInput.Any())
        {
            foreach (var token in fromInput) yield return token;
        }
        else
        {
            foreach (var token in CrossReferenceKeyBuilder.ResolvePaths(flowStateContext, arrayPath)) yield return token;
        }
    }

    private static (JToken Root, string ActualPath) ResolveFilterSource(JToken input, JObject flowStateContext, string arrayPath)
    {
        if (string.IsNullOrWhiteSpace(arrayPath))
            return (input, string.Empty);

        if (arrayPath.StartsWith("flowState.", StringComparison.Ordinal))
        {
            var treePath = arrayPath.Substring("flowState.".Length);
            var fromFlowState = CrossReferenceKeyBuilder.ResolvePaths(flowStateContext, treePath).ToList();
            if (fromFlowState.Any())
            {
                // Peel the leading node name so a wildcard filter preserves structure
                // relative to that node's outputs (the central flow state) instead of
                // re-wrapping the entire flow state under the node name.
                var dot = treePath.IndexOf('.');
                var sourceName = dot < 0 ? treePath : treePath.Substring(0, dot);
                var subPath = dot < 0 ? string.Empty : treePath.Substring(dot + 1);
                var source = flowStateContext[sourceName];
                if (source != null && source.Type != JTokenType.Null)
                    return (source, subPath);
                return (input, treePath);
            }
            return (input, treePath);
        }

        var fromInput = CrossReferenceKeyBuilder.ResolvePaths(input, arrayPath).ToList();
        if (fromInput.Any())
            return (input, arrayPath);
        return (flowStateContext, arrayPath);
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
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Mutable wrapper for the persisted state JObject. Needed because async methods
    /// cannot have ref parameters, yet the PersistedState step type replaces the entire
    /// JObject reference (not just mutates it).
    /// </summary>
    private sealed class PersistedStateRef
    {
        public JObject Value;

        public PersistedStateRef(JObject value)
        {
            Value = value;
        }
    }
}
