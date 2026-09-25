using System.Text.Json;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Host.Controllers;
using Microsoft.AspNetCore.Mvc;

internal static class CollectionConfigRegressionTests
{
    public static async Task RejectMissingDevicesAsync()
    {
        var device = CreateDevice();
        var devices = new DeviceRepository(device);
        await devices.DeleteAsync(device.Id);
        var profiles = new ProfileRepository();
        var controller = new CollectionConfigController(profiles, devices);
        foreach (var deviceId in new[] { "222", device.Id })
        {
            var result = await controller.Create(deviceId, CreateRequest());
            TestSupport.Require(result.Result is NotFoundObjectResult { StatusCode: 404 },
                "Missing or deleted devices must return 404 before saving a profile");
            var payload = JsonSerializer.SerializeToElement(((NotFoundObjectResult)result.Result!).Value);
            TestSupport.Require(payload.GetProperty("code").GetString() == "DEVICE_NOT_FOUND"
                && payload.GetProperty("error").GetString() == $"设备 {deviceId} 不存在，请从已注册设备中选择。",
                "The error must identify the missing device and explain how to choose a registered device");
            TestSupport.Require(profiles.AddCalls == 0, "Missing devices must never reach profile persistence");
        }
    }

    public static async Task CreateForRegisteredDeviceAsync()
    {
        var device = CreateDevice();
        var profiles = new ProfileRepository();
        var controller = new CollectionConfigController(profiles, new DeviceRepository(device));
        var request = CreateRequest();
        var result = await controller.Create(device.Id, request);
        TestSupport.Require(result.Result is CreatedAtActionResult { StatusCode: 201 },
            "A registered device with a non-GUID ID must accept a collection profile");
        var created = (CreatedAtActionResult)result.Result!;
        var response = (CollectionProfileDto)created.Value!;
        TestSupport.Require(profiles.AddCalls == 1 && profiles.Stored is not null,
            "A valid profile must be saved exactly once");
        var stored = profiles.Stored!;
        TestSupport.Require(stored.DeviceId == device.Id && response.DeviceId == device.Id
            && stored.Name == request.Name && response.Name == request.Name && stored.IsEnabled,
            "Device IDs and profile settings must be preserved");
        TestSupport.Require(created.ActionName == nameof(CollectionConfigController.GetProfile)
            && created.RouteValues!["profileId"]?.ToString() == stored.Id && response.Id == stored.Id,
            "The created response must point to the saved profile");
        TestSupport.Require(stored.Groups.Count == request.Groups.Count
            && stored.Groups.Select(group => group.Id).Distinct().Count() == request.Groups.Count,
            "Every collection group must be retained with its own identity");
        foreach (var (group, source) in stored.Groups.Zip(request.Groups))
        {
            TestSupport.Require(group.ProfileId == stored.Id && group.GroupName == source.GroupName
                && group.IntervalMs == source.IntervalMs && group.Tags.Count == source.Tags.Count,
                "Groups must preserve their settings and reference the saved profile");
            foreach (var (tag, sourceTag) in group.Tags.Zip(source.Tags))
                TestSupport.Require(tag.GroupId == group.Id && tag.Address == sourceTag.Address
                    && tag.DataType == sourceTag.DataType && tag.DisplayName == sourceTag.DisplayName
                    && tag.Unit == sourceTag.Unit, "Tags must preserve their settings and reference their group");
        }
        TestSupport.Require(JsonSerializer.Serialize(response.Groups) == JsonSerializer.Serialize(request.Groups),
            "The response must include all saved group and tag settings");
    }

    private static Device CreateDevice() => new()
    {
        Id = "plc-line-77", Name = "PLC fixture", Type = DeviceType.PLC,
        Brand = "Siemens", Model = "S7", Protocol = ProtocolType.SiemensS7,
        ConnectionConfig = new() { Host = "127.0.0.1", Port = 1 },
    };

    private static CreateCollectionProfileRequest CreateRequest() => new()
    {
        Name = "Manual collection",
        Groups =
        [
            new()
            {
                GroupName = "Fast", IntervalMs = 1000,
                Tags = [new() { Address = "I5", DataType = DataType.Int32, DisplayName = "Input", Unit = "count" }],
            },
            new()
            {
                GroupName = "Slow", IntervalMs = 2000,
                Tags = [new() { Address = "DB1.DBD0", DataType = DataType.Int32, DisplayName = "Total", Unit = null }],
            },
        ],
    };

    private sealed class DeviceRepository(Device device) : IDeviceRepository
    {
        private Device? _stored = device;
        public Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(_stored?.Id == deviceId ? _stored : null);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Device added, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device updated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string deviceId, CancellationToken ct = default)
        {
            if (_stored?.Id == deviceId) _stored = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ProfileRepository : ICollectionProfileRepository
    {
        public CollectionProfile? Stored { get; private set; }
        public int AddCalls { get; private set; }
        public Task<CollectionProfile?> GetByIdAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CollectionProfile>> GetByDeviceIdAsync(string deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(CollectionProfile profile, CancellationToken ct = default)
        {
            Stored = profile;
            AddCalls++;
            return Task.CompletedTask;
        }
        public Task UpdateAsync(CollectionProfile profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
