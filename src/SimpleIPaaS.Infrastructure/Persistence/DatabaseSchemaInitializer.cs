using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SimpleIPaaS.Infrastructure.Persistence;

public static class DatabaseSchemaInitializer
{
    public static void EnsureUpToDate(IPaaSContext context)
    {
        context.Database.EnsureCreated();

        using var connection = new SqliteConnection(context.Database.GetConnectionString());
        connection.Open();

        EnsureFlowVersionsTable(connection);
        EnsureFlowTestTables(connection);
        EnsureHostHeartbeatsTable(connection);
        EnsureIntegrationsTable(connection);
        EnsureApiKeysTable(connection);
        EnsureCrossReferenceTables(connection);
        EnsureStepPacketLogsTable(connection);
        EnsureAppLogEntriesTable(connection);
        EnsureColumn(connection, "AppLogEntries", "FlowName", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "IntegrationId", "TEXT NULL");
        EnsureColumn(connection, "AppLogEntries", "IntegrationName", "TEXT NULL");
        EnsureColumn(connection, "IntegrationFlows", "IntegrationId", "TEXT NULL");
        EnsureColumn(connection, "IntegrationFlows", "PublishedVersion", "INTEGER NULL");
        EnsureColumn(connection, "IntegrationFlows", "LastPublishedAt", "TEXT NULL");
        EnsureColumn(connection, "IntegrationFlows", "PersistedStateJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "IntegrationSteps", "UrlMode", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "IntegrationSteps", "AuthConfigJson", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationFlows", "NextRunAt", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TriggerSource", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationFlows", "AllowPostReplay", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "DeadLetterEntries", "FlowStateJson", "TEXT NOT NULL DEFAULT '{}'");
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
        EnsureColumn(connection, "DeadLetterEntries", "AttemptHistoryJson", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "DeadLetterEntries", "FlowName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DeadLetterEntries", "IntegrationName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DeadLetterEntries", "NodeName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationFlows", "RunAt", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TriggerPayloadJson", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "IsTest", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "FlowExecutions", "TestCaseId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TestRunId", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TestDefinitionJson", "TEXT NULL");
        EnsureColumn(connection, "DeadLetterEntries", "ReplayRequestedAt", "TEXT NULL");
        EnsureDatabasePragmas(connection);
    }

    private static void EnsureDatabasePragmas(SqliteConnection connection)
    {
        // The API and the Engine share this database file in WAL mode. journal_mode=WAL
        // persists in the database file itself; busy_timeout is per-connection and lets a
        // write from one process wait on the other instead of failing with SQLITE_BUSY.
        using var walCommand = connection.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode=WAL;";
        walCommand.ExecuteScalar();

        using var busyTimeoutCommand = connection.CreateCommand();
        busyTimeoutCommand.CommandText = "PRAGMA busy_timeout=8000;";
        busyTimeoutCommand.ExecuteNonQuery();
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
                TenantId TEXT NOT NULL,
                VersionNumber INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                ChangeNote TEXT NOT NULL DEFAULT '',
                RolledBackFromVersion INTEGER NULL,
                SnapshotJson TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_FlowVersions_TenantId_FlowId_VersionNumber
                ON FlowVersions (TenantId, FlowId, VersionNumber);

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
                TenantId TEXT NOT NULL,
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
            CREATE UNIQUE INDEX IF NOT EXISTS IX_FlowTestCases_TenantId_FlowId_Name
                ON FlowTestCases (TenantId, FlowId, Name);
            CREATE TABLE IF NOT EXISTS FlowTestRuns (
                Id TEXT NOT NULL CONSTRAINT PK_FlowTestRuns PRIMARY KEY,
                TestCaseId TEXT NOT NULL,
                FlowId TEXT NOT NULL,
                TenantId TEXT NOT NULL,
                FlowExecutionId TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Queued',
                ResultJson TEXT NOT NULL DEFAULT '{}',
                ErrorMessage TEXT NULL,
                FlowUpdatedAt TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FlowTestRuns_TenantId_TestCaseId_StartedAt
                ON FlowTestRuns (TenantId, TestCaseId, StartedAt);
            """;
        command.ExecuteNonQuery();
    }
    private static void EnsureIntegrationsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Integrations (
                Id TEXT NOT NULL CONSTRAINT PK_Integrations PRIMARY KEY,
                TenantId TEXT NOT NULL,
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
                TenantId TEXT NOT NULL,
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
                TenantId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_CrossReferenceLists_TenantId_Name
                ON CrossReferenceLists (TenantId, Name);

            CREATE TABLE IF NOT EXISTS CrossReferenceEntries (
                Id TEXT NOT NULL CONSTRAINT PK_CrossReferenceEntries PRIMARY KEY,
                TenantId TEXT NOT NULL,
                ListName TEXT NOT NULL,
                KeyValue TEXT NOT NULL,
                ValueJson TEXT NOT NULL,
                FlowId TEXT NULL,
                FlowName TEXT NULL,
                IntegrationId TEXT NULL,
                IntegrationName TEXT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_CrossReferenceEntries_TenantId_ListName_KeyValue
                ON CrossReferenceEntries (TenantId, ListName, KeyValue);
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
                TenantId TEXT NOT NULL,
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
    // DatabaseLoggerProvider opens its own raw connection (it must never go through EF —
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
                TenantId TEXT NULL,
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
}
