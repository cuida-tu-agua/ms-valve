using Microsoft.EntityFrameworkCore;
using SyWater.Valve.Infrastructure.Persistence.Entities;

namespace SyWater.Valve.Infrastructure.Persistence;

/// <summary>EF Core session against sy-water-db. The schema is owned by ms-valve-db (Liquibase): no EF migrations.</summary>
public class ValveDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Filter of UX_vcmd_place_pending (SQL Server syntax). The SQLite tests replace it.</summary>
    public const string PendingFilter = "[status] = N'SENT'";

    /// <summary>The filter used for the index (the SQLite test context overrides it: SQLite has no N'' literals).</summary>
    protected virtual string PendingIndexFilter => PendingFilter;

    public DbSet<ValveStateEntity> States => Set<ValveStateEntity>();
    public DbSet<ValveCommandEntity> Commands => Set<ValveCommandEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ValveStateEntity>().Property(v => v.Id).HasDefaultValueSql("NEWID()");
        modelBuilder.Entity<ValveStateEntity>().HasIndex(v => v.PlaceId).IsUnique();    // UQ_vstate_place
        modelBuilder.Entity<ValveStateEntity>().HasIndex(v => v.DeviceId).IsUnique();   // UQ_vstate_device

        modelBuilder.Entity<ValveCommandEntity>().HasIndex(c => c.PlaceId).IsUnique()
            .HasFilter(PendingIndexFilter).HasDatabaseName("UX_vcmd_place_pending");
    }
}
