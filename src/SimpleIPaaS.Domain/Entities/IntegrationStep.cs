using System;

namespace SimpleIPaaS.Domain.Entities;

public class IntegrationStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid FlowId { get; set; }
    public StepType StepType { get; set; }
    
    // UI Properties
    public double PositionX { get; set; }
    public double PositionY { get; set; }

    // API Config
    public string EndpointUrl { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "GET"; 
    
    // Auth Config (Legacy/Inline)
    public AuthType AuthType { get; set; } = AuthType.None;
    public string AuthToken { get; set; } = string.Empty;
    public string AuthUsername { get; set; } = string.Empty;
    public string AuthPassword { get; set; } = string.Empty;
    
    // Auth Config (Enterprise Connection)
    public Guid? ConnectionId { get; set; }

    // Mapping & Step Config
    public string MappingCode { get; set; } = string.Empty; 
    public string StepConfig { get; set; } = string.Empty; // JSON blob for step-specific settings

    // Cumulative State & Scripting Additions
    public string NodeName { get; set; } = string.Empty;
    public string UrlCode { get; set; } = string.Empty;
    public string PreFlightCode { get; set; } = string.Empty;
    public string PostFlightCode { get; set; } = string.Empty;
}
