using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Writes the storage settings row on first start, so retention is always an explicit, visible setting.
//  * A new installation (no flows, no runs) gets the production defaults: history is pruned.
//  * An existing installation that never saved storage settings gets what it effectively had (runs and payloads
//    kept forever), so upgrading never starts deleting history on its own. Settings → Storage changes it.
// Both hosts run this at start; if they race, the second insert fails on the primary key and is ignored.
public static class StorageSettingsSeeder
{
    public static void EnsureRow(IPaaSContext context, ILogger logger)
    {
        if (context.StorageSettings.AsNoTracking().Any())
        {
            return;
        }

        var existingInstall = context.IntegrationFlows.IgnoreQueryFilters().Any() || context.FlowExecutions.IgnoreQueryFilters().Any();
        var datasets = existingInstall ? StorageCatalog.PreviousDefaults() : StorageCatalog.Defaults();
        context.StorageSettings.Add(new StorageSettings
        {
            Id = 1,
            DatasetsJson = StorageCatalog.Serialize(datasets),
            UpdatedAt = DateTime.UtcNow,
        });

        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateException) when (context.StorageSettings.AsNoTracking().Any())
        {
            // The other host wrote it first.
            return;
        }

        logger.LogInformation(
            existingInstall
                ? "Storage settings written for an existing installation: retention unchanged ({Retention}). Change it in Settings → Storage."
                : "Storage settings written for a new installation with the default retention ({Retention}).",
            string.Join(", ", datasets.Select(item => $"{item.Dataset} {(item.RetentionDays == 0 ? "forever" : item.RetentionDays + "d")}")));
    }
}
