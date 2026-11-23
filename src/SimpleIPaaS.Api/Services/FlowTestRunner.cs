using System.Threading.Channels;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Services;

// Test runs are interactive work, so they run inside the API instead of the Engine
// queue. The Engine only claims Queued FlowExecutions; every execution created here
// starts InProgress and can therefore never be picked up by the Engine.
public sealed class FlowTestRunner : BackgroundService
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(120);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FlowTestRunner> _logger;
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim _slots = new(2, 2);

    public FlowTestRunner(IServiceScopeFactory scopeFactory, ILogger<FlowTestRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<(Guid TestRunId, Guid ExecutionId)> StartAsync(
        Guid flowId,
        Guid testCaseId,
        Guid? suiteRunId = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var flows = scope.ServiceProvider.GetRequiredService<IIntegrationRepository>();
        var tests = scope.ServiceProvider.GetRequiredService<IFlowTestRepository>();
        var executions = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();

        var flow = await flows.GetByIdAsync(flowId)
            ?? throw new InvalidOperationException("The flow no longer exists.");
        var test = await tests.GetAsync(testCaseId)
            ?? throw new InvalidOperationException("The scenario no longer exists.");
        if (test.FlowId != flowId)
            throw new InvalidOperationException("The scenario belongs to another flow.");

        var execution = CreateExecution(flow, test, suiteRunId);
        await executions.AddFlowExecutionAsync(execution);

        var run = new FlowTestRun
        {
            TestCaseId = test.Id,
            FlowId = flowId,
            SuiteRunId = suiteRunId,
            FlowExecutionId = execution.Id,
            Status = "Running",
            StartedAt = DateTime.UtcNow,
            FlowUpdatedAt = flow.UpdatedAt
        };
        await tests.AddRunAsync(run);

        await _queue.Writer.WriteAsync(
            new WorkItem(WorkKind.Single, flow, test, run, execution.Id, null), cancellationToken);
        return (run.Id, execution.Id);
    }

    public async Task<FlowTestSuiteStartDto> StartSuiteAsync(
        Guid flowId,
        bool requiredOnly,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var flows = scope.ServiceProvider.GetRequiredService<IIntegrationRepository>();
        var tests = scope.ServiceProvider.GetRequiredService<IFlowTestRepository>();

        var flow = await flows.GetByIdAsync(flowId)
            ?? throw new InvalidOperationException("The flow no longer exists.");
        var all = await tests.GetByFlowIdAsync(flowId);
        var selected = requiredOnly
            ? all.Where(test => test.Enabled && test.RequiredForPublish).ToList()
            : all.Where(test => test.Enabled).ToList();

        var suiteRunId = Guid.NewGuid();
        var runs = new List<FlowTestRun>();
        foreach (var test in selected)
        {
            var run = new FlowTestRun
            {
                TestCaseId = test.Id,
                FlowId = flowId,
                SuiteRunId = suiteRunId,
                Status = "Queued",
                // Stamped when the suite reaches this scenario, not when it was queued.
                StartedAt = default,
                FlowUpdatedAt = flow.UpdatedAt
            };
            await tests.AddRunAsync(run);
            runs.Add(run);
        }

        await _queue.Writer.WriteAsync(
            new WorkItem(WorkKind.Suite, flow, null, null, Guid.Empty, runs), cancellationToken);

        return new FlowTestSuiteStartDto
        {
            SuiteRunId = suiteRunId,
            TestRunIds = runs.Select(run => run.Id).ToList()
        };
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MarkInterruptedAsync(stoppingToken);

        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await _slots.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteWorkAsync(work, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Host shutdown: rows are marked interrupted on the next startup.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled flow-test work item failed.");
                }
                finally
                {
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task ExecuteWorkAsync(WorkItem work, CancellationToken stoppingToken)
    {
        if (work.Kind == WorkKind.Single && work.Test != null && work.Run != null)
        {
            await RunOneAsync(work.Flow, work.Test, work.Run, work.ExecutionId, stoppingToken);
            return;
        }

        foreach (var run in work.SuiteRuns ?? Array.Empty<FlowTestRun>())
        {
            if (stoppingToken.IsCancellationRequested) break;
            using var scope = _scopeFactory.CreateScope();
            var tests = scope.ServiceProvider.GetRequiredService<IFlowTestRepository>();
            var test = await tests.GetAsync(run.TestCaseId);
            if (test == null) continue;
            await RunOneAsync(work.Flow, test, run, Guid.Empty, stoppingToken);
        }
    }

    private async Task RunOneAsync(
        IntegrationFlow flow,
        FlowTestCase test,
        FlowTestRun run,
        Guid executionId,
        CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var tests = scope.ServiceProvider.GetRequiredService<IFlowTestRepository>();
        var executions = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();
        var executor = scope.ServiceProvider.GetRequiredService<FlowExecutor>();
        var recorder = scope.ServiceProvider.GetRequiredService<FlowTestResultRecorder>();

        if (run.Status is "Passed" or "Failed" or "Cancelled") return;

        run.Status = "Running";
        if (run.StartedAt == default) run.StartedAt = DateTime.UtcNow;
        await tests.UpdateRunAsync(run);

        var execution = executionId != Guid.Empty
            ? await executions.GetFlowExecutionAsync(executionId)
            : null;
        if (execution == null)
        {
            execution = CreateExecution(flow, test, run.SuiteRunId);
            await executions.AddFlowExecutionAsync(execution);
        }

        run.FlowExecutionId = execution.Id;
        await tests.UpdateRunAsync(run);

        var context = FlowTestContext.FromJson(test.DefinitionJson, execution.Id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(ExecutionTimeout);

        try
        {
            var result = await executor.ExecuteFlowAsync(
                flow.Id, execution.Id, null, timeout.Token, context);
            if (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                await MarkTimedOutAsync(tests, executions, run, result);
                return;
            }

            await recorder.RecordAsync(run.Id, test.Id, result, flow, context.Definition, stoppingToken);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            var current = await executions.GetFlowExecutionAsync(execution.Id) ?? execution;
            await MarkTimedOutAsync(tests, executions, run, current);
        }
        catch (Exception ex)
        {
            var current = await executions.GetFlowExecutionAsync(execution.Id) ?? execution;
            current.Status = ExecutionStatus.Failed;
            current.ErrorMessage = ex.Message;
            current.CompletedAt = DateTime.UtcNow;
            await executions.UpdateFlowExecutionAsync(current);
            run.Status = "Failed";
            run.ErrorMessage = ex.Message;
            run.CompletedAt = DateTime.UtcNow;
            await tests.UpdateRunAsync(run);
            await recorder.RecordAsync(
                run.Id, test.Id, current, flow, context.Definition, stoppingToken,
                runnerError: ex.Message);
        }
    }

    private static async Task MarkTimedOutAsync(
        IFlowTestRepository tests,
        IExecutionRepository executions,
        FlowTestRun run,
        FlowExecution execution)
    {
        const string message = "Timed out after 120 s";
        execution.Status = ExecutionStatus.Cancelled;
        execution.ErrorMessage = message;
        execution.CompletedAt = DateTime.UtcNow;
        await executions.UpdateFlowExecutionAsync(execution);
        run.Status = "Failed";
        run.ErrorMessage = message;
        run.CompletedAt = DateTime.UtcNow;
        await tests.UpdateRunAsync(run);
    }

    private static FlowExecution CreateExecution(IntegrationFlow flow, FlowTestCase test, Guid? suiteRunId) => new()
    {
        FlowId = flow.Id,
        FlowName = flow.Name,
        Status = ExecutionStatus.InProgress,
        StartedAt = DateTime.UtcNow,
        TriggerSource = "Test",
        IsTest = true,
        TestCaseId = test.Id,
        TestDefinitionJson = test.DefinitionJson,
        // Intentionally no TestRunId/SuiteRunId on the execution: the test run row owns them.
        ParentExecutionId = null
    };

    private async Task MarkInterruptedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tests = scope.ServiceProvider.GetRequiredService<IFlowTestRepository>();
            var executions = scope.ServiceProvider.GetRequiredService<IExecutionRepository>();
            var runs = await tests.GetInterruptedRunsAsync();
            foreach (var run in runs)
            {
                run.Status = "Failed";
                run.ErrorMessage = "Interrupted (API restarted)";
                run.CompletedAt = DateTime.UtcNow;
                await tests.UpdateRunAsync(run);

                if (run.FlowExecutionId != Guid.Empty)
                {
                    var execution = await executions.GetFlowExecutionAsync(run.FlowExecutionId);
                    if (execution != null && execution.Status is ExecutionStatus.Queued or ExecutionStatus.InProgress)
                    {
                        execution.Status = ExecutionStatus.Failed;
                        execution.ErrorMessage = "Interrupted (API restarted)";
                        execution.CompletedAt = DateTime.UtcNow;
                        await executions.UpdateFlowExecutionAsync(execution);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not mark interrupted flow-test runs.");
        }
    }

    private enum WorkKind { Single, Suite }

    private sealed record WorkItem(
        WorkKind Kind,
        IntegrationFlow Flow,
        FlowTestCase? Test,
        FlowTestRun? Run,
        Guid ExecutionId,
        IReadOnlyList<FlowTestRun>? SuiteRuns);
}
