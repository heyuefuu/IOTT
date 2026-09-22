using System.Text.Json;
using System.Text.Json.Serialization;
using IndustrialIoT.Application.Commands;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.Interfaces;
using IndustrialIoT.Domain.ValueObjects;

internal static class TransferConfigurationRegressionTests
{
    public static async Task RunAsync()
    {
        await VerifyClearingAsync();
        await VerifyPartialUpdatesAsync();
        await VerifyTransferUpdatesAsync();
    }

    private static async Task VerifyClearingAsync()
    {
        string[] bodies =
        [
            """{"clearTransfer":true}""",
            """{"clearTransfer":true,"transfer":{"protocol":"FTP","host":"127.0.0.1","port":21,"username":"ftp-user"}}""",
            """{"clearTransfer":true,"extendedProperties":{"DeviceId":"machine-sn"}}""",
        ];
        foreach (var body in bodies)
        {
            var request = Deserialize(body);
            TestSupport.Require(request.ClearTransfer, "Host must deserialize clearTransfer from its HTTP contract");
            var repository = new DeviceRepository(CreateDevice());
            repository.Stored.ConnectionConfig.ExtendedProperties["transferDeviceId"] = "old-file-device";
            var primary = repository.Stored.ConnectionConfig;
            var result = await new UpdateDeviceCommandHandler(repository).Handle(
                new UpdateDeviceCommand(repository.Stored.Id, request), CancellationToken.None);
            TestSupport.Require(repository.UpdateCalls == 1 && repository.Stored.ConnectionConfig.Transfer is null && result.Transfer is null,
                "Clearing must be persisted and must override a supplied transfer without restoring its hidden password");
            TestSupport.Require(repository.Stored.ConnectionConfig == primary with
                { Transfer = null, ExtendedProperties = request.ExtendedProperties ?? primary.ExtendedProperties } && result.Protocol == ProtocolType.NCLinkApi,
                "Clearing must preserve the primary CNC connection and its credentials");
            if (request.ExtendedProperties is not null)
                TestSupport.Require(!repository.Stored.ConnectionConfig.ExtendedProperties.ContainsKey("transferDeviceId"),
                    "Cancelling a file channel must also accept removal of its legacy linked-device route");
        }
    }

    private static async Task VerifyPartialUpdatesAsync()
    {
        string[] bodies =
        [
            """{"name":"Renamed"}""",
            """{"name":"Renamed","clearTransfer":false}""",
            """{"name":"Renamed","transfer":null}""",
        ];
        foreach (var body in bodies)
        {
            var request = Deserialize(body);
            TestSupport.Require(!request.ClearTransfer, "Omitting the clear flag must preserve backwards-compatible partial updates");
            var repository = new DeviceRepository(CreateDevice());
            var transfer = repository.Stored.ConnectionConfig.Transfer;
            var result = await new UpdateDeviceCommandHandler(repository).Handle(
                new UpdateDeviceCommand(repository.Stored.Id, request), CancellationToken.None);
            TestSupport.Require(repository.UpdateCalls == 1 && result.Name == "Renamed" &&
                repository.Stored.ConnectionConfig.Transfer == transfer && result.Transfer?.Protocol == ProtocolType.FTP,
                "Partial updates must keep the independent transfer and stored password");
        }
    }

    private static async Task VerifyTransferUpdatesAsync()
    {
        var repository = new DeviceRepository(CreateDevice());
        var handler = new UpdateDeviceCommandHandler(repository);
        var redacted = Deserialize("""{"transfer":{"protocol":"FTP","host":"127.0.0.1","port":21,"username":"ftp-user"}}""");
        await handler.Handle(new UpdateDeviceCommand(repository.Stored.Id, redacted), CancellationToken.None);
        TestSupport.Require(repository.Stored.ConnectionConfig.Transfer?.Password == "ftp-secret",
            "An ordinary transfer edit must keep a hidden password for the same endpoint");
        var moved = Deserialize("""{"transfer":{"protocol":"FTP","host":"127.0.0.2","port":21,"username":"ftp-user"}}""");
        await handler.Handle(new UpdateDeviceCommand(repository.Stored.Id, moved), CancellationToken.None);
        TestSupport.Require(repository.Stored.ConnectionConfig.Transfer is { Host: "127.0.0.2", Password: null },
            "A new transfer endpoint must replace the channel without inheriting an unrelated password");
    }

    private static UpdateDeviceRequest Deserialize(string body)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<UpdateDeviceRequest>(body, options)!;
    }

    private static Device CreateDevice() => new()
    {
        Id = "transfer-cnc", Name = "CNC", Type = DeviceType.CNC, Brand = "HNC", Model = "848",
        Protocol = ProtocolType.NCLinkApi,
        ConnectionConfig = new DeviceConnectionConfig
        {
            Host = "127.0.0.1", Port = 19001, Username = "cnc-user", Password = "cnc-secret",
            ExtendedProperties = new() { ["DeviceId"] = "machine-sn" },
            Transfer = new TransferConnectionConfig
            {
                Protocol = ProtocolType.FTP, Host = "127.0.0.1", Port = 21, Username = "ftp-user", Password = "ftp-secret",
            },
        },
    };

    private sealed class DeviceRepository(Device device) : IDeviceRepository
    {
        public Device Stored { get; private set; } = device;
        public int UpdateCalls { get; private set; }
        public Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default) => Task.FromResult<Device?>(
            Stored.Id == deviceId ? Stored : null);
        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Device>>([Stored]);
        public Task<IReadOnlyList<Device>> GetByTypeAsync(DeviceType type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Device>>(Stored.Type == type ? [Stored] : []);
        public Task AddAsync(Device added, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Device updated, CancellationToken ct = default)
        {
            Stored = updated;
            UpdateCalls++;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
