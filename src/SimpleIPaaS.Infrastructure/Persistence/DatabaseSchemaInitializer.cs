using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimpleIPaaS.Infrastructure.Persistence;

public static class DatabaseSchemaInitializer
{
    public static void EnsureUpToDate(IPaaSContext context, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (context.Database.IsNpgsql())
        {
            // A new provider with no legacy schemas to upgrade: the model is the schema (see PostgresSchema).
            PostgresSchema.EnsureUpToDate(context, logger);
            return;
        }

        context.Database.EnsureCreated();
        if (context.Database.IsSqlServer())
        {
            using var sqlConnection = new SqlConnection(context.Database.GetConnectionString());
            sqlConnection.Open();
            RemoveTenancy(sqlConnection, logger);
            EnsureSqlServerAiProviderSettingsTable(sqlConnection);
            EnsureSqlServerAlertSettingsTable(sqlConnection);
            EnsureSqlServerAiBuildJobsTable(sqlConnection);
            EnsureSqlServerAiChatTables(sqlConnection);
            EnsureSqlServerAiBuildSettingsTable(sqlConnection);
            EnsureSqlServerStorageSettingsTable(sqlConnection);
            EnsureSqlServerColumn(sqlConnection, "IntegrationFlows", "DefaultDeadLetterResyncStepId", "uniqueidentifier NULL");
            EnsureSqlServerColumn(sqlConnection, "IntegrationFlows", "HelperCode", "nvarchar(max) NOT NULL CONSTRAINT DF_IntegrationFlows_HelperCode DEFAULT N''");
            EnsureSqlServerColumn(sqlConnection, "IntegrationSteps", "IsArchived", "bit NOT NULL CONSTRAINT DF_IntegrationSteps_IsArchived DEFAULT 0");
            EnsureSqlServerColumn(sqlConnection, "IntegrationSteps", "HttpErrorMode", "int NOT NULL CONSTRAINT DF_IntegrationSteps_HttpErrorMode DEFAULT 0");
            EnsureSqlServerColumn(sqlConnection, "DeadLetterEntries", "PersistedStateJson", "nvarchar(max) NOT NULL CONSTRAINT DF_DeadLetterEntries_PersistedStateJson DEFAULT N'{}'");
            EnsureSqlServerColumn(sqlConnection, "DeadLetterEntries", "ReplayFromStepId", "uniqueidentifier NULL");
            EnsureSqlServerColumn(sqlConnection, "DeadLetterEntries", "AlertedAt", "datetime2 NULL");
            EnsureSqlServerColumn(sqlConnection, "DeadLetterEntries", "ManualReplayOnly", "bit NOT NULL CONSTRAINT DF_DeadLetterEntries_ManualReplayOnly DEFAULT 0");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "ParentExecutionId", "uniqueidentifier NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "FanOutNodeId", "uniqueidentifier NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "FanOutNodeName", "nvarchar(256) NOT NULL CONSTRAINT DF_FlowExecutions_FanOutNodeName DEFAULT N''");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "FanOutItemIndex", "int NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "FanOutItemCount", "int NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "FanOutItemJson", "nvarchar(max) NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "StartStepIdsJson", "nvarchar(max) NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "InitialFlowStateJson", "nvarchar(max) NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "InitialPersistedStateJson", "nvarchar(max) NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "FanOutSnapshotId", "uniqueidentifier NULL");
            EnsureSqlServerFanOutSnapshotsTable(sqlConnection);
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "Identifier", "nvarchar(512) NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowExecutions", "Message", "nvarchar(max) NULL");
            EnsureSqlServerColumn(sqlConnection, "FlowTestRuns", "SuiteRunId", "uniqueidentifier NULL");
            EnsureSqlServerIdentityTables(sqlConnection);
            EnsurePerformanceIndexes(sqlConnection);
            return;
        }

        using var connection = new SqliteConnection(context.Database.GetConnectionString());
        connection.Open();

        RemoveTenancy(connection, logger);

        EnsureFlowVersionsTable(connection);
        EnsureFlowTestTables(connection);
        EnsureHostHeartbeatsTable(connection);
        EnsureIntegrationsTable(connection);
        EnsureApiKeysTable(connection);
        EnsureIdentityTables(connection);
        EnsureAiProviderSettingsTable(connection);
        EnsureAlertSettingsTable(connection);
        EnsureAiBuildJobsTable(connection);
        EnsureAiChatTables(connection);
        EnsureAiBuildSettingsTable(connection);
        EnsureCrossReferenceTables(connection);
        EnsureStorageSettingsTable(connection);
        EnsureStepPacketLogsTable(connection);
        EnsureAppLogEntriesTable(connection);
        EnsureColumn(connection, "AppLogEntries", "FlowName", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "IntegrationId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "IntegrationName", "TEXT NULL");
        EnsureColumn(connection, "IntegrationFlows", "IntegrationId", "TEXT NULL");
        EnsureColumn(connection, "IntegrationFlows", "PublishedVersion", "INTEGER NULL");
        EnsureColumn(connection, "IntegrationFlows", "LastPublishedAt", "TEXT NULL");
        EnsureColumn(connection, "IntegrationFlows", "PersistedStateJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "IntegrationFlows", "HelperCode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationSteps", "UrlMode", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "IntegrationSteps", "AuthConfigJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationSteps", "IsArchived", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "IntegrationSteps", "HttpErrorMode", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "IntegrationFlows", "NextRunAt", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TriggerSource", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationFlows", "AllowPostReplay", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "IntegrationFlows", "DefaultDeadLetterResyncStepId", "TEXT NULL");
        EnsureColumn(connection, "DeadLetterEntries", "FlowStateJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "DeadLetterEntries", "PersistedStateJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "DeadLetterEntries", "ReplayFromStepId", "TEXT NULL");
        EnsureColumn(connection, "StepExecutions", "NodeName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StepExecutions", "ReceivedInput", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "StepExecutions", "RecoveredAt", "TEXT NULL");
        EnsureColumn(connection, "StepExecutions", "RecoveredByDeadLetterId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "RecoveredAt", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "FlowName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FlowExecutions", "IntegrationId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "IntegrationName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FlowExecutions", "TraceParent", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "QueuedAt", "TEXT NULL");
        EnsureColumn(connection, "DeadLetterEntries", "ResolvedAt", "TEXT NULL");
        EnsureColumn(connection, "DeadLetterEntries", "AlertedAt", "TEXT NULL");
        EnsureColumn(connection, "DeadLetterEntries", "ManualReplayOnly", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "DeadLetterEntries", "AttemptHistoryJson", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "DeadLetterEntries", "FlowName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DeadLetterEntries", "IntegrationName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DeadLetterEntries", "NodeName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationFlows", "RunAt", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TriggerPayloadJson", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "ParentExecutionId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "FanOutNodeId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "FanOutNodeName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FlowExecutions", "FanOutItemIndex", "INTEGER NULL");
        EnsureColumn(connection, "FlowExecutions", "FanOutItemCount", "INTEGER NULL");
        EnsureColumn(connection, "FlowExecutions", "FanOutItemJson", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "StartStepIdsJson", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "InitialFlowStateJson", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "InitialPersistedStateJson", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "FanOutSnapshotId", "TEXT NULL");
        EnsureFanOutSnapshotsTable(connection);
        EnsureColumn(connection, "FlowExecutions", "IsTest", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "FlowExecutions", "TestCaseId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TestRunId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TestDefinitionJson", "TEXT NULL");
        EnsureColumn(connection, "FlowTestRuns", "SuiteRunId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "Identifier", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "Message", "TEXT NULL");
        EnsureColumn(connection, "DeadLetterEntries", "ReplayRequestedAt", "TEXT NULL");
        EnsurePerformanceIndexes(connection);
        EnsureDatabasePragmas(connection);
    }

    // Indexes for the hot read paths, found by running the Engine claim poll and the UI list queries against
    // a 60,000-run database (tools/FusetorBench): every one of them was a full table scan plus a sort.
    //  * Status+StartedAt narrows the Engine's "next queued run" poll (every worker, every 2 seconds) to the few
    //    Queued rows and also serves the status-filtered run list.
    //  * StartedAt and FlowId+StartedAt serve the newest-first run lists and their filters.
    //  * Status+CreatedAt and ReplayRequestedAt serve the dead-letter scans (Engine every minute, UI on open).
    // Dropped indexes were either redundant (a prefix of a new composite) or low-cardinality columns on a
    // table that is written on every log line: each one cost an extra b-tree update per insert.
    private static readonly (string Name, string Table, string Columns, string? Where)[] PerformanceIndexes =
    {
        ("IX_FlowExecutions_Status_StartedAt", "FlowExecutions", "Status, StartedAt", null),
        ("IX_FlowExecutions_StartedAt", "FlowExecutions", "StartedAt", null),
        ("IX_FlowExecutions_FlowId_StartedAt", "FlowExecutions", "FlowId, StartedAt", null),
        ("IX_DeadLetterEntries_Status_CreatedAt", "DeadLetterEntries", "Status, CreatedAt", null),
        // Partial: nearly every row has no manual replay request, so a full index is all NULLs and the planner
        // ignores it (measured: a full scan plus sort of the whole table). Only requested rows are indexed.
        ("IX_DeadLetterEntries_ReplayRequested", "DeadLetterEntries", "ReplayRequestedAt", "ReplayRequestedAt IS NOT NULL"),
        // Retention finds expired runs by CompletedAt and expired cross-reference keys by CreatedAt; without these
        // every batch (up to 20 per pass) scanned the whole table. Partial: unfinished runs are never candidates.
        ("IX_FlowExecutions_CompletedAt", "FlowExecutions", "CompletedAt", "CompletedAt IS NOT NULL"),
        ("IX_CrossReferenceEntries_CreatedAt", "CrossReferenceEntries", "CreatedAt", null),
    };

    private static readonly (string Name, string Table)[] RedundantIndexes =
    {
        ("IX_FlowExecutions_FlowId", "FlowExecutions"),
        ("IX_StepPacketLogs_StepExecutionId", "StepPacketLogs"),
        ("IX_AppLogEntries_Service", "AppLogEntries"),
        ("IX_AppLogEntries_Outcome", "AppLogEntries"),
        ("IX_AppLogEntries_EventName", "AppLogEntries"),
        ("IX_AppLogEntries_Level", "AppLogEntries"),
    };

    internal static IReadOnlyList<(string Name, string Table, string Columns, string? Where)> PerformanceIndexDefinitions => PerformanceIndexes;
    internal static IReadOnlyList<(string Name, string Table)> RedundantIndexDefinitions => RedundantIndexes;

    internal static void EnsurePerformanceIndexes(SqliteConnection connection)
    {
        foreach (var (name, table, columns, where) in PerformanceIndexes)
        {
            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE INDEX IF NOT EXISTS \"{name}\" ON \"{table}\" ({columns})" + (where == null ? ";" : $" WHERE {where};");
            create.ExecuteNonQuery();
        }

        foreach (var (name, _) in RedundantIndexes)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP INDEX IF EXISTS \"{name}\";";
            drop.ExecuteNonQuery();
        }
    }

    internal static void EnsurePerformanceIndexes(SqlConnection connection)
    {
        foreach (var (name, table, columns, where) in PerformanceIndexes)
        {
            var bracketed = string.Join(", ", columns.Split(',').Select(column => $"[{column.Trim()}]"));
            using var create = connection.CreateCommand();
            create.CommandText =
                $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'dbo.{table}')) " +
                $"CREATE INDEX [{name}] ON [dbo].[{table}] ({bracketed})" + (where == null ? ";" : $" WHERE [{where.Replace(" IS NOT NULL", "")}] IS NOT NULL;");
            create.ExecuteNonQuery();
        }

        foreach (var (name, table) in RedundantIndexes)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText =
                $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'dbo.{table}')) " +
                $"DROP INDEX [{name}] ON [dbo].[{table}];";
            drop.ExecuteNonQuery();
        }
    }

    // Host-wide AI build budgets. One row, id = 1.
    private static void EnsureAiBuildSettingsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AiBuildSettings (
                Id INTEGER NOT NULL CONSTRAINT PK_AiBuildSettings PRIMARY KEY,
                MaxToolCalls INTEGER NOT NULL DEFAULT 48,
                MaxChatRounds INTEGER NOT NULL DEFAULT 60,
                MaxDraftRepairs INTEGER NOT NULL DEFAULT 4,
                MaxBuildMinutes INTEGER NOT NULL DEFAULT 30,
                MaxContextChars INTEGER NOT NULL DEFAULT 100000,
                MaxNodes INTEGER NOT NULL DEFAULT 100,
                MaxEdges INTEGER NOT NULL DEFAULT 250,
                MaxHistoryTurns INTEGER NOT NULL DEFAULT 12,
                MaxHistoryCharsPerTurn INTEGER NOT NULL DEFAULT 4000,
                MaxPromptChars INTEGER NOT NULL DEFAULT 48000,
                MaxFlowPromptChars INTEGER NOT NULL DEFAULT 72000,
                MaxOutputTokens INTEGER NOT NULL DEFAULT 32768,
                UpdatedAt TEXT NOT NULL
            );

            INSERT OR IGNORE INTO AiBuildSettings (Id, UpdatedAt)
            VALUES (1, CURRENT_TIMESTAMP);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSqlServerAiBuildSettingsTable(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.AiBuildSettings', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AiBuildSettings (
                    Id int NOT NULL CONSTRAINT PK_AiBuildSettings PRIMARY KEY,
                    MaxToolCalls int NOT NULL CONSTRAINT DF_AiBuildSettings_ToolCalls DEFAULT 48,
                    MaxChatRounds int NOT NULL CONSTRAINT DF_AiBuildSettings_Rounds DEFAULT 60,
                    MaxDraftRepairs int NOT NULL CONSTRAINT DF_AiBuildSettings_Repairs DEFAULT 4,
                    MaxBuildMinutes int NOT NULL CONSTRAINT DF_AiBuildSettings_Minutes DEFAULT 30,
                    MaxContextChars int NOT NULL CONSTRAINT DF_AiBuildSettings_Context DEFAULT 100000,
                    MaxNodes int NOT NULL CONSTRAINT DF_AiBuildSettings_Nodes DEFAULT 100,
                    MaxEdges int NOT NULL CONSTRAINT DF_AiBuildSettings_Edges DEFAULT 250,
                    MaxHistoryTurns int NOT NULL CONSTRAINT DF_AiBuildSettings_HistoryTurns DEFAULT 12,
                    MaxHistoryCharsPerTurn int NOT NULL CONSTRAINT DF_AiBuildSettings_HistoryChars DEFAULT 4000,
                    MaxPromptChars int NOT NULL CONSTRAINT DF_AiBuildSettings_PromptChars DEFAULT 48000,
                    MaxFlowPromptChars int NOT NULL CONSTRAINT DF_AiBuildSettings_FlowPromptChars DEFAULT 72000,
                    MaxOutputTokens int NOT NULL CONSTRAINT DF_AiBuildSettings_OutputTokens DEFAULT 32768,
                    UpdatedAt datetime2 NOT NULL
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.AiBuildSettings WHERE Id = 1)
                INSERT INTO dbo.AiBuildSettings (Id, UpdatedAt) VALUES (1, SYSUTCDATETIME());
            """;
        command.ExecuteNonQuery();
    }

    // Host-wide storage configuration: which backend serves each growing dataset (relational
    // database or Elasticsearch) and how long its data is kept. One row, id = 1.
    private static void EnsureStorageSettingsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS StorageSettings (
                Id INTEGER NOT NULL CONSTRAINT PK_StorageSettings PRIMARY KEY,
                ElasticsearchEnabled INTEGER NOT NULL DEFAULT 0,
                ElasticsearchUrl TEXT NOT NULL DEFAULT '',
                ElasticsearchUsername TEXT NOT NULL DEFAULT '',
                EncryptedElasticsearchPassword TEXT NOT NULL DEFAULT '',
                EncryptedElasticsearchApiKey TEXT NOT NULL DEFAULT '',
                IndexPrefix TEXT NOT NULL DEFAULT 'fusetor',
                AllowInvalidCertificate INTEGER NOT NULL DEFAULT 0,
                TimeoutSeconds INTEGER NOT NULL DEFAULT 15,
                DatasetsJson TEXT NOT NULL DEFAULT '[]',
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSqlServerStorageSettingsTable(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.StorageSettings', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.StorageSettings (
                    Id int NOT NULL CONSTRAINT PK_StorageSettings PRIMARY KEY,
                    ElasticsearchEnabled bit NOT NULL CONSTRAINT DF_StorageSettings_Enabled DEFAULT 0,
                    ElasticsearchUrl nvarchar(500) NOT NULL CONSTRAINT DF_StorageSettings_Url DEFAULT N'',
                    ElasticsearchUsername nvarchar(200) NOT NULL CONSTRAINT DF_StorageSettings_User DEFAULT N'',
                    EncryptedElasticsearchPassword nvarchar(max) NOT NULL CONSTRAINT DF_StorageSettings_Password DEFAULT N'',
                    EncryptedElasticsearchApiKey nvarchar(max) NOT NULL CONSTRAINT DF_StorageSettings_ApiKey DEFAULT N'',
                    IndexPrefix nvarchar(60) NOT NULL CONSTRAINT DF_StorageSettings_Prefix DEFAULT N'fusetor',
                    AllowInvalidCertificate bit NOT NULL CONSTRAINT DF_StorageSettings_Tls DEFAULT 0,
                    TimeoutSeconds int NOT NULL CONSTRAINT DF_StorageSettings_Timeout DEFAULT 15,
                    DatasetsJson nvarchar(max) NOT NULL CONSTRAINT DF_StorageSettings_Datasets DEFAULT N'[]',
                    UpdatedAt datetime2 NOT NULL
                );
            END
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureDatabasePragmas(SqliteConnection connection)
    {
        // The API and the Engine share this database file in WAL mode. journal_mode=WAL
        // persists in the database file itself; busy_timeout is per-connection and lets a
        // write from one process wait on the other instead of failing with SQLITE_BUSY.
        using var walCommand = connection.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode=WAL;";
        walCommand.ExecuteScalar();

        SqliteTuning.Apply(connection);

        // Refreshes the planner statistics for any index created above; a no-op once they are current.
        using var optimizeCommand = connection.CreateCommand();
        optimizeCommand.CommandText = "PRAGMA optimize=0x10002;";
        optimizeCommand.ExecuteNonQuery();
    }

    // One shared start state per Fan Out (see FanOutSnapshot); deleted with the parent run.
    private static void EnsureFanOutSnapshotsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS FanOutSnapshots (
                Id TEXT NOT NULL CONSTRAINT PK_FanOutSnapshots PRIMARY KEY,
                ParentExecutionId TEXT NOT NULL,
                FanOutNodeId TEXT NOT NULL,
                ItemVariable TEXT NOT NULL,
                NodeName TEXT NOT NULL,
                FlowStateJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT FK_FanOutSnapshots_FlowExecutions_ParentExecutionId FOREIGN KEY (ParentExecutionId)
                    REFERENCES FlowExecutions (Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_FanOutSnapshots_ParentExecutionId
                ON FanOutSnapshots (ParentExecutionId);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSqlServerFanOutSnapshotsTable(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID('dbo.FanOutSnapshots', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FanOutSnapshots (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_FanOutSnapshots PRIMARY KEY,
                    ParentExecutionId uniqueidentifier NOT NULL,
                    FanOutNodeId uniqueidentifier NOT NULL,
                    ItemVariable nvarchar(200) NOT NULL,
                    NodeName nvarchar(256) NOT NULL,
                    FlowStateJson nvarchar(max) NOT NULL,
                    CreatedAt datetime2 NOT NULL,
                    CONSTRAINT FK_FanOutSnapshots_FlowExecutions_ParentExecutionId FOREIGN KEY (ParentExecutionId)
                        REFERENCES dbo.FlowExecutions (Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_FanOutSnapshots_ParentExecutionId ON dbo.FanOutSnapshots (ParentExecutionId);
            END
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureHostHeartbeatsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS HostHeartbeats (
                Id TEXT NOT NULL CONSTRAINT PK_HostHeartbeats PRIMARY KEY,
                ServiceName TEXT NOT NULL,
                InstanceId TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Healthy'
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_HostHeartbeats_ServiceName_InstanceId
                ON HostHeartbeats (ServiceName, InstanceId);

            CREATE INDEX IF NOT EXISTS IX_HostHeartbeats_LastSeenAt
                ON HostHeartbeats (LastSeenAt);
            """;
        command.ExecuteNonQuery();
    }
    private static void EnsureFlowVersionsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS FlowVersions (
                Id TEXT NOT NULL CONSTRAINT PK_FlowVersions PRIMARY KEY,
                FlowId TEXT NOT NULL,
                VersionNumber INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                ChangeNote TEXT NOT NULL DEFAULT '',
                RolledBackFromVersion INTEGER NULL,
                SnapshotJson TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_FlowVersions_FlowId_VersionNumber
                ON FlowVersions (FlowId, VersionNumber);

            CREATE INDEX IF NOT EXISTS IX_FlowVersions_FlowId_CreatedAt
                ON FlowVersions (FlowId, CreatedAt);
            """;
        command.ExecuteNonQuery();
    }
    private static void EnsureFlowTestTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS FlowTestCases (
                Id TEXT NOT NULL CONSTRAINT PK_FlowTestCases PRIMARY KEY,
                FlowId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                Enabled INTEGER NOT NULL DEFAULT 1,
                RequiredForPublish INTEGER NOT NULL DEFAULT 0,
                DefinitionJson TEXT NOT NULL DEFAULT '{}',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                LastRunAt TEXT NULL,
                LastPassedAt TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_FlowTestCases_FlowId_Name
                ON FlowTestCases (FlowId, Name);
            CREATE TABLE IF NOT EXISTS FlowTestRuns (
                Id TEXT NOT NULL CONSTRAINT PK_FlowTestRuns PRIMARY KEY,
                TestCaseId TEXT NOT NULL,
                FlowId TEXT NOT NULL,
                SuiteRunId TEXT NULL,
                FlowExecutionId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Queued',
                ResultJson TEXT NOT NULL DEFAULT '{}',
                ErrorMessage TEXT NULL,
                FlowUpdatedAt TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FlowTestRuns_TestCaseId_StartedAt
                ON FlowTestRuns (TestCaseId, StartedAt);
            """;
        command.ExecuteNonQuery();
    }
    private static void EnsureIntegrationsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Integrations (
                Id TEXT NOT NULL CONSTRAINT PK_Integrations PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureApiKeysTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ApiKeys (
                Id TEXT NOT NULL CONSTRAINT PK_ApiKeys PRIMARY KEY,
                Name TEXT NOT NULL,
                KeyHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                RevokedAt TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureCrossReferenceTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CrossReferenceLists (
                Id TEXT NOT NULL CONSTRAINT PK_CrossReferenceLists PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_CrossReferenceLists_Name
                ON CrossReferenceLists (Name);

            CREATE TABLE IF NOT EXISTS CrossReferenceEntries (
                Id TEXT NOT NULL CONSTRAINT PK_CrossReferenceEntries PRIMARY KEY,
                ListName TEXT NOT NULL,
                KeyValue TEXT NOT NULL,
                ValueJson TEXT NOT NULL,
                FlowId TEXT NULL,
                FlowName TEXT NULL,
                IntegrationId TEXT NULL,
                IntegrationName TEXT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_CrossReferenceEntries_ListName_KeyValue
                ON CrossReferenceEntries (ListName, KeyValue);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureStepPacketLogsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS StepPacketLogs (
                Id TEXT NOT NULL CONSTRAINT PK_StepPacketLogs PRIMARY KEY,
                StepExecutionId TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                Kind TEXT NOT NULL DEFAULT '',
                PageNumber INTEGER NULL,
                Attempt INTEGER NULL,
                HttpMethod TEXT NOT NULL DEFAULT '',
                RequestUrl TEXT NOT NULL DEFAULT '',
                StatusCode INTEGER NULL,
                RequestHeadersJson TEXT NOT NULL DEFAULT '{}',
                RequestBody TEXT NOT NULL DEFAULT '',
                ResponseHeadersJson TEXT NOT NULL DEFAULT '{}',
                ResponseBody TEXT NOT NULL DEFAULT '',
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                DurationMs INTEGER NULL,
                Error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_StepPacketLogs_StepExecutionId_Sequence
                ON StepPacketLogs (StepExecutionId, Sequence);
            """;
        command.ExecuteNonQuery();
    }

    // Application log sink shared by both hosts. Public because the batched
    // DatabaseLoggerProvider opens its own raw connection (it must never go through EF â€”
    // that would feed EF's own command logging straight back into this table) and needs
    // to be able to create the table before its very first flush, independently of
    // whichever host happens to boot first.
    public static void EnsureAppLogEntriesTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AppLogEntries (
                Id INTEGER NOT NULL CONSTRAINT PK_AppLogEntries PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Level TEXT NOT NULL,
                Source TEXT NOT NULL,
                Category TEXT NOT NULL,
                Message TEXT NOT NULL,
                Exception TEXT NULL,
                FlowExecutionId TEXT NULL,
                FlowId TEXT NULL,
                FlowName TEXT NULL,
                IntegrationId TEXT NULL,
                IntegrationName TEXT NULL,
                NodeName TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_Timestamp
                ON AppLogEntries (Timestamp);

            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_Level
                ON AppLogEntries (Level);

            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_FlowExecutionId
                ON AppLogEntries (FlowExecutionId);

            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "AppLogEntries", "FlowName", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "IntegrationId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "IntegrationName", "TEXT NULL");
        EnsureObservabilityColumns(connection);

        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_IntegrationId
                ON AppLogEntries (IntegrationId);

            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_FlowId
                ON AppLogEntries (FlowId);
            """;
        indexCommand.ExecuteNonQuery();
    }

    private static void EnsureObservabilityColumns(SqliteConnection connection)
    {
        EnsureColumn(connection, "AppLogEntries", "EventId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "EventVersion", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "AppLogEntries", "Service", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AppLogEntries", "HostInstance", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AppLogEntries", "EnvironmentName", "TEXT NOT NULL DEFAULT 'Development'");
        EnsureColumn(connection, "AppLogEntries", "ApplicationVersion", "TEXT NOT NULL DEFAULT 'unknown'");
        EnsureColumn(connection, "AppLogEntries", "Component", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AppLogEntries", "EventName", "TEXT NOT NULL DEFAULT 'log'");
        EnsureColumn(connection, "AppLogEntries", "Operation", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AppLogEntries", "Outcome", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "AppLogEntries", "DurationMs", "INTEGER NULL");
        EnsureColumn(connection, "AppLogEntries", "TraceId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "SpanId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "ParentSpanId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "CardId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "CardType", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "StepExecutionId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "Invocation", "INTEGER NULL");
        EnsureColumn(connection, "AppLogEntries", "LoopPath", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "RetryAttempt", "INTEGER NULL");
        EnsureColumn(connection, "AppLogEntries", "IsTest", "INTEGER NULL");
        EnsureColumn(connection, "AppLogEntries", "TestCaseId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "TestRunId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "ConnectionId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "PropertiesJson", "TEXT NOT NULL DEFAULT '{}'");

        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_EventName ON AppLogEntries (EventName);
            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_TraceId ON AppLogEntries (TraceId);
            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_CardId ON AppLogEntries (CardId);
            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_Outcome ON AppLogEntries (Outcome);
            CREATE INDEX IF NOT EXISTS IX_AppLogEntries_Service ON AppLogEntries (Service);
            """;
        indexes.ExecuteNonQuery();
    }
    private static void EnsureSqlServerAiBuildJobsTable(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.AiBuildJobs', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AiBuildJobs](
                    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_AiBuildJobs] PRIMARY KEY,
                    [ProviderId] uniqueidentifier NOT NULL,
                    [ProviderName] nvarchar(120) NOT NULL CONSTRAINT [DF_AiBuildJobs_ProviderName] DEFAULT N'',
                    [Model] nvarchar(256) NOT NULL CONSTRAINT [DF_AiBuildJobs_Model] DEFAULT N'',
                    [Instruction] nvarchar(max) NOT NULL CONSTRAINT [DF_AiBuildJobs_Instruction] DEFAULT N'',
                    [CurrentFlowJson] nvarchar(max) NULL,
                    [Status] nvarchar(32) NOT NULL CONSTRAINT [DF_AiBuildJobs_Status] DEFAULT N'Queued',
                    [ProgressNote] nvarchar(512) NOT NULL CONSTRAINT [DF_AiBuildJobs_ProgressNote] DEFAULT N'Queued',
                    [ToolCallsUsed] int NOT NULL CONSTRAINT [DF_AiBuildJobs_ToolCallsUsed] DEFAULT 0,
                    [WarningsJson] nvarchar(max) NOT NULL CONSTRAINT [DF_AiBuildJobs_WarningsJson] DEFAULT N'[]',
                    [SourcesJson] nvarchar(max) NOT NULL CONSTRAINT [DF_AiBuildJobs_SourcesJson] DEFAULT N'[]',
                    [ResultFlowJson] nvarchar(max) NULL,
                    [Error] nvarchar(max) NOT NULL CONSTRAINT [DF_AiBuildJobs_Error] DEFAULT N'',
                    [CreatedAt] datetime2 NOT NULL,
                    [StartedAt] datetime2 NULL,
                    [CompletedAt] datetime2 NULL
                );
                CREATE INDEX [IX_AiBuildJobs_CreatedAt] ON [dbo].[AiBuildJobs]([CreatedAt]);
                CREATE INDEX [IX_AiBuildJobs_Status_CreatedAt] ON [dbo].[AiBuildJobs]([Status], [CreatedAt]);
            END;
            """;
        command.ExecuteNonQuery();
    }
    private static void EnsureAiBuildJobsTable(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS AiBuildJobs (
                    Id TEXT NOT NULL CONSTRAINT PK_AiBuildJobs PRIMARY KEY,
                    ProviderId TEXT NOT NULL,
                    ProviderName TEXT NOT NULL DEFAULT '',
                    Model TEXT NOT NULL DEFAULT '',
                    Instruction TEXT NOT NULL DEFAULT '',
                    CurrentFlowJson TEXT NULL,
                    Status TEXT NOT NULL DEFAULT 'Queued',
                    ProgressNote TEXT NOT NULL DEFAULT 'Queued',
                    ToolCallsUsed INTEGER NOT NULL DEFAULT 0,
                    WarningsJson TEXT NOT NULL DEFAULT '[]',
                    SourcesJson TEXT NOT NULL DEFAULT '[]',
                    ResultFlowJson TEXT NULL,
                    Error TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    StartedAt TEXT NULL,
                    CompletedAt TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_AiBuildJobs_CreatedAt ON AiBuildJobs (CreatedAt);
                CREATE INDEX IF NOT EXISTS IX_AiBuildJobs_Status_CreatedAt ON AiBuildJobs (Status, CreatedAt);
                """;
            command.ExecuteNonQuery();
        }
    }
    private static void EnsureSqlServerAiChatTables(SqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                IF OBJECT_ID(N'dbo.AiChatSessions', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[AiChatSessions](
                        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_AiChatSessions] PRIMARY KEY,
                        [Title] nvarchar(256) NOT NULL CONSTRAINT [DF_AiChatSessions_Title] DEFAULT N'',
                        [FlowId] uniqueidentifier NULL,
                        [Status] nvarchar(32) NOT NULL CONSTRAINT [DF_AiChatSessions_Status] DEFAULT N'Active',
                        [DraftFlowJson] nvarchar(max) NULL,
                        [LastProviderId] uniqueidentifier NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [UpdatedAt] datetime2 NOT NULL
                    );
                    CREATE INDEX [IX_AiChatSessions_UpdatedAt] ON [dbo].[AiChatSessions]([UpdatedAt]);
                END;
                IF OBJECT_ID(N'dbo.AiChatMessages', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[AiChatMessages](
                        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_AiChatMessages] PRIMARY KEY,
                        [SessionId] uniqueidentifier NOT NULL,
                        [Role] nvarchar(32) NOT NULL CONSTRAINT [DF_AiChatMessages_Role] DEFAULT N'User',
                        [Content] nvarchar(max) NOT NULL CONSTRAINT [DF_AiChatMessages_Content] DEFAULT N'',
                        [Instruction] nvarchar(max) NOT NULL CONSTRAINT [DF_AiChatMessages_Instruction] DEFAULT N'',
                        [Status] nvarchar(32) NOT NULL CONSTRAINT [DF_AiChatMessages_Status] DEFAULT N'Complete',
                        [StepsJson] nvarchar(max) NOT NULL CONSTRAINT [DF_AiChatMessages_StepsJson] DEFAULT N'[]',
                        [WarningsJson] nvarchar(max) NOT NULL CONSTRAINT [DF_AiChatMessages_WarningsJson] DEFAULT N'[]',
                        [SourcesJson] nvarchar(max) NOT NULL CONSTRAINT [DF_AiChatMessages_SourcesJson] DEFAULT N'[]',
                        [ResultFlowJson] nvarchar(max) NULL,
                        [Error] nvarchar(max) NOT NULL CONSTRAINT [DF_AiChatMessages_Error] DEFAULT N'',
                        [ProviderId] uniqueidentifier NULL,
                        [ProviderName] nvarchar(120) NOT NULL CONSTRAINT [DF_AiChatMessages_ProviderName] DEFAULT N'',
                        [Model] nvarchar(256) NOT NULL CONSTRAINT [DF_AiChatMessages_Model] DEFAULT N'',
                        [CreatedAt] datetime2 NOT NULL,
                        [CompletedAt] datetime2 NULL
                    );
                    CREATE INDEX [IX_AiChatMessages_SessionId_CreatedAt] ON [dbo].[AiChatMessages]([SessionId], [CreatedAt]);
                    CREATE INDEX [IX_AiChatMessages_Status_CreatedAt] ON [dbo].[AiChatMessages]([Status], [CreatedAt]);
                END;
                """;
            command.ExecuteNonQuery();
        }
    }
    private static void EnsureAiChatTables(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS AiChatSessions (
                    Id TEXT NOT NULL CONSTRAINT PK_AiChatSessions PRIMARY KEY,
                    Title TEXT NOT NULL DEFAULT '',
                    FlowId TEXT NULL,
                    Status TEXT NOT NULL DEFAULT 'Active',
                    DraftFlowJson TEXT NULL,
                    LastProviderId TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_AiChatSessions_UpdatedAt ON AiChatSessions (UpdatedAt);
                CREATE TABLE IF NOT EXISTS AiChatMessages (
                    Id TEXT NOT NULL CONSTRAINT PK_AiChatMessages PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    Role TEXT NOT NULL DEFAULT 'User',
                    Content TEXT NOT NULL DEFAULT '',
                    Instruction TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT 'Complete',
                    StepsJson TEXT NOT NULL DEFAULT '[]',
                    WarningsJson TEXT NOT NULL DEFAULT '[]',
                    SourcesJson TEXT NOT NULL DEFAULT '[]',
                    ResultFlowJson TEXT NULL,
                    Error TEXT NOT NULL DEFAULT '',
                    ProviderId TEXT NULL,
                    ProviderName TEXT NOT NULL DEFAULT '',
                    Model TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    CompletedAt TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_AiChatMessages_SessionId_CreatedAt ON AiChatMessages (SessionId, CreatedAt);
                CREATE INDEX IF NOT EXISTS IX_AiChatMessages_Status_CreatedAt ON AiChatMessages (Status, CreatedAt);
                """;
            command.ExecuteNonQuery();
        }
    }
    private static void EnsureSqlServerAlertSettingsTable(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.AlertSettings', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AlertSettings](
                    [Id] int NOT NULL CONSTRAINT [PK_AlertSettings] PRIMARY KEY,
                    [Enabled] bit NOT NULL CONSTRAINT [DF_AlertSettings_Enabled] DEFAULT 0,
                    [Recipients] nvarchar(1000) NOT NULL CONSTRAINT [DF_AlertSettings_Recipients] DEFAULT N'',
                    [CooldownMinutes] int NOT NULL CONSTRAINT [DF_AlertSettings_CooldownMinutes] DEFAULT 15,
                    [EnabledAt] datetime2 NULL,
                    [LastSentAt] datetime2 NULL,
                    [UpdatedAt] datetime2 NOT NULL
                );
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSqlServerAiProviderSettingsTable(SqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                IF OBJECT_ID(N'dbo.AiProviderSettings', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[AiProviderSettings](
                        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_AiProviderSettings] PRIMARY KEY,
                        [Name] nvarchar(120) NOT NULL CONSTRAINT [DF_AiProviderSettings_Name] DEFAULT N'Default provider',
                        [IsDefault] bit NOT NULL CONSTRAINT [DF_AiProviderSettings_IsDefault] DEFAULT 0,
                        [ProviderType] nvarchar(32) NOT NULL,
                        [ApiMode] nvarchar(32) NOT NULL CONSTRAINT [DF_AiProviderSettings_ApiMode] DEFAULT N'ChatCompletions',
                        [ReasoningEffort] nvarchar(16) NULL,
                        [EnableWebSearch] bit NOT NULL CONSTRAINT [DF_AiProviderSettings_EnableWebSearch] DEFAULT 0,
                        [Endpoint] nvarchar(2048) NOT NULL,
                        [Model] nvarchar(256) NOT NULL,
                        [EncryptedApiKey] nvarchar(max) NOT NULL,
                        [UpdatedAt] datetime2 NOT NULL
                    );
                END;
                """;
            command.ExecuteNonQuery();
        }

        // Keep existing single-provider installations intact while upgrading the old
        // tenant-only unique index to the provider-name key.
        EnsureSqlServerColumn(connection, "AiProviderSettings", "Name",
            "nvarchar(120) NOT NULL CONSTRAINT DF_AiProviderSettings_Name DEFAULT N'Default provider'");
        EnsureSqlServerColumn(connection, "AiProviderSettings", "IsDefault",
            "bit NOT NULL CONSTRAINT DF_AiProviderSettings_IsDefault DEFAULT 1");
        // Provider API mode. ChatCompletions is the safe default for every row that existed
        // before Responses support: they speak the chat-completions shape by definition.
        EnsureSqlServerColumn(connection, "AiProviderSettings", "ApiMode",
            "nvarchar(32) NOT NULL CONSTRAINT DF_AiProviderSettings_ApiMode DEFAULT N'ChatCompletions'");
        EnsureSqlServerColumn(connection, "AiProviderSettings", "ReasoningEffort", "nvarchar(16) NULL");
        EnsureSqlServerColumn(connection, "AiProviderSettings", "EnableWebSearch",
            "bit NOT NULL CONSTRAINT DF_AiProviderSettings_EnableWebSearch DEFAULT 0");

        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiProviderSettings_TenantId' AND object_id = OBJECT_ID(N'dbo.AiProviderSettings'))
                DROP INDEX [IX_AiProviderSettings_TenantId] ON [dbo].[AiProviderSettings];
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiProviderSettings_TenantId_Name' AND object_id = OBJECT_ID(N'dbo.AiProviderSettings'))
                DROP INDEX [IX_AiProviderSettings_TenantId_Name] ON [dbo].[AiProviderSettings];
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiProviderSettings_Name' AND object_id = OBJECT_ID(N'dbo.AiProviderSettings'))
                CREATE UNIQUE INDEX [IX_AiProviderSettings_Name] ON [dbo].[AiProviderSettings]([Name]);
            """;
        indexes.ExecuteNonQuery();
    }
    private static void EnsureAlertSettingsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AlertSettings (
                Id INTEGER NOT NULL CONSTRAINT PK_AlertSettings PRIMARY KEY,
                Enabled INTEGER NOT NULL DEFAULT 0,
                Recipients TEXT NOT NULL DEFAULT '',
                CooldownMinutes INTEGER NOT NULL DEFAULT 15,
                EnabledAt TEXT NULL,
                LastSentAt TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureAiProviderSettingsTable(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS AiProviderSettings (
                    Id TEXT NOT NULL CONSTRAINT PK_AiProviderSettings PRIMARY KEY,
                    Name TEXT NOT NULL DEFAULT 'Default provider',
                    IsDefault INTEGER NOT NULL DEFAULT 0,
                    ProviderType TEXT NOT NULL,
                    ApiMode TEXT NOT NULL DEFAULT 'ChatCompletions',
                    ReasoningEffort TEXT NULL,
                    EnableWebSearch INTEGER NOT NULL DEFAULT 0,
                    Endpoint TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    EncryptedApiKey TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        EnsureColumn(connection, "AiProviderSettings", "Name", "TEXT NOT NULL DEFAULT 'Default provider'");
        EnsureColumn(connection, "AiProviderSettings", "IsDefault", "INTEGER NOT NULL DEFAULT 1");
        // Existing rows get ChatCompletions: they were saved when that was the only shape.
        EnsureColumn(connection, "AiProviderSettings", "ApiMode", "TEXT NOT NULL DEFAULT 'ChatCompletions'");
        EnsureColumn(connection, "AiProviderSettings", "ReasoningEffort", "TEXT NULL");
        EnsureColumn(connection, "AiProviderSettings", "EnableWebSearch", "INTEGER NOT NULL DEFAULT 0");

        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            DROP INDEX IF EXISTS IX_AiProviderSettings_TenantId;
            DROP INDEX IF EXISTS IX_AiProviderSettings_TenantId_Name;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AiProviderSettings_Name
                ON AiProviderSettings(Name);
            """;
        indexes.ExecuteNonQuery();
    }
    private static void EnsureSqlServerIdentityTables(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.AspNetUsers', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AspNetUsers] (
                    [Id] uniqueidentifier NOT NULL,
                    [UserName] nvarchar(256) NULL,
                    [NormalizedUserName] nvarchar(256) NULL,
                    [Email] nvarchar(256) NULL,
                    [NormalizedEmail] nvarchar(256) NULL,
                    [EmailConfirmed] bit NOT NULL,
                    [PasswordHash] nvarchar(max) NULL,
                    [SecurityStamp] nvarchar(max) NULL,
                    [ConcurrencyStamp] nvarchar(max) NULL,
                    [PhoneNumber] nvarchar(max) NULL,
                    [PhoneNumberConfirmed] bit NOT NULL,
                    [TwoFactorEnabled] bit NOT NULL,
                    [LockoutEnd] datetimeoffset NULL,
                    [LockoutEnabled] bit NOT NULL,
                    [AccessFailedCount] int NOT NULL,
                    [FullName] nvarchar(200) NOT NULL CONSTRAINT [DF_AspNetUsers_FullName] DEFAULT N'',
                    [Role] nvarchar(32) NOT NULL CONSTRAINT [DF_AspNetUsers_Role] DEFAULT N'User',
                    [IsDisabled] bit NOT NULL CONSTRAINT [DF_AspNetUsers_IsDisabled] DEFAULT 0,
                    [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_AspNetUsers_CreatedAt] DEFAULT SYSUTCDATETIME(),
                    [UpdatedAt] datetime2 NULL,
                    [LastLoginAt] datetime2 NULL,
                    CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
                );
                CREATE INDEX [EmailIndex] ON [dbo].[AspNetUsers] ([NormalizedEmail]);
                CREATE UNIQUE INDEX [UserNameIndex] ON [dbo].[AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL;
            END;
            IF OBJECT_ID(N'dbo.AspNetUserClaims', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AspNetUserClaims] (
                    [Id] int NOT NULL IDENTITY,
                    [UserId] uniqueidentifier NOT NULL,
                    [ClaimType] nvarchar(max) NULL,
                    [ClaimValue] nvarchar(max) NULL,
                    CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE CASCADE
                );
                CREATE INDEX [IX_AspNetUserClaims_UserId] ON [dbo].[AspNetUserClaims] ([UserId]);
            END;
            IF OBJECT_ID(N'dbo.AspNetUserLogins', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AspNetUserLogins] (
                    [LoginProvider] nvarchar(450) NOT NULL,
                    [ProviderKey] nvarchar(450) NOT NULL,
                    [ProviderDisplayName] nvarchar(max) NULL,
                    [UserId] uniqueidentifier NOT NULL,
                    CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
                    CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE CASCADE
                );
                CREATE INDEX [IX_AspNetUserLogins_UserId] ON [dbo].[AspNetUserLogins] ([UserId]);
            END;
            IF OBJECT_ID(N'dbo.AspNetUserTokens', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AspNetUserTokens] (
                    [UserId] uniqueidentifier NOT NULL,
                    [LoginProvider] nvarchar(450) NOT NULL,
                    [Name] nvarchar(450) NOT NULL,
                    [Value] nvarchar(max) NULL,
                    CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
                    CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE CASCADE
                );
            END;
            IF OBJECT_ID(N'dbo.RefreshTokens', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[RefreshTokens] (
                    [Id] uniqueidentifier NOT NULL,
                    [UserId] uniqueidentifier NOT NULL,
                    [TokenHash] nvarchar(64) NOT NULL,
                    [ExpiresAt] datetime2 NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [RevokedAt] datetime2 NULL,
                    [ReplacedByTokenHash] nvarchar(64) NULL,
                    [CreatedByIp] nvarchar(64) NULL,
                    [UserAgent] nvarchar(400) NULL,
                    CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_RefreshTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX [IX_RefreshTokens_TokenHash] ON [dbo].[RefreshTokens] ([TokenHash]);
                CREATE INDEX [IX_RefreshTokens_UserId] ON [dbo].[RefreshTokens] ([UserId]);
            END;
            IF OBJECT_ID(N'dbo.AuthAuditEntries', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AuthAuditEntries] (
                    [Id] uniqueidentifier NOT NULL,
                    [TimestampUtc] datetime2 NOT NULL,
                    [UserId] uniqueidentifier NULL,
                    [Email] nvarchar(320) NOT NULL CONSTRAINT [DF_AuthAuditEntries_Email] DEFAULT N'',
                    [EventType] nvarchar(64) NOT NULL CONSTRAINT [DF_AuthAuditEntries_EventType] DEFAULT N'',
                    [Success] bit NOT NULL,
                    [IpAddress] nvarchar(64) NULL,
                    [UserAgent] nvarchar(400) NULL,
                    [Details] nvarchar(1000) NULL,
                    CONSTRAINT [PK_AuthAuditEntries] PRIMARY KEY ([Id])
                );
                CREATE INDEX [IX_AuthAuditEntries_TimestampUtc] ON [dbo].[AuthAuditEntries] ([TimestampUtc]);
                CREATE INDEX [IX_AuthAuditEntries_Email] ON [dbo].[AuthAuditEntries] ([Email]);
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureIdentityTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AspNetUsers (
                Id TEXT NOT NULL CONSTRAINT PK_AspNetUsers PRIMARY KEY,
                UserName TEXT NULL,
                NormalizedUserName TEXT NULL,
                Email TEXT NULL,
                NormalizedEmail TEXT NULL,
                EmailConfirmed INTEGER NOT NULL,
                PasswordHash TEXT NULL,
                SecurityStamp TEXT NULL,
                ConcurrencyStamp TEXT NULL,
                PhoneNumber TEXT NULL,
                PhoneNumberConfirmed INTEGER NOT NULL,
                TwoFactorEnabled INTEGER NOT NULL,
                LockoutEnd TEXT NULL,
                LockoutEnabled INTEGER NOT NULL,
                AccessFailedCount INTEGER NOT NULL,
                FullName TEXT NOT NULL DEFAULT '',
                Role TEXT NOT NULL DEFAULT 'User',
                IsDisabled INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                UpdatedAt TEXT NULL,
                LastLoginAt TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UserNameIndex ON AspNetUsers (NormalizedUserName);
            CREATE INDEX IF NOT EXISTS EmailIndex ON AspNetUsers (NormalizedEmail);
            CREATE INDEX IF NOT EXISTS IX_AspNetUsers_Role ON AspNetUsers (Role);

            CREATE TABLE IF NOT EXISTS AspNetUserClaims (
                Id INTEGER NOT NULL CONSTRAINT PK_AspNetUserClaims PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL,
                ClaimType TEXT NULL,
                ClaimValue TEXT NULL,
                CONSTRAINT FK_AspNetUserClaims_AspNetUsers_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_AspNetUserClaims_UserId ON AspNetUserClaims (UserId);

            CREATE TABLE IF NOT EXISTS AspNetUserLogins (
                LoginProvider TEXT NOT NULL,
                ProviderKey TEXT NOT NULL,
                ProviderDisplayName TEXT NULL,
                UserId TEXT NOT NULL,
                CONSTRAINT PK_AspNetUserLogins PRIMARY KEY (LoginProvider, ProviderKey),
                CONSTRAINT FK_AspNetUserLogins_AspNetUsers_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_AspNetUserLogins_UserId ON AspNetUserLogins (UserId);

            CREATE TABLE IF NOT EXISTS AspNetUserTokens (
                UserId TEXT NOT NULL,
                LoginProvider TEXT NOT NULL,
                Name TEXT NOT NULL,
                Value TEXT NULL,
                CONSTRAINT PK_AspNetUserTokens PRIMARY KEY (UserId, LoginProvider, Name),
                CONSTRAINT FK_AspNetUserTokens_AspNetUsers_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS RefreshTokens (
                Id TEXT NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
                UserId TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                RevokedAt TEXT NULL,
                ReplacedByTokenHash TEXT NULL,
                CreatedByIp TEXT NULL,
                UserAgent TEXT NULL,
                CONSTRAINT FK_RefreshTokens_AspNetUsers_UserId FOREIGN KEY (UserId) REFERENCES AspNetUsers (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_RefreshTokens_TokenHash ON RefreshTokens (TokenHash);
            CREATE INDEX IF NOT EXISTS IX_RefreshTokens_UserId ON RefreshTokens (UserId);

            CREATE TABLE IF NOT EXISTS AuthAuditEntries (
                Id TEXT NOT NULL CONSTRAINT PK_AuthAuditEntries PRIMARY KEY,
                TimestampUtc TEXT NOT NULL,
                UserId TEXT NULL,
                Email TEXT NOT NULL DEFAULT '',
                EventType TEXT NOT NULL DEFAULT '',
                Success INTEGER NOT NULL,
                IpAddress TEXT NULL,
                UserAgent TEXT NULL,
                Details TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AuthAuditEntries_TimestampUtc ON AuthAuditEntries (TimestampUtc);
            CREATE INDEX IF NOT EXISTS IX_AuthAuditEntries_Email ON AuthAuditEntries (Email);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSqlServerColumn(SqlConnection connection, string tableName, string columnName, string definition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"IF COL_LENGTH('dbo.{tableName}', '{columnName}') IS NULL ALTER TABLE [dbo].[{tableName}] ADD [{columnName}] {definition};";
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (existingColumns.Contains(columnName))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------------------------------
    // Tenancy removal. One deployment serves one customer, so tenant_id is not just unused:
    // the columns are NOT NULL without defaults and would break inserts once the EF model no
    // longer sets them. This step is idempotent and is called by both hosts at startup.
    // ---------------------------------------------------------------------------------------
    private static readonly (string Table, string SqliteSql, string SqlServerSql)[] ReplacementIndexes =
    {
        ("FlowVersions",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_FlowVersions_FlowId_VersionNumber ON FlowVersions (FlowId, VersionNumber);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowVersions_FlowId_VersionNumber' AND object_id = OBJECT_ID(N'dbo.FlowVersions')) CREATE UNIQUE INDEX [IX_FlowVersions_FlowId_VersionNumber] ON [dbo].[FlowVersions]([FlowId], [VersionNumber]);"),
        ("FlowTestCases",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_FlowTestCases_FlowId_Name ON FlowTestCases (FlowId, Name);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowTestCases_FlowId_Name' AND object_id = OBJECT_ID(N'dbo.FlowTestCases')) CREATE UNIQUE INDEX [IX_FlowTestCases_FlowId_Name] ON [dbo].[FlowTestCases]([FlowId], [Name]);"),
        ("FlowTestRuns",
            "CREATE INDEX IF NOT EXISTS IX_FlowTestRuns_TestCaseId_StartedAt ON FlowTestRuns (TestCaseId, StartedAt);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowTestRuns_TestCaseId_StartedAt' AND object_id = OBJECT_ID(N'dbo.FlowTestRuns')) CREATE INDEX [IX_FlowTestRuns_TestCaseId_StartedAt] ON [dbo].[FlowTestRuns]([TestCaseId], [StartedAt]);"),
        ("AiProviderSettings",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_AiProviderSettings_Name ON AiProviderSettings (Name);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiProviderSettings_Name' AND object_id = OBJECT_ID(N'dbo.AiProviderSettings')) CREATE UNIQUE INDEX [IX_AiProviderSettings_Name] ON [dbo].[AiProviderSettings]([Name]);"),
        ("AiBuildJobs",
            "CREATE INDEX IF NOT EXISTS IX_AiBuildJobs_CreatedAt ON AiBuildJobs (CreatedAt);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiBuildJobs_CreatedAt' AND object_id = OBJECT_ID(N'dbo.AiBuildJobs')) CREATE INDEX [IX_AiBuildJobs_CreatedAt] ON [dbo].[AiBuildJobs]([CreatedAt]);"),
        ("AiChatSessions",
            "CREATE INDEX IF NOT EXISTS IX_AiChatSessions_UpdatedAt ON AiChatSessions (UpdatedAt);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiChatSessions_UpdatedAt' AND object_id = OBJECT_ID(N'dbo.AiChatSessions')) CREATE INDEX [IX_AiChatSessions_UpdatedAt] ON [dbo].[AiChatSessions]([UpdatedAt]);"),
        ("CrossReferenceLists",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_CrossReferenceLists_Name ON CrossReferenceLists (Name);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CrossReferenceLists_Name' AND object_id = OBJECT_ID(N'dbo.CrossReferenceLists')) CREATE UNIQUE INDEX [IX_CrossReferenceLists_Name] ON [dbo].[CrossReferenceLists]([Name]);"),
        ("CrossReferenceEntries",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_CrossReferenceEntries_ListName_KeyValue ON CrossReferenceEntries (ListName, KeyValue);",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CrossReferenceEntries_ListName_KeyValue' AND object_id = OBJECT_ID(N'dbo.CrossReferenceEntries')) CREATE UNIQUE INDEX [IX_CrossReferenceEntries_ListName_KeyValue] ON [dbo].[CrossReferenceEntries]([ListName], [KeyValue]);")
    };

    public static void RemoveTenancy(SqliteConnection connection, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var tenantTables = GetSqliteTenantTables(connection);
        var alertSettingsNeedsRebuild = SqliteAlertSettingsNeedsRebuild(connection);
        if (tenantTables.Count == 0 && !alertSettingsNeedsRebuild)
        {
            return;
        }

        if (HasMultipleSqliteTenants(connection, tenantTables, logger))
        {
            return;
        }

        if (!TryBackupSqliteDatabase(connection, logger))
        {
            return;
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            // Re-detect under the write lock: another host may have finished while we waited.
            tenantTables = GetSqliteTenantTables(connection, transaction);
            alertSettingsNeedsRebuild = SqliteAlertSettingsNeedsRebuild(connection, transaction);
            if (tenantTables.Count == 0 && !alertSettingsNeedsRebuild)
            {
                transaction.Commit();
                return;
            }

            foreach (var table in tenantTables)
            {
                foreach (var index in GetSqliteIndexesUsingTenant(connection, transaction, table))
                {
                    ExecuteSqlite(connection, transaction, $"DROP INDEX IF EXISTS \"{EscapeIdentifier(index)}\";");
                }
            }

            if (alertSettingsNeedsRebuild)
            {
                RebuildSqliteAlertSettings(connection, transaction);
                tenantTables.RemoveAll(table => string.Equals(table, "AlertSettings", StringComparison.OrdinalIgnoreCase));
            }

            foreach (var table in tenantTables)
            {
                ExecuteSqlite(connection, transaction,
                    $"ALTER TABLE \"{EscapeIdentifier(table)}\" DROP COLUMN \"TenantId\";");
            }

            foreach (var (table, sqliteSql, _) in ReplacementIndexes)
            {
                if (SqliteTableExists(connection, transaction, table))
                {
                    ExecuteSqlite(connection, transaction, sqliteSql);
                }
            }

            transaction.Commit();
            logger.LogInformation(
                "Tenancy removal complete: tenant_id is gone from {TableCount} table(s).", tenantTables.Count);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            logger.LogError(ex, "Tenancy removal failed; the database transaction was rolled back.");
            throw;
        }
    }

    public static void RemoveTenancy(SqlConnection connection, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var tenantTables = GetSqlServerTenantTables(connection);
        var alertSettingsNeedsRebuild = SqlServerAlertSettingsNeedsRebuild(connection);
        if (tenantTables.Count == 0 && !alertSettingsNeedsRebuild)
        {
            return;
        }

        if (HasMultipleSqlServerTenants(connection, tenantTables, logger))
        {
            return;
        }

        logger.LogWarning(
            "Removing multi-tenancy from SQL Server. No automatic backup is taken; restore from a database backup if needed.");

        using var transaction = connection.BeginTransaction();
        try
        {
            // The API and the Engine both call this at startup. SQLite waits on BEGIN IMMEDIATE;
            // on SQL Server a transaction-scoped application lock makes the second host wait for
            // the first instead of racing it and failing on an index/column that was just dropped.
            using (var lockCommand = connection.CreateCommand())
            {
                lockCommand.Transaction = transaction;
                lockCommand.CommandText = """
                    DECLARE @result int;
                    EXEC @result = sp_getapplock
                        @Resource = N'Fusetor.RemoveTenancy',
                        @LockMode = N'Exclusive',
                        @LockOwner = N'Transaction',
                        @LockTimeout = 120000;
                    SELECT @result;
                    """;
                var lockResult = Convert.ToInt32(lockCommand.ExecuteScalar());
                if (lockResult < 0)
                {
                    throw new InvalidOperationException(
                        $"Could not acquire the tenancy-removal lock (sp_getapplock returned {lockResult}).");
                }
            }

            tenantTables = GetSqlServerTenantTables(connection, transaction);
            alertSettingsNeedsRebuild = SqlServerAlertSettingsNeedsRebuild(connection, transaction);
            if (tenantTables.Count == 0 && !alertSettingsNeedsRebuild)
            {
                transaction.Commit();
                return;
            }

            foreach (var table in tenantTables)
            {
                foreach (var index in GetSqlServerIndexesUsingTenant(connection, transaction, table))
                {
                    ExecuteSqlServer(connection, transaction,
                        $"DROP INDEX [{EscapeSqlServerIdentifier(index)}] ON [dbo].[{EscapeSqlServerIdentifier(table)}];");
                }

                foreach (var constraint in GetSqlServerDefaultConstraints(connection, transaction, table))
                {
                    ExecuteSqlServer(connection, transaction,
                        $"ALTER TABLE [dbo].[{EscapeSqlServerIdentifier(table)}] DROP CONSTRAINT [{EscapeSqlServerIdentifier(constraint)}];");
                }
            }

            if (alertSettingsNeedsRebuild)
            {
                RebuildSqlServerAlertSettings(connection, transaction);
                tenantTables.RemoveAll(table => string.Equals(table, "AlertSettings", StringComparison.OrdinalIgnoreCase));
            }

            foreach (var table in tenantTables)
            {
                ExecuteSqlServer(connection, transaction,
                    $"ALTER TABLE [dbo].[{EscapeSqlServerIdentifier(table)}] DROP COLUMN [TenantId];");
            }

            foreach (var (table, _, sqlServerSql) in ReplacementIndexes)
            {
                if (SqlServerTableExists(connection, transaction, table))
                {
                    ExecuteSqlServer(connection, transaction, sqlServerSql);
                }
            }

            transaction.Commit();
            logger.LogInformation(
                "Tenancy removal complete: tenant_id is gone from {TableCount} table(s).", tenantTables.Count);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            logger.LogError(ex, "Tenancy removal failed; the database transaction was rolled back.");
            throw;
        }
    }

    private static string EscapeIdentifier(string value) => value.Replace("\"", "\"\"");

    private static string EscapeSqlServerIdentifier(string value) => value.Replace("]", "]]");

    private static string EscapeLiteral(string value) => value.Replace("'", "''");

    private static void ExecuteSqlite(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteSqlServer(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> GetSqliteTenantTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var allTables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                allTables.Add(reader.GetString(0));
            }
        }

        return allTables
            .Where(table => GetSqliteColumns(connection, transaction, table).ContainsKey("TenantId"))
            .ToList();
    }

    private static Dictionary<string, string> GetSqliteColumns(
        SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{EscapeIdentifier(table)}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns[reader.GetString(1)] = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        }

        return columns;
    }

    private static bool SqliteTableExists(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static bool SqliteAlertSettingsNeedsRebuild(
        SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var columns = GetSqliteColumns(connection, transaction, "AlertSettings");
        if (columns.Count == 0)
        {
            return false;
        }

        return columns.ContainsKey("TenantId")
            || !columns.TryGetValue("Id", out var idType)
            || !string.Equals(idType, "INTEGER", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMultipleSqliteTenants(
        SqliteConnection connection, IReadOnlyCollection<string> tables, ILogger logger)
    {
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(DISTINCT \"TenantId\") FROM \"{EscapeIdentifier(table)}\" WHERE \"TenantId\" IS NOT NULL;";
            if (Convert.ToInt64(command.ExecuteScalar()) > 1)
            {
                logger.LogError(
                    "Tenancy removal skipped: table {Table} contains more than one distinct TenantId. Refusing to merge tenant data.",
                    table);
                return true;
            }
        }

        return false;
    }

    private static bool TryBackupSqliteDatabase(SqliteConnection connection, ILogger logger)
    {
        var dataSource = connection.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.StartsWith(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("SQLite database is in-memory; skipping the pre-migration backup.");
            return true;
        }

        try
        {
            var databasePath = Path.GetFullPath(dataSource);
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }

            Directory.CreateDirectory(directory);
            var databaseFileName = Path.GetFileName(databasePath);
            if (string.IsNullOrEmpty(databaseFileName))
            {
                databaseFileName = "ipaas.db";
            }

            var backupBaseName =
                $"{databaseFileName}.bak-pre-remove-tenancy-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var backupPath = Path.Combine(directory, backupBaseName);

            if (File.Exists(backupPath))
            {
                backupPath = Path.Combine(directory, $"{backupBaseName}-{Guid.NewGuid():N}");
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{EscapeLiteral(backupPath)}';";
            command.ExecuteNonQuery();
            logger.LogInformation("Created pre-migration SQLite backup at {BackupPath}.", backupPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not create the pre-migration SQLite backup; skipping tenancy removal.");
            return false;
        }
    }

    private static IEnumerable<string> GetSqliteIndexesUsingTenant(
        SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        var indexes = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_list(\"{EscapeIdentifier(table)}\");";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (!name.StartsWith("sqlite_autoindex", StringComparison.OrdinalIgnoreCase))
                {
                    indexes.Add(name);
                }
            }
        }

        foreach (var index in indexes.ToList())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_info(\"{EscapeIdentifier(index)}\");";
            using var reader = command.ExecuteReader();
            var usesTenant = false;
            while (reader.Read())
            {
                if (!reader.IsDBNull(2)
                    && string.Equals(reader.GetString(2), "TenantId", StringComparison.OrdinalIgnoreCase))
                {
                    usesTenant = true;
                    break;
                }
            }

            if (!usesTenant)
            {
                indexes.Remove(index);
            }
        }

        return indexes;
    }

    private static void RebuildSqliteAlertSettings(SqliteConnection connection, SqliteTransaction transaction)
    {
        var columns = GetSqliteColumns(connection, transaction, "AlertSettings");
        string Expr(string name, string fallback) => columns.ContainsKey(name) ? name : fallback;

        var enabled = columns.ContainsKey("Enabled") ? "COALESCE(Enabled, 0)" : "0";
        var recipients = columns.ContainsKey("Recipients") ? "COALESCE(Recipients, '')" : "''";
        var cooldown = columns.ContainsKey("CooldownMinutes") ? "COALESCE(CooldownMinutes, 15)" : "15";
        var enabledAt = Expr("EnabledAt", "NULL");
        var lastSentAt = Expr("LastSentAt", "NULL");
        var updatedAt = columns.ContainsKey("UpdatedAt")
            ? "COALESCE(UpdatedAt, CURRENT_TIMESTAMP)"
            : "CURRENT_TIMESTAMP";

        ExecuteSqlite(connection, transaction, "DROP TABLE IF EXISTS AlertSettings_new;");
        ExecuteSqlite(connection, transaction, """
            CREATE TABLE AlertSettings_new (
                Id INTEGER NOT NULL CONSTRAINT PK_AlertSettings PRIMARY KEY,
                Enabled INTEGER NOT NULL DEFAULT 0,
                Recipients TEXT NOT NULL DEFAULT '',
                CooldownMinutes INTEGER NOT NULL DEFAULT 15,
                EnabledAt TEXT NULL,
                LastSentAt TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """);
        ExecuteSqlite(connection, transaction, $"""
            INSERT INTO AlertSettings_new (Id, Enabled, Recipients, CooldownMinutes, EnabledAt, LastSentAt, UpdatedAt)
            SELECT 1, {enabled}, {recipients}, {cooldown}, {enabledAt}, {lastSentAt}, {updatedAt}
            FROM AlertSettings ORDER BY rowid LIMIT 1;
            """);
        ExecuteSqlite(connection, transaction, "DROP TABLE AlertSettings;");
        ExecuteSqlite(connection, transaction, "ALTER TABLE AlertSettings_new RENAME TO AlertSettings;");
    }

    private static List<string> GetSqlServerTenantTables(
        SqlConnection connection, SqlTransaction? transaction = null)
    {
        var tables = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT t.name
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            WHERE c.name = N'TenantId' AND t.schema_id = SCHEMA_ID(N'dbo')
            ORDER BY t.name;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static Dictionary<string, string> GetSqlServerColumns(
        SqlConnection connection, SqlTransaction? transaction, string table)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.name, ty.name
            FROM sys.columns c
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            WHERE c.object_id = OBJECT_ID(@table);
            """;
        command.Parameters.AddWithValue("@table", $"dbo.{table}");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        return columns;
    }

    private static bool SqlServerTableExists(
        SqlConnection connection, SqlTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.AddWithValue("@table", $"dbo.{table}");
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static bool SqlServerAlertSettingsNeedsRebuild(
        SqlConnection connection, SqlTransaction? transaction = null)
    {
        var columns = GetSqlServerColumns(connection, transaction, "AlertSettings");
        if (columns.Count == 0)
        {
            return false;
        }

        return columns.ContainsKey("TenantId")
            || !columns.TryGetValue("Id", out var idType)
            || !string.Equals(idType, "int", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMultipleSqlServerTenants(
        SqlConnection connection, IReadOnlyCollection<string> tables, ILogger logger)
    {
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(DISTINCT [TenantId]) FROM [dbo].[{EscapeSqlServerIdentifier(table)}] WHERE [TenantId] IS NOT NULL;";
            if (Convert.ToInt64(command.ExecuteScalar()) > 1)
            {
                logger.LogError(
                    "Tenancy removal skipped: table {Table} contains more than one distinct TenantId. Refusing to merge tenant data.",
                    table);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetSqlServerIndexesUsingTenant(
        SqlConnection connection, SqlTransaction transaction, string table)
    {
        var indexes = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE i.object_id = OBJECT_ID(@table)
              AND c.name = N'TenantId'
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0;
            """;
        command.Parameters.AddWithValue("@table", $"dbo.{table}");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                indexes.Add(reader.GetString(0));
            }
        }

        return indexes;
    }

    private static IEnumerable<string> GetSqlServerDefaultConstraints(
        SqlConnection connection, SqlTransaction transaction, string table)
    {
        var constraints = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT dc.name
            FROM sys.default_constraints dc
            JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE dc.parent_object_id = OBJECT_ID(@table) AND c.name = N'TenantId';
            """;
        command.Parameters.AddWithValue("@table", $"dbo.{table}");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            constraints.Add(reader.GetString(0));
        }

        return constraints;
    }

    private static void RebuildSqlServerAlertSettings(SqlConnection connection, SqlTransaction transaction)
    {
        var columns = GetSqlServerColumns(connection, transaction, "AlertSettings");
        string Expr(string name, string fallback) => columns.ContainsKey(name) ? name : fallback;

        var enabled = columns.ContainsKey("Enabled") ? "ISNULL(Enabled, 0)" : "CAST(0 AS bit)";
        var recipients = columns.ContainsKey("Recipients") ? "ISNULL(Recipients, N'')" : "N''";
        var cooldown = columns.ContainsKey("CooldownMinutes") ? "ISNULL(CooldownMinutes, 15)" : "15";
        var enabledAt = Expr("EnabledAt", "NULL");
        var lastSentAt = Expr("LastSentAt", "NULL");
        var updatedAt = columns.ContainsKey("UpdatedAt") ? "ISNULL(UpdatedAt, SYSUTCDATETIME())" : "SYSUTCDATETIME()";

        ExecuteSqlServer(connection, transaction, """
            IF OBJECT_ID(N'dbo.AlertSettings_new', N'U') IS NOT NULL DROP TABLE [dbo].[AlertSettings_new];
            CREATE TABLE [dbo].[AlertSettings_new](
                [Id] int NOT NULL PRIMARY KEY,
                [Enabled] bit NOT NULL DEFAULT 0,
                [Recipients] nvarchar(1000) NOT NULL DEFAULT N'',
                [CooldownMinutes] int NOT NULL DEFAULT 15,
                [EnabledAt] datetime2 NULL,
                [LastSentAt] datetime2 NULL,
                [UpdatedAt] datetime2 NOT NULL
            );
            """);
        ExecuteSqlServer(connection, transaction, $"""
            INSERT INTO [dbo].[AlertSettings_new] (Id, Enabled, Recipients, CooldownMinutes, EnabledAt, LastSentAt, UpdatedAt)
            SELECT TOP (1) 1, {enabled}, {recipients}, {cooldown}, {enabledAt}, {lastSentAt}, {updatedAt}
            FROM [dbo].[AlertSettings];
            """);
        ExecuteSqlServer(connection, transaction, "DROP TABLE [dbo].[AlertSettings];");
        ExecuteSqlServer(connection, transaction, "EXEC sp_rename N'dbo.AlertSettings_new', N'AlertSettings';");
    }
}

