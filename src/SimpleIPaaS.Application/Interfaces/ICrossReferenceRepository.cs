using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

public record CrossReferenceListSummary(CrossReferenceList List, int EntryCount);

public record CrossReferenceEntryPage(IReadOnlyList<CrossReferenceEntry> Entries, int TotalCount);

public interface ICrossReferenceRepository
{
    Task<IReadOnlyList<CrossReferenceListSummary>> GetListsAsync();
    Task<CrossReferenceList?> GetListAsync(string name);
    Task<CrossReferenceList> EnsureListAsync(string name, string description);
    Task<bool> DeleteListAsync(string name);
    Task<int> ClearListAsync(string name);
    Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys);
    Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries);
    Task<CrossReferenceEntryPage> GetEntriesAsync(string listName, string? search, int page, int pageSize);
    Task<bool> DeleteEntryAsync(Guid id);
}
