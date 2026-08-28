using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Infrastructure.Logging;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Tests.Logging;

public sealed class DatabaseLoggerProviderTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public DatabaseLoggerProviderTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"ipaas-logtest-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_databasePath}";
    }

    private static DatabaseLoggerOptions Options(Action<DatabaseLoggerOptions>? configure = null)
    {
        var options = new DatabaseLoggerOptions
        {
            // Keep the coalescing window short so tests do not sit on the default 1s.
            FlushIntervalSeconds = 0.05,
            BatchSize = 50
        };

        configure?.Invoke(options);
        return options;
    }

    private DatabaseLoggerProvider CreateProvider(DatabaseLoggerOptions options, string source = "Engine")
    {
        var provider = new DatabaseLoggerProvider(options, source, _connectionString);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        return provider;
    }

    private List<Row> ReadRows()
    {
        var rows = new List<Row>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        DatabaseSchemaInitializer.EnsureAppLogEntriesTable(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Level, Source, Category, Message, Exception, TenantId, FlowExecutionId, FlowId, NodeName
            FROM AppLogEntries ORDER BY Id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    private static int CountRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AppLogEntries;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void Batches_QueuedEntries_AndFlushesThemToTheDatabase()
    {
        using (var provider = CreateProvider(Options()))
        {
            var logger = provider.CreateLogger("SimpleIPaaS.Engine.Workers.FlowExecutionWorker");

            for (var i = 0; i < 25; i++)
            {
                logger.LogInformation("Claimed queued execution number {Index}", i);
            }
        }
        // Disposing the provider performs the final synchronous flush.

        var rows = ReadRows();

        Assert.Equal(25, rows.Count);
        Assert.All(rows, row => Assert.Equal("Information", row.Level));
        Assert.All(rows, row => Assert.Equal("Engine", row.Source));
        Assert.All(rows, row => Assert.Equal("SimpleIPaaS.Engine.Workers.FlowExecutionWorker", row.Category));
        Assert.Equal("Claimed queued execution number 0", rows[0].Message);
        Assert.Equal("Claimed queued execution number 24", rows[24].Message);

        // Ids are the live-tail cursor: they must ascend strictly.
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i].Id > rows[i - 1].Id);
        }
    }

    [Fact]
    public void Source_DistinguishesTheApiHostFromTheEngineHost()
    {
        using (var engine = CreateProvider(Options(), "Engine"))
        {
            engine.CreateLogger("Worker").LogInformation("from the engine");
        }

        using (var api = CreateProvider(Options(), "Api"))
        {
            api.CreateLogger("Controller").LogInformation("from the api");
        }

        var rows = ReadRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Engine", rows[0].Source);
        Assert.Equal("Api", rows[1].Source);
    }

    [Fact]
    public void Honours_TheConfiguredMinimumLevel()
    {
        using (var provider = CreateProvider(Options(options => options.MinimumLevel = LogLevel.Warning)))
        {
            var logger = provider.CreateLogger("Worker");

            Assert.False(logger.IsEnabled(LogLevel.Debug));
            Assert.False(logger.IsEnabled(LogLevel.Information));
            Assert.True(logger.IsEnabled(LogLevel.Warning));

            logger.LogDebug("debug is below the floor");
            logger.LogInformation("information is below the floor");
            logger.LogWarning("warning is kept");
            logger.LogError("error is kept");
        }

        var rows = ReadRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "Warning", "Error" }, rows.Select(row => row.Level).ToArray());
    }

    [Fact]
    public void Writes_NothingWhenDisabled()
    {
        using (var provider = CreateProvider(Options(options => options.Enabled = false)))
        {
            var logger = provider.CreateLogger("Worker");
            Assert.False(logger.IsEnabled(LogLevel.Critical));
            logger.LogCritical("should never be persisted");
        }

        Assert.Empty(ReadRows());
    }

    // The recursion guard: EF Core logs the SQL for every command it runs. If those entries
    // reached this sink, each INSERT would emit another log line and the table would grow
    // without bound.
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Microsoft.EntityFrameworkCore.Infrastructure")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.Data.Sqlite.SomeInternal")]
    [InlineData("SimpleIPaaS.Infrastructure.Logging.DatabaseLogWriter")]
    public void Suppresses_CategoriesThatWouldFeedBackIntoItself(string category)
    {
        using (var provider = CreateProvider(Options(options => options.MinimumLevel = LogLevel.Trace)))
        {
            Assert.True(provider.IsExcluded(category));

            var logger = provider.CreateLogger(category);
            Assert.False(logger.IsEnabled(LogLevel.Critical));

            logger.LogError("Executed DbCommand INSERT INTO AppLogEntries ...");
        }

        Assert.Empty(ReadRows());
    }

    [Fact]
    public void Does_NotSuppressOrdinaryApplicationCategories()
    {
        using (var provider = CreateProvider(Options()))
        {
            Assert.False(provider.IsExcluded("SimpleIPaaS.Application.Services.FlowExecutor"));
            Assert.False(provider.IsExcluded("Microsoft.Hosting.Lifetime"));

            provider.CreateLogger("SimpleIPaaS.Application.Services.FlowExecutor").LogInformation("node started");
        }

        Assert.Single(ReadRows());
    }

    [Fact]
    public void Suppresses_ExtraCategoryPrefixesFromConfiguration()
    {
        var options = Options(o => o.ExcludedCategoryPrefixes = new[] { "Noisy.Vendor" });

        using (var provider = CreateProvider(options))
        {
            Assert.True(provider.IsExcluded("Noisy.Vendor.Thing"));
            provider.CreateLogger("Noisy.Vendor.Thing").LogError("chatter");
            provider.CreateLogger("Quiet.Vendor.Thing").LogError("kept");
        }

        var rows = ReadRows();
        Assert.Single(rows);
        Assert.Equal("Quiet.Vendor.Thing", rows[0].Category);
    }

    [Fact]
    public void Captures_CorrelationIdsFromScopesAndStructuredState()
    {
        var executionId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        using (var provider = CreateProvider(Options()))
        {
            var logger = provider.CreateLogger("SimpleIPaaS.Engine.Workers.FlowExecutionWorker");

            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["ExecutionId"] = executionId,
                ["FlowId"] = flowId,
                ["TenantId"] = tenantId
            }))
            {
                // NodeName comes from the message state rather than the scope.
                logger.LogInformation("Execution {ExecutionId}: node {NodeName} started", executionId, "Fetch Orders");
            }

            // Outside the scope nothing should be correlated.
            logger.LogInformation("idle");
        }

        var rows = ReadRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal(executionId, rows[0].FlowExecutionId);
        Assert.Equal(flowId, rows[0].FlowId);
        Assert.Equal(tenantId, rows[0].TenantId);
        Assert.Equal("Fetch Orders", rows[0].NodeName);

        Assert.Null(rows[1].FlowExecutionId);
        Assert.Null(rows[1].FlowId);
        Assert.Null(rows[1].NodeName);
    }

    [Fact]
    public void Persists_TheFullExceptionText()
    {
        using (var provider = CreateProvider(Options()))
        {
            var logger = provider.CreateLogger("Worker");

            try
            {
                throw new InvalidOperationException("HTTP request failed with status code 503");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Execution failed");
            }
        }

        var row = Assert.Single(ReadRows());

        Assert.Equal("Error", row.Level);
        Assert.Equal("Execution failed", row.Message);
        Assert.NotNull(row.Exception);
        Assert.Contains("InvalidOperationException", row.Exception);
        Assert.Contains("503", row.Exception);
    }

    [Fact]
    public void Truncates_OversizedMessagesAndExceptions()
    {
        var options = Options(o =>
        {
            o.MaxMessageLength = 40;
            o.MaxExceptionLength = 60;
        });

        using (var provider = CreateProvider(options))
        {
            provider.CreateLogger("Worker").LogError(
                new InvalidOperationException(new string('E', 5_000)),
                new string('M', 5_000));
        }

        var row = Assert.Single(ReadRows());

        Assert.True(row.Message.Length < 100);
        Assert.EndsWith("[truncated]", row.Message);
        Assert.NotNull(row.Exception);
        Assert.True(row.Exception!.Length < 120);
        Assert.EndsWith("[truncated]", row.Exception);
    }

    [Fact]
    public void Never_BlocksTheCaller_WhenTheBacklogIsFull()
    {
        // A long flush interval keeps the pump parked so the bounded channel really fills up.
        var options = Options(o =>
        {
            o.QueueCapacity = 16;
            o.FlushIntervalSeconds = 30;
        });

        var writer = new DatabaseLogWriter(options, _connectionString);
        using var provider = new DatabaseLoggerProvider(options, "Engine", writer);
        var logger = provider.CreateLogger("Worker");

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 5_000; i++)
        {
            logger.LogInformation("hot path entry {Index}", i);
        }

        stopwatch.Stop();

        // Enqueue is a lock-free TryWrite; 5000 of them must not take anywhere near the
        // 30s flush interval, and the surplus is dropped rather than queued or blocked.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Logging blocked for {stopwatch.Elapsed}.");
        Assert.True(writer.DroppedCount > 0, "Expected the bounded backlog to drop entries instead of blocking.");
    }

    [Fact]
    public void Retention_PrunesEntriesOlderThanTheConfiguredWindow()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        DatabaseSchemaInitializer.EnsureAppLogEntriesTable(connection);

        InsertAt(connection, DateTime.UtcNow.AddHours(-100));
        InsertAt(connection, DateTime.UtcNow.AddHours(-73));
        InsertAt(connection, DateTime.UtcNow.AddHours(-71));
        InsertAt(connection, DateTime.UtcNow);

        Assert.Equal(4, CountRows(connection));

        var removed = DatabaseLogWriter.Prune(connection, new DatabaseLoggerOptions
        {
            RetentionHours = 72,
            MaxRows = 0
        });

        Assert.Equal(2, removed);
        Assert.Equal(2, CountRows(connection));
    }

    [Fact]
    public void Retention_EnforcesTheMaximumRowCap()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        DatabaseSchemaInitializer.EnsureAppLogEntriesTable(connection);

        for (var i = 0; i < 50; i++)
        {
            InsertAt(connection, DateTime.UtcNow);
        }

        var removed = DatabaseLogWriter.Prune(connection, new DatabaseLoggerOptions
        {
            RetentionHours = 0,
            MaxRows = 10
        });

        Assert.Equal(40, removed);
        Assert.Equal(10, CountRows(connection));

        // The rows that survive are the newest ones.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(Id), MAX(Id) FROM AppLogEntries;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(41, reader.GetInt64(0));
        Assert.Equal(50, reader.GetInt64(1));
    }

    [Fact]
    public void Retention_DisabledWhenBothLimitsAreOff()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        DatabaseSchemaInitializer.EnsureAppLogEntriesTable(connection);

        InsertAt(connection, DateTime.UtcNow.AddYears(-1));

        var removed = DatabaseLogWriter.Prune(connection, new DatabaseLoggerOptions
        {
            RetentionHours = 0,
            MaxRows = 0
        });

        Assert.Equal(0, removed);
        Assert.Equal(1, CountRows(connection));
    }

    private static void InsertAt(SqliteConnection connection, DateTime timestampUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppLogEntries (Timestamp, Level, Source, Category, Message)
            VALUES ($timestamp, 'Information', 'Engine', 'Worker', 'seeded');
            """;
        command.Parameters.AddWithValue("$timestamp", timestampUtc);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // A stray temp file is not worth failing a test run over.
        }
    }

    private sealed record Row(
        long Id,
        string Level,
        string Source,
        string Category,
        string Message,
        string? Exception,
        Guid? TenantId,
        Guid? FlowExecutionId,
        Guid? FlowId,
        string? NodeName);
}
