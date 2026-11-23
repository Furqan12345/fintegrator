using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Elasticsearch;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Packet logs in Elasticsearch.
//
// Two things are worth knowing here:
//   * The document denormalises what the relational query had to join for (flow name, integration
//     name, node name, connection name, manual/managed flag), so a page of packets is ONE search
//     instead of a four-way join. The context is resolved from the relational database once per
//     write â€” those rows stay there by definition, they are small, and they are read by id.
//   * Bodies and headers are stored, not indexed (see the packet mapping): they are read back for
//     the detail view but never searched, so indexing them would only cost disk and CPU.
public sealed class ElasticPacketLogStore : IPacketLogStore
{
    private static readonly string[] SearchableFields = { "request_url", "flow_name", "integration_name", "node_name" };

    private readonly ElasticsearchClientProvider _clients;
    private readonly IPaaSContext _context;

    public ElasticPacketLogStore(ElasticsearchClientProvider clients, IPaaSContext context)
    {
        _clients = clients;
        _context = context;
    }

    public async Task AddAsync(StepPacketLog packet, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);

        var enrichment = await ResolveEnrichmentAsync(packet, cancellationToken).ConfigureAwait(false);
        var alias = StorageIndexNaming.WriteAlias(snapshot.IndexPrefix, StorageDatasets.PacketLogs);
        var document = ElasticsearchDocuments.FromPacketLog(packet, enrichment);

        var result = await client.BulkAsync(
            alias,
            new[] { ElasticsearchBulkOperation.Create(packet.Id.ToString(), document) },
            cancellationToken).ConfigureAwait(false);

        if (result.Failures > 0 && !result.Items.Any(item => item.Succeeded))
        {
            throw new InvalidOperationException(
                $"Elasticsearch rejected the packet log write: {result.Items.FirstOrDefault(item => !item.Succeeded)?.ErrorReason ?? "unknown error"}");
        }
    }

    public async Task<IReadOnlyList<StepPacketLog>> GetByStepExecutionAsync(Guid stepExecutionId, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["query"] = ElasticsearchQueryBuilder.Bool(filter: new[]
            {
                ElasticsearchQueryBuilder.Term("step_execution_id", stepExecutionId.ToString())
            }),
            ["size"] = 1000,
            ["sort"] = ElasticsearchQueryBuilder.Sorts(ElasticsearchQueryBuilder.SortAscending("sequence"))
        };

        var result = await client.SearchAsync(Pattern(snapshot), body, cancellationToken).ConfigureAwait(false);
        return result.Hits.Where(hit => hit.Source != null)
            .Select(hit => ElasticsearchDocuments.ToPacketLog(hit.Source!, Guid.Empty))
            .ToList();
    }

    public async Task<IReadOnlyList<StepPacketLogSummary>> GetSummariesAsync(Guid flowExecutionId, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["query"] = ElasticsearchQueryBuilder.Bool(filter: new[]
            {
                ElasticsearchQueryBuilder.Term("flow_execution_id", flowExecutionId.ToString())
            }),
            ["size"] = 2000,
            ["sort"] = ElasticsearchQueryBuilder.Sorts(
                ElasticsearchQueryBuilder.SortAscending("step_execution_id"),
                ElasticsearchQueryBuilder.SortAscending("sequence"))
        };

        var result = await client.SearchAsync(Pattern(snapshot), body, cancellationToken).ConfigureAwait(false);
        return result.Hits.Where(hit => hit.Source != null)
            .Select(hit => ElasticsearchDocuments.ToPacketSummary(hit.Source!))
            .ToList();
    }

    public async Task<StepPacketBody?> GetBodyAsync(Guid flowExecutionId, Guid packetId, CancellationToken cancellationToken = default)
    {
        var source = await FindOneAsync(packetId, flowExecutionId, cancellationToken).ConfigureAwait(false);
        if (source == null)
        {
            return null;
        }

        return new StepPacketBody(
            Guid.TryParse(source["packet_id"]?.GetValue<string>(), out var id) ? id : packetId,
            source["request_headers_json"]?.GetValue<string>() ?? "{}",
            source["request_body"]?.GetValue<string>() ?? string.Empty,
            source["response_headers_json"]?.GetValue<string>() ?? "{}",
            source["response_body"]?.GetValue<string>() ?? string.Empty);
    }

    public async Task<IReadOnlyList<PacketLogListItem>> GetPageAsync(PacketLogPageFilter filter, CancellationToken cancellationToken = default)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);

        var filters = new List<JsonObject>
        {
            ElasticsearchQueryBuilder.Term("kind", "HTTP")
        };

        if (filter.FlowId.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term("flow_id", filter.FlowId.Value.ToString()));
        }

        if (filter.FlowExecutionId.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term("flow_execution_id", filter.FlowExecutionId.Value.ToString()));
        }

        if (filter.IntegrationId.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term("integration_id", filter.IntegrationId.Value.ToString()));
        }

        if (filter.ConnectionId.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term("connection_id", filter.ConnectionId.Value.ToString()));
        }

        if (filter.ManualOnly)
        {
            filters.Add(ElasticsearchQueryBuilder.Term("is_manual", "true"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            filters.Add(ElasticsearchQueryBuilder.SearchAcross(SearchableFields, filter.Search.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filter.Method))
        {
            filters.Add(ElasticsearchQueryBuilder.Term("http_method", filter.Method.Trim()));
        }

        var successRange = ElasticsearchQueryBuilder.Range("status_code", gte: "200", lt: "300");
        if (string.Equals(filter.Outcome, "success", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add(successRange);
        }
        else if (string.Equals(filter.Outcome, "failure", StringComparison.OrdinalIgnoreCase))
        {
            // Missing status codes count as failures too, so "not a 2xx" is the right predicate.
            filters.Add(ElasticsearchQueryBuilder.Bool(mustNot: new[] { successRange }));
        }

        if (filter.Hours.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Range("started_at", gte: Iso(DateTime.UtcNow.AddHours(-filter.Hours.Value))));
        }

        var page = Math.Max(1, filter.Page);
        var pageSize = filter.PageSize < 1 ? 50 : Math.Min(filter.PageSize, 500);

        var body = new JsonObject
        {
            ["query"] = ElasticsearchQueryBuilder.Bool(filter: filters),
            ["from"] = (page - 1) * pageSize,
            ["size"] = pageSize,
            ["sort"] = ElasticsearchQueryBuilder.Sorts(ElasticsearchQueryBuilder.SortDescending("started_at"))
        };

        var result = await client.SearchAsync(Pattern(snapshot), body, cancellationToken).ConfigureAwait(false);
        return result.Hits.Where(hit => hit.Source != null)
            .Select(hit => ElasticsearchDocuments.ToPacketListItem(hit.Source!))
            .ToList();
    }

    public async Task<PacketLogDetailItem?> GetDetailAsync(Guid packetId, CancellationToken cancellationToken = default)
    {
        var source = await FindOneAsync(packetId, null, cancellationToken).ConfigureAwait(false);
        return source == null ? null : ElasticsearchDocuments.ToPacketDetail(source);
    }

    private async Task<JsonNode?> FindOneAsync(Guid packetId, Guid? flowExecutionId, CancellationToken cancellationToken)
    {
        var (client, snapshot) = await RequireAsync(cancellationToken).ConfigureAwait(false);

        var filters = new List<JsonObject>
        {
            ElasticsearchQueryBuilder.Term("packet_id", packetId.ToString())
        };

        if (flowExecutionId.HasValue)
        {
            filters.Add(ElasticsearchQueryBuilder.Term("flow_execution_id", flowExecutionId.Value.ToString()));
        }

        var body = new JsonObject
        {
            ["query"] = ElasticsearchQueryBuilder.Bool(filter: filters),
            ["size"] = 1
        };

        var result = await client.SearchAsync(Pattern(snapshot), body, cancellationToken).ConfigureAwait(false);
        return result.Hits.FirstOrDefault()?.Source;
    }

    // One join in the relational database gives the document everything the packet page filters and
    // displays on. Runs and steps stay in SQL, so this is a primary-key lookup, not a scan.
    private async Task<PacketLogEnrichment> ResolveEnrichmentAsync(StepPacketLog packet, CancellationToken cancellationToken)
    {
        var row = await (
            from step in _context.StepExecutions.AsNoTracking()
            join run in _context.FlowExecutions.AsNoTracking() on step.FlowExecutionId equals run.Id
            join node in _context.IntegrationSteps.AsNoTracking().IgnoreQueryFilters() on step.StepId equals node.Id into nodes
            from node in nodes.DefaultIfEmpty()
            where step.Id == packet.StepExecutionId
            select new
            {
                step.NodeName,
                RunId = run.Id,
                run.FlowId,
                run.FlowName,
                run.IntegrationId,
                run.IntegrationName,
                ConnectionId = node == null ? (Guid?)null : node.ConnectionId
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row == null)
        {
            return PacketLogEnrichment.Empty(Guid.Empty);
        }

        string? connectionName = null;
        if (row.ConnectionId.HasValue)
        {
            connectionName = await _context.Connections.AsNoTracking()
                .Where(connection => connection.Id == row.ConnectionId.Value)
                .Select(connection => connection.Name)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        return new PacketLogEnrichment(
            row.RunId,
            row.FlowId,
            row.FlowName ?? string.Empty,
            row.IntegrationId,
            row.IntegrationName ?? string.Empty,
            row.ConnectionId,
            connectionName,
            row.NodeName ?? string.Empty,
            IsManual: row.ConnectionId == null);
    }

    private async Task<(ElasticsearchClient Client, StorageSettingsSnapshot Snapshot)> RequireAsync(CancellationToken cancellationToken)
    {
        var resolved = await _clients.GetAsync(cancellationToken).ConfigureAwait(false);
        if (resolved == null)
        {
            throw new InvalidOperationException("The packet-log dataset is configured for Elasticsearch, but no cluster is reachable or configured.");
        }

        return (resolved.Value.Client, resolved.Value.Snapshot);
    }

    private static string Pattern(StorageSettingsSnapshot snapshot) =>
        StorageIndexNaming.Pattern(snapshot.IndexPrefix, StorageDatasets.PacketLogs);

    private static string Iso(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
