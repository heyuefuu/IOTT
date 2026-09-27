using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

internal static partial class HuazhongRobotRegressionTests
{
    // Explicit opt-in: creates and deletes only a new, uniquely named LocalDB test database.
    public static async Task PersistenceAsync()
    {
        var databaseName = "IOTTImportRegression_" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<IoTDbContext>().UseSqlServer(
            $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Integrated Security=true;TrustServerCertificate=true").Options;
        await using var database = new IoTDbContext(options);
        await database.Database.EnsureCreatedAsync();
        try
        {
            var device = new Device { Name = "Atomic import", Type = DeviceType.Robot,
                Brand = "HSR", Model = "HSR", Protocol = ProtocolType.HuazhongRobot,
                ConnectionConfig = new() { Host = "127.0.0.1", Port = 502,
                    ExtendedProperties = new() { ["AddressMap"] = "[]" } } };
            await new EfDeviceRepository(database).AddAsync(device);
            var config = device.ConnectionConfig with { ExtendedProperties = new() { ["AddressMap"] = "[1]" } };
            var invalid = Profile(device.Id, new string('x', 201));
            var failed = false;
            try { await new EfCollectionImportRepository(database).SaveAsync(device, config, invalid); }
            catch (DbUpdateException) { failed = true; }
            TestSupport.Require(failed, "Persistence fixture did not reject the oversized tag address");
            await using (var check = new IoTDbContext(options))
            {
                var stored = await check.Devices.SingleAsync();
                TestSupport.Require(stored.ConnectionConfig.ExtendedProperties["AddressMap"] == "[]"
                    && !await check.CollectionProfiles.AnyAsync() && !await check.CollectionGroups.AnyAsync()
                    && !await check.TagConfigs.AnyAsync(), "Failed transaction left a map or partial profile in SQL");
            }
            TestSupport.Require(device.ConnectionConfig.ExtendedProperties["AddressMap"] == "[]",
                "Failed transaction changed the tracked device configuration");
            var reloaded = (await new EfDeviceRepository(database).GetByIdAsync(device.Id))!;
            await new EfCollectionImportRepository(database).SaveAsync(reloaded, config, Profile(device.Id, "240"));
            await using var verified = new IoTDbContext(options);
            TestSupport.Require((await verified.Devices.SingleAsync()).ConnectionConfig.ExtendedProperties["AddressMap"] == "[1]"
                && await verified.CollectionProfiles.CountAsync() == 1 && await verified.CollectionGroups.CountAsync() == 1
                && await verified.TagConfigs.CountAsync() == 1, "Successful import did not persist its complete graph");
        }
        finally { await database.Database.EnsureDeletedAsync(); }
    }

    private static CollectionProfile Profile(string deviceId, string address)
    {
        var profile = new CollectionProfile { DeviceId = deviceId, Name = "Atomic import" };
        var group = new CollectionGroup { ProfileId = profile.Id, GroupName = "Main", IntervalMs = 1000 };
        group.Tags.Add(new TagConfig { GroupId = group.Id, Address = address, DataType = DataType.UInt16 });
        profile.Groups.Add(group);
        return profile;
    }
}
