using MachineConnectionApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace MachineConnectionApi.Data;

public class MCConfigurationDbContext : DbContext
{
    public MCConfigurationDbContext(DbContextOptions<MCConfigurationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Datacollection> Datacollections => Set<Datacollection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Datacollection>(e =>
        {
            e.ToTable("datacollection");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Path).HasMaxLength(500).IsRequired();
            e.Property(x => x.Datatype).HasMaxLength(50).IsRequired();
            e.Property(x => x.Datetime).HasColumnName("datetime").HasColumnType("date");
            e.Property(x => x.CollectionFrequency).HasColumnName("collectionfrequency");
            e.Property(x => x.DeviceId).HasColumnName("device_id").HasMaxLength(64).IsRequired();
            e.Property(x => x.Protocol).HasColumnName("protocol").HasMaxLength(32).IsRequired().HasDefaultValue("IndustrialIoT");
            e.HasIndex(x => new { x.DeviceId, x.Path }).IsUnique();
        });
    }
}
