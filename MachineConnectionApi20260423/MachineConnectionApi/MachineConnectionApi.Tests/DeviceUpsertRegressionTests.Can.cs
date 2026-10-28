namespace MachineConnectionApi.Tests;

using MachineConnectionApi.Models;
using Microsoft.AspNetCore.Mvc;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task CanConfigurationSurvivesEdits()
    {
        var properties = new Dictionary<string, string>
        {
            ["CanTransport"] = "SLCAN", ["PortName"] = "COM3", ["BaudRate"] = "115200",
            ["CanBitRate"] = "500000", ["DataBits"] = "8", ["Parity"] = "None",
            ["StopBits"] = "One", ["Handshake"] = "None",
        };
        var request = new MachineDeviceUpsertRequest
        {
            Name = "CAN", Type = "CNC", Protocol = "CAN", Host = "COM3", Port = 0,
            ExtendedProperties = properties,
        };
        var store = new MemoryDeviceStore();
        var controller = CreateController(store);
        var created = GetOkValue(await controller.Create(request, CancellationToken.None));
        var renamed = GetOkValue(await controller.Update(created.Id, new() { Name = "CAN edited" }, CancellationToken.None));
        Expect(renamed.Host == "COM3" && renamed.Port == 0 && renamed.ExtendedProperties["CanBitRate"] == "500000",
            "Partial CAN edits lost SLCAN configuration");
        foreach (var invalid in new (string Key, string Value)[]
        {
            ("CanTransport", "PCAN"), ("PortName", " "), ("BaudRate", "0"), ("CanBitRate", "333333"),
            ("DataBits", "7"), ("Parity", "Even"), ("StopBits", "Two"), ("Handshake", "RequestToSend"),
            ("CanTransport", null!), ("Handshake", null!),
            ("CanFrameMaxAgeMs", "0"), ("CanFrameMaxAgeMs", "-1"), ("CanFrameMaxAgeMs", "invalid"),
        })
        {
            var settings = new Dictionary<string, string>(properties) { [invalid.Key] = invalid.Value };
            Expect((await controller.Create(request with { ExtendedProperties = settings }, CancellationToken.None)).Result is BadRequestObjectResult,
                $"Invalid CAN {invalid.Key} accepted on create");
            Expect((await controller.Update(created.Id, new() { ExtendedProperties = settings }, CancellationToken.None)).Result is BadRequestObjectResult,
                $"Invalid CAN {invalid.Key} accepted on update");
        }
        Expect((await controller.Create(request with { Port = 502 }, CancellationToken.None)).Result is BadRequestObjectResult,
            "SLCAN must not accept a TCP port");
        Expect((await controller.Update(created.Id, new() { Protocol = "ModbusTCP" }, CancellationToken.None)).Result is BadRequestObjectResult,
            "Switching SLCAN to TCP must require a TCP port");
        Console.WriteLine("PASS CAN SLCAN configuration validation and partial updates");
    }
}
