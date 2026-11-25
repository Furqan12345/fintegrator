using System.Globalization;
using Npgsql;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Daily range partitions for the append-only tables (see PostgresLayout), managed in-process instead of by
// pg_partman so a plain PostgreSQL works. Both hosts call EnsureAhead (schema start-up, every retention pass,
// and on a "no partition for row" write failure); an advisory transaction lock serialises them.
//
// There is deliberately no DEFAULT partition: DETACH ... CONCURRENTLY (retention without blocking writers)
// is not allowed while one exists. Partitions are kept from yesterday to a week ahead, and a writer that still
// hits a missing day creates it and retries.
//
// Creating a partition (and a plain DROP of an attached one) takes an ACCESS EXCLUSIVE lock on the parent. Behind a
// long query on that table the request would wait, and every new write would queue behind it. The scheduled
// creation therefore gives up after MaintenanceLockTimeout and tries again on the next pass (a week of partitions
// is kept ahead, so a skipped pass costs nothing); only a writer whose day is missing waits, because its row has
// nowhere else to go. Drops run on one host at a time (a session try-lock; the other host skips that pass).
public static class PostgresPartitions
{
    public const int DaysAhead = 7;
    public static readonly TimeSpan MaintenanceLockTimeout = TimeSpan.FromSeconds(2);
    private const long MaintenanceLockKey = 0x46_55_53_45_50_41_52_54; // "FUSEPART"
    private const long DropLockKey = 0x46_55_53_45_44_52_4F_50; // "FUSEDROP"

    public static string PartitionName(string table, DateOnly day) =>
        $"{table}_p{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    public static bool TryParseDay(string table, string partitionName, out DateOnly day)
    {
        day = default;
        var prefix = table + "_p";
        return partitionName.StartsWith(prefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(partitionName[prefix.Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    public static DateOnly UtcDay(DateTime value) =>
        DateOnly.FromDateTime(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value);

    // Partitions from yesterday to DaysAhead days ahead, for every partitioned table. Scheduled maintenance: a table
    // whose parent is busy is skipped after MaintenanceLockTimeout. Returns the tables skipped.
    public static Task<IReadOnlyList<string>> EnsureAheadAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        var today = UtcDay(DateTime.UtcNow);
        return EnsureRangeAsync(connection, today.AddDays(-1), today.AddDays(DaysAhead), MaintenanceLockTimeout, cancellationToken);
    }

    // Creates the missing day partitions of every partitioned table, one table per transaction. With a lock timeout
    // a busy table is skipped (and returned); without one (a writer whose day is missing) it waits for the lock.
    public static async Task<IReadOnlyList<string>> EnsureRangeAsync(
        NpgsqlConnection connection, DateOnly from, DateOnly to, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default)
    {
        var skipped = new List<string>();
        foreach (var table in PostgresLayout.PartitionedTables)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({MaintenanceLockKey});", connection, transaction))
            {
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (lockTimeout is { } timeout)
            {
                await using var setTimeout = new NpgsqlCommand(
                    $"SET LOCAL lock_timeout = '{(int)timeout.TotalMilliseconds}ms';", connection, transaction);
                await setTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var existing = await ListAsync(connection, transaction, table.Name, cancellationToken).ConfigureAwait(false);
                for (var day = from; day <= to; day = day.AddDays(1))
                {
                    var name = PartitionName(table.Name, day);
                    if (existing.Contains(name))
                    {
                        continue;
                    }

                    var sql =
                        $"CREATE TABLE IF NOT EXISTS \"{name}\" PARTITION OF \"{table.Name}\" " +
                        $"FOR VALUES FROM ('{Bound(day)}') TO ('{Bound(day.AddDays(1))}');";
                    await using var create = new NpgsqlCommand(sql, connection, transaction);
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (lockTimeout != null && exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                skipped.Add(table.Name);
            }
        }

        return skipped;
    }

    // Drops every partition of a table whose whole day is older than the cutoff. Detaches CONCURRENTLY first
    // so writers to today's partition are never blocked; falls back to a plain DROP when that is not possible.
    // Returns the partitions dropped and the (estimated) rows they held.
    public static async Task<(int Partitions, long Rows)> DropOlderThanAsync(
        NpgsqlDataSource dataSource, string table, DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var cutoffDay = UtcDay(cutoffUtc);
        var result = await WithDropLockAsync(dataSource, async connection =>
        {
            var dropped = 0;
            long rows = 0;
            var partitions = await ListAsync(connection, null, table, cancellationToken).ConfigureAwait(false);
            foreach (var partition in partitions.OrderBy(name => name, StringComparer.Ordinal))
            {
                // A day [d, d+1) is entirely older than the cutoff when d+1 <= cutoff day.
                if (!TryParseDay(table, partition, out var day) || day.AddDays(1) > cutoffDay)
                {
                    continue;
                }

                var estimate = await EstimateRowsAsync(connection, partition, cancellationToken).ConfigureAwait(false);
                if (await DropPartitionAsync(connection, table, partition, cancellationToken).ConfigureAwait(false))
                {
                    rows += estimate;
                    dropped++;
                }
            }

            return (dropped, rows, 0L);
        }, cancellationToken).ConfigureAwait(false);

        return (result.Partitions, result.Rows);
    }

    // A row cap applied in whole days: keeps the newest days whose rows add up to at most maxRows and drops every
    // older day. Today, future days and the newest day holding rows are always kept. Returns what was dropped and the
    // rows kept; kept is over maxRows only when the kept days alone exceed it (the caller decides what to do then).
    // (0, 0, 0) when the other host holds the drop lock.
    public static Task<(int Partitions, long Rows, long KeptRows)> DropOldestOverCapAsync(
        NpgsqlDataSource dataSource, string table, long maxRows, CancellationToken cancellationToken = default) =>
        WithDropLockAsync(dataSource, async connection =>
        {
            var today = UtcDay(DateTime.UtcNow);
            var dropped = 0;
            long rows = 0;
            long kept = 0;
            var overCap = false;
            var partitions = (await ListAsync(connection, null, table, cancellationToken).ConfigureAwait(false))
                .Select(name => (Name: name, Parsed: TryParseDay(table, name, out var day), Day: day))
                .Where(partition => partition.Parsed)
                .OrderByDescending(partition => partition.Day)
                .ToList();
            foreach (var partition in partitions)
            {
                var count = await CountRowsAsync(connection, partition.Name, cancellationToken).ConfigureAwait(false);
                overCap = overCap || (partition.Day < today && kept > 0 && kept + count > maxRows);
                if (!overCap)
                {
                    kept += count;
                    continue;
                }

                // This day and every older one go.
                if (await DropPartitionAsync(connection, table, partition.Name, cancellationToken).ConfigureAwait(false))
                {
                    rows += count;
                    dropped++;
                }
            }

            return (dropped, rows, kept);
        }, cancellationToken);

    // Only the host holding this lock drops: the other host's DETACH would be refused and it would fall back to a
    // plain DROP, which locks the whole table. The other host skips the pass and gets (0, 0, 0).
    private static async Task<(int Partitions, long Rows, long KeptRows)> WithDropLockAsync(
        NpgsqlDataSource dataSource, Func<NpgsqlConnection, Task<(int, long, long)>> drop, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var tryLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({DropLockKey});", connection))
        {
            if (!(bool)(await tryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                return (0, 0, 0);
            }
        }

        try
        {
            return await drop(connection).ConfigureAwait(false);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({DropLockKey});", connection);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Detaches CONCURRENTLY, then drops. False when the fallback plain DROP met a busy parent (the next pass retries).
    private static async Task<bool> DropPartitionAsync(NpgsqlConnection connection, string table, string partition, CancellationToken cancellationToken)
    {
        var detached = true;
        try
        {
            await ExecuteAsync(connection, $"ALTER TABLE \"{table}\" DETACH PARTITION \"{partition}\" CONCURRENTLY;", cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException)
        {
            // Older server, or a detach left half-done by a crash: a plain DROP still works, but it locks the
            // parent, so it gets the maintenance lock timeout and waits for the next pass if the table is busy.
            detached = false;
        }

        try
        {
            if (!detached)
            {
                await ExecuteAsync(connection, $"SET lock_timeout = '{(int)MaintenanceLockTimeout.TotalMilliseconds}ms';", cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS \"{partition}\";", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException exception) when (!detached && exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return false;
        }
        finally
        {
            if (!detached)
            {
                await ExecuteAsync(connection, "RESET lock_timeout;", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public static async Task<HashSet<string>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string table, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT child.relname
            FROM pg_inherits inheritance
            JOIN pg_class child ON child.oid = inheritance.inhrelid
            WHERE inheritance.inhparent = to_regclass(@parent);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("parent", $"\"{table}\"");
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public static async Task<bool> IsPartitionedAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_partitioned_table WHERE partrelid = to_regclass(@table));", connection);
        command.Parameters.AddWithValue("table", $"\"{table}\"");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    // SQLSTATE 23514 with "no partition of relation ... found for row", directly or wrapped (EF's DbUpdateException).
    public static bool IsMissingPartition(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.CheckViolation } postgres
                && postgres.MessageText.Contains("no partition", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Bound(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00+00";

    private static async Task<long> EstimateRowsAsync(NpgsqlConnection connection, string partition, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT GREATEST(reltuples, 0)::bigint FROM pg_class WHERE oid = to_regclass(@name);", connection);
        command.Parameters.AddWithValue("name", $"\"{partition}\"");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long value ? value : 0;
    }

    // The planner's estimate, or an exact count for a partition never analyzed yet (usually today's, still filling).
    private static async Task<long> CountRowsAsync(NpgsqlConnection connection, string partition, CancellationToken cancellationToken)
    {
        await using var estimate = new NpgsqlCommand("SELECT reltuples::bigint FROM pg_class WHERE oid = to_regclass(@name);", connection);
        estimate.Parameters.AddWithValue("name", $"\"{partition}\"");
        if (await estimate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long value && value >= 0)
        {
            return value;
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{partition}\";", connection);
        return (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
