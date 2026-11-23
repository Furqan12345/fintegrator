using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class AlertSettingsSchemaTests
{
    [Fact]
    public void EnsureUpToDate_CreatesAlertSettingsAndDeadLetterAlertedAtColumn()
    {
        using var connection = new SqliteConnection("Data Source=AlertSchemaTest;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            DatabaseSchemaInitializer.EnsureUpToDate(context);
        }

        Assert.False(ColumnExists(connection, "AlertSettings", "TenantId"));
        Assert.True(ColumnExists(connection, "AlertSettings", "Recipients"));
        Assert.True(ColumnExists(connection, "AlertSettings", "CooldownMinutes"));
        Assert.True(ColumnExists(connection, "AlertSettings", "EnabledAt"));
        Assert.True(ColumnExists(connection, "DeadLetterEntries", "AlertedAt"));
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

