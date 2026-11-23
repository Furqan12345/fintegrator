using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

// Shared by the API runner and the Engine's legacy queued-test path so a run is
// evaluated and persisted exactly the same way in both hosts.
public sealed class FlowTestResultRecorder
{
    private readonly IFlowTestRepository _tests;
    private readonly IExecutionRepository _executions;
    private readonly ILogger<FlowTestResultRecorder> _logger;

    public FlowTestResultRecorder(
        IFlowTestRepository tests,
        IExecutionRepository executions,
        ILogger<FlowTestResultRecorder> logger)
    {
        _tests = tests;
        _executions = executions;
        _logger = logger;
    }

    public async Task<FlowTestEvaluation?> RecordAsync(
        Guid testRunId,
        Guid testCaseId,
        FlowExecution execution,
        IntegrationFlow flow,
        FlowTestDefinition definition,
        CancellationToken cancellationToken = default,
        string? runnerError = null)
    {
        var run = await _tests.GetRunAsync(testRunId);
        if (run == null)
        {
            _logger.LogWarning("Test run {TestRunId} disappeared before it could be recorded.", testRunId);
            return null;
        }

        var steps = await _executions.GetStepExecutionsAsync(execution.Id);
        var evaluation = FlowTestEvaluator.Evaluate(execution, steps, definition, flow);
        if (!string.IsNullOrWhiteSpace(runnerError))
        {
            evaluation.Details.Passed = false;
            evaluation.Details.Headline = $"Test runner error: {runnerError}";
            evaluation.Details.Failures.Add($"Test runner error: {runnerError}");
            evaluation = new FlowTestEvaluation(false, evaluation.Details.Failures, evaluation.Details);
        }
        var completedAt = DateTime.UtcNow;

        run.Status = evaluation.Passed ? "Passed" : "Failed";
        run.ResultJson = evaluation.ToJson();
        run.ErrorMessage = evaluation.Failures.Count == 0 ? null : string.Join("\n", evaluation.Failures);
        run.CompletedAt = completedAt;
        if (run.StartedAt == default) run.StartedAt = execution.StartedAt;
        await _tests.UpdateRunAsync(run);

        await _tests.RecordRunTimestampsAsync(testCaseId, completedAt, evaluation.Passed);
        try
        {
            await _tests.TrimRunsAsync(testCaseId);
        }
        catch (Exception ex)
        {
            // Retention is housekeeping; a trim failure must never change the recorded result.
            _logger.LogWarning(ex, "Could not trim old runs for test case {TestCaseId}.", testCaseId);
        }

        _logger.LogInformation(
            "Test run {TestRunId} for case {TestCaseId} finished {Status}.",
            testRunId, testCaseId, run.Status);
        return evaluation;
    }
}
