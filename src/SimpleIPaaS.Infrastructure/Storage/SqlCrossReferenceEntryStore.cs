using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Cross-reference entries in the relational database â€” the entry half of what used to be
// CrossReferenceRepository, unchanged in behaviour (insert-only, unique key, race-safe retry).
public sealed class SqlCrossReferenceEntryStore : ICrossReferenceEntryStore
{
    private const int KeyLookupBatchSize = 400;

    private readonly IPaaSContext _context;

    public SqlCrossReferenceEntryStore(IPaaSContext context)
    {
        _context = context;
    }

    public async Task<long> CountAsync(string listName, CancellationToken cancellationToken = default)
    {
        return await _context.CrossReferenceEntries
            .AsNoTracking()
            .LongCountAsync(entry => entry.ListName == listName, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> CountByListAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _context.CrossReferenceEntries
            .AsNoTracking()
            .GroupBy(entry => entry.ListName)
            .Select(group => new { ListName = group.Key, Count = (long)group.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(count => count.ListName, count => count.Count, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyCollection<string>> GetExistingKeysAsync(string listName, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
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
                .Where(entry => entry.ListName == listName && batch.Contains(entry.KeyValue))
                .Select(entry => entry.KeyValue)
                .ToListAsync(cancellationToken);

            foreach (var match in matches)
            {
                found.Add(match);
            }
        }

        return found;
    }

    public async Task<int> UpsertEntriesAsync(string listName, IReadOnlyCollection<CrossReferenceEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return 0;
        }

        var existing = await GetExistingKeysAsync(listName, entries.Select(entry => entry.KeyValue).ToList(), cancellationToken);

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
        }

        _context.CrossReferenceEntries.AddRange(additions);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return additions.Count;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in additions)
            {
                _context.Entry(entry).State = EntityState.Detached;
            }

            return await InsertOneByOneAsync(additions, cancellationToken);
        }
    }

    public async Task<CrossReferenceEntryPage> GetEntriesAsync(string listName, string? search, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 50 : Math.Min(pageSize, 200);

        var query = _context.CrossReferenceEntries
            .AsNoTracking()
            .Where(entry => entry.ListName == listName);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(entry => EF.Functions.Like(entry.KeyValue, $"%{term}%") || EF.Functions.Like(entry.ValueJson, $"%{term}%"));
        }

        var total = await query.CountAsync(cancellationToken);

        var entries = await query
            .OrderByDescending(entry => entry.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new CrossReferenceEntryPage(entries, total);
    }

    public async Task<bool> DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await _context.CrossReferenceEntries.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entry == null)
        {
            return false;
        }

        _context.CrossReferenceEntries.Remove(entry);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> ClearListAsync(string listName, CancellationToken cancellationToken = default)
    {
        return await _context.CrossReferenceEntries
            .Where(entry => entry.ListName == listName)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<int> InsertOneByOneAsync(IEnumerable<CrossReferenceEntry> entries, CancellationToken cancellationToken)
    {
        var inserted = 0;

        foreach (var entry in entries)
        {
            _context.CrossReferenceEntries.Add(entry);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
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
