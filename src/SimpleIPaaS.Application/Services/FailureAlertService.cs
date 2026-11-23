using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Domain.Entities;

namespace SimpleIPaaS.Application.Services;

// Pure host-wide alert logic so it can be tested without a worker or an SMTP server.
// One digest per tick; entries are marked only after every recipient accepted it.
public sealed class FailureAlertService
{
    private const int MaxEntriesPerDigest = 200;
    private static int _missingBaseUrlWarned;

    private readonly IExecutionRepository _repository;
    private readonly IEmailSender _emailSender;
    private readonly FailureAlertEmailBuilder _emailBuilder;
    private readonly ILogger<FailureAlertService> _logger;
    private readonly string _publicBaseUrl;

    public FailureAlertService(
        IExecutionRepository repository,
        IEmailSender emailSender,
        FailureAlertEmailBuilder emailBuilder,
        ILogger<FailureAlertService> logger,
        IConfiguration? configuration = null)
    {
        _repository = repository;
        _emailSender = emailSender;
        _emailBuilder = emailBuilder;
        _logger = logger;
        _publicBaseUrl = (configuration?["App:PublicBaseUrl"] ?? string.Empty).Trim();
    }

    public static IReadOnlyList<string> ParseRecipients(string? recipients) =>
        (recipients ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<int> ProcessAsync(
        AlertSettings settings,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled)
        {
            return 0;
        }

        var recipients = ParseRecipients(settings.Recipients);
        if (recipients.Count == 0)
        {
            return 0;
        }

        var cooldown = Math.Max(1, settings.CooldownMinutes);
        if (settings.LastSentAt.HasValue && settings.LastSentAt.Value.AddMinutes(cooldown) > now)
        {
            return 0;
        }

        // A null EnabledAt means alerts were never switched on; do not email the backlog.
        var since = settings.EnabledAt ?? now;
        var candidates = await _repository.GetUnalertedDeadLettersAsync(
            since, MaxEntriesPerDigest);
        if (candidates.Count == 0)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(_publicBaseUrl) &&
            Interlocked.Exchange(ref _missingBaseUrlWarned, 1) == 0)
        {
            _logger.LogWarning(
                "App:PublicBaseUrl is not configured; failure alert emails will be sent without links.");
        }

        var email = _emailBuilder.Build(candidates, _publicBaseUrl);

        try
        {
            foreach (var recipient in recipients)
            {
                await _emailSender.SendAsync(
                    recipient,
                    email.Subject,
                    email.HtmlBody,
                    email.TextBody,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Mark nothing: the next tick retries. Some recipients may have received the
            // message already, so this is deliberately at-least-once.
            _logger.LogWarning(ex,
                "Could not send the failure alert digest; it will be retried.");
            return 0;
        }

        await _repository.MarkDeadLettersAlertedAsync(
            candidates.Select(candidate => candidate.Entry.Id).ToList(), now);
        settings.LastSentAt = now;
        return candidates.Count;
    }
}
