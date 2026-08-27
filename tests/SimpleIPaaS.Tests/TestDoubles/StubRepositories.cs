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

    public Task<IEnumerable<IntegrationFlow>> GetByIntegrationIdAsync(Guid integrationId) =>
        Task.FromResult<IEnumerable<IntegrationFlow>>(_flows.Values.Where(f => f.IntegrationId == integrationId).ToList());

    public Task AddAsync(IntegrationFlow flow)
    {
        _flows[flow.Id] = flow;
        return Task.CompletedTask;
    }

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

    public Task<IEnumerable<IntegrationFlow>> GetActiveCronFlowsAcrossTenantsAsync() =>
        Task.FromResult<IEnumerable<IntegrationFlow>>(_flows.Values.Where(f => f.TriggerType == TriggerType.Cron).ToList());
}

public sealed class StubExecutionRepository : IExecutionRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, FlowExecution> _flowExecutions = new();

    public void Seed(FlowExecution execution) => _flowExecutions[execution.Id] = execution;

    public List<StepExecution> StepExecutions { get; } = new();
    public List<StepPacketLog> StepPacketLogs { get; } = new();

    public List<DeadLetterEntry> DeadLetters { get; } = new();

    public Task<FlowExecution?> GetFlowExecutionAsync(Guid id) =>
        Task.FromResult(_flowExecutions.TryGetValue(id, out var execution) ? execution : null);

    public Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(Guid? flowId = null, ExecutionStatus? status = null, int page = 1, int pageSize = 50, Guid? integrationId = null) =>
        Task.FromResult<IEnumerable<FlowExecution>>(_flowExecutions.Values.ToList());

    public Task AddFlowExecutionAsync(FlowExecution execution)
    {
        lock (_lock) { _flowExecutions[execution.Id] = execution; }
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

    public Task AddDeadLetterEntryAsync(DeadLetterEntry entry)
    {
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
                next.TenantId,
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

    public Task<IReadOnlyList<DeadLetterEntry>> GetReplayableDeadLettersAcrossTenantsAsync(int batchSize)
    {
        lock (_lock)
        {
            IReadOnlyList<DeadLetterEntry> entries = DeadLetters
                .Where(d => d.Status == "Pending" || d.ReplayRequestedAt != null)
                .OrderBy(d => d.CreatedAt)
                .Take(batchSize < 1 ? 100 : batchSize)
                .ToList();

            return Task.FromResult(entries);
        }
    }

    public Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.ToList());

    public Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.Where(d => d.FlowExecutionId == flowExecutionId).ToList());

    public Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry) => Task.CompletedTask;
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

public sealed class StubTransportEngine : ITransportEngine
{
    private readonly Queue<(int StatusCode, string Response)> _responses = new();

    public List<string> RequestedUrls { get; } = new();

    public void Enqueue(int statusCode, string response) => _responses.Enqueue((statusCode, response));

    public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default)
    {
        RequestedUrls.Add(step.EndpointUrl);
        var response = _responses.Count > 0 ? _responses.Dequeue() : (200, "{}");
        return Task.FromResult(new TransportResponse(response.Item1, response.Item2, new Dictionary<string, string[]>(), step.EndpointUrl));
    }
}

public sealed class UnreachableTransportEngine : ITransportEngine
{
    public Task<TransportResponse> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No transport dispatch was expected in this test.");
}

public sealed class UnreachableCodeExecutionService : IAdvancedCodeExecutionService
{
    public Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson) =>
        throw new InvalidOperationException("No script execution was expected in this test.");

    public Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson) =>
        throw new InvalidOperationException("No script execution was expected in this test.");
}

public sealed class StubTenantContext : SimpleIPaaS.Infrastructure.MultiTenancy.ITenantContext
{
    public StubTenantContext(Guid tenantId)
    {
        TenantId = tenantId;
    }

    public Guid TenantId { get; private set; }

    public void SetTenantId(Guid tenantId) => TenantId = tenantId;
}
