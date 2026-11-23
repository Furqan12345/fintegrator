using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;

namespace SimpleIPaaS.Infrastructure.Logging;

public static class DatabaseLoggingExtensions
{
    public const string ConfigurationSection = "Logging:Database";

    // Registers the database log sink for a host.
    //
    //   builder.Logging.AddDatabaseLogging(builder.Configuration, "Engine");
    //
    // Configuration keys (all optional):
    //   Logging:Database:Enabled              bool   (default true)
    //   Logging:Database:MinimumLevel         string (default Information)
    //   Logging:Database:RetentionHours       int    (default 72)
    //   Logging:Database:MaxRows              int    (default 100000)
    //   Logging:Database:BatchSize            int    (default 200)
    //   Logging:Database:FlushIntervalSeconds double (default 1)
    //   Logging:Database:QueueCapacity        int    (default 20000)
    //   Logging:Database:PruneIntervalMinutes double (default 5)
    //   Logging:Database:MaxMessageLength     int    (default 4000)
    //   Logging:Database:MaxExceptionLength   int    (default 16000)
    //   Logging:Database:ExcludedCategoryPrefixes string[] (extra suppressions)
    public static ILoggingBuilder AddDatabaseLogging(
        this ILoggingBuilder builder,
        IConfiguration configuration,
        string source)
    {
        var options = ReadOptions(configuration);
        if (!options.Enabled)
        {
            return builder;
        }

        var postgres = DatabaseProvider.Resolve(configuration) == DatabaseProviderKind.PostgreSql;
        var connectionString = postgres ? string.Empty : DefaultDatabasePath.Resolve(configuration.GetConnectionString("DefaultConnection"));

        // Where the relational writer's retention windows come from: the storage settings page, so
        // "keep logs for N days" has a single answer whether the logs live in the database or in
        // Elasticsearch.
        builder.Services.AddSingleton<ILogRetentionPolicy>(provider =>
            new SettingsLogRetentionPolicy(provider.GetRequiredService<IStorageSettingsProvider>()));

        // The sink stack: one writer per backend, each with its own queue and counters, and a
        // router that sends each entry to whichever backend the logs dataset is configured for.
        //
        // Nothing here resolves the storage settings (or anything that logs) at construction time:
        // the logging pipeline is built first, so touching that graph from here would be a cycle.
        // Each writer resolves what it needs lazily, on first use.
        builder.Services.AddSingleton<IAppLogWriter>(provider =>
        {
            var runtime = provider.GetRequiredService<StorageRuntime>();
            // SQLite: prepared INSERTs per batch. PostgreSQL: one binary COPY per batch into day partitions.
            IAppLogBatchStore store = postgres
                ? new PostgresAppLogBatchStore(() => provider.GetRequiredService<Npgsql.NpgsqlDataSource>())
                : new SqliteAppLogBatchStore(connectionString);
            var relational = new SqlAppLogWriter(new DatabaseLogWriter(options, store, provider));
            var elasticsearch = new ElasticsearchLogWriter(runtime, options);

            return new RoutingLogWriter(relational, elasticsearch, runtime);
        });

        // Registered through a factory so the DI container owns the instance and disposes it
        // on shutdown — Dispose performs the final synchronous flush of any queued entries.
        builder.Services.AddSingleton<ILoggerProvider>(provider =>
            new DatabaseLoggerProvider(options, source, provider.GetRequiredService<IAppLogWriter>()));

        return builder;
    }

    public static DatabaseLoggerOptions ReadOptions(IConfiguration configuration)
    {
        var options = new DatabaseLoggerOptions();
        configuration.GetSection(ConfigurationSection).Bind(options);
        options.EnvironmentName = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"] ?? options.EnvironmentName;
        return options;
    }
}
