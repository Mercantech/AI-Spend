using AiSpend.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiSpend.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<ProviderConnection> ProviderConnections => Set<ProviderConnection>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<UsageDaily> UsageDaily => Set<UsageDaily>();
    public DbSet<LiveMeter> LiveMeters => Set<LiveMeter>();
    public DbSet<ProviderRaw> ProviderRaws => Set<ProviderRaw>();
    public DbSet<Budget> Budgets => Set<Budget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<Membership>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrganizationId, x.UserSub }).IsUnique();
            e.HasIndex(x => x.UserSub);
            e.HasOne(x => x.Organization).WithMany(o => o.Memberships).HasForeignKey(x => x.OrganizationId);
        });

        modelBuilder.Entity<ProviderConnection>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.LastSyncError).HasMaxLength(2000);
            e.HasOne(x => x.Organization).WithMany(o => o.Connections).HasForeignKey(x => x.OrganizationId);
        });

        modelBuilder.Entity<UsageEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrganizationId, x.Provider, x.ExternalKey }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
            e.HasIndex(x => new { x.OrganizationId, x.Provider, x.Model });
            e.Property(x => x.ExternalKey).HasMaxLength(500);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.UserEmail).HasMaxLength(320);
            e.Property(x => x.Kind).HasMaxLength(100);
            e.Property(x => x.ConversationId).HasMaxLength(200);
            e.Property(x => x.CostCents).HasPrecision(18, 4);
            e.Property(x => x.ChargedCents).HasPrecision(18, 4);
            e.Property(x => x.CursorTokenFee).HasPrecision(18, 4);
            e.Property(x => x.RequestsCosts).HasPrecision(18, 4);
            e.HasOne(x => x.Organization).WithMany(o => o.UsageEvents).HasForeignKey(x => x.OrganizationId);
            e.HasOne(x => x.RawPayload).WithMany().HasForeignKey(x => x.RawPayloadId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UsageDaily>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrganizationId, x.Day, x.Provider, x.Model, x.UserEmail }).IsUnique();
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.UserEmail).HasMaxLength(320);
            e.Property(x => x.CostCents).HasPrecision(18, 4);
            e.Property(x => x.ChargedCents).HasPrecision(18, 4);
            e.HasOne(x => x.Organization).WithMany(o => o.UsageDaily).HasForeignKey(x => x.OrganizationId);
        });

        modelBuilder.Entity<LiveMeter>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrganizationId, x.Provider, x.Label }).IsUnique();
            e.Property(x => x.Label).HasMaxLength(100);
            e.Property(x => x.DisplayValue).HasMaxLength(40);
            e.HasOne(x => x.Organization).WithMany(o => o.LiveMeters).HasForeignKey(x => x.OrganizationId);
        });

        modelBuilder.Entity<ProviderRaw>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        });

        modelBuilder.Entity<Budget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MonthlyLimitUsd).HasPrecision(18, 4);
            e.HasOne(x => x.Organization).WithMany(o => o.Budgets).HasForeignKey(x => x.OrganizationId);
        });
    }
}
