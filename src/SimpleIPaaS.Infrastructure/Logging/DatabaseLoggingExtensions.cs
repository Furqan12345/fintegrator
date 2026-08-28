using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Infrastructure.Persistence;

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

        var connectionString = DefaultDatabasePath.Resolve(configuration.GetConnectionString("DefaultConnection"));

        // Registered through a factory so the DI container owns the instance and disposes it
        // on shutdown — Dispose performs the final synchronous flush of any queued entries.
        builder.Services.AddSingleton<ILoggerProvider>(_ =>
            new DatabaseLoggerProvider(options, source, connectionString));

        return builder;
    }

    public static DatabaseLoggerOptions ReadOptions(IConfiguration configuration)
    {
        var options = new DatabaseLoggerOptions();
        configuration.GetSection(ConfigurationSection).Bind(options);
        return options;
    }
}
