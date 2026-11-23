using System.Text.Json;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Client.Components.FlowTests;
using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class HttpErrorRoutingTests
{
    private sealed class ResponseTransport : ITransportEngine
    {
        private readonly int _status;
        private readonly string _body;

        public ResponseTransport(int status, string body)
        {
            _status = status;
            _body = body;
        }

        public Task<TransportResponse> DispatchAsync(
            IntegrationStep step,
            string? payload,
            Guid? connectionId = null,
            CancellationToken cancellationToken = default,
            FlowTestContext? testContext = null)
        {
            var headers = new Dictionary<string, string[]>
            {
                ["Set-Cookie"] = new[] { "session=secret" },
                ["Authorization"] = new[] { "Bearer secret" },
                ["X-Trace"] = new[] { "trace-1" }
            };
            var now = DateTime.UtcNow;
            return Task.FromResult(new TransportResponse(_status, _body, headers, step.EndpointUrl)
            {
                StartedAt = now,
                CompletedAt = now,
                Attempts = new[]
                {
                    new TransportAttempt(_status, _body, headers, step.EndpointUrl, step.HttpMethod,
                        new Dictionary<string, string[]>(), payload ?? string.Empty, now, now)
                }
            });
        }
    }

    private static (FlowExecutor Executor, StubExecutionRepository Executions, FlowExecution Execution) CreateExecutor(
        IntegrationFlow flow,
        int status,
        string body,
        IAdvancedCodeExecutionService? code = null)
    {
        var flows = new StubIntegrationRepository();
        flows.Seed(flow);
        var executions = new StubExecutionRepository();
        var execution = new FlowExecution { FlowId = flow.Id, FlowName = flow.Name, TriggerSource = "Manual" };
        executions.Seed(execution);
        var executor = new FlowExecutor(
            flows,
            new ResponseTransport(status, body),
            code ?? new UnreachableCodeExecutionService(),
            executions,
            new StubCrossReferenceRepository(),
            new FakeEmailSender(),
            NullLogger<FlowExecutor>.Instance);
        return (executor, executions, execution);
    }

    private static IntegrationFlow BuildRoutingFlow(HttpErrorMode mode, out IntegrationStep http, out IntegrationStep errorChild, out IntegrationStep successChild)
    {
        http = new IntegrationStep
        {
            NodeName = "FetchOrders",
            StepType = StepType.HttpAction,
            EndpointUrl = "https://api.example.test/orders",
            HttpErrorMode = mode
        };
        errorChild = new IntegrationStep { NodeName = "HandleError", StepType = StepType.Mapping };
        successChild = new IntegrationStep { NodeName = "HandleSuccess", StepType = StepType.Mapping };
        var flow = new IntegrationFlow { Name = "ZZ Test - HTTP error routing" };
        flow.Nodes.Add(http);
        flow.Nodes.Add(errorChild);
        flow.Nodes.Add(successChild);
        flow.Edges.Add(new IntegrationEdge
        {
            SourceNodeId = http.Id, TargetNodeId = successChild.Id, SourcePortId = "right", TargetPortId = "left"
        });
        flow.Edges.Add(new IntegrationEdge
        {
            SourceNodeId = http.Id, TargetNodeId = errorChild.Id, SourcePortId = "error", TargetPortId = "left"
        });
        return flow;
    }

    [Fact]
    public async Task ContinueModeRoutesErrorAndBuildsSafeEnvelope()
    {
        var flow = BuildRoutingFlow(HttpErrorMode.Continue, out var http, out var errorChild, out var successChild);
        http.PostFlightCode = "return input;"; // unreachable code service proves this is skipped on the error path
        var (executor, executions, execution) = CreateExecutor(flow, 400, "{\"message\":\"bad\"}");

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Empty(executions.DeadLetters);
        Assert.Contains(executions.StepExecutions, step => step.StepId == errorChild.Id);
        Assert.DoesNotContain(executions.StepExecutions, step => step.StepId == successChild.Id);
        var httpStep = executions.StepExecutions.Single(step => step.StepId == http.Id);
        Assert.Equal(ExecutionStatus.Success, httpStep.Status);
        Assert.Equal(400, httpStep.HttpStatusCode);

        var envelope = JObject.Parse(httpStep.ResponsePayload);
        Assert.False(envelope.Value<bool>("ok"));
        Assert.Equal(400, envelope.Value<int>("statusCode"));
        Assert.Equal("bad", envelope["body"]!.Value<string>("message"));
        Assert.Equal("trace-1", envelope["headers"]!["X-Trace"]!.First()!.Value<string>());
        Assert.Null(envelope["headers"]!["Set-Cookie"]);
        Assert.Null(envelope["headers"]!["Authorization"]);
        Assert.DoesNotContain("secret", httpStep.ResponsePayload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContinueModeEnvelopeKeepsHtmlBodyReadable()
    {
        var flow = BuildRoutingFlow(HttpErrorMode.Continue, out var http, out _, out _);
        const string html = "<!DOCTYPE HTML>\n<html lang=\"en\"><body>missing</body></html>";
        var (executor, executions, execution) = CreateExecutor(flow, 404, html);

        await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        var payload = executions.StepExecutions.Single(step => step.StepId == http.Id).ResponsePayload!;
        Assert.Contains("<!DOCTYPE HTML>", payload);
        Assert.Contains("<html lang=\"en\">", JObject.Parse(payload).Value<string>("body"));
        Assert.DoesNotContain("\\u003C", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u003E", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u0027", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectionalRoutingExitsRightAndBottomThenEntersLeft()
    {
        var source = new Blazor.Diagrams.Core.Geometry.Point(100, 100);
        var rightTarget = new Blazor.Diagrams.Core.Geometry.Point(500, 260);
        var rightRoute = DirectionalPathGenerator.BuildRoute(
            source, PortAlignment.Right, rightTarget, PortAlignment.Left);
        Assert.NotNull(rightRoute);
        Assert.True(rightRoute![1].X > source.X);
        Assert.Equal(source.Y, rightRoute[1].Y);
        Assert.Equal(rightTarget.Y, rightRoute[^2].Y);

        var aboveLeftTarget = new Blazor.Diagrams.Core.Geometry.Point(360, 20);
        var bottomRoute = DirectionalPathGenerator.BuildRoute(
            source, PortAlignment.Bottom, aboveLeftTarget, PortAlignment.Left);
        Assert.NotNull(bottomRoute);
        Assert.Equal(source.X, bottomRoute![1].X);
        Assert.True(bottomRoute[1].Y > source.Y);
        Assert.True(bottomRoute[^2].X < aboveLeftTarget.X);
        Assert.Equal(aboveLeftTarget.Y, bottomRoute[^2].Y);
    }

    [Theory]
    [InlineData(0, -80)]
    [InlineData(0, 80)]
    [InlineData(8, -80)]
    [InlineData(8, 80)]
    [InlineData(15, -80)]
    [InlineData(15, 80)]
    [InlineData(40, -80)]
    [InlineData(40, 80)]
    public void TightRightToLeftRoutesUseOneVerticalRunAndStubs(double gap, double dy)
    {
        var source = new Point(100, 100);
        var target = new Point(100 + gap, 100 + dy);
        var route = DirectionalPathGenerator.BuildRoute(
            source, PortAlignment.Right, target, PortAlignment.Left);

        Assert.NotNull(route);
        Assert.True(route![1].X > source.X);
        Assert.Equal(route[1].X, route[2].X);
        Assert.Equal(source.Y, route[1].Y);
        Assert.Equal(target.Y, route[2].Y);
        Assert.Equal(target, route[^1]);
        Assert.True(route[1].X - source.X >= 4d);
        if (gap >= 32)
        {
            Assert.True(route[1].X - source.X >= 16d);
            Assert.True(target.X - route[2].X >= 16d);
        }
    }

    [Fact]
    public void DiagramPortLayoutKeepsErrorOnRightAndBranchFalseOnBottom()
    {
        Assert.True(DiagramPortLayout.IsErrorPort("error"));
        Assert.False(DiagramPortLayout.IsErrorPort("right"));
        Assert.Equal(PortAlignment.Right, DiagramPortLayout.OutputAlignment("error"));
        Assert.Equal(PortAlignment.Right, DiagramPortLayout.OutputAlignment("true"));
        Assert.Equal(PortAlignment.Bottom, DiagramPortLayout.OutputAlignment("false"));
        Assert.Equal(PortAlignment.Bottom, DiagramPortLayout.OutputAlignment("completed"));
    }

    [Fact]
    public void ExpectedFailureHeadlineSaysPassedWhenRunFailedAsExpected()
    {
        var node = new IntegrationStep { NodeName = "FetchOrders", StepType = StepType.HttpAction };
        var flow = new IntegrationFlow { Name = "ZZ Test - headline" };
        flow.Nodes.Add(node);
        var execution = new FlowExecution { Status = ExecutionStatus.Failed };
        var step = new StepExecution
        {
            StepId = node.Id,
            NodeName = node.NodeName,
            Status = ExecutionStatus.Failed,
            HttpStatusCode = 400,
            ErrorMessage = "HTTP request failed with status code 400: {\"message\":\"bad request\"}",
            StartedAt = DateTime.UtcNow
        };

        var evaluation = FlowTestEvaluator.Evaluate(
            execution,
            new[] { step },
            new FlowTestDefinition { ExpectedRunResult = "Failed" },
            flow);

        Assert.True(evaluation.Passed);
        Assert.Equal(
            "Passed: the run failed as expected at \"FetchOrders\" (HTTP 400: {\"message\":\"bad request\"})",
            evaluation.Details.Headline);
    }

    [Fact]
    public void ExpectedFailureHintRespectsContinueMode()
    {
        var cardId = Guid.NewGuid();
        var checks = new[]
        {
            new FlowTestAssertionDto
            {
                CardId = cardId, Type = "Field", Field = "HttpStatusCode", ExpectedValue = "400"
            }
        };

        Assert.False(FlowTestSession.ShouldSuggestExpectedFailure("Success", "HttpAction", "Continue", cardId, checks));
        Assert.False(FlowTestSession.ShouldSuggestExpectedFailure("Failed", "HttpAction", "Stop", cardId, checks));
        Assert.True(FlowTestSession.ShouldSuggestExpectedFailure("Success", "HttpAction", "Stop", cardId, checks));
        Assert.True(FlowTestSession.ShouldSuggestExpectedFailure("Success", "Mapping", null, cardId, checks));
    }

    [Fact]
    public async Task ContinueModeRoutesSuccessThroughNormalPort()
    {
        var flow = BuildRoutingFlow(HttpErrorMode.Continue, out var http, out var errorChild, out var successChild);
        var (executor, executions, execution) = CreateExecutor(flow, 200, "{\"orders\":[]}");

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Contains(executions.StepExecutions, step => step.StepId == successChild.Id);
        Assert.DoesNotContain(executions.StepExecutions, step => step.StepId == errorChild.Id);
        var httpStep = executions.StepExecutions.Single(step => step.StepId == http.Id);
        Assert.Equal("{\"orders\":[]}", httpStep.ResponsePayload);
    }

    [Fact]
    public async Task StopModeNoSettingStillFailsAndDeadLettersOn400()
    {
        var flow = BuildRoutingFlow(HttpErrorMode.Stop, out var http, out var errorChild, out var successChild);
        var (executor, executions, execution) = CreateExecutor(flow, 400, "{\"message\":\"bad\"}");

        var result = await executor.ExecuteFlowAsync(flow.Id, execution.Id, "{}", CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Single(executions.DeadLetters);
        Assert.DoesNotContain(executions.StepExecutions, step => step.StepId == errorChild.Id);
        Assert.DoesNotContain(executions.StepExecutions, step => step.StepId == successChild.Id);
        var httpStep = executions.StepExecutions.Single(step => step.StepId == http.Id);
        Assert.Equal(ExecutionStatus.Failed, httpStep.Status);
    }

    [Fact]
    public void EvaluatorUsesExplicitExpectedRunResult()
    {
        var execution = new FlowExecution { Status = ExecutionStatus.Failed };
        var successExpected = FlowTestEvaluator.Evaluate(execution, Array.Empty<StepExecution>(), new FlowTestDefinition());
        Assert.False(successExpected.Passed);
        Assert.Equal("Run result must be Success", successExpected.Details.Checks.Single().Description);

        var failedExpected = FlowTestEvaluator.Evaluate(execution, Array.Empty<StepExecution>(),
            new FlowTestDefinition { ExpectedRunResult = "Failed" });
        Assert.True(failedExpected.Passed);
        Assert.Equal("Run result must be Failed", failedExpected.Details.Checks.Single().Description);

        var anyExpected = FlowTestEvaluator.Evaluate(execution, Array.Empty<StepExecution>(),
            new FlowTestDefinition { ExpectedRunResult = "Any" });
        Assert.True(anyExpected.Passed);
        Assert.Equal("Run result must be Any", anyExpected.Details.Checks.Single().Description);

        var successExecution = new FlowExecution { Status = ExecutionStatus.Success };
        var continueHandled = FlowTestEvaluator.Evaluate(successExecution, Array.Empty<StepExecution>(),
            new FlowTestDefinition { ExpectedRunResult = "Success" });
        Assert.True(continueHandled.Passed);
    }

    [Fact]
    public void DebugRulesTreatHttpErrorPortLikeBranchPort()
    {
        var http = new DebugNodeSession { NodeId = Guid.NewGuid(), State = DebugNodeState.Success, ActivePortId = "error" };
        var successTarget = Guid.NewGuid();
        var errorTarget = Guid.NewGuid();
        var graph = DebugGraph.Build(new[] { http.NodeId, successTarget, errorTarget }, new[]
        {
            new DebugGraph.DebugEdge(http.NodeId, successTarget, "right", "left"),
            new DebugGraph.DebugEdge(http.NodeId, errorTarget, "error", "left")
        });
        var sessions = new Dictionary<Guid, DebugNodeSession>
        {
            [http.NodeId] = http,
            [successTarget] = new() { NodeId = successTarget },
            [errorTarget] = new() { NodeId = errorTarget }
        };

        Assert.False(DebugSessionRules.IsUnlocked(graph.Incoming[successTarget], sessions));
        Assert.True(DebugSessionRules.IsUnlocked(graph.Incoming[errorTarget], sessions));
    }
}
