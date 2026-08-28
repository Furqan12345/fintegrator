using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

// Read-only window onto the shared AppLogEntries table plus a maintenance clear.
// Writes happen out-of-band from both hosts through DatabaseLoggerProvider.
[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly ILogRepository _repository;

    public LogsController(ILogRepository repository)
    {
        _repository = repository;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetLogs(
        [FromQuery] string? level = null,
        [FromQuery] string? source = null,
        [FromQuery] string? search = null,
        [FromQuery] Guid? flowExecutionId = null,
        [FromQuery] DateTime? since = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildQuery(level, source, search, flowExecutionId, since, out var query, out var problem))
        {
            return problem!;
        }

        var result = await _repository.GetPageAsync(query, page, pageSize, cancellationToken);

        return Ok(new LogPageDto
        {
            Items = result.Items.Select(ToDto).ToList(),
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
            Cursor = result.Cursor
        });
    }

    // Live tail. Returns only entries newer than afterId, oldest-first, and a stable cursor
    // to pass back on the next poll.
    [HttpGet("tail")]
    public async Task<IActionResult> Tail(
        [FromQuery] long afterId = 0,
        [FromQuery] string? level = null,
        [FromQuery] string? source = null,
        [FromQuery] string? search = null,
        [FromQuery] Guid? flowExecutionId = null,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (afterId < 0)
        {
            ModelState.AddModelError(nameof(afterId), "afterId cannot be negative.");
            return ValidationProblem(ModelState);
        }

        if (!TryBuildQuery(level, source, search, flowExecutionId, null, out var query, out var problem))
        {
            return problem!;
        }

        var result = await _repository.GetTailAsync(afterId, query, limit, cancellationToken);

        return Ok(new LogTailDto
        {
            Entries = result.Entries.Select(ToDto).ToList(),
            Cursor = result.Cursor,
            Reset = result.Reset
        });
    }

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources(CancellationToken cancellationToken = default)
    {
        var sources = await _repository.GetSourcesAsync(cancellationToken);
        return Ok(sources);
    }

    [HttpGet("levels")]
    public IActionResult GetLevels() => Ok(LogRepository.LevelNames);

    [HttpDelete("")]
    public async Task<IActionResult> Clear([FromQuery] DateTime? olderThan = null, CancellationToken cancellationToken = default)
    {
        var cutoff = olderThan.HasValue ? ToUtc(olderThan.Value) : (DateTime?)null;
        var deleted = await _repository.DeleteAsync(cutoff, cancellationToken);
        return Ok(new LogDeleteResultDto { Deleted = deleted });
    }

    private bool TryBuildQuery(
        string? level,
        string? source,
        string? search,
        Guid? flowExecutionId,
        DateTime? since,
        out LogQuery query,
        out IActionResult? problem)
    {
        query = new LogQuery();
        problem = null;

        if (!string.IsNullOrWhiteSpace(level) && LogRepository.ExpandMinimumLevel(level) == null)
        {
            ModelState.AddModelError(nameof(level), $"'{level}' is not a valid log level.");
            problem = ValidationProblem(ModelState);
            return false;
        }

        query = new LogQuery
        {
            MinimumLevel = level,
            Source = source,
            Search = search,
            FlowExecutionId = flowExecutionId,
            SinceUtc = since.HasValue ? ToUtc(since.Value) : null
        };

        return true;
    }

    // Timestamps are stored in UTC; normalise whatever the caller sent.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static AppLogEntryDto ToDto(AppLogEntry entry) => new()
    {
        Id = entry.Id,
        Timestamp = entry.Timestamp,
        Level = entry.Level,
        Source = entry.Source,
        Category = entry.Category,
        Message = entry.Message,
        Exception = entry.Exception,
        TenantId = entry.TenantId,
        FlowExecutionId = entry.FlowExecutionId,
        FlowId = entry.FlowId,
        NodeName = entry.NodeName
    };
}
