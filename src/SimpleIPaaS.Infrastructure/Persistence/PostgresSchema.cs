using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Schema start-up for PostgreSQL. Both hosts run it at start; a session advisory lock makes the second one
// wait for the first. The database itself must exist (create it with createdb / CREATE DATABASE).
//
//  1. EnsureCreated builds every table from the PostgreSQL model (PostgresLayout).
//  2. The append-only tables are converted, once, into tables partitioned by day: EnsureCreated cannot create
//     a partitioned table, so the plain table it made is swapped for a partitioned one (rows copied over).
//     AppLogEntries is split by level first, then by day (ConvertAppLogs).
//  3. The hot-path indexes shared with the other providers, and partitions from yesterday to a week ahead.
public static class PostgresSchema
{
    private const long SchemaLockKey = 0x46_55_53_45_53_43_48_4D; // "FUSESCHM"

    public static void EnsureUpToDate(IPaaSContext context, ILogger logger)
    {
        context.Database.OpenConnection();
        try
        {
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            Execute(connection, $"SELECT pg_advisory_lock({SchemaLockKey});");
            try
            {
                context.Database.EnsureCreated();
                EnsureModelTables(context, connection, logger);
                MoveColumnsToColdTables(connection, logger);
                EnsureModelColumns(context, connection, logger);
                foreach (var table in PostgresLayout.PartitionedTables)
                {
                    ConvertToPartitioned(connection, table, logger);
                }

                ConvertAppLogs(connection, logger);

                EnsureIndexes(connection);
                PostgresPartitions.EnsureAheadAsync(connection).GetAwaiter().GetResult();
                foreach (var warning in ServerLoggingWarnings(connection))
                {
                    logger.LogWarning("PostgreSQL: {Warning}", warning);
                }
            }
            finally
            {
                Execute(connection, $"SELECT pg_advisory_unlock({SchemaLockKey});");
            }
        }
        finally
        {
            context.Database.CloseConnection();
        }
    }

    private static void ConvertToPartitioned(NpgsqlConnection connection, PostgresLayout.PartitionedTable table, ILogger logger)
    {
        if (PostgresPartitions.IsPartitionedAsync(connection, table.Name).GetAwaiter().GetResult())
        {
            return;
        }

        var name = table.Name;
        var old = name + "__unpartitioned";
        var key = table.PartitionColumn;

        using (var transaction = connection.BeginTransaction())
        {
            Execute(connection, $"ALTER TABLE \"{name}\" RENAME TO \"{old}\";", transaction);
            Execute(connection, $"CREATE TABLE \"{name}\" (LIKE \"{old}\" INCLUDING DEFAULTS) PARTITION BY RANGE (\"{key}\");", transaction);

            // Partitions for whatever the plain table already holds (normally nothing: this runs on a new database).
            using (var range = new NpgsqlCommand($"SELECT min(\"{key}\"), max(\"{key}\") FROM \"{old}\";", connection, transaction))
            using (var reader = range.ExecuteReader())
            {
                reader.Read();
                if (!reader.IsDBNull(0))
                {
                    var from = PostgresPartitions.UtcDay(reader.GetDateTime(0));
                    var to = PostgresPartitions.UtcDay(reader.GetDateTime(1));
                    reader.Close();
                    for (var day = from; day <= to; day = day.AddDays(1))
                    {
                        Execute(connection,
                            $"CREATE TABLE IF NOT EXISTS \"{PostgresPartitions.PartitionName(name, day)}\" PARTITION OF \"{name}\" " +
                            $"FOR VALUES FROM ('{day:yyyy-MM-dd} 00:00:00+00') TO ('{day.AddDays(1):yyyy-MM-dd} 00:00:00+00');",
                            transaction);
                    }
                }
            }

            var copied = ExecuteCount(connection, $"INSERT INTO \"{name}\" SELECT * FROM \"{old}\";", transaction);
            Execute(connection, $"DROP TABLE \"{old}\";", transaction);

            AddKeyAndIndexes(connection, table, transaction);

            transaction.Commit();
            logger.LogInformation("PostgreSQL: {Table} is now partitioned by day on {Column} ({Rows} existing rows moved).", name, key, copied);
        }
    }

    // AppLogEntries is list-partitioned on Level into AppLogWarnings (warnings and errors) and AppLogDetail (the
    // default partition, every other level), each range-partitioned by day, so the two log windows drop whole days
    // independently. EnsureCreated makes a plain table, and a database from before the split has one partitioned by
    // day only; either is swapped for the split layout once, its rows moved over (the log table was kept under a
    // 1,000,000-row cap before the split, so the move is short). Reads and writes keep using AppLogEntries.
    private static void ConvertAppLogs(NpgsqlConnection connection, ILogger logger)
    {
        var table = PostgresLayout.AppLogs;
        var name = table.Name;
        var key = table.PartitionColumn;
        if (PartitionStrategy(connection, name) == "l")
        {
            return;
        }

        var old = name + "__old";
        using var transaction = connection.BeginTransaction();
        Execute(connection, $"ALTER TABLE \"{name}\" RENAME TO \"{old}\";", transaction);
        Execute(connection, $"CREATE TABLE \"{name}\" (LIKE \"{old}\" INCLUDING DEFAULTS) PARTITION BY LIST (\"Level\");", transaction);

        // LIKE copies no identity, and the old default points at a sequence that belongs to the old table and goes
        // with it. A new sequence is set up once the old table is gone (it reuses the name).
        Execute(connection, $"ALTER TABLE \"{name}\" ALTER COLUMN \"Id\" DROP DEFAULT;", transaction);
        var levels = string.Join(", ", PostgresLayout.WarningLevels.Select(level => $"'{level}'"));
        Execute(connection,
            $"CREATE TABLE \"{PostgresLayout.AppLogWarnings}\" PARTITION OF \"{name}\" FOR VALUES IN ({levels}) PARTITION BY RANGE (\"{key}\");",
            transaction);
        Execute(connection,
            $"CREATE TABLE \"{PostgresLayout.AppLogDetail}\" PARTITION OF \"{name}\" DEFAULT PARTITION BY RANGE (\"{key}\");",
            transaction);

        using (var range = new NpgsqlCommand($"SELECT min(\"{key}\"), max(\"{key}\") FROM \"{old}\";", connection, transaction))
        using (var reader = range.ExecuteReader())
        {
            reader.Read();
            if (!reader.IsDBNull(0))
            {
                var from = PostgresPartitions.UtcDay(reader.GetDateTime(0));
                var to = PostgresPartitions.UtcDay(reader.GetDateTime(1));
                reader.Close();
                foreach (var part in new[] { PostgresLayout.AppLogWarnings, PostgresLayout.AppLogDetail })
                {
                    for (var day = from; day <= to; day = day.AddDays(1))
                    {
                        Execute(connection,
                            $"CREATE TABLE IF NOT EXISTS \"{PostgresPartitions.PartitionName(part, day)}\" PARTITION OF \"{part}\" " +
                            $"FOR VALUES FROM ('{day:yyyy-MM-dd} 00:00:00+00') TO ('{day.AddDays(1):yyyy-MM-dd} 00:00:00+00');",
                            transaction);
                    }
                }
            }
        }

        var copied = ExecuteCount(connection, $"INSERT INTO \"{name}\" SELECT * FROM \"{old}\";", transaction);
        Execute(connection, $"DROP TABLE \"{old}\";", transaction);

        Execute(connection, $"CREATE SEQUENCE IF NOT EXISTS \"{name}_Id_seq\" OWNED BY \"{name}\".\"Id\";", transaction);
        Execute(connection, $"ALTER TABLE \"{name}\" ALTER COLUMN \"Id\" SET DEFAULT nextval('\"{name}_Id_seq\"');", transaction);
        Execute(connection, $"SELECT setval('\"{name}_Id_seq\"', GREATEST((SELECT max(\"Id\") FROM \"{name}\"), 0) + 1, false);", transaction);
        AddKeyAndIndexes(connection, table, transaction);

        transaction.Commit();
        logger.LogInformation(
            "PostgreSQL: {Table} is now split into warnings and errors ({Warnings}) and other levels ({Detail}), each partitioned by day ({Rows} existing rows moved).",
            name, PostgresLayout.AppLogWarnings, PostgresLayout.AppLogDetail, copied);
    }

    // "r" (range), "l" (list), or null when the table is not partitioned.
    private static string? PartitionStrategy(NpgsqlConnection connection, string table)
    {
        using var command = new NpgsqlCommand("SELECT partstrat::text FROM pg_partitioned_table WHERE partrelid = to_regclass(@table);", connection);
        command.Parameters.AddWithValue("table", $"\"{table}\"");
        return command.ExecuteScalar() as string;
    }

    private static void AddKeyAndIndexes(NpgsqlConnection connection, PostgresLayout.PartitionedTable table, NpgsqlTransaction transaction)
    {
        var name = table.Name;
        var primaryKey = string.Join(", ", table.PrimaryKey.Select(column => $"\"{column}\""));
        Execute(connection, $"ALTER TABLE \"{name}\" ADD CONSTRAINT \"PK_{name}\" PRIMARY KEY ({primaryKey});", transaction);
        foreach (var columns in table.Indexes)
        {
            var indexName = "IX_" + name + "_" + string.Join("_", columns.Replace("\"", string.Empty).Split(',').Select(column => column.Trim()));
            Execute(connection, $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{name}\" ({columns});", transaction);
        }
    }

    // Fusetor runs with the server log files off (ops/POSTGRES.md, "Server settings and WAL"). It never changes
    // server settings itself (that needs a superuser and a restart); it reports the ones that make the server log
    // grow: the Engine polls and writes continuously, so statement logging fills a disk quickly.
    internal static IReadOnlyList<string> ServerLoggingWarnings(NpgsqlConnection connection)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = new NpgsqlCommand(
            "SELECT name, setting FROM pg_settings WHERE name IN ('logging_collector', 'log_statement', 'log_min_duration_statement');", connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                settings[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var warnings = new List<string>();
        if (settings.GetValueOrDefault("logging_collector") == "on")
        {
            warnings.Add("logging_collector is on: the server writes log files, and PostgreSQL does not delete old ones unless rotation is configured. Fusetor expects it off (set logging_collector = off and restart PostgreSQL).");
        }

        if (settings.TryGetValue("log_statement", out var statements) && statements != "none")
        {
            warnings.Add($"log_statement is '{statements}': every such statement the API and Engine run is logged. Set log_statement = none.");
        }

        if (settings.TryGetValue("log_min_duration_statement", out var duration) && int.TryParse(duration, out var ms) && ms >= 0 && ms < 1000)
        {
            warnings.Add($"log_min_duration_statement is {ms} ms: most statements are logged. Set it to -1 (off) or at least a few seconds.");
        }

        return warnings;
    }

    // EnsureCreated does nothing on a database that already has tables, so a table added by a later release is
    // created here: EF generates the CREATE TABLE (keys, foreign keys) and CREATE INDEX statements for every table
    // the model maps but the database lacks. A new append-only table is then partitioned by the next step.
    private static void EnsureModelTables(IPaaSContext context, NpgsqlConnection connection, ILogger logger)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema();", connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        var designModel = context.GetService<IDesignTimeModel>().Model;
        var relationalModel = designModel.GetRelationalModel();
        var missing = relationalModel.Tables.Select(table => table.Name).Where(name => !existing.Contains(name)).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0)
        {
            return;
        }

        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, relationalModel)
            .Where(operation => operation switch
            {
                CreateTableOperation table => missing.Contains(table.Name),
                CreateIndexOperation index => missing.Contains(index.Table),
                _ => false,
            })
            .ToList();
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations, designModel))
        {
            Execute(connection, command.CommandText);
        }

        logger.LogInformation("PostgreSQL: created missing table(s) {Tables}.", string.Join(", ", missing.OrderBy(name => name)));
    }

    // A database created before the run and dead-letter payload tables still has those columns on the hot rows.
    // Their values are copied to the cold tables once, then the columns are dropped: the model no longer writes
    // them, and the dead-letter ones are NOT NULL without a default, so every new dead letter would fail.
    private static void MoveColumnsToColdTables(NpgsqlConnection connection, ILogger logger)
    {
        foreach (var (table, coldTable, keyColumn, columns) in PostgresLayout.MovedColumns)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            var coldNullable = new Dictionary<string, bool>(StringComparer.Ordinal);
            using (var command = new NpgsqlCommand(
                "SELECT table_name, column_name, is_nullable = 'YES' FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND table_name IN (@hot, @cold);", connection))
            {
                command.Parameters.AddWithValue("hot", table);
                command.Parameters.AddWithValue("cold", coldTable);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetString(0) == table) present.Add(reader.GetString(1));
                    else coldNullable[reader.GetString(1)] = reader.GetBoolean(2);
                }
            }

            var legacy = columns.Where(present.Contains).ToList();
            if (legacy.Count == 0)
            {
                continue;
            }

            var source = columns.Select(column => present.Contains(column)
                ? $"\"{column}\""
                : coldNullable.GetValueOrDefault(column, true) ? "NULL" : column.EndsWith("Json", StringComparison.Ordinal) ? "'{}'" : "''");
            var anyValue = string.Join(" OR ", legacy.Select(column => $"\"{column}\" IS NOT NULL"));
            using var transaction = connection.BeginTransaction();
            var moved = ExecuteCount(connection,
                $"INSERT INTO \"{coldTable}\" (\"{keyColumn}\", {string.Join(", ", columns.Select(column => $"\"{column}\""))}) " +
                $"SELECT \"Id\", {string.Join(", ", source)} FROM \"{table}\" WHERE {anyValue} ON CONFLICT (\"{keyColumn}\") DO NOTHING;",
                transaction);
            foreach (var column in legacy)
            {
                Execute(connection, $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\";", transaction);
            }

            transaction.Commit();
            logger.LogInformation("PostgreSQL: moved {Columns} of {Rows} {Table} row(s) to {ColdTable}.",
                string.Join(", ", legacy), moved, table, coldTable);
        }
    }

    // Additive upgrades, the PostgreSQL counterpart of the EnsureColumn lists for SQLite and SQL Server: every
    // column the model maps but the database lacks is added (NOT NULL columns with a type default), so a newer
    // build starts on an older database. Columns are never dropped or altered here.
    private static void EnsureModelColumns(IPaaSContext context, NpgsqlConnection connection, ILogger logger)
    {
        var existing = new HashSet<(string Table, string Column)>();
        using (var command = new NpgsqlCommand(
            "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = current_schema();", connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var tables = existing.Select(entry => entry.Table).ToHashSet();
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table == null || !tables.Contains(table))
            {
                continue;
            }

            var tableId = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(tableId);
                if (column == null || existing.Contains((table, column)))
                {
                    continue;
                }

                var type = property.GetColumnType();
                var definition = property.IsNullable ? $"{type} NULL" : $"{type} NOT NULL DEFAULT {DefaultFor(type)}";
                Execute(connection, $"ALTER TABLE \"{table}\" ADD COLUMN IF NOT EXISTS \"{column}\" {definition};");
                existing.Add((table, column));
                logger.LogInformation("PostgreSQL: added column {Table}.{Column} ({Definition}).", table, column, definition);
            }
        }
    }

    private static string DefaultFor(string columnType) => columnType.ToLowerInvariant() switch
    {
        var type when type.StartsWith("integer") || type.StartsWith("bigint") || type.StartsWith("smallint")
            || type.StartsWith("numeric") || type.StartsWith("double") || type.StartsWith("real") => "0",
        "boolean" => "false",
        "uuid" => "'00000000-0000-0000-0000-000000000000'",
        var type when type.StartsWith("timestamp") => "'epoch'",
        var type when type.StartsWith("interval") => "'0'",
        "bytea" => "''::bytea",
        _ => "''",
    };

    private static void EnsureIndexes(NpgsqlConnection connection)
    {
        foreach (var (name, table, columns, where) in DatabaseSchemaInitializer.PerformanceIndexDefinitions)
        {
            var quoted = string.Join(", ", columns.Split(',').Select(column => $"\"{column.Trim()}\""));
            var filter = where == null ? string.Empty : " WHERE " + QuoteLeadingIdentifier(where);
            Execute(connection, $"CREATE INDEX IF NOT EXISTS \"{name}\" ON \"{table}\" ({quoted}){filter};");
        }

        foreach (var (name, _) in DatabaseSchemaInitializer.RedundantIndexDefinitions)
        {
            Execute(connection, $"DROP INDEX IF EXISTS \"{name}\";");
        }
    }

    // "ReplayRequestedAt IS NOT NULL" -> "\"ReplayRequestedAt\" IS NOT NULL" (unquoted identifiers fold to lower case).
    private static string QuoteLeadingIdentifier(string predicate)
    {
        var space = predicate.IndexOf(' ');
        return space < 0 ? $"\"{predicate}\"" : $"\"{predicate[..space]}\"{predicate[space..]}";
    }

    private static void Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }

    private static int ExecuteCount(NpgsqlConnection connection, string sql, NpgsqlTransaction transaction)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        return command.ExecuteNonQuery();
    }
}
