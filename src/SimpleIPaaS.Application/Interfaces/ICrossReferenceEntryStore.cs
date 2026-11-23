using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

// The entry half of ICrossReferenceRepository. Lists stay in the relational database (there are
// few of them and the flow designer reads them constantly); entries are the part that grows with
// every imported record, so they can move to Elasticsearch.
public interface ICrossReferenceEntryStore
{
    Task<long> CountAsync(string listName, CancellationToken cancellationToken = default);

    // Entry counts for every list, used by the cross-reference list page.
    Task<IReadOnlyDictionary<string, long>> CountByListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    // Inserts only entries whose key is new and returns how many were added — the semantics the
    // Cross-Reference Store card depends on for exactly-once bookkeeping.
    Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries, CancellationToken cancellationToken = default);

    Task<CrossReferenceEntryPage> GetEntriesAsync(string listName, string? search, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<bool> DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> ClearListAsync(string listName, CancellationToken cancellationToken = default);
}
