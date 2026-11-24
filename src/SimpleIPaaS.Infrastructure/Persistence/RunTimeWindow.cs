using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain;

namespace SimpleIPaaS.Infrastructure.Persistence;

// The time span a run's (or a step's) day-partitioned rows can fall in: packets and log lines carry their own
// timestamp, not the run's, so a lookup by run or step id alone probes every day partition. Adding this window
// lets PostgreSQL skip all the other days. The margin is a whole day on each side: pruning is per day anyway, and
// it keeps late lines (a log written after the run finished, a replay days later moves CompletedAt) inside.
internal static class RunTimeWindow
{
    private static readonly TimeSpan Margin = TimeSpan.FromDays(1);

    public static async Task<(DateTime From, DateTime To)?> ForRunAsync(IPaaSContext context, Guid runId, CancellationToken cancellationToken)
    {
        var run = await context.FlowExecutions.AsNoTracking()
            .Where(execution => execution.Id == runId)
            .Select(execution => new { execution.QueuedAt, execution.StartedAt, execution.CompletedAt, execution.Status })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run == null)
        {
            return null; // run deleted: callers fall back to an unbounded lookup
        }

        var steps = context.StepExecutions.AsNoTracking().Where(step => step.FlowExecutionId == runId);
        var firstStep = await steps.Select(step => (DateTime?)step.StartedAt).MinAsync(cancellationToken).ConfigureAwait(false);
        var lastStep = await steps.Select(step => (DateTime?)(step.CompletedAt ?? step.StartedAt)).MaxAsync(cancellationToken).ConfigureAwait(false);

        var from = Earliest(run.QueuedAt ?? run.StartedAt, run.StartedAt, firstStep);
        var open = run.CompletedAt == null || run.Status is ExecutionStatus.Queued or ExecutionStatus.InProgress;
        var to = open ? DateTime.UtcNow : Latest(run.CompletedAt!.Value, run.StartedAt, lastStep);
        return (from - Margin, to + Margin);
    }

    public static async Task<(DateTime From, DateTime To)?> ForStepAsync(IPaaSContext context, Guid stepExecutionId, CancellationToken cancellationToken)
    {
        var step = await context.StepExecutions.AsNoTracking()
            .Where(candidate => candidate.Id == stepExecutionId)
            .Select(candidate => new { candidate.StartedAt, candidate.CompletedAt })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return step == null ? null : (step.StartedAt - Margin, (step.CompletedAt ?? DateTime.UtcNow) + Margin);
    }

    private static DateTime Earliest(DateTime first, DateTime second, DateTime? third)
    {
        var value = first < second ? first : second;
        return third.HasValue && third.Value < value ? third.Value : value;
    }

    private static DateTime Latest(DateTime first, DateTime second, DateTime? third)
    {
        var value = first > second ? first : second;
        return third.HasValue && third.Value > value ? third.Value : value;
    }
}
