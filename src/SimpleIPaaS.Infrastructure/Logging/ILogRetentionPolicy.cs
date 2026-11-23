using SimpleIPaaS.Infrastructure.Storage;

namespace SimpleIPaaS.Infrastructure.Logging;

// Where the relational log writer's retention windows come from. The default implementation reads
// the storage settings page, so "keep logs for N days" has one answer instead of two.
public interface ILogRetentionPolicy
{
    LogRetentionWindows Resolve(LogRetentionWindows fallback);
}
