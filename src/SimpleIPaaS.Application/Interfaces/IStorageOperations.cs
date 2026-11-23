using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Application.Models.Storage;

namespace SimpleIPaaS.Application.Interfaces;

// Operator-facing storage operations used by the settings page: verify the cluster, create or
// repair the index templates/policies/write index, inspect what exists, and run retention now.
public interface IStorageOperations
{
    // Verifies the stored cluster configuration. When request is set, the submitted URL and
    // credentials are tested instead — and never persisted (the settings page tests unsaved edits).
    Task<StorageConnectionTestResult> TestConnectionAsync(
        StorageConnectionTestRequest? request = null,
        CancellationToken cancellationToken = default);

    Task<StorageApplyResult> ApplyIndexPlanAsync(CancellationToken cancellationToken = default);

    Task<StorageInventory> GetInventoryAsync(CancellationToken cancellationToken = default);

    Task<StorageRetentionRunResult> RunRetentionAsync(CancellationToken cancellationToken = default);
}
