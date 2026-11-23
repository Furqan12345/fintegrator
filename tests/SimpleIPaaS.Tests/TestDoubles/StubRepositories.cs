using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Tests.TestDoubles;

public sealed class StubConnectionRepository : IConnectionRepository
{
    private readonly Dictionary<Guid, Connection> _connections = new();

    public void Seed(Connection connection) => _connections[connection.Id] = connection;

    public List<Connection> Updated { get; } = new();

    public Task<Connection?> GetByIdAsync(Guid id) =>
        Task.FromResult(_connections.TryGetValue(id, out var connection) ? connection : null);

    public Task<IEnumerable<Connection>> GetAllAsync() =>
        Task.FromResult<IEnumerable<Connection>>(_connections.Values.ToList());

    public Task AddAsync(Connection connection)
    {
        _connections[connection.Id] = connection;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Connection connection)
    {
        _connections[connection.Id] = connection;
        Updated.Add(connection);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        _connections.Remove(id);
        return Task.CompletedTask;
    }
}

public sealed class PassthroughEncryptionService : IEncryptionService
{
    public Task<string> EncryptAsync(string plainText) => Task.FromResult(plainText);

    public Task<string> DecryptAsync(string cipherText) => Task.FromResult(cipherText);
}

public sealed class StubIntegrationRepository : IIntegrationRepository
{
    private readonly Dictionary<Guid, IntegrationFlow> _flows = new();

    public void Seed(IntegrationFlow flow) => _flows[flow.Id] = flow;

    public Task<IntegrationFlow?> GetByIdAsync(Guid id) =>
        Task.FromResult(_flows.TryGetValue(id, out var flow) ? flow : null);

    public Task<IEnumerable<IntegrationFlow>> GetAllAsync() =>
        Task.FromResult<IEnumerable<IntegrationFlow>>(_flows.Values.ToList());

    public Task<IEnumerable<IntegrationFlow>> GetAllSummariesAsync() => GetAllAsync();

    public Task<IEnumerable<IntegrationFlow>> GetByIntegrationIdAsync(Guid integrationId) =>
        Task.FromResult<IEnumerable<IntegrationFlow>>(_flows.Values.Where(f => f.IntegrationId == integrationId).ToList());

    public Task AddAsync(IntegrationFlow flow)
    {
        _flows[flow.Id] = flow;
        return Task.CompletedTask;
    }

    public List<FlowVersion> Versions { get; } = new();

    public Task<IReadOnlyList<FlowVersion>> GetVersionsAsync(Guid flowId) =>
        Task.FromResult<IReadOnlyList<FlowVersion>>(Versions.Where(version => version.FlowId == flowId).ToList());

    public Task<FlowVersion?> PublishAsync(Guid flowId, string? changeNote = null) =>
        Task.FromResult<FlowVersion?>(null);

    public Task<IntegrationFlow?> RollbackAsync(Guid flowId, int versionNumber, string? changeNote = null) =>
        Task.FromResult<IntegrationFlow?>(null);
    public Task<bool> UpdateAsync(IntegrationFlow flow)
    {
        _flows[flow.Id] = flow;
        return Task.FromResult(true);
    }

    public Task DeleteAsync(Guid id)
    {
        _flows.Remove(id);
        return Task.CompletedTask;
    }

    public Task UpdatePersistedStateAsync(Guid flowId, string persistedStateJson)
    {
        if (_flows.TryGetValue(flowId, out var flow)) flow.PersistedStateJson = persistedStateJson;
        return Task.CompletedTask;
    }

    public Task<string> GetPersistedStateAsync(Guid flowId) =>
        Task.FromResult(_flows.TryGetValue(flowId, out var flow) ? flow.PersistedStateJson : "{}");

    public Task<bool> UpdateWebhookSecretAsync(Guid flowId, string webhookSecret)
    {
        if (!_flows.TryGetValue(flowId, out var flow)) return Task.FromResult(false);
        flow.WebhookSecret = webhookSecret;
        return Task.FromResult(true);
    }

    public Task<IntegrationFlow?> GetFlowForTriggerAsync(Guid id) => GetByIdAsync(id);

    public Task<IEnumerable<IntegrationFlow>> GetActiveCronFlowsAsync() =>
        Task.FromResult<IEnumerable<IntegrationFlow>>(_flows.Values.Where(f => f.TriggerType == TriggerType.Cron).ToList());

    public Task<IntegrationFlow?> SetScheduleStatusAsync(Guid flowId, FlowStatus status)
    {
        if (!_flows.TryGetValue(flowId, out var flow)) return Task.FromResult<IntegrationFlow?>(null);
        flow.Status = status;
        flow.NextRunAt = status == FlowStatus.Active && flow.RunAt > DateTime.UtcNow ? flow.RunAt : null;
        return Task.FromResult<IntegrationFlow?>(flow);
    }
}

public sealed class StubExecutionRepository : IExecutionRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, FlowExecution> _flowExecutions = new();

    public void Seed(FlowExecution execution) => _flowExecutions[execution.Id] = execution;

    public List<StepExecution> StepExecutions { get; } = new();
    public List<StepPacketLog> StepPacketLogs { get; } = new();

    public List<DeadLetterEntry> DeadLetters { get; } = new();

    public Exception? StepPacketLogException { get; set; }
    public Exception? DeadLetterException { get; set; }

    public Task<FlowExecution?> GetFlowExecutionAsync(Guid id) =>
        Task.FromResult(_flowExecutions.TryGetValue(id, out var execution) ? execution : null);

    public Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(ExecutionQuery query) =>
        Task.FromResult<IEnumerable<FlowExecution>>(_flowExecutions.Values.ToList());

    public Task AddFlowExecutionAsync(FlowExecution execution)
    {
        lock (_lock) { _flowExecutions[execution.Id] = execution; }
        return Task.CompletedTask;
    }

    public Task AddFlowExecutionsAsync(IReadOnlyCollection<FlowExecution> executions)
    {
        lock (_lock)
        {
            foreach (var execution in executions)
            {
                _flowExecutions[execution.Id] = execution;
            }
        }
        return Task.CompletedTask;
    }

    public Task UpdateFlowExecutionAsync(FlowExecution execution)
    {
        lock (_lock) { _flowExecutions[execution.Id] = execution; }
        return Task.CompletedTask;
    }

    public Task AddStepExecutionAsync(StepExecution execution)
    {
        lock (_lock) { StepExecutions.Add(execution); }
        return Task.CompletedTask;
    }

    public Task UpdateStepExecutionAsync(StepExecution execution) => Task.CompletedTask;

    public Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId) =>
        Task.FromResult<IEnumerable<StepExecution>>(StepExecutions.Where(s => s.FlowExecutionId == flowExecutionId).ToList());

    public Task<StepExecution?> GetStepExecutionAsync(Guid flowExecutionId, Guid stepId) =>
        Task.FromResult(StepExecutions.LastOrDefault(s => s.FlowExecutionId == flowExecutionId && s.StepId == stepId));

    public Task<IReadOnlyList<StepExecutionSummary>> GetStepExecutionSummariesAsync(Guid flowExecutionId) =>
        Task.FromResult<IReadOnlyList<StepExecutionSummary>>(StepExecutions
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
            .ToList());

    public Task<StepPayload?> GetStepPayloadAsync(Guid flowExecutionId, Guid stepExecutionId) =>
        Task.FromResult(StepExecutions
            .Where(s => s.FlowExecutionId == flowExecutionId && s.Id == stepExecutionId)
            .Select(s => new StepPayload(s.Id, s.ReceivedInput, s.RequestPayload, s.ResponsePayload))
            .FirstOrDefault());

    public Task AddStepPacketLogAsync(StepPacketLog packet)
    {
        if (StepPacketLogException != null) throw StepPacketLogException;
        lock (_lock) { StepPacketLogs.Add(packet); }
        return Task.CompletedTask;
    }

    public Task<IEnumerable<StepPacketLog>> GetStepPacketLogsAsync(Guid stepExecutionId) =>
        Task.FromResult<IEnumerable<StepPacketLog>>(StepPacketLogs
            .Where(packet => packet.StepExecutionId == stepExecutionId)
            .OrderBy(packet => packet.Sequence)
            .ToList());

    public Task<IReadOnlyList<StepPacketLogSummary>> GetStepPacketLogSummariesAsync(Guid flowExecutionId)
    {
        var stepIds = StepExecutions
            .Where(s => s.FlowExecutionId == flowExecutionId)
            .Select(s => s.Id)
            .ToHashSet();

        return Task.FromResult<IReadOnlyList<StepPacketLogSummary>>(StepPacketLogs
            .Where(packet => stepIds.Contains(packet.StepExecutionId))
            .OrderBy(packet => packet.StepExecutionId)
            .ThenBy(packet => packet.Sequence)
            .Select(packet => new StepPacketLogSummary(
                packet.Id,
                packet.StepExecutionId,
                packet.Sequence,
                packet.Kind,
                packet.PageNumber,
                packet.Attempt,
                packet.HttpMethod,
                packet.RequestUrl,
                packet.StatusCode,
                packet.StartedAt,
                packet.CompletedAt,
                packet.DurationMs,
                packet.Error,
                packet.RequestHeadersJson.Length,
                packet.RequestBody.Length,
                packet.ResponseHeadersJson.Length,
                packet.ResponseBody.Length))
            .ToList());
    }

    public Task<StepPacketBody?> GetStepPacketBodyAsync(Guid flowExecutionId, Guid packetId)
    {
        var stepIds = StepExecutions
            .Where(s => s.FlowExecutionId == flowExecutionId)
            .Select(s => s.Id)
            .ToHashSet();

        return Task.FromResult(StepPacketLogs
            .Where(packet => packet.Id == packetId && stepIds.Contains(packet.StepExecutionId))
            .Select(packet => new StepPacketBody(
                packet.Id,
                packet.RequestHeadersJson,
                packet.RequestBody,
                packet.ResponseHeadersJson,
                packet.ResponseBody))
            .FirstOrDefault());
    }

    public Task<IReadOnlyList<PacketLogListItem>> GetPacketLogPageAsync(PacketLogPageFilter filter)
    {
        var rows = StepPacketLogs
            .Where(packet => packet.Kind == "HTTP")
            .Join(StepExecutions, packet => packet.StepExecutionId, step => step.Id, (packet, step) => new { packet, step })
            .Join(_flowExecutions.Values, x => x.step.FlowExecutionId, run => run.Id, (x, run) => new { x.packet, x.step, run })
            .Where(x => (filter.FlowId == null || x.run.FlowId == filter.FlowId) && (filter.FlowExecutionId == null || x.run.Id == filter.FlowExecutionId))
            .Where(x => filter.Method == null || x.packet.HttpMethod == filter.Method)
            .Where(x => string.Equals(filter.Outcome, "success", StringComparison.OrdinalIgnoreCase)
                ? x.packet.StatusCode != null && x.packet.StatusCode >= 200 && x.packet.StatusCode < 300
                : !string.Equals(filter.Outcome, "failure", StringComparison.OrdinalIgnoreCase) || x.packet.StatusCode == null || x.packet.StatusCode < 200 || x.packet.StatusCode >= 300)
            .Where(x => filter.Hours == null || x.packet.StartedAt >= DateTime.UtcNow.AddHours(-filter.Hours.Value))
            .Where(x => string.IsNullOrWhiteSpace(filter.Search) ||
                        x.packet.RequestUrl.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) ||
                        x.run.FlowName.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) ||
                        x.step.NodeName.Contains(filter.Search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.packet.StartedAt)
            .Skip((Math.Max(1, filter.Page) - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(x => new PacketLogListItem(
                x.packet.Id, x.run.Id, x.packet.StartedAt, x.run.FlowName, x.run.IntegrationName, x.step.NodeName,
                null, x.packet.HttpMethod, x.packet.RequestUrl, x.packet.StatusCode, x.packet.DurationMs,
                x.packet.PageNumber, x.packet.Attempt, x.packet.Error))
            .ToList();
        return Task.FromResult<IReadOnlyList<PacketLogListItem>>(rows);
    }

    public Task<PacketLogDetailItem?> GetPacketLogDetailAsync(Guid packetId)
    {
        var row = StepPacketLogs
            .Where(packet => packet.Id == packetId)
            .Join(StepExecutions, packet => packet.StepExecutionId, step => step.Id, (packet, step) => new { packet, step })
            .Join(_flowExecutions.Values, x => x.step.FlowExecutionId, run => run.Id, (x, run) => new { x.packet, x.step, run })
            .Select(x => new PacketLogDetailItem(
                x.packet.Id, x.run.Id, x.packet.StartedAt, x.packet.CompletedAt, x.packet.DurationMs,
                x.run.FlowName, x.run.IntegrationName, x.step.NodeName, null, x.packet.HttpMethod, x.packet.RequestUrl,
                x.packet.StatusCode, x.packet.PageNumber, x.packet.Attempt, x.packet.Error,
                x.packet.RequestHeadersJson, x.packet.RequestBody, x.packet.ResponseHeadersJson, x.packet.ResponseBody))
            .FirstOrDefault();
        return Task.FromResult(row);
    }

    public Task AddDeadLetterEntryAsync(DeadLetterEntry entry)
    {
        if (DeadLetterException != null) throw DeadLetterException;
        lock (_lock) { DeadLetters.Add(entry); }
        return Task.CompletedTask;
    }

    public Task<DeadLetterEntry?> GetDeadLetterAsync(Guid id) =>
        Task.FromResult(DeadLetters.FirstOrDefault(d => d.Id == id));

    public Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.Where(d => d.Status == "Pending").Take(batchSize).ToList());

    public Task<QueuedExecutionClaim?> TryClaimNextQueuedExecutionAsync()
    {
        lock (_lock)
        {
            var next = _flowExecutions.Values
                .Where(e => e.Status == ExecutionStatus.Queued)
                .OrderBy(e => e.StartedAt)
                .FirstOrDefault();
            if (next == null)
            {
                return Task.FromResult<QueuedExecutionClaim?>(null);
            }

            next.Status = ExecutionStatus.InProgress;
            next.StartedAt = DateTime.UtcNow;

            return Task.FromResult<QueuedExecutionClaim?>(new QueuedExecutionClaim(
                next.Id,
                next.FlowId,
                next.TriggerSource,
                next.TriggerPayloadJson));
        }
    }

    public Task<IReadOnlyList<Guid>> GetCancelledExecutionIdsAsync(IReadOnlyCollection<Guid> executionIds)
    {
        lock (_lock)
        {
            IReadOnlyList<Guid> cancelled = executionIds
                .Where(id => _flowExecutions.TryGetValue(id, out var execution) && execution.Status == ExecutionStatus.Cancelled)
                .ToList();

            return Task.FromResult(cancelled);
        }
    }

    public Task<IReadOnlyList<DeadLetterEntry>> GetReplayableDeadLettersAsync(int batchSize)
    {
        lock (_lock)
        {
            IReadOnlyList<DeadLetterEntry> entries = DeadLetters
                .Where(d => d.Status == "Pending" || d.ReplayRequestedAt != null)
                .OrderBy(d => d.ReplayRequestedAt == null)
                .ThenBy(d => d.CreatedAt)
                .Take(batchSize < 1 ? 100 : batchSize)
                .ToList();

            return Task.FromResult(entries);
        }
    }

    public Task<IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)>> GetUnalertedDeadLettersAsync(
        DateTime since, int max)
    {
        lock (_lock)
        {
            IReadOnlyList<(DeadLetterEntry, Guid?)> rows = DeadLetters
                .Where(entry => entry.AlertedAt == null && entry.CreatedAt >= since)
                .OrderBy(entry => entry.CreatedAt)
                .Take(max < 1 ? 200 : max)
                .Select(entry => (entry, _flowExecutions.TryGetValue(entry.FlowExecutionId, out var run)
                    ? (Guid?)run.FlowId
                    : null))
                .ToList();

            return Task.FromResult(rows);
        }
    }

    public Task<int> MarkDeadLettersAlertedAsync(IReadOnlyCollection<Guid> ids, DateTime at)
    {
        lock (_lock)
        {
            var matches = DeadLetters
                .Where(entry => ids.Contains(entry.Id) && entry.AlertedAt == null)
                .ToList();
            foreach (var entry in matches)
            {
                entry.AlertedAt = at;
            }

            return Task.FromResult(matches.Count);
        }
    }

    public Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.ToList());

    public Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.Where(d => d.FlowExecutionId == flowExecutionId).ToList());

    public Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry) => Task.CompletedTask;

    public Task<IReadOnlyList<(DeadLetterEntry Entry, Guid? FlowId)>> GetDeadLetterPageAsync(DeadLetterPageFilter filter)
    {
        IEnumerable<DeadLetterEntry> query = DeadLetters;
        if (!filter.IncludeTests)
            query = query.Where(entry => !_flowExecutions.TryGetValue(entry.FlowExecutionId, out var run) || !run.IsTest);
        if (filter.Status != null) query = query.Where(entry => entry.Status == filter.Status);
        if (filter.FlowId != null)
            query = query.Where(entry =>
                _flowExecutions.TryGetValue(entry.FlowExecutionId, out var run) && run.FlowId == filter.FlowId);
        if (filter.FlowExecutionId != null)
            query = query.Where(entry => entry.FlowExecutionId == filter.FlowExecutionId);
        if (filter.StepId != null) query = query.Where(entry => entry.StepId == filter.StepId);
        if (filter.Hours != null) query = query.Where(entry => entry.CreatedAt >= DateTime.UtcNow.AddHours(-filter.Hours.Value));
        if (!string.IsNullOrWhiteSpace(filter.Search))
            query = query.Where(entry =>
                entry.NodeName.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) ||
                entry.FlowName.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) ||
                entry.ErrorMessage.Contains(filter.Search, StringComparison.OrdinalIgnoreCase));
        var rows = query
            .OrderByDescending(entry => entry.CreatedAt)
            .Skip((Math.Max(1, filter.Page) - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(entry => (entry, _flowExecutions.TryGetValue(entry.FlowExecutionId, out var run)
                ? (Guid?)run.FlowId
                : null))
            .ToList();
        return Task.FromResult<IReadOnlyList<(DeadLetterEntry, Guid?)>>(rows);
    }

    public Task<bool> TryClaimDeadLetterForManualReplayAsync(Guid id)
    {
        lock (_lock)
        {
            var entry = DeadLetters.FirstOrDefault(item => item.Id == id && item.ReplayRequestedAt != null);
            if (entry == null) return Task.FromResult(false);
            entry.ReplayRequestedAt = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryClaimDeadLetterForAutoRetryAsync(Guid id)
    {
        lock (_lock)
        {
            var entry = DeadLetters.FirstOrDefault(item => item.Id == id && item.Status == "Pending");
            if (entry == null) return Task.FromResult(false);
            entry.Status = "Retrying";
            entry.LastRetriedAt = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryRequestManualReplayAsync(
        Guid id,
        Guid startStepId,
        DateTime requestedAt,
        string? flowStateJson = null)
    {
        lock (_lock)
        {
            var entry = DeadLetters.FirstOrDefault(item => item.Id == id && item.ReplayRequestedAt == null &&
                (item.Status == "Pending" || item.Status == "Retrying"));
            if (entry == null) return Task.FromResult(false);
            entry.Status = "Retrying";
            entry.ReplayFromStepId = startStepId;
            entry.ReplayRequestedAt = requestedAt;
            if (flowStateJson != null)
            {
                entry.FlowStateJson = flowStateJson;
            }
            return Task.FromResult(true);
        }
    }

    public Task<int> ResetStaleRetryingDeadLettersAsync(TimeSpan age)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - age;
            var stale = DeadLetters.Where(entry => entry.Status == "Retrying" && entry.ReplayRequestedAt == null &&
                (entry.LastRetriedAt == null || entry.LastRetriedAt < cutoff)).ToList();
            foreach (var entry in stale) entry.Status = "Pending";
            return Task.FromResult(stale.Count);
        }
    }
}

public sealed class StubFlowTestRepository : IFlowTestRepository
{
    private readonly Dictionary<Guid, FlowTestCase> _cases = new();
    private readonly List<FlowTestRun> _runs = new();

    public IReadOnlyList<FlowTestRun> Runs => _runs;
    public Exception? TrimException { get; set; }

    public void Seed(FlowTestCase testCase) => _cases[testCase.Id] = testCase;

    public Task<IReadOnlyList<FlowTestCase>> GetByFlowIdAsync(Guid flowId) =>
        Task.FromResult<IReadOnlyList<FlowTestCase>>(_cases.Values.Where(testCase => testCase.FlowId == flowId).ToList());

    public Task<FlowTestCase?> GetAsync(Guid id) =>
        Task.FromResult(_cases.TryGetValue(id, out var testCase) ? testCase : null);

    public Task AddAsync(FlowTestCase testCase)
    {
        _cases[testCase.Id] = testCase;
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(FlowTestCase testCase)
    {
        _cases[testCase.Id] = testCase;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id) => Task.FromResult(_cases.Remove(id));

    public Task AddRunAsync(FlowTestRun run)
    {
        _runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<FlowTestRun?> GetRunAsync(Guid id) =>
        Task.FromResult(_runs.FirstOrDefault(run => run.Id == id));

    public Task<IReadOnlyList<FlowTestRun>> GetRunsAsync(Guid testCaseId) =>
        Task.FromResult<IReadOnlyList<FlowTestRun>>(_runs.Where(run => run.TestCaseId == testCaseId).ToList());

    public Task<IReadOnlyList<FlowTestRun>> GetRunsBySuiteAsync(Guid suiteRunId) =>
        Task.FromResult<IReadOnlyList<FlowTestRun>>(_runs.Where(run => run.SuiteRunId == suiteRunId).ToList());

    public Task<FlowTestRun?> GetLatestRunAsync(Guid testCaseId) =>
        Task.FromResult(_runs.Where(run => run.TestCaseId == testCaseId).OrderByDescending(run => run.StartedAt).FirstOrDefault());

    public Task<IReadOnlyList<FlowTestRun>> GetInterruptedRunsAsync() =>
        Task.FromResult<IReadOnlyList<FlowTestRun>>(_runs.Where(run => run.Status is "Queued" or "Running").ToList());

    public Task UpdateRunAsync(FlowTestRun run)
    {
        var index = _runs.FindIndex(candidate => candidate.Id == run.Id);
        if (index >= 0) _runs[index] = run;
        return Task.CompletedTask;
    }

    public Task RecordRunTimestampsAsync(Guid testCaseId, DateTime at, bool passed)
    {
        if (_cases.TryGetValue(testCaseId, out var testCase))
        {
            testCase.LastRunAt = at;
            if (passed) testCase.LastPassedAt = at;
        }
        return Task.CompletedTask;
    }

    public Task TrimRunsAsync(Guid testCaseId, int keep = 20)
    {
        if (TrimException != null) throw TrimException;
        var stale = _runs.Where(run => run.TestCaseId == testCaseId)
            .OrderByDescending(run => run.StartedAt).Skip(keep).ToList();
        foreach (var run in stale) _runs.Remove(run);
        return Task.CompletedTask;
    }
}

public sealed class StubCrossReferenceRepository : ICrossReferenceRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CrossReferenceList> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CrossReferenceEntry> _entries = new();

    public IReadOnlyList<CrossReferenceEntry> Entries => _entries;

    public void SeedKeys(string listName, params string[] keys)
    {
        lock (_lock)
        {
            foreach (var key in keys)
            {
                _entries.Add(new CrossReferenceEntry { ListName = listName, KeyValue = key });
            }
        }
    }

    public Task<IReadOnlyList<CrossReferenceListSummary>> GetListsAsync() =>
        Task.FromResult<IReadOnlyList<CrossReferenceListSummary>>(_lists.Values
            .Select(list => new CrossReferenceListSummary(list, _entries.Count(e => e.ListName == list.Name)))
            .ToList());

    public Task<CrossReferenceList?> GetListAsync(string name) =>
        Task.FromResult(_lists.TryGetValue(name, out var list) ? list : null);

    public Task<CrossReferenceList> EnsureListAsync(string name, string description)
    {
        lock (_lock)
        {
            if (!_lists.TryGetValue(name, out var list))
            {
                list = new CrossReferenceList { Name = name, Description = description };
                _lists[name] = list;
            }
            return Task.FromResult(list);
        }
    }

    public Task<bool> DeleteListAsync(string name)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.ListName == name);
            return Task.FromResult(_lists.Remove(name));
        }
    }

    public Task<int> ClearListAsync(string name) =>
        Task.FromResult(_entries.RemoveAll(e => e.ListName == name));

    public Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys)
    {
        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyCollection<string>>(_entries
                .Where(e => e.ListName == listName && wanted.Contains(e.KeyValue))
                .Select(e => e.KeyValue)
                .ToHashSet(StringComparer.Ordinal));
        }
    }

    public Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries)
    {
        var inserted = 0;

        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (_entries.Any(e => e.ListName == listName && e.KeyValue == entry.KeyValue))
                {
                    continue;
                }

                entry.ListName = listName;
                _entries.Add(entry);
                inserted++;
            }
        }

        return Task.FromResult(inserted);
    }

    public Task<CrossReferenceEntryPage> GetEntriesAsync(string listName, string? search, int page, int pageSize)
    {
        var matches = _entries.Where(e => e.ListName == listName).ToList();
        return Task.FromResult(new CrossReferenceEntryPage(matches, matches.Count));
    }

    public Task<bool> DeleteEntryAsync(Guid id) =>
        Task.FromResult(_entries.RemoveAll(e => e.Id == id) > 0);
}

public sealed class StubIntegrationCatalogRepository : IIntegrationCatalogRepository
{
    private readonly List<Integration> _integrations = new();

    public void Seed(Integration integration) => _integrations.Add(integration);

    public Task<Integration?> GetByIdAsync(Guid id) =>
        Task.FromResult(_integrations.FirstOrDefault(item => item.Id == id));

    public Task<IEnumerable<Integration>> GetAllAsync() =>
        Task.FromResult<IEnumerable<Integration>>(_integrations.ToList());

    public Task<IEnumerable<Integration>> GetAllSummariesAsync() => GetAllAsync();

    public Task AddAsync(Integration integration)
    {
        _integrations.Add(integration);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Integration integration) => Task.CompletedTask;

    public Task DeleteAsync(Guid id)
    {
        _integrations.RemoveAll(item => item.Id == id);
        return Task.CompletedTask;
    }
}

public sealed class StubLogRepository : ILogRepository
{
    public List<AppLogEntry> Entries { get; } = new();

    public Task<LogPage> GetPageAsync(LogQuery query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        IEnumerable<AppLogEntry> filtered = Entries;
        if (!string.IsNullOrWhiteSpace(query.MinimumLevel))
            filtered = filtered.Where(entry => string.Equals(entry.Level, query.MinimumLevel, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.Search))
            filtered = filtered.Where(entry =>
                entry.Message.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                (entry.Exception ?? string.Empty).Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        if (query.FlowId.HasValue)
            filtered = filtered.Where(entry => entry.FlowId == query.FlowId);
        if (query.FlowExecutionId.HasValue)
            filtered = filtered.Where(entry => entry.FlowExecutionId == query.FlowExecutionId);
        if (query.SinceUtc.HasValue)
            filtered = filtered.Where(entry => entry.Timestamp >= query.SinceUtc);

        var items = filtered.OrderByDescending(entry => entry.Timestamp).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new LogPage(items, page, pageSize, items.Count, 0));
    }

    public Task<LogTail> GetTailAsync(long afterId, LogQuery query, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(new LogTail(new List<AppLogEntry>(), 0, false));

    public Task<IReadOnlyList<string>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(new List<string>());

    public Task<LogFacets> GetFacetsAsync(LogQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(new LogFacets(new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>()));

    public Task<IReadOnlyList<AppLogEntry>> GetExecutionTimelineAsync(Guid executionId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AppLogEntry>>(Entries.Where(entry => entry.FlowExecutionId == executionId).Take(limit).ToList());

    public Task<TelemetryHealthSnapshot> GetTelemetryHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TelemetryHealthSnapshot(null, Entries.Count, 0));

    public Task<int> DeleteAsync(DateTime? olderThanUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

public sealed class StubTransportEngine : ITransportEngine
{
    private readonly Queue<(int StatusCode, string Response)> _responses = new();

    public List<string> RequestedUrls { get; } = new();

    public void Enqueue(int statusCode, string response) => _responses.Enqueue((statusCode, response));

    public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default, FlowTestContext? testContext = null)
    {
        RequestedUrls.Add(step.EndpointUrl);
        var response = _responses.Count > 0 ? _responses.Dequeue() : (200, "{}");
        return Task.FromResult(new TransportResponse(response.Item1, response.Item2, new Dictionary<string, string[]>(), step.EndpointUrl));
    }
}

public sealed class UnreachableTransportEngine : ITransportEngine
{
    public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default, FlowTestContext? testContext = null) =>
        throw new InvalidOperationException("No transport dispatch was expected in this test.");
}

public sealed class UnreachableCodeExecutionService : IAdvancedCodeExecutionService
{
    public Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task<string> ExecuteEmailAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson, string? helperCode = null) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task ValidateAsync(string csharpCode, System.Threading.CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

