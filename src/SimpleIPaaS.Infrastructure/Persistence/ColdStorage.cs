using Microsoft.EntityFrameworkCore;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Explicit deletes of runs (flow delete, test-run cleanup, demo reseed) must also remove the PostgreSQL cold
// rows, which have no foreign key (they expire with their partitions otherwise). Call this before the hot
// StepPacketLogs / StepExecutions rows are deleted, because the packet payloads are found through them.
// A no-op on SQLite and SQL Server, where the payloads live in the hot rows.
public static class ColdStorage
{
    public static async Task DeleteForStepsAsync(IPaaSContext context, IQueryable<Guid> stepExecutionIds, CancellationToken cancellationToken = default)
    {
        if (!context.IsPostgres())
        {
            return;
        }

        await context.Set<StepPacketPayload>()
            .Where(payload => context.StepPacketLogs.Any(packet => packet.Id == payload.PacketId && stepExecutionIds.Contains(packet.StepExecutionId)))
            .ExecuteDeleteAsync(cancellationToken);
        await context.Set<StepExecutionPayload>()
            .Where(payload => stepExecutionIds.Contains(payload.StepExecutionId))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
