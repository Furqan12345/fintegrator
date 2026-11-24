using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Storage;

// Packet logs on PostgreSQL: writes go through the batched COPY ingestor; lists and summaries read only the hot
// StepPacketLogs rows (sizes included); headers and bodies come from the cold StepPacketPayloads table, and only
// when a packet is opened. Cold lookups carry the packet's StartedAt, so they touch a single day partition; lookups
// by step or run carry that step's or run's time window (RunTimeWindow), so they touch only the days it ran on.
public sealed class PostgresPacketLogStore : IPacketLogStore
{
    private readonly IPaaSContext _context;
    private readonly PostgresPacketIngestor _ingestor;
    private readonly SqlPacketLogStore _hotQueries;

    public PostgresPacketLogStore(IPaaSContext context, PostgresPacketIngestor ingestor)
    {
        _context = context;
        _ingestor = ingestor;
        // The page query and the detail metadata only read hot columns, which both layouts share.
        _hotQueries = new SqlPacketLogStore(context);
    }

    public Task AddAsync(StepPacketLog packet, CancellationToken cancellationToken = default) =>
        _ingestor.EnqueueAsync(packet, cancellationToken).AsTask();

    public async Task<IReadOnlyList<StepPacketLog>> GetByStepExecutionAsync(Guid stepExecutionId, CancellationToken cancellationToken = default)
    {
        var hot = _context.StepPacketLogs.AsNoTracking().Where(packet => packet.StepExecutionId == stepExecutionId);
        if (await RunTimeWindow.ForStepAsync(_context, stepExecutionId, cancellationToken) is { } window)
        {
            hot = hot.Where(packet => packet.StartedAt >= window.From && packet.StartedAt <= window.To);
        }

        var packets = await hot
            .OrderBy(packet => packet.Sequence)
            .ToListAsync(cancellationToken);
        if (packets.Count == 0)
        {
            return packets;
        }

        var ids = packets.Select(packet => packet.Id).ToList();
        var from = packets.Min(packet => packet.StartedAt);
        var to = packets.Max(packet => packet.StartedAt);
        var bodies = await _context.Set<StepPacketPayload>()
            .AsNoTracking()
            .Where(payload => ids.Contains(payload.PacketId) && payload.StartedAt >= from && payload.StartedAt <= to)
            .ToDictionaryAsync(payload => payload.PacketId, cancellationToken);
        foreach (var packet in packets)
        {
            if (bodies.TryGetValue(packet.Id, out var body))
            {
                packet.RequestHeadersJson = body.RequestHeadersJson;
                packet.RequestBody = body.RequestBody;
                packet.ResponseHeadersJson = body.ResponseHeadersJson;
                packet.ResponseBody = body.ResponseBody;
            }
        }

        return packets;
    }

    public async Task<IReadOnlyList<StepPacketLogSummary>> GetSummariesAsync(Guid flowExecutionId, CancellationToken cancellationToken = default)
    {
        var window = await RunTimeWindow.ForRunAsync(_context, flowExecutionId, cancellationToken);
        if (window == null)
        {
            return Array.Empty<StepPacketLogSummary>(); // no run, no steps to join
        }

        var (windowFrom, windowTo) = window.Value;
        return await (from packet in _context.StepPacketLogs.AsNoTracking()
                      join step in _context.StepExecutions.AsNoTracking() on packet.StepExecutionId equals step.Id
                      where step.FlowExecutionId == flowExecutionId && packet.StartedAt >= windowFrom && packet.StartedAt <= windowTo
                      orderby packet.StepExecutionId, packet.Sequence
                      select new StepPacketLogSummary(
                          packet.Id,
                          packet.StepExecutionId,
                          packet.Sequence,
                          packet.Kind,
                          packet.PageNumber,
                          packet.Attempt,
                          packet.HttpMethod,
                          packet.RequestUrl,
                          packet.StatusCode,
                          packet.StartedAt,
                          packet.CompletedAt,
                          packet.DurationMs,
                          packet.Error,
                          EF.Property<int>(packet, PostgresLayout.RequestHeadersLength),
                          EF.Property<int>(packet, PostgresLayout.RequestBodyLength),
                          EF.Property<int>(packet, PostgresLayout.ResponseHeadersLength),
                          EF.Property<int>(packet, PostgresLayout.ResponseBodyLength)))
            .ToListAsync(cancellationToken);
    }

    public async Task<StepPacketBody?> GetBodyAsync(Guid flowExecutionId, Guid packetId, CancellationToken cancellationToken = default)
    {
        var window = await RunTimeWindow.ForRunAsync(_context, flowExecutionId, cancellationToken);
        if (window == null)
        {
            return null;
        }

        var (windowFrom, windowTo) = window.Value;
        var startedAt = await (from packet in _context.StepPacketLogs.AsNoTracking()
                               join step in _context.StepExecutions.AsNoTracking() on packet.StepExecutionId equals step.Id
                               where packet.Id == packetId && step.FlowExecutionId == flowExecutionId
                                     && packet.StartedAt >= windowFrom && packet.StartedAt <= windowTo
                               select (DateTime?)packet.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (startedAt == null)
        {
            return null;
        }

        var body = await ColdBodyAsync(packetId, startedAt.Value, cancellationToken);
        return new StepPacketBody(
            packetId,
            body?.RequestHeadersJson ?? "{}",
            body?.RequestBody ?? string.Empty,
            body?.ResponseHeadersJson ?? "{}",
            body?.ResponseBody ?? string.Empty);
    }

    public Task<IReadOnlyList<PacketLogListItem>> GetPageAsync(PacketLogPageFilter filter, CancellationToken cancellationToken = default) =>
        _hotQueries.GetPageAsync(filter, cancellationToken);

    public async Task<PacketLogDetailItem?> GetDetailAsync(Guid packetId, CancellationToken cancellationToken = default)
    {
        var detail = await _hotQueries.GetDetailAsync(packetId, cancellationToken);
        if (detail == null)
        {
            return null;
        }

        var body = await ColdBodyAsync(packetId, detail.StartedAt, cancellationToken);
        return detail with
        {
            RequestHeadersJson = body?.RequestHeadersJson ?? "{}",
            RequestBody = body?.RequestBody ?? string.Empty,
            ResponseHeadersJson = body?.ResponseHeadersJson ?? "{}",
            ResponseBody = body?.ResponseBody ?? string.Empty,
        };
    }

    private Task<StepPacketPayload?> ColdBodyAsync(Guid packetId, DateTime startedAt, CancellationToken cancellationToken)
    {
        var at = ExecutionRepository.TruncateToMicroseconds(startedAt);
        return _context.Set<StepPacketPayload>()
            .AsNoTracking()
            .FirstOrDefaultAsync(payload => payload.PacketId == packetId && payload.StartedAt == at, cancellationToken);
    }
}
