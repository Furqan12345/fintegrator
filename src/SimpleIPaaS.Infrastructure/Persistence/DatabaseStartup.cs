using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Start-up against a database that is not reachable yet (a container starting before PostgreSQL, a database
// restarting during a deploy) used to throw and exit the host. The schema step now retries connection failures with
// backoff for Database:StartupWaitSeconds (default 120) before giving up; real errors (bad credentials, a missing
// database, a broken schema) still fail at once.
public static class DatabaseStartup
{
    public static void EnsureSchema(IConfiguration configuration, IServiceProvider services, ILogger logger)
    {
        var maxWait = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("Database:StartupWaitSeconds", 120)));
        var deadline = DateTime.UtcNow + maxWait;
        var delay = TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                using var scope = services.CreateScope();
                DatabaseSchemaInitializer.EnsureUpToDate(scope.ServiceProvider.GetRequiredService<IPaaSContext>(), logger);
                return;
            }
            catch (Exception exception) when (IsConnectionFailure(exception) && DateTime.UtcNow + delay < deadline)
            {
                logger.LogWarning("Database not reachable yet ({Reason}); retrying in {Seconds}s (giving up at {Deadline:u})",
                    Innermost(exception).Message, delay.TotalSeconds, deadline);
                Thread.Sleep(delay);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
    }

    public static bool IsConnectionFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException postgres:
                    // 57P01 admin shutdown, 57P02 crash shutdown, 57P03 cannot connect now (starting / shutting down).
                    return postgres.SqlState is "57P01" or "57P02" or "57P03" || postgres.IsTransient;
                case NpgsqlException npgsql when npgsql.IsTransient:
                case SocketException:
                case TimeoutException:
                    return true;
                case SqlException sql when sql.Number is -2 or 53 or 4060 or 40613 or 10054 or 10060:
                    return true;
            }
        }

        return false;
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException != null) exception = exception.InnerException;
        return exception;
    }
}
