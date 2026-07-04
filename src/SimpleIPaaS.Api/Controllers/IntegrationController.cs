using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/integrations")]
public class IntegrationController : ControllerBase
{
    private readonly IIntegrationCatalogRepository _integrationRepository;
    private readonly IIntegrationRepository _flowRepository;

    public IntegrationController(
        IIntegrationCatalogRepository integrationRepository,
        IIntegrationRepository flowRepository)
    {
        _integrationRepository = integrationRepository;
        _flowRepository = flowRepository;
    }

    [HttpGet("")]
    public async Task<IActionResult> GetAll()
    {
        var integrations = await _integrationRepository.GetAllAsync();
        return Ok(integrations.Select(integration => integration.ToDto()));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var integration = await _integrationRepository.GetByIdAsync(id);
        if (integration == null)
        {
            return NotFound();
        }

        return Ok(integration.ToDto());
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] IntegrationDto dto)
    {
        var integration = dto.ToEntity();
        integration.Id = Guid.NewGuid();
        integration.CreatedAt = DateTime.UtcNow;
        integration.UpdatedAt = DateTime.UtcNow;

        await _integrationRepository.AddAsync(integration);
        return CreatedAtAction(nameof(Get), new { id = integration.Id }, integration.ToDto());
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] IntegrationDto dto)
    {
        if (id != dto.Id)
        {
            return BadRequest("ID mismatch");
        }

        var existing = await _integrationRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return NotFound();
        }

        existing.Name = dto.Name;
        existing.Description = dto.Description;
        existing.UpdatedAt = DateTime.UtcNow;

        await _integrationRepository.UpdateAsync(existing);
        return Ok(existing.ToDto());
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _integrationRepository.DeleteAsync(id);
        return NoContent();
    }

    [HttpGet("{id}/flows")]
    public async Task<IActionResult> GetFlows(Guid id)
    {
        var flows = await _flowRepository.GetByIntegrationIdAsync(id);
        return Ok(flows.Select(flow => flow.ToDto()));
    }
}
