using System;
using System.IO;

namespace SimpleIPaaS.Infrastructure.Persistence;

// Resolves the shared SQLite database location for both hosts (API and Engine).
// With no explicit connection string every process would otherwise create its own
// database file relative to its own working directory — exactly the split-brain
// failure the database-as-bus design must never have. The fallback anchors on the
// repository root (the directory containing SimpleIPaaS.slnx), mirroring how the
// API locates its sample-flow seeder. Any explicitly configured connection string
// (appsettings, environment variable, Docker secret) always wins unchanged.
public static class DefaultDatabasePath
{
    public const string FallbackFileName = "ipaas.db";

    public static string Resolve(string? configuredConnectionString)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir.Parent != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SimpleIPaaS.slnx")))
            {
                return $"Data Source={Path.Combine(dir.FullName, FallbackFileName)}";
            }

            dir = dir.Parent;
        }

        return $"Data Source={FallbackFileName}";
    }
}
