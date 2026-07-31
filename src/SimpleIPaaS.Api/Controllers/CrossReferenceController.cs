using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/crossreferences")]
public class CrossReferenceController : ControllerBase
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.Compiled);

    private readonly ICrossReferenceRepository _repository;

    public CrossReferenceController(ICrossReferenceRepository repository)
    {
        _repository = repository;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetLists()
    {
        var summaries = await _repository.GetListsAsync();
        return Ok(summaries.Select(summary => ToDto(summary.List, summary.EntryCount)));
    }

    [HttpPost("")]
    public async Task<IActionResult> CreateList([FromBody] CrossReferenceListDto dto)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        if (!NamePattern.IsMatch(name))
        {
            return Problem(
                title: "Invalid list name",
                detail: "List names must be 1-64 characters and may contain letters, digits, spaces, dots, underscores and hyphens.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await _repository.GetListAsync(name);
        if (existing != null)
        {
            return Problem(
                title: "List already exists",
                detail: $"A cross-reference list named '{name}' already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var list = await _repository.EnsureListAsync(name, (dto.Description ?? string.Empty).Trim());
        return CreatedAtAction(nameof(GetEntries), new { name = list.Name }, ToDto(list, 0));
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> DeleteList(string name)
    {
        var list = await _repository.GetListAsync(name);
        if (list == null)
        {
            return Problem(
                title: "List not found",
                detail: $"No cross-reference list named '{name}' exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var removed = await _repository.ClearListAsync(name);
        await _repository.DeleteListAsync(name);

        return Ok(new CrossReferenceCommandResultDto { ListName = name, Removed = removed });
    }

    [HttpGet("{name}/entries")]
    public async Task<IActionResult> GetEntries(string name, [FromQuery] string? search = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var list = await _repository.GetListAsync(name);
        if (list == null)
        {
            return Problem(
                title: "List not found",
                detail: $"No cross-reference list named '{name}' exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 50 : Math.Min(pageSize, 200);

        var result = await _repository.GetEntriesAsync(name, search, page, pageSize);

        return Ok(new CrossReferenceEntryPageDto
        {
            Entries = result.Entries.Select(ToDto).ToList(),
            TotalCount = result.TotalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpDelete("{name}/entries")]
    public async Task<IActionResult> ClearList(string name)
    {
        var list = await _repository.GetListAsync(name);
        if (list == null)
        {
            return Problem(
                title: "List not found",
                detail: $"No cross-reference list named '{name}' exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var removed = await _repository.ClearListAsync(name);
        return Ok(new CrossReferenceCommandResultDto { ListName = name, Removed = removed });
    }

    [HttpDelete("entries/{id}")]
    public async Task<IActionResult> DeleteEntry(Guid id)
    {
        var deleted = await _repository.DeleteEntryAsync(id);
        if (!deleted)
        {
            return Problem(
                title: "Entry not found",
                detail: $"No cross-reference entry with id '{id}' exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(new CrossReferenceCommandResultDto { Removed = 1 });
    }

    private static CrossReferenceListDto ToDto(CrossReferenceList list, int entryCount)
    {
        return new CrossReferenceListDto
        {
            Id = list.Id,
            Name = list.Name,
            Description = list.Description,
            EntryCount = entryCount,
            CreatedAt = list.CreatedAt
        };
    }

    private static CrossReferenceEntryDto ToDto(CrossReferenceEntry entry)
    {
        return new CrossReferenceEntryDto
        {
            Id = entry.Id,
            ListName = entry.ListName,
            KeyValue = entry.KeyValue,
            ValueJson = entry.ValueJson,
            FlowId = entry.FlowId,
            CreatedAt = entry.CreatedAt
        };
    }
}
