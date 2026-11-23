using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class RemoveTenancyMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fusetor-remove-tenancy-" + Guid.NewGuid().ToString("N"));

    public RemoveTenancyMigrationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void LegacyDatabase_MigratesColumnsIndexesAndAlertSettings_AndCreatesABackup()
    {
        var path = Path.Combine(_directory, "ipaas.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        SeedLegacySchema(connection);

        DatabaseSchemaInitializer.RemoveTenancy(connection, new ListLogger());

        Assert.DoesNotContain("TenantId", Columns(connection, "IntegrationFlows"));
        Assert.DoesNotContain("TenantId", Columns(connection, "FlowVersions"));
        Assert.DoesNotContain("TenantId", Columns(connection, "AlertSettings"));

        var indexes = Indexes(connection, "FlowVersions");
        Assert.DoesNotContain("IX_FlowVersions_TenantId_FlowId_VersionNumber", indexes);
        Assert.Contains("IX_FlowVersions_FlowId_VersionNumber", indexes);

        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM IntegrationFlows;"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM FlowVersions;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM AlertSettings;"));
        Assert.Equal(1L, Scalar(connection, "SELECT Id FROM AlertSettings;"));
        Assert.Equal("ops@example.com", ScalarText(connection, "SELECT Recipients FROM AlertSettings;"));

        var backups = Directory.GetFiles(_directory, "ipaas.db.bak-pre-remove-tenancy-*");
        Assert.Single(backups);
    }

    [Fact]
    public void Backup_UsesTheRealDatabaseFileName()
    {
        var path = Path.Combine(_directory, "fusetor-prod.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        SeedLegacySchema(connection);

        DatabaseSchemaInitializer.RemoveTenancy(connection, new ListLogger());

        Assert.Single(Directory.GetFiles(_directory, "fusetor-prod.db.bak-pre-remove-tenancy-*"));
        Assert.Empty(Directory.GetFiles(_directory, "ipaas.db.bak-pre-remove-tenancy-*"));
    }

    [Fact]
    public void SecondRun_IsANoOp()
    {
        var path = Path.Combine(_directory, "ipaas.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        SeedLegacySchema(connection);

        DatabaseSchemaInitializer.RemoveTenancy(connection, new ListLogger());
        var backupsAfterFirstRun = Directory.GetFiles(_directory, "ipaas.db.bak-pre-remove-tenancy-*").Length;

        DatabaseSchemaInitializer.RemoveTenancy(connection, new ListLogger());

        Assert.Equal(backupsAfterFirstRun, Directory.GetFiles(_directory, "ipaas.db.bak-pre-remove-tenancy-*").Length);
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM IntegrationFlows;"));
    }

    [Fact]
    public void MultipleTenantsInOneTable_AreLeftUntouched_AndAnErrorIsLogged()
    {
        var path = Path.Combine(_directory, "ipaas.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IntegrationFlows (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    TenantId TEXT NOT NULL
                );
                INSERT INTO IntegrationFlows (Id, Name, TenantId) VALUES
                    ('11111111-1111-1111-1111-111111111111', 'A flow', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
                    ('22222222-2222-2222-2222-222222222222', 'B flow', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
                """;
            command.ExecuteNonQuery();
        }

        var logger = new ListLogger();
        DatabaseSchemaInitializer.RemoveTenancy(connection, logger);

        Assert.Contains("TenantId", Columns(connection, "IntegrationFlows"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM IntegrationFlows;"));
        Assert.Contains(logger.Errors, message => message.Contains("more than one distinct TenantId"));
        Assert.Empty(Directory.GetFiles(_directory, "ipaas.db.bak-pre-remove-tenancy-*"));
    }

    [Fact]
    public void FreshDatabase_HasNoTenantIdAnywhere()
    {
        var path = Path.Combine(_directory, "fresh.db");
        var options = new DbContextOptionsBuilder<IPaaSContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);
        }

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var tableNames = new List<string>();
        using (var reader = tables.ExecuteReader())
        {
            while (reader.Read())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        foreach (var table in tableNames)
        {
            Assert.DoesNotContain("TenantId", Columns(connection, table));
        }

        Assert.Equal("INTEGER", ColumnType(connection, "AlertSettings", "Id"));
    }

    private static void SeedLegacySchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IntegrationFlows (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                TenantId TEXT NOT NULL
            );
            CREATE TABLE FlowVersions (
                Id TEXT NOT NULL PRIMARY KEY,
                FlowId TEXT NOT NULL,
                VersionNumber INTEGER NOT NULL,
                TenantId TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IX_FlowVersions_TenantId_FlowId_VersionNumber
                ON FlowVersions (TenantId, FlowId, VersionNumber);
            CREATE TABLE AlertSettings (
                Id TEXT NOT NULL PRIMARY KEY,
                TenantId TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                Recipients TEXT NOT NULL DEFAULT '',
                CooldownMinutes INTEGER NOT NULL DEFAULT 15,
                EnabledAt TEXT NULL,
                LastSentAt TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IX_AlertSettings_TenantId ON AlertSettings (TenantId);
            INSERT INTO IntegrationFlows (Id, Name, TenantId) VALUES
                ('11111111-1111-1111-1111-111111111111', 'Orders', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
                ('22222222-2222-2222-2222-222222222222', 'Invoices', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
            INSERT INTO FlowVersions (Id, FlowId, VersionNumber, TenantId) VALUES
                ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 1, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
                ('44444444-4444-4444-4444-444444444444', '11111111-1111-1111-1111-111111111111', 2, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
            INSERT INTO AlertSettings (Id, TenantId, Enabled, Recipients, CooldownMinutes, UpdatedAt)
            VALUES ('55555555-5555-5555-5555-555555555555', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 1, 'ops@example.com', 15, '2026-11-20T00:00:00Z');
            """;
        command.ExecuteNonQuery();
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string? ColumnType(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        return null;
    }

    private static HashSet<string> Indexes(SqliteConnection connection, string table)
    {
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{table}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            indexes.Add(reader.GetString(1));
        }

        return indexes;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string? ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the temp directory is disposable.
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
