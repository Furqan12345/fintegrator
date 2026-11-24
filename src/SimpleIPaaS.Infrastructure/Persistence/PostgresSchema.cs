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
                EnsureModelColumns(context, connection, logger);
                foreach (var table in PostgresLayout.PartitionedTables)
                {
                    ConvertToPartitioned(connection, table, logger);
                }

                EnsureIndexes(connection);
                PostgresPartitions.EnsureAheadAsync(connection).GetAwaiter().GetResult();
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
        var identity = name == PostgresLayout.AppLogEntries;

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

            // LIKE does not copy the identity of AppLogEntries.Id; a sequence takes its place (created after the
            // old table is gone, because the old identity sequence used the same name).
            if (identity)
            {
                Execute(connection, $"CREATE SEQUENCE IF NOT EXISTS \"{name}_Id_seq\" OWNED BY \"{name}\".\"Id\";", transaction);
                Execute(connection, $"ALTER TABLE \"{name}\" ALTER COLUMN \"Id\" SET DEFAULT nextval('\"{name}_Id_seq\"');", transaction);
                Execute(connection, $"SELECT setval('\"{name}_Id_seq\"', GREATEST((SELECT max(\"Id\") FROM \"{name}\"), 0) + 1, false);", transaction);
            }

            var primaryKey = string.Join(", ", table.PrimaryKey.Select(column => $"\"{column}\""));
            Execute(connection, $"ALTER TABLE \"{name}\" ADD CONSTRAINT \"PK_{name}\" PRIMARY KEY ({primaryKey});", transaction);
            foreach (var columns in table.Indexes)
            {
                var indexName = "IX_" + name + "_" + string.Join("_", columns.Replace("\"", string.Empty).Split(',').Select(column => column.Trim()));
                Execute(connection, $"CREATE INDEX IF NOT EXISTS \"{indexName}\" ON \"{name}\" ({columns});", transaction);
            }

            transaction.Commit();
            logger.LogInformation("PostgreSQL: {Table} is now partitioned by day on {Column} ({Rows} existing rows moved).", name, key, copied);
        }
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
