using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace SimpleIPaaS.Infrastructure.Persistence;

public enum DatabaseProviderKind
{
    Sqlite,
    SqlServer,
    PostgreSql,
}

// One place that turns "Database:Provider" into a configured IPaaSContext, so the API and the Engine
// cannot drift apart (they must land on the same database).
//
//   "Database:Provider": "Sqlite"      (default) single file, development
//   "Database:Provider": "SqlServer"
//   "Database:Provider": "PostgreSql"  production layout: hot/cold tables, daily partitions, COPY ingestion
//                                      (see ops/POSTGRES.md)
public static class DatabaseProvider
{
    public static DatabaseProviderKind Resolve(IConfiguration configuration) =>
        (configuration["Database:Provider"] ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" => DatabaseProviderKind.SqlServer,
            "postgresql" or "postgres" or "npgsql" or "pgsql" => DatabaseProviderKind.PostgreSql,
            _ => DatabaseProviderKind.Sqlite,
        };

    // The database log sink (Debug Logs console) is available on SQLite and PostgreSQL.
    public static bool SupportsDatabaseLogging(IConfiguration configuration) =>
        Resolve(configuration) != DatabaseProviderKind.SqlServer;

    public static IServiceCollection AddIPaaSDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var kind = Resolve(configuration);
        if (kind == DatabaseProviderKind.PostgreSql)
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            // Npgsql reads "Data Source" as a host name, so a leftover SQLite string would fail later with a
            // confusing DNS error. Fail here instead.
            if (string.IsNullOrWhiteSpace(connectionString)
                || !(connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
                     || connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Database:Provider is PostgreSql but ConnectionStrings:DefaultConnection is not a PostgreSQL connection string (Host=...;Database=...;Username=...;Password=...).");

            // One pooled data source per process, shared by EF Core and the COPY writers (packet and log ingestion).
            services.AddSingleton(_ => PostgresDataSource.Create(connectionString));
            // No retrying execution strategy: two code paths open their own transactions, which a retrying
            // strategy rejects. Transient failures surface to the callers that already handle them.
            services.AddDbContext<IPaaSContext>((provider, options) =>
                options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

            // Packet logs are written off the flow's hot path: a bounded channel drained by one COPY writer.
            // Registered only here: the store needs the ingestor, and Development validates every registration
            // at start-up, so a PostgreSQL-only service registered for SQLite stops the host from starting.
            services.AddSingleton<Storage.PostgresPacketIngestor>();
            services.AddHostedService(provider => provider.GetRequiredService<Storage.PostgresPacketIngestor>());
            services.AddScoped<Storage.PostgresPacketLogStore>();
            return services;
        }

        services.AddDbContext<IPaaSContext>(options =>
        {
            if (kind == DatabaseProviderKind.SqlServer)
                options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("SQL Server connection string is required."), sql => sql.EnableRetryOnFailure());
            else
                options.UseTunedSqlite(DefaultDatabasePath.Resolve(configuration.GetConnectionString("DefaultConnection")));
        });
        return services;
    }
}

public static class PostgresDataSource
{
    public static NpgsqlDataSource Create(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        // Name the connections so they are recognisable in pg_stat_activity.
        if (string.IsNullOrWhiteSpace(builder.ApplicationName))
            builder.ApplicationName = "fusetor";
        return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }
}
