using System;
using System.Linq;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Mappings;

public static class DtoMappings
{
    public static IntegrationDto ToDto(this Integration integration)
    {
        return new IntegrationDto
        {
            Id = integration.Id,
            TenantId = integration.TenantId,
            Name = integration.Name,
            Description = integration.Description,
            CreatedAt = integration.CreatedAt,
            UpdatedAt = integration.UpdatedAt,
            Flows = integration.Flows
                .OrderByDescending(flow => flow.UpdatedAt)
                .Select(ToDto)
                .ToList()
        };
    }

    public static IntegrationFlowDto ToDto(this IntegrationFlow flow)
    {
        return new IntegrationFlowDto
        {
            Id = flow.Id,
            TenantId = flow.TenantId,
            IntegrationId = flow.IntegrationId,
            Name = flow.Name,
            Description = flow.Description,
            Status = flow.Status.ToString(),
            TriggerType = flow.TriggerType.ToString(),
            CronExpression = flow.CronExpression,
            WebhookSecret = flow.WebhookSecret,
            CreatedAt = flow.CreatedAt,
            UpdatedAt = flow.UpdatedAt,
            PersistedStateJson = string.IsNullOrWhiteSpace(flow.PersistedStateJson) ? "{}" : flow.PersistedStateJson,
            Nodes = flow.Nodes.Select(ToDto).ToList(),
            Edges = flow.Edges.Select(edge => new IntegrationEdgeDto
            {
                Id = edge.Id,
                SourceNodeId = edge.SourceNodeId,
                TargetNodeId = edge.TargetNodeId,
                SourcePortId = edge.SourcePortId,
                TargetPortId = edge.TargetPortId,
                Condition = edge.Condition,
                Order = edge.Order
            }).ToList()
        };
    }

    public static IntegrationStepDto ToDto(this IntegrationStep step)
    {
        return new IntegrationStepDto
        {
            Id = step.Id,
            StepType = step.StepType.ToString(),
            NodeName = step.NodeName,
            PositionX = step.PositionX,
            PositionY = step.PositionY,
            EndpointUrl = step.EndpointUrl,
            HttpMethod = step.HttpMethod,
            UrlMode = step.UrlMode.ToString(),
            AuthType = step.AuthType.ToString(),
            AuthToken = step.AuthToken,
            AuthUsername = step.AuthUsername,
            AuthPassword = step.AuthPassword,
            ConnectionId = step.ConnectionId,
            MappingCode = step.MappingCode,
            UrlCode = step.UrlCode,
            PreFlightCode = step.PreFlightCode,
            PostFlightCode = step.PostFlightCode,
            StepConfig = step.StepConfig
        };
    }

    public static Integration ToEntity(this IntegrationDto dto)
    {
        return new Integration
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            TenantId = dto.TenantId,
            Name = dto.Name,
            Description = dto.Description,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt,
            Flows = dto.Flows.Select(ToEntity).ToList()
        };
    }

    public static IntegrationFlow ToEntity(this IntegrationFlowDto dto)
    {
        return new IntegrationFlow
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            TenantId = dto.TenantId,
            IntegrationId = dto.IntegrationId,
            Name = dto.Name,
            Description = dto.Description,
            Status = Enum.TryParse<FlowStatus>(dto.Status, out var status) ? status : FlowStatus.Draft,
            TriggerType = Enum.TryParse<TriggerType>(dto.TriggerType, out var triggerType) ? triggerType : TriggerType.Manual,
            CronExpression = dto.CronExpression,
            WebhookSecret = dto.WebhookSecret,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt,
            PersistedStateJson = string.IsNullOrWhiteSpace(dto.PersistedStateJson) ? "{}" : dto.PersistedStateJson,
            Nodes = dto.Nodes.Select(ToEntity).ToList(),
            Edges = dto.Edges.Select(edge => new IntegrationEdge
            {
                Id = edge.Id == Guid.Empty ? Guid.NewGuid() : edge.Id,
                SourceNodeId = edge.SourceNodeId,
                TargetNodeId = edge.TargetNodeId,
                SourcePortId = edge.SourcePortId,
                TargetPortId = edge.TargetPortId,
                Condition = edge.Condition,
                Order = edge.Order
            }).ToList()
        };
    }

    public static IntegrationStep ToEntity(this IntegrationStepDto dto)
    {
        return new IntegrationStep
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            StepType = Enum.TryParse<StepType>(dto.StepType, out var stepType) ? stepType : StepType.Mapping,
            NodeName = dto.NodeName,
            PositionX = dto.PositionX,
            PositionY = dto.PositionY,
            EndpointUrl = dto.EndpointUrl,
            HttpMethod = dto.HttpMethod,
            UrlMode = Enum.TryParse<UrlMode>(dto.UrlMode, out var urlMode) ? urlMode : UrlMode.Manual,
            AuthType = Enum.TryParse<AuthType>(dto.AuthType, out var authType) ? authType : AuthType.None,
            AuthToken = dto.AuthToken,
            AuthUsername = dto.AuthUsername,
            AuthPassword = dto.AuthPassword,
            ConnectionId = dto.ConnectionId,
            MappingCode = dto.MappingCode,
            UrlCode = dto.UrlCode,
            PreFlightCode = dto.PreFlightCode,
            PostFlightCode = dto.PostFlightCode,
            StepConfig = dto.StepConfig
        };
    }
}
