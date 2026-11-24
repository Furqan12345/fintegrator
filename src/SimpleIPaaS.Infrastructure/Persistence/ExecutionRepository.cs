using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;

using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Storage;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class ExecutionRepository : IExecutionRepository
{
    private readonly IPaaSContext _context;
    private readonly Func<IPacketLogStore> _packetLogStoreSelector;
    // PostgreSQL keeps step payloads in the cold StepExecutionPayloads table (see PostgresLayout).
    private readonly bool _postgres;

    public ExecutionRepository(IPaaSContext context, Func<IPacketLogStore>? packetLogStoreSelector = null)
    {
        _context = context;
        _postgres = context.IsPostgres();

        // Packet logs (the request/response messages) are the growing half of this repository and
        // can live in Elasticsearch. The selector is asked per call rather than once, so a dataset
        // moved between backends takes effect on the next write instead of the next process start.
        _packetLogStoreSelector = packetLogStoreSelector ?? (() => new SqlPacketLogStore(context));
    }

    private IPacketLogStore PacketLogStore => _packetLogStoreSelector();

    public async Task<FlowExecution?> GetFlowExecutionAsync(Guid id)
    {
        var execution = await _context.FlowExecutions.FirstOrDefaultAsync(e => e.Id == id);
        if (_postgres && execution != null)
        {
            await HydrateRunPayloadAsync(execution);
        }

        return execution;
    }

    // PostgreSQL: a run's large JSON lives in FlowExecutionPayloads (see PostgresLayout). Written with the run,
    // never by a status update. A run without any of it has no cold row.
    private void StageRunPayload(FlowExecution execution)
    {
        if (!_postgres || (execution.TriggerPayloadJson == null && execution.TestDefinitionJson == null
            && execution.FanOutItemJson == null && execution.InitialFlowStateJson == null && execution.InitialPersistedStateJson == null))
        {
            return;
        }

        _context.Set<FlowExecutionPayload>().Add(new FlowExecutionPayload
        {
            FlowExecutionId = execution.Id,
            TriggerPayloadJson = CleanOrNull(execution.TriggerPayloadJson),
            TestDefinitionJson = CleanOrNull(execution.TestDefinitionJson),
            FanOutItemJson = CleanOrNull(execution.FanOutItemJson),
            InitialFlowStateJson = CleanOrNull(execution.InitialFlowStateJson),
            InitialPersistedStateJson = CleanOrNull(execution.InitialPersistedStateJson),
        });
    }

    private async Task HydrateRunPayloadAsync(FlowExecution execution)
    {
        var payload = await _context.Set<FlowExecutionPayload>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.FlowExecutionId == execution.Id);
        if (payload == null)
        {
            return;
        }

        execution.TriggerPayloadJson = payload.TriggerPayloadJson;
        execution.TestDefinitionJson = payload.TestDefinitionJson;
        execution.FanOutItemJson = payload.FanOutItemJson;
        execution.InitialFlowStateJson = payload.InitialFlowStateJson;
        execution.InitialPersistedStateJson = payload.InitialPersistedStateJson;
    }

    private void StageDeadLetterPayload(DeadLetterEntry entry)
    {
        if (!_postgres)
        {
            return;
        }

        _context.Set<DeadLetterPayload>().Add(new DeadLetterPayload
        {
            DeadLetterId = entry.Id,
            Payload = Storage.PostgresPacketIngestor.Clean(entry.Payload),
            FlowStateJson = string.IsNullOrEmpty(entry.FlowStateJson) ? "{}" : Storage.PostgresPacketIngestor.Clean(entry.FlowStateJson),
            PersistedStateJson = string.IsNullOrEmpty(entry.PersistedStateJson) ? "{}" : Storage.PostgresPacketIngestor.Clean(entry.PersistedStateJson),
        });
    }

    private async Task HydrateDeadLetterPayloadAsync(DeadLetterEntry entry)
    {
        var payload = await _context.Set<DeadLetterPayload>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.DeadLetterId == entry.Id);
        if (payload == null)
        {
            return;
        }

        entry.Payload = payload.Payload;
        entry.FlowStateJson = payload.FlowStateJson;
        entry.PersistedStateJson = payload.PersistedStateJson;
    }

    private static string? CleanOrNull(string? value) => value == null ? null : Storage.PostgresPacketIngestor.Clean(value);

    public async Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(ExecutionQuery query)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 50 : Math.Min(query.PageSize, 200);

        var executions = _context.FlowExecutions.AsQueryable();
        if (!query.IncludeTestRuns)
        {
            executions = executions.Where(e => !e.IsTest);
        }

        if (query.FlowId.HasValue && query.FlowId.Value != Guid.Empty)
        {
            executions = executions.Where(e => e.FlowId == query.FlowId.Value);
        }

        if (query.IntegrationId.HasValue && query.IntegrationId.Value != Guid.Empty)
        {
            // An integration owns the runs of the flows that are in it *now*. IntegrationId on the
            // execution row is a denormalised snapshot from when the run happened, so filtering on it
            // alone would hide a moved flow's history from the integration that displays it.
            var integrationId = query.IntegrationId.Value;
            var flowIds = await _context.IntegrationFlows
                .Where(flow => flow.IntegrationId == integrationId)
                .Select(flow => flow.Id)
                .ToListAsync();

            executions = executions.Where(e => flowIds.Contains(e.FlowId));
        }

        if (query.Status.HasValue)
        {
            executions = executions.Where(e => e.Status == query.Status.Value);
        }

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            if (Guid.TryParse(search, out var executionId))
            {
                executions = executions.Where(e => e.Id == executionId);
            }
            else
            {
                // EF.Functions.Like maps to SQL LIKE, which is case-insensitive for ASCII in SQLite.
                // PostgreSQL's LIKE is case-sensitive, so it gets ILIKE to keep the same search behaviour.
                var pattern = $"%{EscapeLike(search)}%";
                executions = _postgres
                    ? executions.Where(e =>
                        EF.Functions.ILike(e.FlowName, pattern, LikeEscape)
                        || (e.Identifier != null && EF.Functions.ILike(e.Identifier, pattern, LikeEscape))
                        || (e.Message != null && EF.Functions.ILike(e.Message, pattern, LikeEscape))
                        || (e.ErrorMessage != null && EF.Functions.ILike(e.ErrorMessage, pattern, LikeEscape)))
                    : executions.Where(e =>
                    EF.Functions.Like(e.FlowName, pattern, LikeEscape)
                    || (e.Identifier != null && EF.Functions.Like(e.Identifier, pattern, LikeEscape))
                    || (e.Message != null && EF.Functions.Like(e.Message, pattern, LikeEscape))
                    || (e.ErrorMessage != null && EF.Functions.Like(e.ErrorMessage, pattern, LikeEscape)));
            }
        }

        if (query.SinceUtc.HasValue)
        {
            executions = executions.Where(e => e.StartedAt >= query.SinceUtc.Value);
        }

        if (query.UntilUtc.HasValue)
        {
            executions = executions.Where(e => e.StartedAt < query.UntilUtc.Value);
        }

        // A list row never needs the run's trigger payload, test definition, fan-out item or the initial
        // flow/persisted state - those are the large JSON columns (a webhook body can be megabytes) and a page
        // of 50-200 rows used to drag every one of them out of the database and through the change tracker.
        // They are left unset here; GetFlowExecutionAsync still returns the full row for the detail views.
        return await executions
            .OrderByDescending(e => e.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new FlowExecution
            {
                Id = e.Id,
                FlowId = e.FlowId,
                FlowName = e.FlowName,
                IntegrationId = e.IntegrationId,
                IntegrationName = e.IntegrationName,
                TraceParent = e.TraceParent,
                Status = e.Status,
                StartedAt = e.StartedAt,
                QueuedAt = e.QueuedAt,
                CompletedAt = e.CompletedAt,
                RecoveredAt = e.RecoveredAt,
                ErrorMessage = e.ErrorMessage,
                TriggerSource = e.TriggerSource,
                IsTest = e.IsTest,
                TestCaseId = e.TestCaseId,
                TestRunId = e.TestRunId,
                ParentExecutionId = e.ParentExecutionId,
                FanOutNodeId = e.FanOutNodeId,
                FanOutNodeName = e.FanOutNodeName,
                FanOutItemIndex = e.FanOutItemIndex,
                FanOutItemCount = e.FanOutItemCount,
                TotalRecords = e.TotalRecords,
                SuccessRecords = e.SuccessRecords,
                FailedRecords = e.FailedRecords,
                Identifier = e.Identifier,
                Message = e.Message
            })
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task AddFlowExecutionAsync(FlowExecution execution)
    {
        _context.FlowExecutions.Add(execution);
        StageRunPayload(execution);
        await _context.SaveChangesAsync();
    }

    public async Task AddFlowExecutionsAsync(IReadOnlyCollection<FlowExecution> executions)
    {
        if (executions.Count == 0)
        {
            return;
        }

        _context.FlowExecutions.AddRange(executions);
        foreach (var execution in executions)
        {
            StageRunPayload(execution);
        }

        await _context.SaveChangesAsync();
    }

    public async Task AddFanOutAsync(FanOutSnapshot snapshot, IReadOnlyCollection<FlowExecution> children)
    {
        if (children.Count == 0)
        {
            return;
        }

        if (_postgres)
        {
            snapshot.FlowStateJson = Storage.PostgresPacketIngestor.Clean(snapshot.FlowStateJson);
        }

        _context.FanOutSnapshots.Add(snapshot);
        _context.FlowExecutions.AddRange(children);
        foreach (var child in children)
        {
            StageRunPayload(child);
        }

        await _context.SaveChangesAsync();
    }

    public Task<FanOutSnapshot?> GetFanOutSnapshotAsync(Guid id) =>
        _context.FanOutSnapshots.AsNoTracking().FirstOrDefaultAsync(snapshot => snapshot.Id == id);

    public async Task UpdateFlowExecutionAsync(FlowExecution execution)
    {
        _context.FlowExecutions.Update(execution);
        await _context.SaveChangesAsync();
    }

    public async Task AddStepExecutionAsync(StepExecution execution)
    {
        _context.StepExecutions.Add(execution);
        if (_postgres) StageStepPayload(execution, isNew: true);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateStepExecutionAsync(StepExecution execution)
    {
        _context.StepExecutions.Update(execution);
        if (!_postgres)
        {
            await _context.SaveChangesAsync();
            return;
        }

        StageStepPayload(execution, isNew: false);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException conflict) when (conflict.Entries.All(entry => entry.Entity is StepExecutionPayload))
        {
            // The cold row is missing (its partition expired, or the step predates the cold table): write it fresh.
            foreach (var entry in conflict.Entries)
            {
                entry.State = EntityState.Added;
            }

            await _context.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId)
    {
        var steps = await _context.StepExecutions
            .Where(s => s.FlowExecutionId == flowExecutionId)
            .OrderBy(s => s.StartedAt)
            .ToListAsync();
        if (_postgres) await HydrateStepPayloadsAsync(steps);
        return steps;
    }

    public async Task<StepExecution?> GetStepExecutionAsync(Guid flowExecutionId, Guid stepId)
    {
        var step = await _context.StepExecutions
            .Where(s => s.FlowExecutionId == flowExecutionId && s.StepId == stepId)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync();
        if (_postgres && step != null) await HydrateStepPayloadsAsync(new[] { step });
        return step;
    }

    // PostgreSQL: the hot StepExecutions row carries the payload sizes and the cold row the payloads. Both go in
    // the same SaveChanges (one round trip, one transaction). The cold key is (step id, StartedAt truncated to the
    // microsecond PostgreSQL stores), so an update addresses one partition and matches the stored key exactly.
    private void StageStepPayload(StepExecution step, bool isNew)
    {
        var entry = _context.Entry(step);
        entry.Property(PostgresLayout.ReceivedInputLength).CurrentValue = step.ReceivedInput?.Length ?? 0;
        entry.Property(PostgresLayout.RequestPayloadLength).CurrentValue = step.RequestPayload?.Length ?? 0;
        entry.Property(PostgresLayout.ResponsePayloadLength).CurrentValue = step.ResponsePayload?.Length ?? 0;

        var payloads = _context.Set<StepExecutionPayload>();
        var tracked = payloads.Local.FirstOrDefault(payload => payload.StepExecutionId == step.Id);
        if (tracked != null)
        {
            tracked.ReceivedInput = SimpleIPaaS.Infrastructure.Storage.PostgresPacketIngestor.Clean(step.ReceivedInput);
            tracked.RequestPayload = SimpleIPaaS.Infrastructure.Storage.PostgresPacketIngestor.Clean(step.RequestPayload);
            tracked.ResponsePayload = SimpleIPaaS.Infrastructure.Storage.PostgresPacketIngestor.Clean(step.ResponsePayload);
            return;
        }

        var cold = new StepExecutionPayload
        {
            StepExecutionId = step.Id,
            CreatedAt = TruncateToMicroseconds(step.StartedAt),
            ReceivedInput = SimpleIPaaS.Infrastructure.Storage.PostgresPacketIngestor.Clean(step.ReceivedInput),
            RequestPayload = SimpleIPaaS.Infrastructure.Storage.PostgresPacketIngestor.Clean(step.RequestPayload),
            ResponsePayload = SimpleIPaaS.Infrastructure.Storage.PostgresPacketIngestor.Clean(step.ResponsePayload),
        };
        if (isNew) payloads.Add(cold);
        else payloads.Update(cold);
    }

    // Loads the cold payloads of already-loaded steps. Bounded by the steps' own StartedAt range so PostgreSQL
    // only probes the partitions of the days the run touched.
    private async Task HydrateStepPayloadsAsync(IReadOnlyCollection<StepExecution> steps)
    {
        if (steps.Count == 0) return;
        var ids = steps.Select(step => step.Id).ToList();
        var from = TruncateToMicroseconds(steps.Min(step => step.StartedAt));
        var to = TruncateToMicroseconds(steps.Max(step => step.StartedAt));
        var payloads = await _context.Set<StepExecutionPayload>()
            .AsNoTracking()
            .Where(payload => ids.Contains(payload.StepExecutionId) && payload.CreatedAt >= from && payload.CreatedAt <= to)
            .ToListAsync();
        var byStep = payloads
            .GroupBy(payload => payload.StepExecutionId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(payload => payload.CreatedAt).First());
        foreach (var step in steps)
        {
            if (byStep.TryGetValue(step.Id, out var payload))
            {
                step.ReceivedInput = payload.ReceivedInput;
                step.RequestPayload = payload.RequestPayload;
                step.ResponsePayload = payload.ResponsePayload;
            }
        }
    }

    internal static DateTime TruncateToMicroseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    public async Task<IReadOnlyList<StepExecutionSummary>> GetStepExecutionSummariesAsync(Guid flowExecutionId)
    {
        if (_postgres)
        {
            // Sizes come from the hot row: listing a run's steps never reads a payload.
            return await _context.StepExecutions
                .AsNoTracking()
                .Where(s => s.FlowExecutionId == flowExecutionId)
                .OrderBy(s => s.StartedAt)
                .Select(s => new StepExecutionSummary(
                    s.Id,
                    s.FlowExecutionId,
                    s.StepId,
                    s.NodeName,
                    s.Status,
                    s.StartedAt,
                    s.CompletedAt,
                    s.RecoveredAt,
                    s.RecoveredByDeadLetterId,
                    s.HttpStatusCode,
                    s.ErrorMessage,
                    EF.Property<int>(s, PostgresLayout.ReceivedInputLength),
                    EF.Property<int>(s, PostgresLayout.RequestPayloadLength),
                    EF.Property<int>(s, PostgresLayout.ResponsePayloadLength)))
                .ToListAsync();
        }

        return await _context.StepExecutions
            .AsNoTracking()
            .Where(s => s.FlowExecutionId == flowExecutionId)
            .OrderBy(s => s.StartedAt)
            .Select(s => new StepExecutionSummary(
                s.Id,
                s.FlowExecutionId,
                s.StepId,
                s.NodeName,
                s.Status,
                s.StartedAt,
                s.CompletedAt,
                s.RecoveredAt,
                s.RecoveredByDeadLetterId,
                s.HttpStatusCode,
                s.ErrorMessage,
                s.ReceivedInput.Length,
                s.RequestPayload.Length,
                s.ResponsePayload.Length))
            .ToListAsync();
    }

    public async Task<StepPayload?> GetStepPayloadAsync(Guid flowExecutionId, Guid stepExecutionId)
    {
        if (_postgres)
        {
            var step = await _context.StepExecutions
                .AsNoTracking()
                .Where(s => s.FlowExecutionId == flowExecutionId && s.Id == stepExecutionId)
                .Select(s => new { s.Id, s.StartedAt })
                .FirstOrDefaultAsync();
            if (step == null) return null;

            var at = TruncateToMicroseconds(step.StartedAt);
            var payload = await _context.Set<StepExecutionPayload>()
                .AsNoTracking()
                .Where(p => p.StepExecutionId == step.Id && p.CreatedAt == at)
                .FirstOrDefaultAsync();
            return new StepPayload(
                step.Id,
                payload?.ReceivedInput ?? string.Empty,
                payload?.RequestPayload ?? string.Empty,
                payload?.ResponsePayload ?? string.Empty);
        }

        return await _context.StepExecutions
            .AsNoTracking()
            .Where(s => s.FlowExecutionId == flowExecutionId && s.Id == stepExecutionId)
            .Select(s => new StepPayload(s.Id, s.ReceivedInput, s.RequestPayload, s.ResponsePayload))
            .FirstOrDefaultAsync();
    }

    public async Task AddStepPacketLogAsync(StepPacketLog packet)
    {
        await PacketLogStore.AddAsync(packet);
    }

    public async Task<IEnumerable<StepPacketLog>> GetStepPacketLogsAsync(Guid stepExecutionId)
    {
        return await PacketLogStore.GetByStepExecutionAsync(stepExecutionId);
    }

    public Task<IReadOnlyList<StepPacketLogSummary>> GetStepPacketLogSummariesAsync(Guid flowExecutionId) =>
        PacketLogStore.GetSummariesAsync(flowExecutionId);

    public Task<StepPacketBody?> GetStepPacketBodyAsync(Guid flowExecutionId, Guid packetId) =>
        PacketLogStore.GetBodyAsync(flowExecutionId, packetId);

    public Task<IReadOnlyList<PacketLogListItem>> GetPacketLogPageAsync(PacketLogPageFilter filter) =>
        PacketLogStore.GetPageAsync(filter);

    public Task<PacketLogDetailItem?> GetPacketLogDetailAsync(Guid packetId) =>
        PacketLogStore.GetDetailAsync(packetId);

    public async Task<bool> ReleaseClaimAsync(Guid executionId) =>
        await _context.FlowExecutions
            .Where(e => e.Id == executionId && e.Status == ExecutionStatus.InProgress)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.Status, ExecutionStatus.Queued)) == 1;

    public async Task<IReadOnlyList<Guid>> FailStaleExecutionsAsync(DateTime startedBefore, DateTime testRunsStartedBefore, string errorMessage)
    {
        var candidates = await _context.FlowExecutions
            .AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.InProgress
                && (e.IsTest ? e.StartedAt < testRunsStartedBefore : e.StartedAt < startedBefore))
            .OrderBy(e => e.StartedAt)
            .Select(e => e.Id)
            .Take(200)
            .ToListAsync();

        var failed = new List<Guid>();
        var now = DateTime.UtcNow;
        foreach (var id in candidates)
        {
            var changed = await _context.FlowExecutions
                .Where(e => e.Id == id && e.Status == ExecutionStatus.InProgress
                    && (e.IsTest ? e.StartedAt < testRunsStartedBefore : e.StartedAt < startedBefore))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(e => e.Status, ExecutionStatus.Failed)
                    .SetProperty(e => e.ErrorMessage, errorMessage)
                    .SetProperty(e => e.CompletedAt, now));
            if (changed == 0)
            {
                continue;
            }

            failed.Add(id);
            await _context.StepExecutions
                .Where(step => step.FlowExecutionId == id && step.Status == ExecutionStatus.InProgress)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(step => step.Status, ExecutionStatus.Failed)
                    .SetProperty(step => step.ErrorMessage, errorMessage)
                    .SetProperty(step => step.CompletedAt, now));
        }

        return failed;
    }

    public async Task<QueuedExecutionClaim?> TryClaimNextQueuedExecutionAsync(IReadOnlyCollection<Guid>? excludedFlowIds = null)
    {
        // The claim is an atomic guarded UPDATE (WHERE Status = Queued) so two engines can
        // never take the same row.
        var excluded = excludedFlowIds is { Count: > 0 } ? excludedFlowIds.ToList() : null;
        var queued = _context.FlowExecutions
            .AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Queued);
        if (excluded != null)
        {
            queued = queued.Where(e => !excluded.Contains(e.FlowId));
        }

        var nextId = await queued
            .OrderBy(e => e.QueuedAt ?? e.StartedAt)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync();

        if (nextId == null)
        {
            return null;
        }

        var claimed = await _context.FlowExecutions
            .Where(e => e.Id == nextId.Value && e.Status == ExecutionStatus.Queued)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.Status, ExecutionStatus.InProgress)
                .SetProperty(e => e.StartedAt, DateTime.UtcNow));

        if (claimed == 0)
        {
            return null; // lost the race to another engine instance; poll again
        }

        var execution = await _context.FlowExecutions
            .AsNoTracking()
            .FirstAsync(e => e.Id == nextId.Value);
        var triggerPayload = _postgres
            ? await _context.Set<FlowExecutionPayload>().AsNoTracking()
                .Where(row => row.FlowExecutionId == execution.Id)
                .Select(row => row.TriggerPayloadJson)
                .FirstOrDefaultAsync()
            : execution.TriggerPayloadJson;

        return new QueuedExecutionClaim(
            execution.Id,
            execution.FlowId,
            execution.TriggerSource,
            triggerPayload,
            execution.FlowName,
            execution.IntegrationId,
            execution.IntegrationName,
            execution.TraceParent,
            execution.IsTest,
            execution.TestCaseId,
            execution.TestRunId,
            execution.QueuedAt);
    }

    public async Task<IReadOnlyList<Guid>> GetCancelledExecutionIdsAsync(IReadOnlyCollection<Guid> executionIds)
    {
        if (executionIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        // The API marks running executions Cancelled in this same table; the Engine's
        // watcher turns those rows into local CancellationToken cancellations.
        return await _context.FlowExecutions
            .AsNoTracking()
            .Where(e => executionIds.Contains(e.Id) && e.Status == ExecutionStatus.Cancelled)
            .Select(e => e.Id)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<DeadLetterEntry>> GetReplayableDeadLettersAsync(int batchSize)
    {
        var take = batchSize < 1 ? 100 : batchSize;

        // Manual requests first: an old backlog of Pending entries must never fill the whole batch and
        // starve a replay the operator just asked for. Each half is its own indexed, ordered, limited
        // query (ReplayRequestedAt index, Status+CreatedAt index). One query with OR and a computed
        // ORDER BY forced a join and a sort over every pending entry on each scan.
        var manual = await _context.DeadLetterEntries.AsNoTracking()
            .Where(entry => entry.ReplayRequestedAt != null
                && !_context.FlowExecutions.Any(run => run.Id == entry.FlowExecutionId && run.IsTest))
            .OrderBy(entry => entry.CreatedAt)
            .Take(take)
            .ToListAsync();
        if (manual.Count >= take)
        {
            return manual;
        }

        // No "ReplayRequestedAt IS NULL" here on purpose: that predicate lets the planner pick the
        // ReplayRequestedAt index (nearly every row is NULL) and sort the whole table instead of walking
        // Status+CreatedAt. Entries that are also manual requests are removed in memory instead.
        var manualIds = manual.Select(entry => entry.Id).ToHashSet();
        var pending = (await _context.DeadLetterEntries.AsNoTracking()
            .Where(entry => entry.Status == "Pending"
                && !_context.FlowExecutions.Any(run => run.Id == entry.FlowExecutionId && run.IsTest))
            .OrderBy(entry => entry.CreatedAt)
            .Take(take)
            .ToListAsync())
            .Where(entry => !manualIds.Contains(entry.Id) && entry.ReplayRequestedAt == null)
            .Take(take - manual.Count)
            .ToList();

        manual.AddRange(pending);
        return manual;
    }

    public async Task<IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)>> GetUnalertedDeadLettersAsync(
        DateTime since, int max)
    {
        var rows = await (from entry in _context.DeadLetterEntries.AsNoTracking()
                          join run in _context.FlowExecutions.AsNoTracking()
                              on entry.FlowExecutionId equals run.Id into runs
                          from run in runs.DefaultIfEmpty()
                          where entry.AlertedAt == null
                                && entry.CreatedAt >= since
                                && (run == null || !run.IsTest)
                          orderby entry.CreatedAt
                          select new
                          {
                              entry,
                              FlowId = run == null ? (Guid?)null : run.FlowId
                          })
                         .Take(max < 1 ? 200 : max)
                         .ToListAsync();

        return rows.Select(row => (row.entry, row.FlowId)).ToList();
    }

    public async Task<int> MarkDeadLettersAlertedAsync(IReadOnlyCollection<Guid> ids, DateTime at)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        return await _context.DeadLetterEntries
            .Where(entry => ids.Contains(entry.Id) && entry.AlertedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.AlertedAt, at));
    }


    public async Task AddDeadLetterEntryAsync(DeadLetterEntry entry)

    {
        _context.DeadLetterEntries.Add(entry);
        StageDeadLetterPayload(entry);
        await _context.SaveChangesAsync();
    }

    public async Task<DeadLetterEntry?> GetDeadLetterAsync(Guid id)
    {
        var entry = await _context.DeadLetterEntries.FirstOrDefaultAsync(d => d.Id == id);
        if (_postgres && entry != null)
        {
            await HydrateDeadLetterPayloadAsync(entry);
        }

        return entry;
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize)
    {
        return await (from entry in _context.DeadLetterEntries
                      join run in _context.FlowExecutions on entry.FlowExecutionId equals run.Id into runs
                      from run in runs.DefaultIfEmpty()
                      where entry.Status == "Pending" && (run == null || !run.IsTest)
                      orderby entry.CreatedAt
                      select entry)
            .Take(batchSize)
            .ToListAsync();
    }


    public async Task<IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)>> GetDeadLetterPageAsync(DeadLetterPageFilter filter)
    {
        var query =
            from entry in _context.DeadLetterEntries.AsNoTracking()
            join run in _context.FlowExecutions.AsNoTracking() on entry.FlowExecutionId equals run.Id into runs
            from run in runs.DefaultIfEmpty()
            where (filter.Status == null || entry.Status == filter.Status)
                && (filter.IncludeTests || run == null || !run.IsTest)
                && (filter.FlowId == null || (run != null && run.FlowId == filter.FlowId))
                && (filter.IntegrationId == null || (run != null && run.IntegrationId == filter.IntegrationId))
                && (filter.FlowExecutionId == null || entry.FlowExecutionId == filter.FlowExecutionId)
                && (filter.StepId == null || entry.StepId == filter.StepId)
            select new { entry, FlowId = run == null ? (Guid?)null : run.FlowId };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x =>
                x.entry.NodeName.Contains(search) ||
                x.entry.FlowName.Contains(search) ||
                x.entry.IntegrationName.Contains(search) ||
                x.entry.ErrorMessage.Contains(search));
        }
        if (filter.Hours.HasValue)
        {
            var since = DateTime.UtcNow.AddHours(-filter.Hours.Value);
            query = query.Where(x => x.entry.CreatedAt >= since);
        }

        var rows = await query
            .OrderByDescending(x => x.entry.CreatedAt)
            .Skip((Math.Max(1, filter.Page) - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();
        return rows.Select(x => (x.entry, x.FlowId)).ToList();
    }

    public async Task<bool> TryClaimDeadLetterForManualReplayAsync(Guid id) =>
        await _context.DeadLetterEntries
            .Where(entry => entry.Id == id && entry.ReplayRequestedAt != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.ReplayRequestedAt, (DateTime?)null)) == 1;

    public async Task<bool> TryClaimDeadLetterForAutoRetryAsync(Guid id) =>
        await _context.DeadLetterEntries
            .Where(entry => entry.Id == id && entry.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entry => entry.Status, "Retrying")
                .SetProperty(entry => entry.LastRetriedAt, DateTime.UtcNow)) == 1;

    // Queues a manual replay exactly once: the update only matches while no replay is already
    // queued, so concurrent retry clicks leave a single pending replay request for the worker.
    public async Task<bool> TryRequestManualReplayAsync(
        Guid id,
        Guid startStepId,
        DateTime requestedAt,
        string? flowStateJson = null)
    {
        if (!_postgres)
        {
            return await TryRequestManualReplayInRowAsync(id, startStepId, requestedAt, flowStateJson);
        }

        // PostgreSQL: the flow state lives in DeadLetterPayloads. The guarded update and the edited state commit
        // together, so a losing concurrent click still cannot overwrite the winner's state.
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var claimed = await _context.DeadLetterEntries
            .Where(entry => entry.Id == id && entry.ReplayRequestedAt == null &&
                (entry.Status == "Pending" || entry.Status == "Retrying"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entry => entry.Status, "Retrying")
                .SetProperty(entry => entry.ReplayFromStepId, startStepId)
                .SetProperty(entry => entry.ReplayRequestedAt, requestedAt)) == 1;
        if (claimed && flowStateJson != null)
        {
            var state = Storage.PostgresPacketIngestor.Clean(flowStateJson);
            var updated = await _context.Set<DeadLetterPayload>()
                .Where(row => row.DeadLetterId == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FlowStateJson, state));
            if (updated == 0)
            {
                _context.Set<DeadLetterPayload>().Add(new DeadLetterPayload { DeadLetterId = id, FlowStateJson = state });
                await _context.SaveChangesAsync();
            }
        }

        await transaction.CommitAsync();
        return claimed;
    }

    private async Task<bool> TryRequestManualReplayInRowAsync(
        Guid id,
        Guid startStepId,
        DateTime requestedAt,
        string? flowStateJson) =>
        await _context.DeadLetterEntries
            .Where(entry => entry.Id == id && entry.ReplayRequestedAt == null &&
                (entry.Status == "Pending" || entry.Status == "Retrying"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(entry => entry.Status, "Retrying")
                .SetProperty(entry => entry.ReplayFromStepId, startStepId)
                .SetProperty(entry => entry.ReplayRequestedAt, requestedAt)
                // A null edit leaves the captured state untouched; the update stays atomic,
                // so a losing concurrent click can never overwrite the winner's state.
                .SetProperty(entry => entry.FlowStateJson, entry => flowStateJson ?? entry.FlowStateJson)) == 1;

    public async Task<int> ResetStaleRetryingDeadLettersAsync(TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        return await _context.DeadLetterEntries
            .Where(entry => entry.Status == "Retrying" && entry.ReplayRequestedAt == null &&
                (entry.LastRetriedAt == null || entry.LastRetriedAt < cutoff))
            .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.Status, "Pending"));
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null)
    {
        var query = from entry in _context.DeadLetterEntries
                    join run in _context.FlowExecutions on entry.FlowExecutionId equals run.Id into runs
                    from run in runs.DefaultIfEmpty()
                    where run == null || !run.IsTest
                    select entry;

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(d => d.Status == status);
        }

        return await query
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId)
    {
        var isTest = await _context.FlowExecutions
            .Where(execution => execution.Id == flowExecutionId)
            .Select(execution => (bool?)execution.IsTest)
            .FirstOrDefaultAsync();
        if (isTest == true) return Array.Empty<DeadLetterEntry>();

        return await _context.DeadLetterEntries
            .Where(d => d.FlowExecutionId == flowExecutionId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry)
    {
        _context.DeadLetterEntries.Update(entry);
        await _context.SaveChangesAsync();
    }

    private const string LikeEscape = "\\";

    // SQLite LIKE has no bracket escaping; wildcards are neutralised with an ESCAPE character.
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
