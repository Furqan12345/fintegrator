using System;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Security;

namespace SimpleIPaaS.Infrastructure.Persistence;

public class IPaaSContext : IdentityUserContext<AppUser, Guid>
{
    public IPaaSContext(DbContextOptions<IPaaSContext> options) : base(options)
    {
    }

    public DbSet<IntegrationFlow> IntegrationFlows { get; set; } = null!;
    public DbSet<FlowVersion> FlowVersions { get; set; } = null!;
    public DbSet<FlowTestCase> FlowTestCases { get; set; } = null!;
    public DbSet<FlowTestRun> FlowTestRuns { get; set; } = null!;
    public DbSet<HostHeartbeat> HostHeartbeats { get; set; } = null!;
    public DbSet<IntegrationStep> IntegrationSteps { get; set; } = null!;
    public DbSet<IntegrationEdge> IntegrationEdges { get; set; } = null!;
    public DbSet<Integration> Integrations { get; set; } = null!;
    
    public DbSet<Connection> Connections { get; set; } = null!;
    public DbSet<FlowExecution> FlowExecutions { get; set; } = null!;
    public DbSet<StepExecution> StepExecutions { get; set; } = null!;
    public DbSet<StepPacketLog> StepPacketLogs { get; set; } = null!;
    public DbSet<FanOutSnapshot> FanOutSnapshots { get; set; } = null!;
    public DbSet<DeadLetterEntry> DeadLetterEntries { get; set; } = null!;
    public DbSet<ApiKey> ApiKeys { get; set; } = null!;
    public DbSet<AiProviderSettings> AiProviderSettings { get; set; } = null!;
    public DbSet<AlertSettings> AlertSettings { get; set; } = null!;
    public DbSet<AiBuildJob> AiBuildJobs { get; set; } = null!;
    public DbSet<AiChatSession> AiChatSessions { get; set; } = null!;
    public DbSet<AiChatMessage> AiChatMessages { get; set; } = null!;
    // Host-wide build budgets.
    public DbSet<AiBuildSettings> AiBuildSettings { get; set; } = null!;
    public DbSet<CrossReferenceList> CrossReferenceLists { get; set; } = null!;
    public DbSet<CrossReferenceEntry> CrossReferenceEntries { get; set; } = null!;

    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;
    public DbSet<AuthAuditEntry> AuthAuditEntries { get; set; } = null!;

    // Host-wide storage configuration: which backend serves each growing dataset, and for how long.
    // The API and the Engine must agree on where a dataset lives, and an Elasticsearch index is
    // a host-level object.
    public DbSet<StorageSettings> StorageSettings { get; set; } = null!;

    // Host-wide application log: entries come from background workers that log outside any
    // request scope (startup, cron scans, claim loops), and the Debug Logs console has to show
    // them all.
    public DbSet<AppLogEntry> AppLogEntries { get; set; } = null!;

    // PostgreSQL stores DateTime as timestamptz, which only accepts UTC values. The codebase uses UtcNow
    // throughout, but values that round-trip through SQLite-era code paths can be Kind=Unspecified, and Npgsql
    // rejects those at write time. Every DateTime is normalised to UTC on the way in and marked UTC on the way out.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        if (Database.IsNpgsql())
        {
            configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
            configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
        }
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(
                value => value.Kind == DateTimeKind.Utc
                    ? value
                    : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc),
                value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
        {
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.Property(user => user.FullName).HasMaxLength(200);
            entity.Property(user => user.Role).HasMaxLength(32);
            entity.HasIndex(user => user.Role);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.Property(token => token.TokenHash).HasMaxLength(64);
            entity.Property(token => token.ReplacedByTokenHash).HasMaxLength(64);
            entity.Property(token => token.CreatedByIp).HasMaxLength(64);
            entity.Property(token => token.UserAgent).HasMaxLength(400);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => token.UserId);
            entity.HasOne<AppUser>()
                .WithMany()
                .HasForeignKey(token => token.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthAuditEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Email).HasMaxLength(320);
            entity.Property(entry => entry.EventType).HasMaxLength(64);
            entity.Property(entry => entry.IpAddress).HasMaxLength(64);
            entity.Property(entry => entry.UserAgent).HasMaxLength(400);
            entity.Property(entry => entry.Details).HasMaxLength(1000);
            entity.HasIndex(entry => entry.TimestampUtc);
            entity.HasIndex(entry => entry.Email);
        });

        modelBuilder.Entity<HostHeartbeat>()
            .HasIndex(e => new { e.ServiceName, e.InstanceId })
            .IsUnique();

        modelBuilder.Entity<FlowVersion>()
            .HasIndex(e => new { e.FlowId, e.VersionNumber })
            .IsUnique();

        modelBuilder.Entity<AiProviderSettings>()
            .Property(e => e.Name)
            .HasMaxLength(120);
        modelBuilder.Entity<AiProviderSettings>()
            .HasIndex(e => e.Name)
            .IsUnique();

        // Host-wide, one row (Id = 1): one deployment belongs to one customer.
        modelBuilder.Entity<AlertSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Recipients).HasMaxLength(1000);
        });

        modelBuilder.Entity<AiBuildJob>().HasIndex(e => e.CreatedAt);
        modelBuilder.Entity<AiBuildJob>().HasIndex(e => new { e.Status, e.CreatedAt });

        // Single row: infrastructure configuration shared by both hosts.
        modelBuilder.Entity<StorageSettings>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<StorageSettings>()
            .Property(e => e.Id)
            .ValueGeneratedNever();
        modelBuilder.Entity<StorageSettings>()
            .Property(e => e.ElasticsearchUrl)
            .HasMaxLength(500);
        modelBuilder.Entity<StorageSettings>()
            .Property(e => e.IndexPrefix)
            .HasMaxLength(60);

        modelBuilder.Entity<AiBuildSettings>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<AiBuildSettings>()
            .Property(e => e.Id)
            .ValueGeneratedNever();

        modelBuilder.Entity<AiChatSession>().HasIndex(e => e.UpdatedAt);
        modelBuilder.Entity<AiChatMessage>().HasIndex(e => new { e.SessionId, e.CreatedAt });
        modelBuilder.Entity<AiChatMessage>().HasIndex(e => new { e.Status, e.CreatedAt });

        // Archived nodes stay out of editable/runtime graphs.
        modelBuilder.Entity<IntegrationStep>().HasQueryFilter(e => !e.IsArchived);

        modelBuilder.Entity<CrossReferenceList>()
            .HasIndex(e => e.Name)
            .IsUnique();

        modelBuilder.Entity<CrossReferenceEntry>()
            .HasIndex(e => new { e.ListName, e.KeyValue })
            .IsUnique();

        modelBuilder.Entity<AppLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.FlowExecutionId);
            entity.HasIndex(e => e.IntegrationId);
            entity.HasIndex(e => e.FlowId);
        });



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

        // On PostgreSQL StepPacketLogs is partitioned and expires by dropping whole days, so it carries no
        // foreign key (a cascade from StepExecutions would have to probe every partition).
        if (!Database.IsNpgsql())
        {
            modelBuilder.Entity<StepPacketLog>()
                .HasOne<StepExecution>()
                .WithMany()
                .HasForeignKey(e => e.StepExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        }

        modelBuilder.Entity<DeadLetterEntry>()
            .HasOne<FlowExecution>()
            .WithMany()
            .HasForeignKey(e => e.FlowExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FanOutSnapshot>(entity =>
        {
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.ItemVariable).HasMaxLength(200);
            entity.Property(snapshot => snapshot.NodeName).HasMaxLength(256);
            entity.HasIndex(snapshot => snapshot.ParentExecutionId);
            entity.HasOne<FlowExecution>()
                .WithMany()
                .HasForeignKey(snapshot => snapshot.ParentExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
            
        modelBuilder.Entity<DeadLetterEntry>()
            .HasOne<IntegrationStep>()
            .WithMany()
            .HasForeignKey(e => e.StepId)
            .OnDelete(DeleteBehavior.Restrict);

        if (Database.IsNpgsql())
        {
            PostgresLayout.Configure(modelBuilder);
        }
    }
}
