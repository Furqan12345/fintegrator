using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Per-connection SQLite settings shared by the API, the Engine, the log writer and the benchmark.
//
// All of these are connection-scoped, so they have to be applied every time a connection is opened
// (journal_mode=WAL is the exception: it is stored in the file and set once by the schema initializer).
//
//  * synchronous=NORMAL: in WAL mode this is the documented safe setting. A commit no longer waits for
//    an fsync of the WAL, so a power loss can drop the last few transactions but can never corrupt the
//    database. The default (FULL) syncs on every commit, and a card writes two or three commits.
//  * temp_store=MEMORY: sorts and temp b-trees (ORDER BY on the run lists) stay out of temp files.
//  * cache_size: a modest per-connection page cache; a negative value is KiB.
//  * journal_size_limit: stops the WAL file from staying at its high-water mark after a big write.
//  * busy_timeout: the API and the Engine share one file, so a short wait beats a SQLITE_BUSY.
public static class SqliteTuning
{
    public const string ConnectionPragmas =
        "PRAGMA busy_timeout=8000;" +
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA temp_store=MEMORY;" +
        "PRAGMA cache_size=-8192;" +
        "PRAGMA journal_size_limit=67108864;";

    public static DbContextOptionsBuilder UseTunedSqlite(this DbContextOptionsBuilder builder, string connectionString)
        => builder.UseSqlite(connectionString).AddInterceptors(SqliteTuningInterceptor.Instance);

    public static DbContextOptionsBuilder<TContext> UseTunedSqlite<TContext>(
        this DbContextOptionsBuilder<TContext> builder, string connectionString) where TContext : DbContext
        => builder.UseSqlite(connectionString).AddInterceptors(SqliteTuningInterceptor.Instance);

    public static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ConnectionPragmas;
        command.ExecuteNonQuery();
    }
}

public sealed class SqliteTuningInterceptor : DbConnectionInterceptor
{
    public static readonly SqliteTuningInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SqliteTuning.Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SqliteTuning.ConnectionPragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
