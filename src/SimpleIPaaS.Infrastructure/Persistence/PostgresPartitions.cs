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
public static class PostgresPartitions
{
    public const int DaysAhead = 7;
    private const long MaintenanceLockKey = 0x46_55_53_45_50_41_52_54; // "FUSEPART"

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

    // Partitions from yesterday to DaysAhead days ahead, for every partitioned table.
    public static Task EnsureAheadAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        var today = UtcDay(DateTime.UtcNow);
        return EnsureRangeAsync(connection, today.AddDays(-1), today.AddDays(DaysAhead), cancellationToken);
    }

    public static async Task EnsureRangeAsync(NpgsqlConnection connection, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({MaintenanceLockKey});", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var table in PostgresLayout.PartitionedTables)
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
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Drops every partition of a table whose whole day is older than the cutoff. Detaches CONCURRENTLY first
    // so writers to today's partition are never blocked; falls back to a plain DROP when that is not possible.
    // Returns the partitions dropped and the (estimated) rows they held.
    public static async Task<(int Partitions, long Rows)> DropOlderThanAsync(
        NpgsqlDataSource dataSource, string table, DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var cutoffDay = UtcDay(cutoffUtc);
        var dropped = 0;
        long rows = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var partitions = await ListAsync(connection, null, table, cancellationToken).ConfigureAwait(false);
        foreach (var partition in partitions.OrderBy(name => name, StringComparer.Ordinal))
        {
            // A day [d, d+1) is entirely older than the cutoff when d+1 <= cutoff day.
            if (!TryParseDay(table, partition, out var day) || day.AddDays(1) > cutoffDay)
            {
                continue;
            }

            rows += await EstimateRowsAsync(connection, partition, cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, $"ALTER TABLE \"{table}\" DETACH PARTITION \"{partition}\" CONCURRENTLY;", cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException)
            {
                // Older server, or a detach left half-done by a crash: a plain DROP still works (it briefly locks the parent).
            }

            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS \"{partition}\";", cancellationToken).ConfigureAwait(false);
            dropped++;
        }

        return (dropped, rows);
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

    // SQLSTATE 23514 with "no partition of relation ... found for row".
    public static bool IsMissingPartition(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.CheckViolation } postgres
        && postgres.MessageText.Contains("no partition", StringComparison.OrdinalIgnoreCase);

    private static string Bound(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00+00";

    private static async Task<long> EstimateRowsAsync(NpgsqlConnection connection, string partition, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT GREATEST(reltuples, 0)::bigint FROM pg_class WHERE oid = to_regclass(@name);", connection);
        command.Parameters.AddWithValue("name", $"\"{partition}\"");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long value ? value : 0;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
