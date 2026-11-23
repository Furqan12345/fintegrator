using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Infrastructure.Elasticsearch;

// Everything the Elasticsearch datasets need in order to resolve a related entity (node, run,
// connection) that lives in the relational database but has to be searchable next to the document.
public sealed record PacketLogEnrichment(
    Guid FlowExecutionId,
    Guid? FlowId,
    string FlowName,
    Guid? IntegrationId,
    string IntegrationName,
    Guid? ConnectionId,
    string? ConnectionName,
    string NodeName,
    bool IsManual)
{
    public static PacketLogEnrichment Empty(Guid flowExecutionId) =>
        new(flowExecutionId, null, string.Empty, null, string.Empty, null, null, string.Empty, true);
}

// Entity/document mapping. Kept in one place so the field names in the mappings, the writes and
// the reads cannot drift apart.
public static class ElasticsearchDocuments
{
    public static JsonObject FromLogEntry(AppLogEntry entry, long sequence) => new()
    {
        ["sequence"] = sequence,
        ["event_id"] = entry.EventId.ToString(),
        ["event_version"] = entry.EventVersion,
        ["timestamp"] = Iso(entry.Timestamp),
        ["level"] = entry.Level,
        ["source"] = entry.Source,
        ["service"] = entry.Service,
        ["host_instance"] = entry.HostInstance,
        ["environment"] = entry.EnvironmentName,
        ["application_version"] = entry.ApplicationVersion,
        ["category"] = entry.Category,
        ["component"] = entry.Component,
        ["event_name"] = entry.EventName,
        ["operation"] = entry.Operation,
        ["outcome"] = entry.Outcome,
        ["duration_ms"] = entry.DurationMs,
        ["trace_id"] = entry.TraceId,
        ["span_id"] = entry.SpanId,
        ["parent_span_id"] = entry.ParentSpanId,
        ["message"] = entry.Message,
        ["exception"] = entry.Exception,
        ["flow_execution_id"] = entry.FlowExecutionId?.ToString(),
        ["flow_id"] = entry.FlowId?.ToString(),
        ["flow_name"] = entry.FlowName,
        ["integration_id"] = entry.IntegrationId?.ToString(),
        ["integration_name"] = entry.IntegrationName,
        ["connection_id"] = entry.ConnectionId?.ToString(),
        ["card_id"] = entry.CardId?.ToString(),
        ["card_type"] = entry.CardType,
        ["step_execution_id"] = entry.StepExecutionId?.ToString(),
        ["invocation"] = entry.Invocation,
        ["loop_path"] = entry.LoopPath,
        ["retry_attempt"] = entry.RetryAttempt,
        ["is_test"] = entry.IsTest,
        ["test_case_id"] = entry.TestCaseId?.ToString(),
        ["test_run_id"] = entry.TestRunId?.ToString(),
        ["node_name"] = entry.NodeName,
        ["properties"] = ParseOrNull(entry.PropertiesJson)
    };

    public static JsonObject FromPacketLog(StepPacketLog packet, PacketLogEnrichment enrichment) => new()
    {
        ["packet_id"] = packet.Id.ToString(),
        ["sequence"] = packet.Sequence,
        ["kind"] = packet.Kind,
        ["page_number"] = packet.PageNumber,
        ["attempt"] = packet.Attempt,
        ["step_execution_id"] = packet.StepExecutionId.ToString(),
        ["flow_execution_id"] = enrichment.FlowExecutionId.ToString(),
        ["flow_id"] = enrichment.FlowId?.ToString(),
        ["flow_name"] = enrichment.FlowName,
        ["integration_id"] = enrichment.IntegrationId?.ToString(),
        ["integration_name"] = enrichment.IntegrationName,
        ["connection_id"] = enrichment.ConnectionId?.ToString(),
        ["connection_name"] = enrichment.ConnectionName,
        ["node_name"] = enrichment.NodeName,
        ["is_manual"] = enrichment.IsManual,
        ["http_method"] = packet.HttpMethod,
        ["request_url"] = packet.RequestUrl,
        ["status_code"] = packet.StatusCode,
        ["duration_ms"] = packet.DurationMs,
        ["started_at"] = Iso(packet.StartedAt),
        ["completed_at"] = packet.CompletedAt.HasValue ? Iso(packet.CompletedAt.Value) : null,
        ["error"] = packet.Error,
        ["request_headers_json"] = packet.RequestHeadersJson,
        ["response_headers_json"] = packet.ResponseHeadersJson,
        ["request_body"] = packet.RequestBody,
        ["response_body"] = packet.ResponseBody,
        ["request_headers_size"] = packet.RequestHeadersJson.Length,
        ["request_body_size"] = packet.RequestBody.Length,
        ["response_headers_size"] = packet.ResponseHeadersJson.Length,
        ["response_body_size"] = packet.ResponseBody.Length
    };

    public static JsonObject FromCrossReferenceEntry(CrossReferenceEntry entry) => new()
    {
        ["entry_id"] = entry.Id.ToString(),
        ["list_name"] = entry.ListName,
        ["key_value"] = entry.KeyValue,
        ["value_json"] = entry.ValueJson,
        ["created_at"] = Iso(entry.CreatedAt),
        ["flow_id"] = entry.FlowId?.ToString()
    };

    // Deterministic document id: list + key. Elasticsearch has no unique constraint, so this is
    // what makes "insert only when the key is new" work — a create against an existing id always
    // comes back as a 409 version conflict, exactly like a unique index would in SQL.
    public static string CrossReferenceDocumentId(string listName, string keyValue)
    {
        var raw = $"{listName}|{keyValue}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    public static AppLogEntry ToLogEntry(JsonNode source, string? id = null)
    {
        var entry = new AppLogEntry
        {
            // The Elasticsearch "sequence" field is this dataset's cursor: it plays the role the
            // AUTOINCREMENT Id plays in AppLogEntries, so the log console pages and tails exactly
            // as it does against the relational store.
            Id = Long(source, "sequence") ?? 0L,
            EventVersion = (int)(Long(source, "event_version") ?? 1L),
            Timestamp = Date(source, "timestamp") ?? DateTime.UtcNow,
            Level = Str(source, "level") ?? string.Empty,
            Source = Str(source, "source") ?? string.Empty,
            Service = Str(source, "service") ?? string.Empty,
            HostInstance = Str(source, "host_instance") ?? string.Empty,
            EnvironmentName = Str(source, "environment") ?? string.Empty,
            ApplicationVersion = Str(source, "application_version") ?? string.Empty,
            Category = Str(source, "category") ?? string.Empty,
            Component = Str(source, "component") ?? string.Empty,
            EventName = Str(source, "event_name") ?? "log",
            Operation = Str(source, "operation") ?? string.Empty,
            Outcome = Str(source, "outcome") ?? string.Empty,
            DurationMs = Long(source, "duration_ms"),
            TraceId = Str(source, "trace_id"),
            SpanId = Str(source, "span_id"),
            ParentSpanId = Str(source, "parent_span_id"),
            Message = Str(source, "message") ?? string.Empty,
            Exception = Str(source, "exception"),
            FlowExecutionId = GuidOf(source, "flow_execution_id"),
            FlowId = GuidOf(source, "flow_id"),
            FlowName = Str(source, "flow_name"),
            IntegrationId = GuidOf(source, "integration_id"),
            IntegrationName = Str(source, "integration_name"),
            ConnectionId = GuidOf(source, "connection_id"),
            CardId = GuidOf(source, "card_id"),
            CardType = Str(source, "card_type"),
            StepExecutionId = GuidOf(source, "step_execution_id"),
            Invocation = (int?)Long(source, "invocation"),
            LoopPath = Str(source, "loop_path"),
            RetryAttempt = (int?)Long(source, "retry_attempt"),
            IsTest = Bool(source, "is_test"),
            TestCaseId = GuidOf(source, "test_case_id"),
            TestRunId = GuidOf(source, "test_run_id"),
            NodeName = Str(source, "node_name"),
            PropertiesJson = source["properties"]?.ToJsonString() ?? "{}"
        };

        if (Guid.TryParse(Str(source, "event_id"), out var eventId))
        {
            entry.EventId = eventId;
        }
        else if (Guid.TryParse(id, out var fromId))
        {
            entry.EventId = fromId;
        }

        return entry;
    }

    public static CrossReferenceEntry ToCrossReferenceEntry(JsonNode source)
    {
        var entry = new CrossReferenceEntry
        {
            ListName = Str(source, "list_name") ?? string.Empty,
            KeyValue = Str(source, "key_value") ?? string.Empty,
            ValueJson = Str(source, "value_json") ?? "{}",
            CreatedAt = Date(source, "created_at") ?? DateTime.UtcNow,
            FlowId = GuidOf(source, "flow_id")
        };

        if (Guid.TryParse(Str(source, "entry_id"), out var id))
        {
            entry.Id = id;
        }

        return entry;
    }

    public static StepPacketLog ToPacketLog(JsonNode source, Guid flowExecutionId)
    {
        var packet = new StepPacketLog
        {
            Sequence = (int)(Long(source, "sequence") ?? 1L),
            Kind = Str(source, "kind") ?? string.Empty,
            PageNumber = (int?)Long(source, "page_number"),
            Attempt = (int?)Long(source, "attempt"),
            HttpMethod = Str(source, "http_method") ?? string.Empty,
            RequestUrl = Str(source, "request_url") ?? string.Empty,
            StatusCode = (int?)Long(source, "status_code"),
            RequestHeadersJson = Str(source, "request_headers_json") ?? "{}",
            RequestBody = Str(source, "request_body") ?? string.Empty,
            ResponseHeadersJson = Str(source, "response_headers_json") ?? "{}",
            ResponseBody = Str(source, "response_body") ?? string.Empty,
            StartedAt = Date(source, "started_at") ?? DateTime.UtcNow,
            CompletedAt = Date(source, "completed_at"),
            DurationMs = Long(source, "duration_ms"),
            Error = Str(source, "error")
        };

        if (Guid.TryParse(Str(source, "packet_id"), out var packetId))
        {
            packet.Id = packetId;
        }

        if (Guid.TryParse(Str(source, "step_execution_id"), out var stepExecutionId))
        {
            packet.StepExecutionId = stepExecutionId;
        }

        return packet;
    }

    public static StepPacketLogSummary ToPacketSummary(JsonNode source)
    {
        var summary = new StepPacketLogSummary(
            GuidOf(source, "packet_id") ?? Guid.Empty,
            GuidOf(source, "step_execution_id") ?? Guid.Empty,
            (int)(Long(source, "sequence") ?? 1L),
            Str(source, "kind") ?? string.Empty,
            (int?)Long(source, "page_number"),
            (int?)Long(source, "attempt"),
            Str(source, "http_method") ?? string.Empty,
            Str(source, "request_url") ?? string.Empty,
            (int?)Long(source, "status_code"),
            Date(source, "started_at") ?? DateTime.UtcNow,
            Date(source, "completed_at"),
            Long(source, "duration_ms"),
            Str(source, "error"),
            (int)(Long(source, "request_headers_size") ?? 0L),
            (int)(Long(source, "request_body_size") ?? 0L),
            (int)(Long(source, "response_headers_size") ?? 0L),
            (int)(Long(source, "response_body_size") ?? 0L));

        return summary;
    }

    public static PacketLogListItem ToPacketListItem(JsonNode source) => new(
        GuidOf(source, "packet_id") ?? Guid.Empty,
        GuidOf(source, "flow_execution_id") ?? Guid.Empty,
        Date(source, "started_at") ?? DateTime.UtcNow,
        Str(source, "flow_name") ?? string.Empty,
        Str(source, "integration_name") ?? string.Empty,
        Str(source, "node_name") ?? string.Empty,
        Str(source, "connection_name"),
        Str(source, "http_method") ?? string.Empty,
        Str(source, "request_url") ?? string.Empty,
        (int?)Long(source, "status_code"),
        Long(source, "duration_ms"),
        (int?)Long(source, "page_number"),
        (int?)Long(source, "attempt"),
        Str(source, "error"));

    public static PacketLogDetailItem ToPacketDetail(JsonNode source) => new(
        GuidOf(source, "packet_id") ?? Guid.Empty,
        GuidOf(source, "flow_execution_id") ?? Guid.Empty,
        Date(source, "started_at") ?? DateTime.UtcNow,
        Date(source, "completed_at"),
        Long(source, "duration_ms"),
        Str(source, "flow_name") ?? string.Empty,
        Str(source, "integration_name") ?? string.Empty,
        Str(source, "node_name") ?? string.Empty,
        Str(source, "connection_name"),
        Str(source, "http_method") ?? string.Empty,
        Str(source, "request_url") ?? string.Empty,
        (int?)Long(source, "status_code"),
        (int?)Long(source, "page_number"),
        (int?)Long(source, "attempt"),
        Str(source, "error"),
        Str(source, "request_headers_json") ?? "{}",
        Str(source, "request_body") ?? string.Empty,
        Str(source, "response_headers_json") ?? "{}",
        Str(source, "response_body") ?? string.Empty);

    private static string Iso(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static JsonNode? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (Exception)
        {
            // A malformed properties blob must never fail a log write.
            return JsonValue.Create(json);
        }
    }

    private static string? Str(JsonNode source, string field) =>
        source[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? Long(JsonNode source, string field)
    {
        if (source[field] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<double>(out var asDouble))
        {
            return (long)asDouble;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? Bool(JsonNode source, string field)
    {
        if (source[field] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) ? parsed : null;
    }

    private static Guid? GuidOf(JsonNode source, string field)
    {
        var text = Str(source, field);
        return System.Guid.TryParse(text, out var parsed) ? parsed : null;
    }

    private static DateTime? Date(JsonNode source, string field)
    {
        if (source[field] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<DateTime>(out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        if (value.TryGetValue<long>(out var epochMillis))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMillis).UtcDateTime;
        }

        if (value.TryGetValue<string>(out var text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fromText))
        {
            return DateTime.SpecifyKind(fromText, DateTimeKind.Utc);
        }

        return null;
    }
}
