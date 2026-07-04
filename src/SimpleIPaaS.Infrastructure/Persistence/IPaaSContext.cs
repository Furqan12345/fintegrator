using System;
using Microsoft.EntityFrameworkCore;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.MultiTenancy;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class IPaaSContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public IPaaSContext(DbContextOptions<IPaaSContext> options, ITenantContext tenantContext) : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<IntegrationFlow> IntegrationFlows { get; set; } = null!;
    public DbSet<IntegrationStep> IntegrationSteps { get; set; } = null!;
    public DbSet<IntegrationEdge> IntegrationEdges { get; set; } = null!;
    public DbSet<Integration> Integrations { get; set; } = null!;
    
    public DbSet<Connection> Connections { get; set; } = null!;
    public DbSet<FlowExecution> FlowExecutions { get; set; } = null!;
    public DbSet<StepExecution> StepExecutions { get; set; } = null!;
    public DbSet<DeadLetterEntry> DeadLetterEntries { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Global Query Filters for TenantId
        modelBuilder.Entity<IntegrationFlow>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        modelBuilder.Entity<IntegrationStep>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        modelBuilder.Entity<IntegrationEdge>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        modelBuilder.Entity<Integration>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        modelBuilder.Entity<Connection>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        modelBuilder.Entity<FlowExecution>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        modelBuilder.Entity<StepExecution>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        modelBuilder.Entity<DeadLetterEntry>().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
        
        modelBuilder.Entity<IntegrationFlow>()
            .HasMany(f => f.Nodes)
            .WithOne()
            .HasForeignKey(s => s.FlowId)
            .OnDelete(DeleteBehavior.Cascade);
            
        modelBuilder.Entity<IntegrationFlow>()
            .HasMany(f => f.Edges)
            .WithOne()
            .HasForeignKey(e => e.FlowId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Integration>()
            .HasMany(i => i.Flows)
            .WithOne()
            .HasForeignKey(flow => flow.IntegrationId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<FlowExecution>()
            .HasOne<IntegrationFlow>()
            .WithMany()
            .HasForeignKey(e => e.FlowId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StepExecution>()
            .HasOne<FlowExecution>()
            .WithMany()
            .HasForeignKey(e => e.FlowExecutionId)
            .OnDelete(DeleteBehavior.Cascade);
            
        modelBuilder.Entity<StepExecution>()
            .HasOne<IntegrationStep>()
            .WithMany()
            .HasForeignKey(e => e.StepId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DeadLetterEntry>()
            .HasOne<FlowExecution>()
            .WithMany()
            .HasForeignKey(e => e.FlowExecutionId)
            .OnDelete(DeleteBehavior.Cascade);
            
        modelBuilder.Entity<DeadLetterEntry>()
            .HasOne<IntegrationStep>()
            .WithMany()
            .HasForeignKey(e => e.StepId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
