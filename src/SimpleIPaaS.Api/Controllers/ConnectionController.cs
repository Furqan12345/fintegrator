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
    private readonly ITransportEngine _transportEngine;

    public ConnectionController(
        IConnectionRepository repository,
        IEncryptionService encryptionService,
        ITransportEngine transportEngine)
    {
        _repository = repository;
        _encryptionService = encryptionService;
        _transportEngine = transportEngine;
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
        var dtos = connections.Select(ToRedactedDto);
        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var connection = await _repository.GetByIdAsync(id);
        if (connection == null) return NotFound();
        return Ok(ToRedactedDto(connection));
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] ConnectionDto dto)
    {
        if (!Enum.TryParse<AuthType>(dto.AuthType, true, out var authType))
        {
            ModelState.AddModelError(nameof(dto.AuthType), $"'{dto.AuthType}' is not a valid AuthType.");
        }

        if (!Enum.TryParse<ConnectionStatus>(dto.Status, true, out var status))
        {
            ModelState.AddModelError(nameof(dto.Status), $"'{dto.Status}' is not a valid ConnectionStatus.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var authConfigJson = await _encryptionService.EncryptAsync(dto.AuthConfigJson);

        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            BaseUrl = dto.BaseUrl,
            AuthType = authType,
            AuthConfigJson = authConfigJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = status
        };

        await _repository.AddAsync(connection);
        return CreatedAtAction(nameof(Get), new { id = connection.Id }, ToRedactedDto(connection));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ConnectionDto dto)
    {
        if (!Enum.TryParse<AuthType>(dto.AuthType, true, out var authType))
        {
            ModelState.AddModelError(nameof(dto.AuthType), $"'{dto.AuthType}' is not a valid AuthType.");
        }

        if (!Enum.TryParse<ConnectionStatus>(dto.Status, true, out var status))
        {
            ModelState.AddModelError(nameof(dto.Status), $"'{dto.Status}' is not a valid ConnectionStatus.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var connection = await _repository.GetByIdAsync(id);
        if (connection == null) return NotFound();

        connection.Name = dto.Name;
        connection.BaseUrl = dto.BaseUrl;
        connection.AuthType = authType;

        if (!string.IsNullOrEmpty(dto.AuthConfigJson))
        {
            connection.AuthConfigJson = await _encryptionService.EncryptAsync(dto.AuthConfigJson);
        }

        connection.UpdatedAt = DateTime.UtcNow;
        connection.Status = status;

        await _repository.UpdateAsync(connection);
        return Ok(ToRedactedDto(connection));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var connection = await _repository.GetByIdAsync(id);
        if (connection == null) return NotFound();

        await _repository.DeleteAsync(id);
        return NoContent();
    }

    [HttpPost("{id}/test")]
    public async Task<IActionResult> Test(Guid id)
    {
        var connection = await _repository.GetByIdAsync(id);
        if (connection == null) return NotFound();

        var probe = new IntegrationStep
        {
            EndpointUrl = string.Empty,
            HttpMethod = "GET"
        };

        try
        {
            var response = await _transportEngine.DispatchAsync(probe, null, id);
            var statusCode = response.StatusCode;
            return Ok(new
            {
                success = statusCode >= 200 && statusCode < 300,
                statusCode
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                statusCode = 0,
                error = ex.Message
            });
        }
    }

    private static ConnectionDto ToRedactedDto(Connection connection)
    {
        return new ConnectionDto
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
        };
    }
}
