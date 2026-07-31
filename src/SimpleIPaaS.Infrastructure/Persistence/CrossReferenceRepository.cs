using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class CrossReferenceRepository : ICrossReferenceRepository
{
    private const int KeyLookupBatchSize = 400;

    private readonly IPaaSContext _context;
    private readonly ITenantContext _tenantContext;

    public CrossReferenceRepository(IPaaSContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<IReadOnlyList<CrossReferenceListSummary>> GetListsAsync()
    {
        var lists = await _context.CrossReferenceLists
            .AsNoTracking()
            .OrderBy(l => l.Name)
            .ToListAsync();

        var counts = await _context.CrossReferenceEntries
            .AsNoTracking()
            .GroupBy(e => e.ListName)
            .Select(g => new { ListName = g.Key, Count = g.Count() })
            .ToListAsync();

        var countByName = counts.ToDictionary(c => c.ListName, c => c.Count, StringComparer.OrdinalIgnoreCase);

        return lists
            .Select(list => new CrossReferenceListSummary(list, countByName.TryGetValue(list.Name, out var count) ? count : 0))
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
            TenantId = _tenantContext.TenantId,
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
        return await _context.CrossReferenceEntries
            .Where(e => e.ListName == name)
            .ExecuteDeleteAsync();
    }

    public async Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return found;
        }

        foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(KeyLookupBatchSize))
        {
            var matches = await _context.CrossReferenceEntries
                .AsNoTracking()
                .Where(e => e.ListName == listName && batch.Contains(e.KeyValue))
                .Select(e => e.KeyValue)
                .ToListAsync();

            foreach (var match in matches)
            {
                found.Add(match);
            }
        }

        return found;
    }

    public async Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries)
    {
        if (entries.Count == 0)
        {
            return 0;
        }

        var existing = await GetExistingKeysAsync(listName, entries.Select(e => e.KeyValue).ToList());

        var additions = entries
            .Where(entry => !existing.Contains(entry.KeyValue))
            .GroupBy(entry => entry.KeyValue, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (additions.Count == 0)
        {
            return 0;
        }

        foreach (var entry in additions)
        {
            entry.ListName = listName;
            if (entry.TenantId == Guid.Empty)
            {
                entry.TenantId = _tenantContext.TenantId;
            }
        }

        _context.CrossReferenceEntries.AddRange(additions);

        try
        {
            await _context.SaveChangesAsync();
            return additions.Count;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in additions)
            {
                _context.Entry(entry).State = EntityState.Detached;
            }

            return await InsertOneByOneAsync(additions);
        }
    }

    public async Task<CrossReferenceEntryPage> GetEntriesAsync(string listName, string? search, int page, int pageSize)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 50 : Math.Min(pageSize, 200);

        var query = _context.CrossReferenceEntries
            .AsNoTracking()
            .Where(e => e.ListName == listName);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(e => EF.Functions.Like(e.KeyValue, $"%{term}%") || EF.Functions.Like(e.ValueJson, $"%{term}%"));
        }

        var total = await query.CountAsync();

        var entries = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new CrossReferenceEntryPage(entries, total);
    }

    public async Task<bool> DeleteEntryAsync(Guid id)
    {
        var entry = await _context.CrossReferenceEntries.FirstOrDefaultAsync(e => e.Id == id);
        if (entry == null)
        {
            return false;
        }

        _context.CrossReferenceEntries.Remove(entry);
        await _context.SaveChangesAsync();
        return true;
    }

    private async Task<int> InsertOneByOneAsync(IEnumerable<CrossReferenceEntry> entries)
    {
        var inserted = 0;

        foreach (var entry in entries)
        {
            _context.CrossReferenceEntries.Add(entry);

            try
            {
                await _context.SaveChangesAsync();
                inserted++;
            }
            catch (DbUpdateException)
            {
                _context.Entry(entry).State = EntityState.Detached;
            }
        }

        return inserted;
    }
}
