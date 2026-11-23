using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models.Storage;
using SimpleIPaaS.Infrastructure.Persistence;
using SimpleIPaaS.Infrastructure.Storage;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Api.Controllers;

// Storage settings: the database-versus-Elasticsearch choice per growing dataset, the cluster
// connection, and the retention window that applies to whichever backend is in use.
//
// Host-level infrastructure configuration: both the API and the Engine must agree on where
// each growing dataset lives and how long it is kept.
[ApiController]
[Route("api/storage")]
[Authorize(Policy = "Admin")]
public sealed class StorageSettingsController : ControllerBase
{
    private readonly IStorageSettingsProvider _settings;
    private readonly StorageSettingsWriter _writer;
    private readonly IStorageOperations _operations;
    private readonly IPaaSContext _context;

    public StorageSettingsController(
        IStorageSettingsProvider settings,
        StorageSettingsWriter writer,
        IStorageOperations operations,
        IPaaSContext context)
    {
        _settings = settings;
        _writer = writer;
        _operations = operations;
        _context = context;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<StorageSettingsDto>> GetSettings(CancellationToken cancellationToken)
    {
        var snapshot = await _settings.GetAsync(cancellationToken);
        var updatedAt = await _context.StorageSettings
            .AsNoTracking()
            .Select(row => (DateTime?)row.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(ToDto(snapshot, updatedAt));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<StorageSaveResultDto>> UpdateSettings(
        [FromBody] StorageSettingsUpdateDto request,
        CancellationToken cancellationToken)
    {
        var update = new StorageSettingsUpdate
        {
            ElasticsearchEnabled = request.ElasticsearchEnabled,
            Url = request.Url,
            Username = request.Username,
            Password = string.IsNullOrEmpty(request.Password) ? null : request.Password,
            ClearPassword = request.ClearPassword,
            ApiKey = string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey,
            ClearApiKey = request.ClearApiKey,
            IndexPrefix = request.IndexPrefix,
            AllowInvalidCertificate = request.AllowInvalidCertificate,
            TimeoutSeconds = request.TimeoutSeconds,
            Datasets = request.Datasets.Select(ToModel).ToList()
        };

        var outcome = await _writer.SaveAsync(update, cancellationToken);

        // Invalid input is a 400 with the actionable message, never a 200 that says success:false.
        // A save that only failed to prepare an Elasticsearch index plan stays a 200 — it did save.
        if (outcome.ValidationFailed)
        {
            return BadRequest(new { message = outcome.Message });
        }

        var updatedAt = await _context.StorageSettings
            .AsNoTracking()
            .Select(row => (DateTime?)row.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new StorageSaveResultDto
        {
            Success = outcome.Success,
            Message = outcome.Message,
            Settings = ToDto(outcome.Snapshot, updatedAt),
            Applied = outcome.Applied.Select(ToDto).ToList()
        });
    }

    // Tests the connection. An optional body lets the settings page test the values currently in
    // its form (including a new URL or credentials) without saving them; an empty body keeps the
    // original behaviour and tests the persisted configuration.
    [HttpPost("test")]
    public async Task<ActionResult<StorageConnectionTestDto>> Test(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StorageConnectionTestRequestDto? request,
        CancellationToken cancellationToken)
    {
        var result = await _operations.TestConnectionAsync(
            request == null
                ? null
                : new StorageConnectionTestRequest(
                    request.Url,
                    request.Username,
                    string.IsNullOrEmpty(request.Password) ? null : request.Password,
                    string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey,
                    request.AllowInvalidCertificate,
                    request.TimeoutSeconds),
            cancellationToken);

        return Ok(new StorageConnectionTestDto
        {
            Success = result.Success,
            Message = result.Message,
            ClusterName = result.ClusterName,
            Version = result.Version,
            DurationMs = result.DurationMs
        });
    }

    [HttpPost("apply")]
    public async Task<ActionResult<StorageApplyResultDto>> Apply(CancellationToken cancellationToken)
    {
        var result = await _operations.ApplyIndexPlanAsync(cancellationToken);
        return Ok(new StorageApplyResultDto
        {
            Success = result.Success,
            Message = result.Message,
            Datasets = result.Datasets.Select(ToDto).ToList()
        });
    }

    [HttpGet("status")]
    public async Task<ActionResult<StorageInventoryDto>> GetStatus(CancellationToken cancellationToken)
    {
        var inventory = await _operations.GetInventoryAsync(cancellationToken);

        return Ok(new StorageInventoryDto
        {
            CollectedAtUtc = inventory.CollectedAtUtc,
            Error = inventory.Error,
            Datasets = inventory.Datasets.Select(status => new StorageDatasetStatusDto
            {
                Dataset = status.Dataset,
                Name = status.Name,
                Backend = status.Backend,
                CanUseElasticsearch = status.CanUseElasticsearch,
                UseIlm = status.UseIlm,
                RetentionDays = status.RetentionDays,
                SqlStore = status.SqlStore,
                DocumentCount = status.DocumentCount,
                WriteAlias = status.WriteAlias,
                Pattern = status.Pattern,
                TemplateName = status.TemplateName,
                PolicyName = status.PolicyName,
                Ready = status.Ready,
                Error = status.Error,
                Indices = status.Indices.Select(index => new StorageIndexDto
                {
                    Name = index.Name,
                    DocumentCount = index.DocumentCount,
                    Size = index.Size,
                    CreatedAtUtc = index.CreatedAtUtc,
                    AgeDays = index.AgeDays,
                    Expired = index.Expired
                }).ToList()
            }).ToList()
        });
    }

    [HttpPost("retention/run")]
    public async Task<ActionResult<StorageRetentionReportDto>> RunRetention(CancellationToken cancellationToken)
    {
        var result = await _operations.RunRetentionAsync(cancellationToken);

        return Ok(new StorageRetentionReportDto
        {
            RanAtUtc = result.RanAtUtc,
            Datasets = result.Datasets.Select(outcome => new StorageRetentionOutcomeDto
            {
                Dataset = outcome.Dataset,
                Backend = outcome.Backend,
                Action = outcome.Action,
                Removed = outcome.Removed,
                Error = outcome.Error
            }).ToList()
        });
    }

    private static StorageSettingsDto ToDto(StorageSettingsSnapshot snapshot, DateTime? updatedAt)
    {
        var catalog = StorageCatalog.Definitions
            .Select(definition => new StorageDatasetCatalogDto
            {
                Dataset = definition.Dataset,
                Name = definition.Name,
                Holds = definition.Holds,
                VolumeHint = definition.VolumeHint,
                CanUseElasticsearch = definition.CanUseElasticsearch,
                CanUseIlm = definition.CanUseIlm,
                SqlStore = definition.SqlStore,
                DefaultGranularity = definition.DefaultGranularity.ToString(),
                DefaultRetentionDays = definition.DefaultRetentionDays,
                DefaultUseIlm = definition.DefaultUseIlm
            })
            .ToList();

        var datasets = StorageDatasets.All
            .Select(dataset =>
            {
                var settings = snapshot.For(dataset);
                return new StorageDatasetSettingsDto
                {
                    Dataset = dataset,
                    UseElasticsearch = settings.UseElasticsearch,
                    Granularity = settings.Granularity.ToString(),
                    RetentionDays = settings.RetentionDays,
                    UseIlm = settings.UseIlm
                };
            })
            .ToList();

        return new StorageSettingsDto
        {
            ElasticsearchEnabled = snapshot.ElasticsearchEnabled,
            Url = snapshot.Url,
            Username = snapshot.Username,
            HasPassword = !string.IsNullOrEmpty(snapshot.Password),
            HasApiKey = !string.IsNullOrEmpty(snapshot.ApiKey),
            IndexPrefix = snapshot.IndexPrefix,
            AllowInvalidCertificate = snapshot.AllowInvalidCertificate,
            TimeoutSeconds = snapshot.TimeoutSeconds,
            UpdatedAt = updatedAt,
            Datasets = datasets,
            Catalog = catalog
        };
    }

    private static StorageDatasetSettings ToModel(StorageDatasetSettingsDto dto) => new()
    {
        Dataset = dto.Dataset,
        UseElasticsearch = dto.UseElasticsearch,
        Granularity = Enum.TryParse<StorageGranularity>(dto.Granularity, ignoreCase: true, out var granularity) ? granularity : StorageGranularity.Daily,
        RetentionDays = dto.RetentionDays,
        UseIlm = dto.UseIlm
    };

    private static StorageApplyOutcomeDto ToDto(StorageApplyOutcome outcome) => new()
    {
        Dataset = outcome.Dataset,
        Success = outcome.Success,
        Index = outcome.Index,
        WriteAlias = outcome.WriteAlias,
        TemplateName = outcome.TemplateName,
        PolicyName = outcome.PolicyName,
        Error = outcome.Error
    };
}
