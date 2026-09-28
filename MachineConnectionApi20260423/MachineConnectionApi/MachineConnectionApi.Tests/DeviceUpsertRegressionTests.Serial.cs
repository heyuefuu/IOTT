namespace MachineConnectionApi.Tests;

using MachineConnectionApi.Models;
using Microsoft.AspNetCore.Mvc;

internal static partial class DeviceUpsertRegressionTests
{
    private static async Task SerialConfigurationSurvivesEdits()
    {
        var properties = new Dictionary<string, string>
        {
            ["PortName"] = "COM7", ["BaudRate"] = "19200", ["DataBits"] = "7",
            ["Parity"] = "Even", ["StopBits"] = "Two",
        };
        var request = new MachineDeviceUpsertRequest
        {
            Name = "Serial", Type = "CNC", Protocol = "Serial", Host = "localhost", Port = 0,
            ExtendedProperties = properties,
        };
        var store = new MemoryDeviceStore();
        var controller = CreateController(store);
        var created = GetOkValue(await controller.Create(request, CancellationToken.None));
        Expect(created.Port == 0 && created.ExtendedProperties["BaudRate"] == "19200", "Serial settings were not persisted");
        var renamed = GetOkValue(await controller.Update(created.Id, new() { Name = "Renamed" }, CancellationToken.None));
        Expect(renamed.Port == 0 && renamed.ExtendedProperties["PortName"] == "COM7", "Partial edit lost serial settings");
        foreach (var invalid in new (string Key, string Value)[]
        {
            ("PortName", " "), ("BaudRate", "0"), ("DataBits", "9"), ("Parity", "invalid"), ("StopBits", "None"),
        })
        {
            var settings = new Dictionary<string, string>(properties) { [invalid.Key] = invalid.Value };
            Expect((await controller.Create(request with { ExtendedProperties = settings }, CancellationToken.None)).Result is BadRequestObjectResult,
                $"Invalid primary serial {invalid.Key} accepted");
            Expect((await controller.Update(created.Id, new() { ExtendedProperties = settings }, CancellationToken.None)).Result is BadRequestObjectResult,
                $"Invalid serial edit {invalid.Key} accepted");
            Expect((await controller.Update(created.Id, new()
            {
                Transfer = new() { Protocol = "Serial", Host = "localhost", Port = 0, ExtendedProperties = settings },
            }, CancellationToken.None)).Result is BadRequestObjectResult, $"Invalid nested serial {invalid.Key} accepted");
        }
        var nested = GetOkValue(await controller.Create(request with
        {
            Protocol = "MTConnect", Port = 5000, ExtendedProperties = null,
            Transfer = new() { Protocol = "Serial", Host = "localhost", Port = 0, ExtendedProperties = properties },
        }, CancellationToken.None));
        var updated = GetOkValue(await controller.Update(nested.Id, new() { Name = "Nested edited" }, CancellationToken.None));
        Expect(updated.Transfer?.ExtendedProperties?["PortName"] == "COM7" && updated.Transfer.Port == 0,
            "Partial primary update changed independent serial channel");
        Expect((await controller.Update(created.Id, new() { Protocol = "FTP" }, CancellationToken.None)).Result is BadRequestObjectResult,
            "Changing from serial port zero to FTP must require a TCP port");
        Console.WriteLine("PASS serial port zero, settings validation and partial updates");
    }
}
