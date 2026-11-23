using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Storage;

// The settings page contract with the API: testing an unsaved form must dial the submitted values
// without persisting them, validation failures must be 400s with a message, and clamps must be
// stated in the save response.
public sealed class StorageSettingsEndpointTests : IDisposable
{

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<IPaaSContext> _options;

    public StorageSettingsEndpointTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite(_connection)
            .Options;

        using var seed = CreateContext();
        seed.Database.EnsureCreated();
    }

    private IPaaSContext CreateContext() => new(_options);

    public void Dispose() => _connection.Dispose();

    // ---------------- POST /api/storage/test ----------------

    [Fact]
    public async Task TestConnection_WithASubmittedBody_DialsTheSubmittedValuesAndPersistsNothing()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(url: "http://stored-cluster:9200", username: string.Empty, password: string.Empty));
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"cluster_name\":\"es-mock-cluster\",\"name\":\"es-mock\",\"version\":{\"number\":\"8.13.0\"},\"tagline\":\"You Know, for Search\"}");
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var operations = NewOperations(clients, settings);

        var result = await operations.TestConnectionAsync(new StorageConnectionTestRequest(
            "http://submitted-cluster:9210", "elastic", "secret", null, false, 15));

        Assert.True(result.Success);
        Assert.Equal("submitted-cluster:9210", handler.LastRequest.Host);
        Assert.True(handler.LastRequest.HasBasicAuth);
        Assert.Equal("elastic:secret", Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastRequest.AuthParameter!)));

        // Nothing was written back: the stored configuration is exactly what it was.
        Assert.Equal("http://stored-cluster:9200", settings.Current.Url);
        Assert.Equal(string.Empty, settings.Current.Username);
    }

    [Fact]
    public async Task TestConnection_WithASubmittedBody_UsesTheStoredSecretWhenTheFormLeavesItBlank()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(url: "http://stored-cluster:9200", username: "stored-user", password: "stored-secret"));
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"cluster_name\":\"es-mock-cluster\",\"name\":\"es-mock\",\"version\":{\"number\":\"8.13.0\"}}");
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var operations = NewOperations(clients, settings);

        var result = await operations.TestConnectionAsync(new StorageConnectionTestRequest(
            "http://submitted-cluster:9210", "elastic", null, null, false, 15));

        Assert.True(result.Success);
        // The submitted username wins, the blank password keeps the stored secret.
        Assert.Equal("elastic:stored-secret", Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastRequest.AuthParameter!)));
        Assert.Equal("stored-user", settings.Current.Username);
    }

    [Fact]
    public async Task TestConnection_WithoutABody_DialsTheStoredConfiguration()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot(url: "http://stored-cluster:9200", username: string.Empty, password: string.Empty));
        var handler = RecordingElasticsearchHandler.New();
        handler.Enqueue("{\"cluster_name\":\"es-mock-cluster\",\"name\":\"es-mock\",\"version\":{\"number\":\"8.13.0\"}}");
        using var clients = ElasticsearchClientProvider.ForTests(settings, new HttpClient(handler));
        var operations = NewOperations(clients, settings);

        var result = await operations.TestConnectionAsync();

        Assert.True(result.Success);
        Assert.Equal("stored-cluster:9200", handler.LastRequest.Host);
        Assert.Equal("/", handler.LastRequest.Path);
    }

    [Fact]
    public async Task TestEndpoint_ForwardsASubmittedBodyToTheOperations()
    {
        var operations = new RecordingStorageOperations();
        var controller = new StorageSettingsController(new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot()), NewWriter(), operations, CreateContext());

        var response = await controller.Test(new StorageConnectionTestRequestDto
        {
            Url = "http://submitted-cluster:9210",
            Username = "elastic",
            Password = "secret",
            TimeoutSeconds = 25
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var request = Assert.IsType<StorageConnectionTestRequest>(Assert.Single(operations.TestRequests));
        Assert.Equal("http://submitted-cluster:9210", request.Url);
        Assert.Equal("elastic", request.Username);
        Assert.Equal("secret", request.Password);
        Assert.Equal(25, request.TimeoutSeconds);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task TestEndpoint_ForwardsAnEmptyBodyAsTheStoredConfiguration()
    {
        var operations = new RecordingStorageOperations();
        var controller = new StorageSettingsController(new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot()), NewWriter(), operations, CreateContext());

        await controller.Test(null, CancellationToken.None);

        Assert.Null(Assert.Single(operations.TestRequests));
    }

    // ---------------- PUT /api/storage/settings ----------------

    [Fact]
    public async Task UpdateSettings_WithAnEmptyUrlAnswering400WithTheMessage()
    {
        var controller = NewController();

        var result = await controller.UpdateSettings(new StorageSettingsUpdateDto
        {
            ElasticsearchEnabled = true,
            Url = string.Empty,
            Datasets = Dataset("logs", useElasticsearch: true)
        }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, bad.StatusCode);
        Assert.Contains("Enter the Elasticsearch URL", Message(bad.Value));
    }

    [Fact]
    public async Task UpdateSettings_WithNegativeRetentionAnswering400WithTheMessage()
    {
        var controller = NewController();

        var result = await controller.UpdateSettings(new StorageSettingsUpdateDto
        {
            ElasticsearchEnabled = false,
            Url = "http://127.0.0.1:9210",
            Datasets = Dataset("logs", useElasticsearch: false, retentionDays: -3)
        }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Retention is a number of days", Message(bad.Value));
    }

    [Fact]
    public async Task UpdateSettings_ForTheDatabaseOnlyDataset_RejectsWithTheFullMessageOnce()
    {
        var controller = NewController();

        var result = await controller.UpdateSettings(new StorageSettingsUpdateDto
        {
            ElasticsearchEnabled = false,
            Url = "http://127.0.0.1:9210",
            Datasets = Dataset("executions", useElasticsearch: true)
        }, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var message = Message(bad.Value);
        Assert.Contains("Runs, steps and dead letters cannot be moved to Elasticsearch", message);
        Assert.Contains("atomic", message);
        Assert.DoesNotContain("stays in the database: Stays in the database", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateSettings_StatesTheClampsInTheResponseMessage()
    {
        var controller = NewController();

        var result = await controller.UpdateSettings(new StorageSettingsUpdateDto
        {
            ElasticsearchEnabled = false,
            Url = "http://127.0.0.1:9210",
            Datasets = Dataset("logs", useElasticsearch: false, retentionDays: 99999)
                .Concat(Dataset("executions", useElasticsearch: false, useIlm: true))
                .ToList()
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<StorageSaveResultDto>(ok.Value);
        Assert.True(payload.Success);
        Assert.Contains("capped at 3,650 days", payload.Message);
        Assert.Contains("ILM was switched off for Runs, steps and dead letters", payload.Message);

        // The stored values are the clamped ones, and the response echoes them.
        var logs = payload.Settings.Datasets.First(dataset => dataset.Dataset == StorageDatasets.Logs);
        Assert.Equal(3650, logs.RetentionDays);
        var executions = payload.Settings.Datasets.First(dataset => dataset.Dataset == StorageDatasets.Executions);
        Assert.False(executions.UseIlm);
    }

    // ---------------- helpers ----------------

    private StorageOperations NewOperations(ElasticsearchClientProvider clients, IStorageSettingsProvider settings) =>
        new(clients, new ElasticsearchIndexManager(clients), settings, null!, null!);

    private StorageSettingsController NewController()
    {
        var settings = new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot());
        return new StorageSettingsController(settings, NewWriter(settings), new RecordingStorageOperations(), CreateContext());
    }

    private StorageSettingsWriter NewWriter(IStorageSettingsProvider? settings = null)
    {
        var provider = settings ?? new StubStorageSettingsProvider(StubStorageSettingsProvider.Snapshot());
        var context = CreateContext();
        var runtime = new StorageRuntime(new StubServiceProvider(), TimeSpan.FromMinutes(10));
        var clients = ElasticsearchClientProvider.ForTests(provider, new HttpClient(new UnreachableElasticsearchHandler()));
        return new StorageSettingsWriter(context, new PassthroughEncryptionService(), provider, new ElasticsearchIndexManager(clients), runtime);
    }

    private static List<StorageDatasetSettingsDto> Dataset(
        string dataset,
        bool useElasticsearch,
        int retentionDays = 0,
        bool useIlm = false) =>
        new()
        {
            new StorageDatasetSettingsDto
            {
                Dataset = dataset,
                UseElasticsearch = useElasticsearch,
                Granularity = "Daily",
                RetentionDays = retentionDays,
                UseIlm = useIlm
            }
        };

    private static string Message(object? value) =>
        value?.GetType().GetProperty("message")?.GetValue(value) as string ?? string.Empty;

    private sealed class RecordingStorageOperations : IStorageOperations
    {
        public List<StorageConnectionTestRequest?> TestRequests { get; } = new();

        public Task<StorageConnectionTestResult> TestConnectionAsync(StorageConnectionTestRequest? request = null, CancellationToken cancellationToken = default)
        {
            TestRequests.Add(request);
            return Task.FromResult(new StorageConnectionTestResult(true, "connected", "es-mock-cluster", "8.13.0", "Elasticsearch", 1));
        }

        public Task<StorageApplyResult> ApplyIndexPlanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageApplyResult(true, "applied", Array.Empty<StorageApplyOutcome>()));

        public Task<StorageInventory> GetInventoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageInventory(DateTime.UtcNow, Array.Empty<StorageDatasetStatus>()));

        public Task<StorageRetentionRunResult> RunRetentionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageRetentionRunResult(DateTime.UtcNow, Array.Empty<StorageRetentionOutcome>()));
    }
}


