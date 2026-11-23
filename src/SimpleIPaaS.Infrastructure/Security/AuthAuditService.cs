using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Persistence;

namespace SimpleIPaaS.Infrastructure.Security;

public class AuthAuditService
{
    private readonly IPaaSContext _db;
    private readonly ILogger<AuthAuditService> _logger;

    public AuthAuditService(IPaaSContext db, ILogger<AuthAuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordAsync(
        string eventType,
        bool success,
        Guid? userId,
        string? email,
        string? ipAddress,
        string? userAgent,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _db.AuthAuditEntries.Add(new AuthAuditEntry
            {
                EventType = Truncate(eventType, 64) ?? "Unknown",
                Success = success,
                UserId = userId,
                Email = Truncate(email, 320) ?? string.Empty,
                IpAddress = Truncate(ipAddress, 64),
                UserAgent = Truncate(userAgent, 400),
                Details = Truncate(details, 1000),
                TimestampUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Audit must never make authentication fail. The application log still records it.
            _logger.LogWarning(exception, "Could not write auth audit event {EventType}.", eventType);
        }
    }

    public Task<List<AuthAuditEntry>> QueryAsync(int take, CancellationToken cancellationToken = default) =>
        _db.AuthAuditEntries
            .AsNoTracking()
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
