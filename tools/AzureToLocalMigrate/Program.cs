// One-shot migration: Azure SQL -> local SQLite (repo-root ipaas.db).
//
// Preserves every row and primary key, re-encrypts the secret fields from the
// Azure Encryption:Key to the local development key (so `dotnet run` keeps working
// with no configuration changes), and keeps the `dev-api-key` row available for
// local Development mode. The original Azure secrets stay in
// ops/local/azure-appservice-settings-backup.json.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Services.Security;

var repoRoot = FindRepoRoot();
var backupPath = Path.Combine(repoRoot, "ops", "local", "azure-appservice-settings-backup.json");
if (!File.Exists(backupPath)) throw new InvalidOperationException($"Missing backup file: {backupPath}");

using var backup = JsonDocument.Parse(File.ReadAllText(backupPath));
var settingsElement = backup.RootElement.GetProperty("appSettings");
// PowerShell 5.1 may serialize a top-level array as { "value": [...], "Count": n }.
if (settingsElement.ValueKind == JsonValueKind.Object && settingsElement.TryGetProperty("value", out var wrappedSettings))
    settingsElement = wrappedSettings;
var appSettings = settingsElement.EnumerateArray()
    .Select(element => (Name: element.GetProperty("name").GetString() ?? string.Empty,
                        Value: element.GetProperty("value").GetString() ?? string.Empty))
    .ToList();
var sourceConnectionString =
    appSettings.FirstOrDefault(setting => setting.Name.Contains("DefaultConnection", StringComparison.OrdinalIgnoreCase)).Value
    ?? appSettings.FirstOrDefault(setting => setting.Value.StartsWith("Server=", StringComparison.OrdinalIgnoreCase)).Value
    ?? throw new InvalidOperationException("No source connection string found in the backup.");
var azureKey = backup.RootElement.GetProperty("localDpapiSecrets").TryGetProperty("encryption-key", out var keyElement)
    ? keyElement.GetString()
    : null;
if (string.IsNullOrWhiteSpace(azureKey)) throw new InvalidOperationException("No Azure encryption key found in the backup.");

var localConfig = new ConfigurationBuilder()
    .SetBasePath(Path.Combine(repoRoot, "src", "SimpleIPaaS.Api"))
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();
var sourceEncryption = new EncryptionService(
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = azureKey }).Build(),
    new DevEnvironment(), NullLogger<EncryptionService>.Instance);
var targetEncryption = new EncryptionService(localConfig, new DevEnvironment(), NullLogger<EncryptionService>.Instance);

var sourceOptions = new DbContextOptionsBuilder<IPaaSContext>().UseSqlServer(sourceConnectionString).Options;
var databaseFile = Path.Combine(repoRoot, "ipaas.db");
if (File.Exists(databaseFile))
{
    var backupFile = databaseFile + ".bak-preazure-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    File.Copy(databaseFile, backupFile, overwrite: true);
    File.Delete(databaseFile);
    Console.WriteLine($"Existing local database backed up to {Path.GetFileName(backupFile)}");
}
var targetOptions = new DbContextOptionsBuilder<IPaaSContext>()
    .UseSqlite($"Data Source={databaseFile};Cache=Shared;Pooling=False")
    .Options;

var reencryptWarnings = 0;
async Task<string> ReencryptAsync(string? value)
{
    if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
    try
    {
        return await targetEncryption.EncryptAsync(await sourceEncryption.DecryptAsync(value));
    }
    catch (Exception)
    {
        reencryptWarnings++;
        return value;
    }
}

await using var source = new IPaaSContext(sourceOptions);
await using var target = new IPaaSContext(targetOptions);
DatabaseSchemaInitializer.EnsureUpToDate(target);

var totals = new List<(string Table, int Source, int Target)>();

async Task CopyAsync<T>(string table, DbSet<T> sourceSet, DbSet<T> targetSet, Func<T, Task>? transform = null) where T : class
{
    var rows = await sourceSet.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    foreach (var row in rows)
    {
        if (transform != null) await transform(row);
        targetSet.Add(row);
        if (targetSet.Local.Count >= 500)
        {
            await target.SaveChangesAsync();
            target.ChangeTracker.Clear();
        }
    }
    if (targetSet.Local.Count > 0)
    {
        await target.SaveChangesAsync();
        target.ChangeTracker.Clear();
    }
    var copied = await targetSet.IgnoreQueryFilters().CountAsync();
    totals.Add((table, rows.Count, copied));
    Console.WriteLine($"{table,-24} source={rows.Count,8}  copied={copied,8}");
}

// Dependency order: parents before children.
await CopyAsync("Connections", source.Connections, target.Connections, async connection =>
    connection.AuthConfigJson = await ReencryptAsync(connection.AuthConfigJson));
await CopyAsync("Integrations", source.Integrations, target.Integrations);
await CopyAsync("IntegrationFlows", source.IntegrationFlows, target.IntegrationFlows);
await CopyAsync("IntegrationSteps", source.IntegrationSteps, target.IntegrationSteps, async step =>
{
    step.AuthToken = await ReencryptAsync(step.AuthToken);
    step.AuthUsername = await ReencryptAsync(step.AuthUsername);
    step.AuthPassword = await ReencryptAsync(step.AuthPassword);
    step.AuthConfigJson = await ReencryptAsync(step.AuthConfigJson);
});
await CopyAsync("IntegrationEdges", source.IntegrationEdges, target.IntegrationEdges);
await CopyAsync("FlowVersions", source.FlowVersions, target.FlowVersions);
await CopyAsync("FlowTestCases", source.FlowTestCases, target.FlowTestCases);
await CopyAsync("FlowTestRuns", source.FlowTestRuns, target.FlowTestRuns);
await CopyAsync("FlowExecutions", source.FlowExecutions, target.FlowExecutions);
await CopyAsync("StepExecutions", source.StepExecutions, target.StepExecutions);
await CopyAsync("StepPacketLogs", source.StepPacketLogs, target.StepPacketLogs);
await CopyAsync("DeadLetterEntries", source.DeadLetterEntries, target.DeadLetterEntries);
await CopyAsync("CrossReferenceLists", source.CrossReferenceLists, target.CrossReferenceLists);
await CopyAsync("CrossReferenceEntries", source.CrossReferenceEntries, target.CrossReferenceEntries);
await CopyAsync("ApiKeys", source.ApiKeys, target.ApiKeys);
await CopyAsync("AiProviderSettings", source.AiProviderSettings, target.AiProviderSettings, async provider =>
    provider.EncryptedApiKey = await ReencryptAsync(provider.EncryptedApiKey));
await CopyAsync("AiBuildJobs", source.AiBuildJobs, target.AiBuildJobs);
await CopyAsync("HostHeartbeats", source.HostHeartbeats, target.HostHeartbeats);
await CopyAsync("AppLogEntries", source.AppLogEntries, target.AppLogEntries);

// Keep the local Development key (`dev-api-key`) available so the client can
// authenticate with no setup.
var devHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dev-api-key")));
var devKey = await target.ApiKeys.FirstOrDefaultAsync(key => key.KeyHash == devHash);
if (devKey == null)
{
    target.ApiKeys.Add(new ApiKey
    {
        Id = Guid.NewGuid(),
        Name = "Development (local)",
        KeyHash = devHash,
        CreatedAt = DateTime.UtcNow
    });
}
else
{
    devKey.RevokedAt = null;
}
await target.SaveChangesAsync();
target.ChangeTracker.Clear();

Console.WriteLine($"Secret fields kept as-is because they could not be parsed: {reencryptWarnings}");
Console.WriteLine(totals.All(t => t.Source == t.Target)
    ? "VERIFIED: every table copied with matching row counts."
    : "WARNING: row-count mismatch — inspect the table list above before deleting Azure.");

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir.Parent != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "SimpleIPaaS.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Repository root (SimpleIPaaS.slnx) not found.");
}

sealed class DevEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "AzureToLocalMigrate";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}
