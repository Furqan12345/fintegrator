using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Storage;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class CrossReferenceRepository : ICrossReferenceRepository
{
    private readonly IPaaSContext _context;
    private readonly Func<ICrossReferenceEntryStore> _entryStoreSelector;

    public CrossReferenceRepository(IPaaSContext context, Func<ICrossReferenceEntryStore>? entryStoreSelector = null)
    {
        _context = context;

        // Lists stay in the relational database (few rows, read constantly by the designer); the
        // entries are the growing part and can be served by Elasticsearch. Asked per call so a
        // backend change applies immediately.
        _entryStoreSelector = entryStoreSelector ?? (() => new SqlCrossReferenceEntryStore(context));
    }

    private ICrossReferenceEntryStore EntryStore => _entryStoreSelector();

    public async Task<IReadOnlyList<CrossReferenceListSummary>> GetListsAsync()
    {
        var lists = await _context.CrossReferenceLists
            .AsNoTracking()
            .OrderBy(l => l.Name)
            .ToListAsync();

        var counts = await EntryStore.CountByListAsync();

        return lists
            .Select(list => new CrossReferenceListSummary(
                list,
                counts.TryGetValue(list.Name, out var count) ? (int)Math.Min(count, int.MaxValue) : 0))
            .ToList();
    }

    public async Task<CrossReferenceList?> GetListAsync(string name)
    {
        return await _context.CrossReferenceLists.FirstOrDefaultAsync(l => l.Name == name);
    }

    public async Task<CrossReferenceList> EnsureListAsync(string name, string description)
    {
        var existing = await GetListAsync(name);
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(description) && existing.Description != description)
            {
                existing.Description = description;
                await _context.SaveChangesAsync();
            }

            return existing;
        }

        var list = new CrossReferenceList
        {
            Name = name,
            Description = description ?? string.Empty
        };

        _context.CrossReferenceLists.Add(list);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _context.Entry(list).State = EntityState.Detached;
            var raced = await GetListAsync(name);
            if (raced == null)
            {
                throw;
            }

            return raced;
        }

        return list;
    }

    public async Task<bool> DeleteListAsync(string name)
    {
        var list = await GetListAsync(name);
        await ClearListAsync(name);

        if (list == null)
        {
            return false;
        }

        _context.CrossReferenceLists.Remove(list);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<int> ClearListAsync(string name)
    {
        return await EntryStore.ClearListAsync(name);
    }

    public async Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys)
    {
        return await EntryStore.GetExistingKeysAsync(listName, keys);
    }

    public async Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries)
    {
        return await EntryStore.UpsertEntriesAsync(listName, entries);
    }

    public async Task<CrossReferenceEntryPage> GetEntriesAsync(string listName, string? search, int page, int pageSize)
    {
        return await EntryStore.GetEntriesAsync(listName, search, page, pageSize);
    }

    public async Task<bool> DeleteEntryAsync(Guid id)
    {
        return await EntryStore.DeleteEntryAsync(id);
    }
}
