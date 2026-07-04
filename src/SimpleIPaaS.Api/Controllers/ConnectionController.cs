using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

[ApiController]
[Route("api/connections")]
public class ConnectionController : ControllerBase
{
    private readonly IConnectionRepository _repository;
    private readonly IEncryptionService _encryptionService;

    public ConnectionController(IConnectionRepository repository, IEncryptionService encryptionService)
    {
        _repository = repository;
        _encryptionService = encryptionService;
    }

    // Frontend calls (plural):
    // - GET    api/connections
    // - GET    api/connections/{id}
    // - POST   api/connections
    // - PUT    api/connections/{id}
    // - DELETE api/connections/{id}

    [HttpGet("")]
    public async Task<IActionResult> GetAll()
    {
        var connections = await _repository.GetAllAsync();
        var dtos = connections.Select(connection => new ConnectionDto
        {
            Id = connection.Id,
            TenantId = connection.TenantId,
            Name = connection.Name,
            BaseUrl = connection.BaseUrl,
            AuthType = connection.AuthType.ToString(),
            Status = connection.Status.ToString(),
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
            AuthConfigJson = string.Empty
        });
        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var connection = await _repository.GetByIdAsync(id);
        if (connection == null) return NotFound();
        return Ok(new ConnectionDto
        {
            Id = connection.Id,
            TenantId = connection.TenantId,
            Name = connection.Name,
            BaseUrl = connection.BaseUrl,
            AuthType = connection.AuthType.ToString(),
            Status = connection.Status.ToString(),
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
            AuthConfigJson = string.IsNullOrWhiteSpace(connection.AuthConfigJson)
                ? string.Empty
                : await _encryptionService.DecryptAsync(connection.AuthConfigJson)
        });
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] ConnectionDto dto)
    {
        var authConfigJson = await _encryptionService.EncryptAsync(dto.AuthConfigJson);
        
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            BaseUrl = dto.BaseUrl,
            AuthType = Enum.Parse<AuthType>(dto.AuthType),
            AuthConfigJson = authConfigJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = Enum.Parse<ConnectionStatus>(dto.Status)
        };

        await _repository.AddAsync(connection);
        return CreatedAtAction(nameof(Get), new { id = connection.Id }, new ConnectionDto
        {
            Id = connection.Id,
            TenantId = connection.TenantId,
            Name = connection.Name,
            BaseUrl = connection.BaseUrl,
            AuthType = connection.AuthType.ToString(),
            Status = connection.Status.ToString(),
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
            AuthConfigJson = dto.AuthConfigJson
        });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ConnectionDto dto)
    {
        var connection = await _repository.GetByIdAsync(id);
        if (connection == null) return NotFound();

        connection.Name = dto.Name;
        connection.BaseUrl = dto.BaseUrl;
        connection.AuthType = Enum.Parse<AuthType>(dto.AuthType);
        
        if (!string.IsNullOrEmpty(dto.AuthConfigJson))
        {
            connection.AuthConfigJson = await _encryptionService.EncryptAsync(dto.AuthConfigJson);
        }
        
        connection.UpdatedAt = DateTime.UtcNow;
        connection.Status = Enum.Parse<ConnectionStatus>(dto.Status);

        await _repository.UpdateAsync(connection);
        return Ok(new ConnectionDto
        {
            Id = connection.Id,
            TenantId = connection.TenantId,
            Name = connection.Name,
            BaseUrl = connection.BaseUrl,
            AuthType = connection.AuthType.ToString(),
            Status = connection.Status.ToString(),
            CreatedAt = connection.CreatedAt,
            UpdatedAt = connection.UpdatedAt,
            AuthConfigJson = dto.AuthConfigJson
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _repository.DeleteAsync(id);
        return NoContent();
    }
}
