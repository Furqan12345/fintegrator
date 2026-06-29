using System;

namespace SimpleIPaaS.Infrastructure.MultiTenancy;

public interface ITenantContext
{
    Guid TenantId { get; }
    void SetTenantId(Guid tenantId);
}

public class TenantContext : ITenantContext
{
    private Guid _tenantId;

    public Guid TenantId => _tenantId;

    public void SetTenantId(Guid tenantId)
    {
        _tenantId = tenantId;
    }
}
