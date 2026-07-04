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
        EnsureColumn(connection, "IntegrationFlows", "IntegrationId", "TEXT NULL");
        EnsureColumn(connection, "IntegrationFlows", "PersistedStateJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "IntegrationSteps", "UrlMode", "INTEGER NOT NULL DEFAULT 0");
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
