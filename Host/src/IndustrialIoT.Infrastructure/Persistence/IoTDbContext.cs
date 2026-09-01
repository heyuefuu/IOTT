namespace IndustrialIoT.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.ValueObjects;
using System.Text.Json;
using System.Text.Json.Serialization;

public class IoTDbContext : DbContext
{
    private static readonly JsonSerializerOptions ConnectionConfigJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public IoTDbContext(DbContextOptions<IoTDbContext> options) : base(options) { }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<CollectionProfile> CollectionProfiles => Set<CollectionProfile>();
    public DbSet<CollectionGroup> CollectionGroups => Set<CollectionGroup>();
    public DbSet<TagConfig> TagConfigs => Set<TagConfig>();
    public DbSet<NCProgram> NCPrograms => Set<NCProgram>();
    public DbSet<RealtimeDataRecord> RealtimeDataRecords => Set<RealtimeDataRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasMaxLength(32);
            e.Property(d => d.Name).HasMaxLength(200).IsRequired();
            e.Property(d => d.Brand).HasMaxLength(100).IsRequired();
            e.Property(d => d.Model).HasMaxLength(200).IsRequired();
            e.Property(d => d.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Protocol).HasConversion<string>().HasMaxLength(32);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.ConnectionConfig)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, ConnectionConfigJsonOptions),
                    v => JsonSerializer.Deserialize<DeviceConnectionConfig>(v, ConnectionConfigJsonOptions)!)
                .HasColumnType("nvarchar(max)");
            e.Property(d => d.Metadata)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null)!)
                .HasColumnType("nvarchar(max)");
            e.HasMany(d => d.CollectionProfiles).WithOne().HasForeignKey(p => p.DeviceId);
        });

        modelBuilder.Entity<CollectionProfile>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(32);
            e.Property(p => p.DeviceId).HasMaxLength(32);
            e.Property(p => p.Name).HasMaxLength(200);
            e.HasMany(p => p.Groups).WithOne().HasForeignKey(g => g.ProfileId);
        });

        modelBuilder.Entity<CollectionGroup>(e =>
        {
            e.HasKey(g => g.Id);
            e.Property(g => g.Id).HasMaxLength(32);
            e.Property(g => g.ProfileId).HasMaxLength(32);
            e.Property(g => g.GroupName).HasMaxLength(100);
            e.HasMany(g => g.Tags).WithOne().HasForeignKey(t => t.GroupId);
        });

        modelBuilder.Entity<TagConfig>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(32);
            e.Property(t => t.GroupId).HasMaxLength(32);
            e.Property(t => t.Address).HasMaxLength(200);
            e.Property(t => t.DataType).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.DisplayName).HasMaxLength(200);
            e.Property(t => t.Unit).HasMaxLength(50);
        });

        modelBuilder.Entity<NCProgram>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(32);
            e.Property(p => p.DeviceId).HasMaxLength(32);
            e.Property(p => p.FileName).HasMaxLength(500);
            e.Property(p => p.RemotePath).HasMaxLength(1000);
            e.Property(p => p.Checksum).HasMaxLength(128);
            e.Property(p => p.Direction).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.ErrorMessage).HasMaxLength(2000);
        });

        modelBuilder.Entity<RealtimeDataRecord>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(32);
            e.Property(r => r.DeviceId).HasMaxLength(32).IsRequired();
            e.Property(r => r.GroupName).HasMaxLength(100).IsRequired();
            e.Property(r => r.PayloadJson).HasColumnType("nvarchar(max)");
            e.HasIndex(r => new { r.DeviceId, r.CollectedAt });
        });
    }
}
