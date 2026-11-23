using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class AiProviderSchemaMigrationTests
{
    [Fact]
    public void EnsureUpToDate_AddsArchivedStepColumnToExistingDatabase()
    {
        using var connection = new SqliteConnection("Data Source=ArchivedStepSchemaMigration;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var flowId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.IntegrationFlows.Add(new IntegrationFlow { Id = flowId, Name = "Existing flow" });
            context.IntegrationSteps.Add(new IntegrationStep { FlowId = flowId, NodeName = "Existing node" });
            context.SaveChanges();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE IntegrationSteps DROP COLUMN IsArchived;";
            command.ExecuteNonQuery();
        }

        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);
            var existingNode = Assert.Single(context.IntegrationSteps);
            Assert.Equal("Existing node", existingNode.NodeName);
            Assert.False(existingNode.IsArchived);
        }
    }

    [Fact]
    public void EnsureUpToDate_UpgradesLegacySingleProviderTableWithoutLosingConfiguration()
    {
        using var connection = new SqliteConnection("Data Source=AiProviderSchemaMigration;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();

            context.AiProviderSettings.Add(new AiProviderSettings
            {
                Name = "Default provider",
                IsDefault = true,
                ProviderType = "OpenAICompatible",
                Endpoint = "https://api.openai.com/v1/chat/completions",
                Model = "gpt-test",
                EncryptedApiKey = "existing-encrypted-key",
                UpdatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX IX_AiProviderSettings_Name;
                ALTER TABLE AiProviderSettings DROP COLUMN Name;
                ALTER TABLE AiProviderSettings DROP COLUMN IsDefault;
                """;
            command.ExecuteNonQuery();
        }

        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);

            var migrated = Assert.Single(context.AiProviderSettings);
            Assert.Equal("Default provider", migrated.Name);
            Assert.True(migrated.IsDefault);
            Assert.Equal("existing-encrypted-key", migrated.EncryptedApiKey);
            Assert.Equal("https://api.openai.com/v1/chat/completions", migrated.Endpoint);

            var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA index_list(AiProviderSettings);";
            using var reader = command.ExecuteReader();
            while (reader.Read()) indexes.Add(reader.GetString(1));
            Assert.Contains("IX_AiProviderSettings_Name", indexes);
            Assert.DoesNotContain("IX_AiProviderSettings_TenantId_Name", indexes);
            Assert.DoesNotContain("IX_AiProviderSettings_TenantId", indexes);
        }
    }

    [Fact]
    public void EnsureUpToDate_AddsTheApiModeColumnsAndDefaultsExistingRowsToChatCompletions()
    {
        using var connection = new SqliteConnection("Data Source=AiProviderApiModeMigration;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;

        var providerId = Guid.NewGuid();
        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.AiProviderSettings.Add(new AiProviderSettings
            {
                Id = providerId,
                Name = "OpenAI",
                IsDefault = true,
                ProviderType = "OpenAICompatible",
                Endpoint = "https://api.openai.com/v1/chat/completions",
                Model = "gpt-test",
                EncryptedApiKey = "existing-encrypted-key",
                ApiMode = "Responses",
                ReasoningEffort = "high",
                EnableWebSearch = true,
                UpdatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }

        // Emulate a database created before the API mode existed: the columns are simply absent.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE AiProviderSettings DROP COLUMN ApiMode;
                ALTER TABLE AiProviderSettings DROP COLUMN ReasoningEffort;
                ALTER TABLE AiProviderSettings DROP COLUMN EnableWebSearch;
                """;
            command.ExecuteNonQuery();
        }

        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);

            var migrated = Assert.Single(context.AiProviderSettings);
            Assert.Equal(providerId, migrated.Id);
            Assert.Equal("existing-encrypted-key", migrated.EncryptedApiKey);
            // The safe default: a row that predates the mode was written for Chat Completions.
            Assert.Equal("ChatCompletions", migrated.ApiMode);
            Assert.Null(migrated.ReasoningEffort);
            Assert.False(migrated.EnableWebSearch);
        }

        // Idempotent: a second pass over the migrated database changes nothing.
        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);
            Assert.Equal("ChatCompletions", Assert.Single(context.AiProviderSettings).ApiMode);
        }
    }
}

