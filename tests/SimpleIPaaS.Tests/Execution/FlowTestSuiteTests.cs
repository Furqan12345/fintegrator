using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Api.Services;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public sealed class FlowTestSuiteTests
{
    private static IntegrationFlow FlowWith(params IntegrationStep[] nodes)
    {
        var flow = new IntegrationFlow { Name = "Test flow" };
        foreach (var node in nodes) flow.Nodes.Add(node);
        return flow;
    }

    [Fact]
    public void EvaluatorListsEveryNodeAndMarksUnvisitedCards()
    {
        var fetch = new IntegrationStep { NodeName = "Fetch", StepType = StepType.HttpAction };
        var map = new IntegrationStep { NodeName = "Map", StepType = StepType.Mapping };
        var create = new IntegrationStep { NodeName = "Create", StepType = StepType.HttpAction };
        var success = new IntegrationStep { NodeName = "Success", StepType = StepType.Success };
        var flow = FlowWith(fetch, map, create, success);
        var execution = new FlowExecution { Status = ExecutionStatus.Success };
        var steps = new[]
        {
            new StepExecution { StepId = fetch.Id, NodeName = "Fetch", Status = ExecutionStatus.Success, StartedAt = DateTime.UtcNow },
            new StepExecution { StepId = map.Id, NodeName = "Map", Status = ExecutionStatus.Success, StartedAt = DateTime.UtcNow.AddMilliseconds(10) }
        };

        var result = FlowTestEvaluator.Evaluate(execution, steps, new FlowTestDefinition(), flow);

        Assert.Equal(4, result.Details.Cards.Count);
        Assert.Equal("Passed", result.Details.Cards[0].Status);
        Assert.Contains(result.Details.Cards, card => card.CardName == "Create" && card.Status == "NotReached");
        Assert.Contains(result.Details.Cards, card => card.CardName == "Success" && card.Status == "NotReached");
        Assert.Equal("Run result must be Success", Assert.Single(result.Details.Checks).Description);
        Assert.Equal("All 1 checks passed across 2 cards.", result.Details.Headline);
    }

    [Fact]
    public void EvaluatorNamesTheThrowingCardAndAddsSyntheticRunStatusCheck()
    {
        var fetch = new IntegrationStep { NodeName = "Fetch Orders", StepType = StepType.HttpAction };
        var map = new IntegrationStep { NodeName = "Map Order", StepType = StepType.Mapping };
        var flow = FlowWith(fetch, map);
        var execution = new FlowExecution { Status = ExecutionStatus.Failed, ErrorMessage = "fixture exhausted" };
        var steps = new[]
        {
            new StepExecution
            {
                StepId = fetch.Id,
                NodeName = "Fetch Orders",
                Status = ExecutionStatus.Failed,
                ErrorMessage = "fixture exhausted",
                StartedAt = DateTime.UtcNow
            }
        };

        var result = FlowTestEvaluator.Evaluate(execution, steps, new FlowTestDefinition(), flow);

        Assert.Equal(fetch.Id, result.Details.FailedCardId);
        Assert.Equal("Fetch Orders", result.Details.FailedCardName);
        Assert.Contains("Failed at \"Fetch Orders\"", result.Details.Headline);
        var synthetic = Assert.Single(result.Details.Checks);
        Assert.Equal(-1, synthetic.CheckIndex);
        Assert.Equal("Run result must be Success", synthetic.Description);
    }

    [Fact]
    public void EvaluatorMarksCheckFailureAndKeepsCheckIndex()
    {
        var create = new IntegrationStep { NodeName = "Create Order", StepType = StepType.HttpAction };
        var flow = FlowWith(create);
        var execution = new FlowExecution { Status = ExecutionStatus.Success };
        var steps = new[]
        {
            new StepExecution
            {
                StepId = create.Id,
                NodeName = "Create Order",
                Status = ExecutionStatus.Success,
                ResponsePayload = "{\"number\":\"SO-1002\"}",
                StartedAt = DateTime.UtcNow
            }
        };
        var definition = new FlowTestDefinition
        {
            Assertions = new List<FlowTestAssertion>
            {
                new()
                {
                    Type = "OutputField",
                    CardId = create.Id,
                    NodeName = "Create Order",
                    Field = "ResponsePayload",
                    Path = "number",
                    Operator = "Equals",
                    ExpectedValue = "SO-1001"
                }
            }
        };

        var result = FlowTestEvaluator.Evaluate(execution, steps, definition, flow);

        Assert.Single(result.Details.Checks, item => item.Description == "Run result must be Success");
        var check = Assert.Single(result.Details.Checks, item => item.CheckIndex == 0);
        Assert.Equal(0, check.CheckIndex);
        Assert.Equal("Failed", check.Status);
        Assert.Contains("must equal", check.Description);
        Assert.Equal("Failed", Assert.Single(result.Details.Cards).Status);
        Assert.Contains("Check failed on \"Create Order\"", result.Details.Headline);
    }

    [Fact]
    public void EvaluatorReportsSuppliedAndFixtureAndTruncatesInput()
    {
        var fetch = new IntegrationStep { NodeName = "Fetch", StepType = StepType.HttpAction };
        var map = new IntegrationStep { NodeName = "Map", StepType = StepType.Mapping };
        var flow = FlowWith(fetch, map);
        var execution = new FlowExecution { Status = ExecutionStatus.Success };
        var longInput = new string('x', 70_000);
        var steps = new[]
        {
            new StepExecution { StepId = fetch.Id, NodeName = "Fetch", Status = ExecutionStatus.Success, ReceivedInput = longInput, StartedAt = DateTime.UtcNow },
            new StepExecution { StepId = map.Id, NodeName = "Map", Status = ExecutionStatus.Success, StartedAt = DateTime.UtcNow.AddMilliseconds(1) }
        };
        var definition = new FlowTestDefinition
        {
            HttpResponses = new Dictionary<string, List<FlowHttpResponseOverride>>
            {
                [fetch.Id.ToString()] = new() { new FlowHttpResponseOverride { StatusCode = 200, ResponseBody = "{}" } }
            },
            CardOverrides = new List<FlowTestCardOverride>
            {
                new() { CardId = map.Id, CardName = "Map", Mode = "Supply", OutputJson = "{}" }
            }
        };

        var result = FlowTestEvaluator.Evaluate(execution, steps, definition, flow);

        Assert.Equal("Fixture", result.Details.Cards[0].Status);
        Assert.True(result.Details.Cards[0].InputTruncated);
        Assert.Equal(64 * 1024, result.Details.Cards[0].Input.Length);
        Assert.Equal("Supplied", result.Details.Cards[1].Status);
    }

    [Fact]
    public async Task IsolatedTestFailureDoesNotCreateADeadLetter()
    {
        var http = new IntegrationStep
        {
            NodeName = "Fetch",
            StepType = StepType.HttpAction,
            EndpointUrl = "https://api.example.test/orders"
        };
        var flow = new IntegrationFlow { Name = "ZZ Test - isolation" };
        flow.Nodes.Add(http);

        var flows = new StubIntegrationRepository();
        flows.Seed(flow);
        var executions = new StubExecutionRepository();
        var execution = new FlowExecution { FlowId = flow.Id, FlowName = flow.Name, TriggerSource = "Test", IsTest = true };
        executions.Seed(execution);
        var email = new FakeEmailSender();
        var executor = new FlowExecutor(
            flows,
            new UnreachableTransportEngine(),
            new UnreachableCodeExecutionService(),
            executions,
            new StubCrossReferenceRepository(),
            email,
            NullLogger<FlowExecutor>.Instance);

        var result = await executor.ExecuteFlowAsync(
            flow.Id, execution.Id, "{}", CancellationToken.None,
            FlowTestContext.FromJson("{}", execution.Id));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(executions.DeadLetters);
        Assert.Empty(email.Messages);
    }

    [Fact]
    public async Task RecorderNeverPassesAfterRunnerException()
    {
        var flow = new IntegrationFlow { Name = "Runner error flow" };
        var test = new FlowTestCase { FlowId = flow.Id, Name = "ZZ Test - runner error", DefinitionJson = "{}" };
        var tests = new StubFlowTestRepository();
        tests.Seed(test);
        var executions = new StubExecutionRepository();
        var run = new FlowTestRun
        {
            TestCaseId = test.Id,
            FlowId = flow.Id,
            Status = "Running",
            StartedAt = DateTime.UtcNow
        };
        await tests.AddRunAsync(run);
        var execution = new FlowExecution
        {
            FlowId = flow.Id,
            FlowName = flow.Name,
            Status = ExecutionStatus.Failed,
            IsTest = true
        };
        var definition = new FlowTestDefinition
        {
            Assertions = new List<FlowTestAssertion>
            {
                new() { Type = "ExecutionStatus", Field = "Status", Operator = "Equals", ExpectedValue = "Failed" }
            }
        };
        var recorder = new FlowTestResultRecorder(
            tests, executions, NullLogger<FlowTestResultRecorder>.Instance);

        await recorder.RecordAsync(
            run.Id, test.Id, execution, flow, definition,
            runnerError: "the executor exploded");

        Assert.Equal("Failed", run.Status);
        Assert.Contains("Test runner error: the executor exploded", run.ResultJson);
        Assert.Contains("Test runner error", run.ErrorMessage);
        Assert.Null(test.LastPassedAt);
    }

    [Fact]
    public async Task RecorderKeepsTheResultWhenTrimFails()
    {
        var flow = new IntegrationFlow { Name = "Trim failure flow" };
        var test = new FlowTestCase { FlowId = flow.Id, Name = "ZZ Test - trim", DefinitionJson = "{}" };
        var tests = new StubFlowTestRepository { TrimException = new InvalidOperationException("trim failed") };
        tests.Seed(test);
        var run = new FlowTestRun { TestCaseId = test.Id, FlowId = flow.Id, Status = "Running" };
        await tests.AddRunAsync(run);
        var recorder = new FlowTestResultRecorder(
            tests, new StubExecutionRepository(), NullLogger<FlowTestResultRecorder>.Instance);

        await recorder.RecordAsync(
            run.Id, test.Id,
            new FlowExecution { FlowId = flow.Id, Status = ExecutionStatus.Success, IsTest = true },
            flow, new FlowTestDefinition());

        Assert.Equal("Passed", run.Status);
    }

    [Fact]
    public async Task SuiteRunsAreStampedOnlyWhenTheyActuallyStart()
    {
        var flow = new IntegrationFlow { Name = "ZZ Test - suite timing" };
        var first = new FlowTestCase { FlowId = flow.Id, Name = "ZZ Test - first", DefinitionJson = "{}" };
        var second = new FlowTestCase { FlowId = flow.Id, Name = "ZZ Test - second", DefinitionJson = "{}" };
        var flows = new StubIntegrationRepository();
        flows.Seed(flow);
        var tests = new StubFlowTestRepository();
        tests.Seed(first);
        tests.Seed(second);
        var services = new ServiceCollection()
            .AddSingleton<IIntegrationRepository>(flows)
            .AddSingleton<IFlowTestRepository>(tests)
            .AddSingleton<IExecutionRepository>(new StubExecutionRepository())
            .BuildServiceProvider();
        var runner = new FlowTestRunner(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FlowTestRunner>.Instance);

        await runner.StartSuiteAsync(flow.Id, requiredOnly: false);

        Assert.Equal(2, tests.Runs.Count);
        Assert.All(tests.Runs, run => Assert.Equal(default, run.StartedAt));
    }

    [Fact]
    public async Task FromExecutionUsesRecordedCardStatusAndRedactsSecretHeaders()
    {
        var node = new IntegrationStep
        {
            NodeName = "Create Order",
            StepType = StepType.HttpAction,
            EndpointUrl = "https://example.test/orders"
        };
        var flow = new IntegrationFlow { Name = "ZZ Test - source" };
        flow.Nodes.Add(node);
        var flows = new StubIntegrationRepository();
        flows.Seed(flow);
        var executions = new StubExecutionRepository();
        var execution = new FlowExecution
        {
            FlowId = flow.Id,
            FlowName = flow.Name,
            Status = ExecutionStatus.Failed,
            TriggerPayloadJson = "{\"orderNo\":\"A-1\"}",
            IsTest = false
        };
        executions.Seed(execution);
        var step = new StepExecution
        {
            FlowExecutionId = execution.Id,
            StepId = node.Id,
            NodeName = node.NodeName,
            Status = ExecutionStatus.Failed,
            ResponsePayload = "{\"error\":\"bad\"}",
            ErrorMessage = "boom",
            StartedAt = DateTime.UtcNow
        };
        executions.StepExecutions.Add(step);
        executions.StepPacketLogs.Add(new StepPacketLog
        {
            StepExecutionId = step.Id,
            Kind = "Response",
            StatusCode = 500,
            ResponseBody = "{\"error\":\"bad\"}",
            ResponseHeadersJson = "{\"Authorization\":\"Bearer secret\",\"Set-Cookie\":\"a=b\",\"Proxy-Authorization\":\"x\",\"X-Api-Key\":\"k\",\"X-Secret\":\"s\",\"Credential-Token\":\"t\",\"Safe\":\"yes\"}"
        });

        var controller = new IntegrationFlowController(
            flows, null!, null!, null!, null!, new StubFlowTestRepository(), executions,
            null!, NullLogger<IntegrationFlowController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = Assert.IsType<OkObjectResult>(
            await controller.CreateTestCaseFromExecution(flow.Id, execution.Id));
        var draft = Assert.IsAssignableFrom<FlowTestCaseDto>(result.Value);
        Assert.Equal("Failed", draft.Definition.ExpectedRunResult);
        var cardCheck = Assert.Single(draft.Definition.Assertions, check => check.Type == "CardStatus");
        Assert.Equal("Failed", cardCheck.ExpectedValue);
        var fixture = Assert.Single(draft.Definition.HttpResponses[node.Id.ToString()]);
        Assert.Equal(500, fixture.StatusCode);
        Assert.Equal("{\"error\":\"bad\"}", fixture.ResponseBody);
        Assert.Equal("yes", Assert.Single(fixture.Headers["Safe"]));
        Assert.DoesNotContain("Authorization", fixture.Headers.Keys);
        Assert.DoesNotContain("Set-Cookie", fixture.Headers.Keys);
        Assert.DoesNotContain("Proxy-Authorization", fixture.Headers.Keys);
        Assert.DoesNotContain("X-Api-Key", fixture.Headers.Keys);
        Assert.DoesNotContain("X-Secret", fixture.Headers.Keys);
        Assert.DoesNotContain("Credential-Token", fixture.Headers.Keys);
    }

    [Fact]
    public async Task BulkCreateSavesOneScenarioPerRealTransactionAcrossFlowsAndSkipsTheRest()
    {
        var flows = new StubIntegrationRepository();
        var executions = new StubExecutionRepository();
        var tests = new StubFlowTestRepository();
        (IntegrationFlow Flow, IntegrationStep Node) NewFlow(string name)
        {
            var node = new IntegrationStep { NodeName = "Call", StepType = StepType.HttpAction, EndpointUrl = "https://example.test/x" };
            var flow = new IntegrationFlow { Name = name };
            flow.Nodes.Add(node);
            flows.Seed(flow);
            return (flow, node);
        }

        FlowExecution Run((IntegrationFlow Flow, IntegrationStep Node) source, bool isTest = false, string? identifier = null)
        {
            var execution = new FlowExecution
            {
                FlowId = source.Flow.Id, FlowName = source.Flow.Name, Status = ExecutionStatus.Success,
                TriggerPayloadJson = "{}", IsTest = isTest, Identifier = identifier, CompletedAt = DateTime.UtcNow
            };
            executions.Seed(execution);
            var step = new StepExecution { FlowExecutionId = execution.Id, StepId = source.Node.Id, NodeName = "Call", Status = ExecutionStatus.Success, ResponsePayload = "{\"ok\":true}", StartedAt = DateTime.UtcNow };
            executions.StepExecutions.Add(step);
            executions.StepPacketLogs.Add(new StepPacketLog { StepExecutionId = step.Id, Kind = "Response", StatusCode = 200, ResponseBody = "{\"ok\":true}" });
            return execution;
        }

        var orders = NewFlow("Orders");
        var invoices = NewFlow("Invoices");
        var a = Run(orders, identifier: "SO-1");
        var b = Run(orders, identifier: "SO-2");
        var c = Run(invoices);
        var testRun = Run(orders, isTest: true);
        var missing = Guid.NewGuid();
        var controller = new IntegrationFlowController(
            flows, null!, null!, null!, null!, tests, executions,
            null!, NullLogger<IntegrationFlowController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var first = Assert.IsType<BulkTestCasesFromExecutionsResultDto>(Assert.IsType<OkObjectResult>(
            await controller.CreateTestCasesFromExecutions(new BulkTestCasesFromExecutionsRequestDto { ExecutionIds = { a.Id, b.Id, c.Id, testRun.Id, missing } })).Value);

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, first.Created.Select(item => item.ExecutionId));
        Assert.Equal(2, (await tests.GetByFlowIdAsync(orders.Flow.Id)).Count);
        var invoiceScenario = Assert.Single(await tests.GetByFlowIdAsync(invoices.Flow.Id));
        Assert.Contains("HttpResponses", invoiceScenario.DefinitionJson);
        Assert.Contains(first.Skipped, skip => skip.ExecutionId == testRun.Id && skip.Reason.Contains("test run"));
        Assert.Contains(first.Skipped, skip => skip.ExecutionId == missing && skip.Reason.Contains("no longer exists"));

        // Again: the same transactions already have scenarios, so nothing is duplicated.
        var second = Assert.IsType<BulkTestCasesFromExecutionsResultDto>(Assert.IsType<OkObjectResult>(
            await controller.CreateTestCasesFromExecutions(new BulkTestCasesFromExecutionsRequestDto { ExecutionIds = { a.Id, c.Id } })).Value);
        Assert.Empty(second.Created);
        Assert.All(second.Skipped, skip => Assert.Contains("already has a scenario", skip.Reason));

        // Nothing selected, or too many, is refused.
        Assert.IsType<BadRequestObjectResult>(await controller.CreateTestCasesFromExecutions(new BulkTestCasesFromExecutionsRequestDto()));
        Assert.IsType<BadRequestObjectResult>(await controller.CreateTestCasesFromExecutions(new BulkTestCasesFromExecutionsRequestDto
        {
            ExecutionIds = Enumerable.Range(0, IntegrationFlowController.MaxBulkTestTransactions + 1).Select(_ => Guid.NewGuid()).ToList()
        }));
    }

    [Fact]
    public async Task RunnerStartsTestExecutionInProgressSoTheEngineCannotClaimIt()
    {
        var flow = new IntegrationFlow { Name = "ZZ Test - runner" };
        var test = new FlowTestCase
        {
            FlowId = flow.Id,
            Name = "ZZ Test - scenario",
            DefinitionJson = "{}"
        };
        var flows = new StubIntegrationRepository();
        flows.Seed(flow);
        var tests = new StubFlowTestRepository();
        tests.Seed(test);
        var executions = new StubExecutionRepository();

        var services = new ServiceCollection()
            .AddSingleton<IIntegrationRepository>(flows)
            .AddSingleton<IFlowTestRepository>(tests)
            .AddSingleton<IExecutionRepository>(executions)
            .BuildServiceProvider();
        var runner = new FlowTestRunner(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FlowTestRunner>.Instance);

        var (testRunId, executionId) = await runner.StartAsync(flow.Id, test.Id);

        var execution = await executions.GetFlowExecutionAsync(executionId);
        Assert.NotNull(execution);
        Assert.True(execution!.IsTest);
        Assert.Equal(ExecutionStatus.InProgress, execution.Status);
        Assert.NotNull(await tests.GetRunAsync(testRunId));
        Assert.Equal("Running", (await tests.GetRunAsync(testRunId))!.Status);
        Assert.Null(await executions.TryClaimNextQueuedExecutionAsync());
    }
}
