using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using Newtonsoft.Json.Linq;

namespace SimpleIPaaS.Application.Services;

public class FlowExecutor
{
    private readonly IIntegrationRepository _repository;
    private readonly ITransportEngine _transportEngine;
    private readonly IAdvancedCodeExecutionService _codeExecutionService;
    private readonly IExecutionRepository _executionRepository;

    public FlowExecutor(
        IIntegrationRepository repository,
        ITransportEngine transportEngine,
        IAdvancedCodeExecutionService codeExecutionService,
        IExecutionRepository executionRepository)
    {
        _repository = repository;
        _transportEngine = transportEngine;
        _codeExecutionService = codeExecutionService;
        _executionRepository = executionRepository;
    }

    public async Task<FlowExecution> ExecuteFlowAsync(Guid flowId)
    {
        var flow = await _repository.GetByIdAsync(flowId);
        if (flow == null) throw new ArgumentException("Flow not found", nameof(flowId));

        var flowExecution = new FlowExecution
        {
            FlowId = flow.Id,
            Status = ExecutionStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };
        await _executionRepository.AddFlowExecutionAsync(flowExecution);

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

            // Topologically sort nodes for proper DAG traversal
            var sortedNodes = TopologicalSort(flow);

            var flowStateContext = new JObject();
            var activeNodes = new HashSet<Guid>();

            // All starting nodes are active by default
            foreach (var startNode in startNodes)
            {
                activeNodes.Add(startNode.Id);
            }

            foreach (var node in sortedNodes)
            {
                if (!activeNodes.Contains(node.Id))
                {
                    // Skip execution of this node since it's not active in the current path
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

                string flowStateJson = flowStateContext.ToString(Newtonsoft.Json.Formatting.None);
                string currentPayload = string.Empty;

                try
                {
                    if (node.StepType == StepType.HttpAction)
                    {
                        var url = node.EndpointUrl;
                        if (!string.IsNullOrWhiteSpace(node.UrlCode))
                        {
                            url = await _codeExecutionService.ExecuteUrlAsync(node.UrlCode, flowStateJson);
                        }

                        string requestPayload = string.Empty;
                        if (!string.IsNullOrWhiteSpace(node.PreFlightCode))
                        {
                            requestPayload = await _codeExecutionService.ExecuteMappingAsync(node.PreFlightCode, flowStateJson);
                        }
                        
                        stepExecution.RequestPayload = requestPayload;

                        var nodeToDispatch = new IntegrationStep 
                        { 
                            EndpointUrl = url, 
                            HttpMethod = node.HttpMethod,
                            AuthType = node.AuthType,
                            AuthToken = node.AuthToken,
                            AuthUsername = node.AuthUsername,
                            AuthPassword = node.AuthPassword,
                            ConnectionId = node.ConnectionId
                        };

                        var (statusCode, response) = await _transportEngine.DispatchAsync(nodeToDispatch, requestPayload, node.ConnectionId);
                        stepExecution.HttpStatusCode = statusCode;
                        stepExecution.ResponsePayload = response;

                        if (statusCode < 200 || statusCode >= 300)
                        {
                            throw new Exception($"HTTP request failed with status code {statusCode}: {response}");
                        }

                        currentPayload = response;
                        
                        if (!string.IsNullOrWhiteSpace(node.PostFlightCode))
                        {
                            currentPayload = await _codeExecutionService.ExecutePostFlightAsync(node.PostFlightCode, flowStateJson, response);
                        }

                        // Activate all outgoing links
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
                            currentPayload = await _codeExecutionService.ExecuteMappingAsync(node.MappingCode, flowStateJson);
                            stepExecution.ResponsePayload = currentPayload;
                        }

                        // Activate all outgoing links
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
                            branchResult = await _codeExecutionService.ExecuteBranchAsync(node.MappingCode, flowStateJson);
                        }
                        
                        stepExecution.ResponsePayload = $"{{\"branchResult\": {branchResult.ToString().ToLower()}}}";
                        currentPayload = stepExecution.ResponsePayload;

                        // Activate only the branch matched by port name
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
                catch (Exception ex)
                {
                    stepExecution.Status = ExecutionStatus.Failed;
                    stepExecution.ErrorMessage = ex.Message;
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    flowExecution.FailedRecords++;

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
                    throw; // Stop flow execution
                }

                await _executionRepository.UpdateStepExecutionAsync(stepExecution);
            }

            flowExecution.Status = ExecutionStatus.Success;
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
                result.Insert(0, node); // Add to the front to reverse post-order
            }
        }

        var startNodes = flow.Nodes.Where(n => !flow.Edges.Any(e => e.TargetNodeId == n.Id)).ToList();
        foreach (var startNode in startNodes)
        {
            Visit(startNode);
        }

        return result;
    }
}
