using SimpleIPaaS.Application.Interfaces;
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
    private readonly Dictionary<Guid, FlowExecution> _flowExecutions = new();

    public void Seed(FlowExecution execution) => _flowExecutions[execution.Id] = execution;

    public List<StepExecution> StepExecutions { get; } = new();

    public List<DeadLetterEntry> DeadLetters { get; } = new();

    public Task<FlowExecution?> GetFlowExecutionAsync(Guid id) =>
        Task.FromResult(_flowExecutions.TryGetValue(id, out var execution) ? execution : null);

    public Task<IEnumerable<FlowExecution>> GetFlowExecutionsAsync(Guid? flowId = null, ExecutionStatus? status = null, int page = 1, int pageSize = 50) =>
        Task.FromResult<IEnumerable<FlowExecution>>(_flowExecutions.Values.ToList());

    public Task AddFlowExecutionAsync(FlowExecution execution)
    {
        _flowExecutions[execution.Id] = execution;
        return Task.CompletedTask;
    }

    public Task UpdateFlowExecutionAsync(FlowExecution execution)
    {
        _flowExecutions[execution.Id] = execution;
        return Task.CompletedTask;
    }

    public Task AddStepExecutionAsync(StepExecution execution)
    {
        StepExecutions.Add(execution);
        return Task.CompletedTask;
    }

    public Task UpdateStepExecutionAsync(StepExecution execution) => Task.CompletedTask;

    public Task<IEnumerable<StepExecution>> GetStepExecutionsAsync(Guid flowExecutionId) =>
        Task.FromResult<IEnumerable<StepExecution>>(StepExecutions.Where(s => s.FlowExecutionId == flowExecutionId).ToList());

    public Task<StepExecution?> GetStepExecutionAsync(Guid flowExecutionId, Guid stepId) =>
        Task.FromResult(StepExecutions.LastOrDefault(s => s.FlowExecutionId == flowExecutionId && s.StepId == stepId));

    public Task AddDeadLetterEntryAsync(DeadLetterEntry entry)
    {
        DeadLetters.Add(entry);
        return Task.CompletedTask;
    }

    public Task<DeadLetterEntry?> GetDeadLetterAsync(Guid id) =>
        Task.FromResult(DeadLetters.FirstOrDefault(d => d.Id == id));

    public Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAsync(int batchSize) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.Where(d => d.Status == "Pending").Take(batchSize).ToList());

    public Task<IEnumerable<DeadLetterEntry>> GetPendingDeadLettersAcrossTenantsAsync(int batchSize) =>
        GetPendingDeadLettersAsync(batchSize);

    public Task<IEnumerable<DeadLetterEntry>> GetAllDeadLettersAsync(string? status = null) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.ToList());

    public Task<IEnumerable<DeadLetterEntry>> GetDeadLettersByExecutionAsync(Guid flowExecutionId) =>
        Task.FromResult<IEnumerable<DeadLetterEntry>>(DeadLetters.Where(d => d.FlowExecutionId == flowExecutionId).ToList());

    public Task UpdateDeadLetterEntryAsync(DeadLetterEntry entry) => Task.CompletedTask;
}

public sealed class StubCrossReferenceRepository : ICrossReferenceRepository
{
    private readonly Dictionary<string, CrossReferenceList> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CrossReferenceEntry> _entries = new();

    public IReadOnlyList<CrossReferenceEntry> Entries => _entries;

    public void SeedKeys(string listName, params string[] keys)
    {
        foreach (var key in keys)
        {
            _entries.Add(new CrossReferenceEntry { ListName = listName, KeyValue = key });
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
        if (!_lists.TryGetValue(name, out var list))
        {
            list = new CrossReferenceList { Name = name, Description = description };
            _lists[name] = list;
        }

        return Task.FromResult(list);
    }

    public Task<bool> DeleteListAsync(string name)
    {
        _entries.RemoveAll(e => e.ListName == name);
        return Task.FromResult(_lists.Remove(name));
    }

    public Task<int> ClearListAsync(string name) =>
        Task.FromResult(_entries.RemoveAll(e => e.ListName == name));

    public Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys)
    {
        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyCollection<string>>(_entries
            .Where(e => e.ListName == listName && wanted.Contains(e.KeyValue))
            .Select(e => e.KeyValue)
            .ToHashSet(StringComparer.Ordinal));
    }

    public Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries)
    {
        var inserted = 0;

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

    public void Enqueue(int statusCode, string response) => _responses.Enqueue((statusCode, response));

    public Task<(int StatusCode, string Response)> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : (200, "{}"));
}

public sealed class UnreachableTransportEngine : ITransportEngine
{
    public Task<(int StatusCode, string Response)> DispatchAsync(IntegrationStep step, string? payload, Guid? connectionId = null, CancellationToken cancellationToken = default) =>
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
