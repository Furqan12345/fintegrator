using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Persistence;

public sealed class AiChatSchemaMigrationTests
{
    [Fact]
    public void EnsureUpToDate_CreatesChatTablesOnExistingDatabase()
    {
        using var connection = new SqliteConnection("Data Source=AiChatSchemaMigration;Mode=Memory;Cache=Shared;Pooling=False");
        connection.Open();
        var options = new DbContextOptionsBuilder<IPaaSContext>().UseSqlite(connection).Options;
        var providerId = Guid.NewGuid();

        using (var context = new IPaaSContext(options))
        {
            context.Database.EnsureCreated();
            context.AiProviderSettings.Add(new AiProviderSettings
            {
                Id = providerId,
                Name = "Existing provider",
                ProviderType = "OpenAICompatible",
                Endpoint = "http://localhost/v1/chat/completions",
                Model = "gpt-test",
                EncryptedApiKey = "existing-encrypted-key"
            });
            context.SaveChanges();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE AiChatMessages; DROP TABLE AiChatSessions;";
            command.ExecuteNonQuery();
        }

        using (var context = new IPaaSContext(options))
        {
            DatabaseSchemaInitializer.EnsureUpToDate(context);

            Assert.Empty(context.AiChatSessions.ToList());
            Assert.Empty(context.AiChatMessages.ToList());
            var provider = Assert.Single(context.AiProviderSettings);
            Assert.Equal("Existing provider", provider.Name);

            context.AiChatSessions.Add(new AiChatSession
            {
                Title = "Restore check",
                Status = AiChatSessionStatus.Active
            });
            context.SaveChanges();
        }
        using (var context = new IPaaSContext(options))
        {
            var session = Assert.Single(context.AiChatSessions);
            Assert.Equal("Restore check", session.Title);
        }
    }
}

