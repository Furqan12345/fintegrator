using System;
using System.Text;
using SimpleIPaaS.Domain;

namespace SimpleIPaaS.Api.Observability;

public sealed class OperationalMetrics
{
    private readonly long _startedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long _httpRequests;
    private long _httpErrors;
    private long _httpDurationMilliseconds;
    private long _activeRequests;

    public void RequestStarted() => Interlocked.Increment(ref _activeRequests);

    public void RequestFinished(int statusCode, long elapsedMilliseconds)
    {
        Interlocked.Increment(ref _httpRequests);
        if (statusCode >= 500)
        {
            Interlocked.Increment(ref _httpErrors);
        }

        Interlocked.Add(ref _httpDurationMilliseconds, elapsedMilliseconds);
        Interlocked.Decrement(ref _activeRequests);
    }

    public string RenderPrometheus(IReadOnlyDictionary<ExecutionStatus, long> executionCounts, double engineHeartbeatAgeSeconds, bool engineHeartbeatHealthy)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# HELP simpleipaas_process_start_time_seconds Unix time when the API process started.");
        builder.AppendLine("# TYPE simpleipaas_process_start_time_seconds gauge");
        builder.Append("simpleipaas_process_start_time_seconds ").AppendLine(_startedAtUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP simpleipaas_http_requests_total Total HTTP requests observed by the API.");
        builder.AppendLine("# TYPE simpleipaas_http_requests_total counter");
        builder.Append("simpleipaas_http_requests_total ").AppendLine(Interlocked.Read(ref _httpRequests).ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP simpleipaas_http_errors_total HTTP responses with status 500 or greater.");
        builder.AppendLine("# TYPE simpleipaas_http_errors_total counter");
        builder.Append("simpleipaas_http_errors_total ").AppendLine(Interlocked.Read(ref _httpErrors).ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP simpleipaas_http_request_duration_milliseconds_total Cumulative request duration.");
        builder.AppendLine("# TYPE simpleipaas_http_request_duration_milliseconds_total counter");
        builder.Append("simpleipaas_http_request_duration_milliseconds_total ").AppendLine(Interlocked.Read(ref _httpDurationMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP simpleipaas_http_requests_active Current requests being processed.");
        builder.AppendLine("# TYPE simpleipaas_http_requests_active gauge");
        builder.Append("simpleipaas_http_requests_active ").AppendLine(Interlocked.Read(ref _activeRequests).ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP simpleipaas_engine_heartbeat_age_seconds Age of the latest execution-engine heartbeat.");
        builder.AppendLine("# TYPE simpleipaas_engine_heartbeat_age_seconds gauge");
        builder.Append("simpleipaas_engine_heartbeat_age_seconds ").AppendLine(engineHeartbeatAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP simpleipaas_engine_heartbeat_healthy Whether the execution engine heartbeat is current.");
        builder.AppendLine("# TYPE simpleipaas_engine_heartbeat_healthy gauge");
        builder.Append("simpleipaas_engine_heartbeat_healthy ").AppendLine(engineHeartbeatHealthy ? "1" : "0");

        builder.AppendLine("# HELP simpleipaas_flow_executions Current flow execution count by status.");
        builder.AppendLine("# TYPE simpleipaas_flow_executions gauge");
        foreach (var status in Enum.GetValues<ExecutionStatus>())
        {
            executionCounts.TryGetValue(status, out var count);
            builder.Append("simpleipaas_flow_executions{status=\"")
                .Append(status)
                .Append("\"} ")
                .AppendLine(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}