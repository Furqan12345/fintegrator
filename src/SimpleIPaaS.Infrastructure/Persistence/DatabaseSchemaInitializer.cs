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

        EnsureIntegrationsTable(connection);
        EnsureApiKeysTable(connection);
        EnsureCrossReferenceTables(connection);
        EnsureStepPacketLogsTable(connection);
        EnsureColumn(connection, "IntegrationFlows", "IntegrationId", "TEXT NULL");
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
        EnsureColumn(connection, "DeadLetterEntries", "ResolvedAt", "TEXT NULL");
        EnsureColumn(connection, "DeadLetterEntries", "AttemptHistoryJson", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "DeadLetterEntries", "FlowName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DeadLetterEntries", "IntegrationName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "DeadLetterEntries", "NodeName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "IntegrationFlows", "RunAt", "TEXT NULL");
        EnsureColumn(connection, "FlowExecutions", "TriggerPayloadJson", "TEXT NULL");
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
