using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

// A run failed by crash recovery gets a dead letter (alert + replay entry point) with a rebuilt flow state.
public sealed class RecoveredRunDeadLetterTests
{
    private const string Recovery = "The Engine running this transaction stopped unexpectedly.";
    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();

    private RecoveredRunDeadLetters Service() => new(_executions, _flows, NullLogger<RecoveredRunDeadLetters>.Instance);

    private (IntegrationFlow Flow, IntegrationStep Map, IntegrationStep Post) SeedFlow(string postFlightCode = "")
    {
        var map = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "MapOrder", StepType = StepType.Mapping, MappingCode = "x" };
        var post = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "PostOrder", StepType = StepType.HttpAction, HttpMethod = "POST", PostFlightCode = postFlightCode };
        var flow = new IntegrationFlow { Name = "Orders", PersistedStateJson = "{\"watermark\":5}", Nodes = { map, post } };
        flow.Edges.Add(new IntegrationEdge { SourceNodeId = map.Id, TargetNodeId = post.Id });
        _flows.Seed(flow);
        return (flow, map, post);
    }

    private FlowExecution SeedRun(IntegrationFlow flow)
    {
        var run = new FlowExecution { FlowId = flow.Id, FlowName = flow.Name, Status = ExecutionStatus.Failed, TriggerPayloadJson = "{\"orderId\":42}" };
        _executions.Seed(run);
        return run;
    }

    [Fact]
    public async Task A_run_stopped_mid_card_gets_a_manual_only_dead_letter_with_the_exact_state()
    {
        var (flow, map, post) = SeedFlow();
        var run = SeedRun(flow);
        await _executions.AddStepExecutionAsync(new StepExecution
        {
            FlowExecutionId = run.Id, StepId = map.Id, NodeName = "MapOrder", Status = ExecutionStatus.Success,
            StartedAt = DateTime.UtcNow.AddSeconds(-5), ResponsePayload = "{\"sku\":\"A-1\"}"
        });
        await _executions.AddStepExecutionAsync(new StepExecution
        {
            FlowExecutionId = run.Id, StepId = post.Id, NodeName = "PostOrder", Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddSeconds(-4), ErrorMessage = Recovery, ReceivedInput = "{\"sku\":\"A-1\"}", RequestPayload = "{\"order\":42}"
        });

        var entry = await Service().CreateAsync(run.Id, Recovery);

        Assert.NotNull(entry);
        Assert.Same(entry, Assert.Single(_executions.DeadLetters));
        Assert.Equal(post.Id, entry!.StepId);
        Assert.True(entry.ManualReplayOnly);
        Assert.Equal("Pending", entry.Status);
        Assert.Equal("{\"order\":42}", entry.Payload);
        Assert.Equal("{\"watermark\":5}", entry.PersistedStateJson);
        Assert.Contains("while card 'PostOrder' was running", entry.ErrorMessage);
        Assert.DoesNotContain("approximate", entry.ErrorMessage);

        // Exactly what the run's flow state held when PostOrder started.
        var state = JObject.Parse(entry.FlowStateJson);
        Assert.Equal(42, (int)state["trigger"]!["orderId"]!);
        Assert.Equal("A-1", (string)state["MapOrder"]!["sku"]!);
        Assert.Null(state["PostOrder"]);
    }

    [Fact]
    public async Task A_post_flight_script_upstream_makes_the_state_approximate_and_says_so()
    {
        var (flow, _, post) = SeedFlow(postFlightCode: "return Transform(response);");
        var send = new IntegrationStep { Id = Guid.NewGuid(), NodeName = "Notify", StepType = StepType.Mapping, MappingCode = "y" };
        flow.Nodes.Add(send);
        flow.Edges.Add(new IntegrationEdge { SourceNodeId = post.Id, TargetNodeId = send.Id });
        var run = SeedRun(flow);
        await _executions.AddStepExecutionAsync(new StepExecution
        {
            FlowExecutionId = run.Id, StepId = post.Id, NodeName = "PostOrder", Status = ExecutionStatus.Success, HttpStatusCode = 201,
            StartedAt = DateTime.UtcNow.AddSeconds(-5), ResponsePayload = "{\"raw\":true}"
        });

        var entry = await Service().CreateAsync(run.Id, Recovery);

        // Between cards: anchored to the last finished card; the operator chooses where to resume.
        Assert.Equal(post.Id, entry!.StepId);
        Assert.Contains("after card 'PostOrder' finished", entry.ErrorMessage);
        Assert.Contains("approximate for 'PostOrder' (post-flight script)", entry.ErrorMessage);
    }

    [Fact]
    public async Task No_second_dead_letter_when_a_card_already_failed_and_none_for_test_runs()
    {
        var (flow, map, _) = SeedFlow();
        var run = SeedRun(flow);
        await _executions.AddDeadLetterEntryAsync(new DeadLetterEntry { FlowExecutionId = run.Id, StepId = map.Id });
        Assert.Null(await Service().CreateAsync(run.Id, Recovery));

        var testRun = new FlowExecution { FlowId = flow.Id, IsTest = true };
        _executions.Seed(testRun);
        Assert.Null(await Service().CreateAsync(testRun.Id, Recovery));
        Assert.Single(_executions.DeadLetters);
    }

    [Fact]
    public async Task A_run_that_never_started_a_card_is_anchored_to_its_start_card()
    {
        var (flow, map, _) = SeedFlow();
        var run = SeedRun(flow);

        var entry = await Service().CreateAsync(run.Id, Recovery);

        Assert.Equal(map.Id, entry!.StepId);
        Assert.Contains("before its first card started", entry.ErrorMessage);
        Assert.Equal(42, (int)JObject.Parse(entry.FlowStateJson)["trigger"]!["orderId"]!);
    }

    [Fact]
    public void Manual_only_dead_letters_are_never_auto_retried()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DeadLetter:MaxAutoRetryAttempts"] = "3" })
            .Build();
        var service = new DeadLetterService(_executions, _flows, null!, NullLogger<DeadLetterService>.Instance, configuration);

        Assert.True(service.CanAutoRetry(new DeadLetterEntry()));
        Assert.False(service.CanAutoRetry(new DeadLetterEntry { ManualReplayOnly = true }));
    }
}
