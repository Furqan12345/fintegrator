using System;
using System.Collections.Generic;

namespace SimpleIPaaS.Shared.Models;

public class IntegrationFlowDto
{
    public Guid Id { get; set; } = Guid.Empty;
    public Guid TenantId { get; set; }
    public Guid? IntegrationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PersistedStateJson { get; set; } = "{}";
    
    public string Status { get; set; } = "Draft";
    public string TriggerType { get; set; } = "Manual";
    public string CronExpression { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    public List<IntegrationStepDto> Nodes { get; set; } = new();
    public List<IntegrationEdgeDto> Edges { get; set; } = new();
}

public class IntegrationStepDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StepType { get; set; } = string.Empty; // HttpAction, Mapping, Branch, Debug
    public string NodeName { get; set; } = string.Empty;
    
    // UI Properties
    public double PositionX { get; set; }
    public double PositionY { get; set; }

    // API Config
    public string EndpointUrl { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "GET";
    public string UrlMode { get; set; } = "Manual";
    
    // Auth
    public string AuthType { get; set; } = "None"; // None, Basic, Bearer, ApiKey, OAuth2ClientCredentials, OAuth2AuthCode, OAuth2RefreshToken, Custom
    public string AuthToken { get; set; } = string.Empty;
    public string AuthUsername { get; set; } = string.Empty;
    public string AuthPassword { get; set; } = string.Empty;
    public string AuthConfigJson { get; set; } = string.Empty;

    public Guid? ConnectionId { get; set; }

    // Mapping and Scripts
    public string MappingCode { get; set; } = string.Empty;
    public string UrlCode { get; set; } = string.Empty;
    public string PreFlightCode { get; set; } = string.Empty;
    public string PostFlightCode { get; set; } = string.Empty;

    public string StepConfig { get; set; } = string.Empty;
}

public class IntegrationEdgeDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string SourcePortId { get; set; } = string.Empty;
    public string TargetPortId { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public int Order { get; set; }
}
