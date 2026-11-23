using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Interfaces;

// Packet logs (the request/response messages behind every card) split away from the rest of
// IExecutionRepository so the growing payload history can live in Elasticsearch while runs and
// steps stay in the relational database.
//
// The read models are the ones already declared in IExecutionRepository: callers (and the UI)
// must not be able to tell which backend answered.
public interface IPacketLogStore
{
    Task AddAsync(StepPacketLog packet, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StepPacketLog>> GetByStepExecutionAsync(Guid stepExecutionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StepPacketLogSummary>> GetSummariesAsync(Guid flowExecutionId, CancellationToken cancellationToken = default);

    Task<StepPacketBody?> GetBodyAsync(Guid flowExecutionId, Guid packetId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PacketLogListItem>> GetPageAsync(PacketLogPageFilter filter, CancellationToken cancellationToken = default);

    Task<PacketLogDetailItem?> GetDetailAsync(Guid packetId, CancellationToken cancellationToken = default);
}
