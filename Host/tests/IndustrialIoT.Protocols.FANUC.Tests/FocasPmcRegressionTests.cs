namespace IndustrialIoT.Protocols.FANUC.Tests;

using System.Reflection;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

internal static class FocasPmcRegressionTests
{
    public static async Task RunAll()
    {
        var api = DispatchProxy.Create<IFocasApi, PmcProbeApi>();
        var probe = (PmcProbeApi)(object)api;
        await using var driver = new FocasDriver(NullLogger<FocasDriver>.Instance, api);
        var connection = await driver.ConnectAsync(new DeviceConnectionConfig { Host = "fixture", Port = 8193 });
        if (!connection.Success) throw new InvalidOperationException(connection.ErrorMessage);
        probe.FailRead = true;
        var failed = await driver.WriteTagAsync("/CNC/PMC/R1200.0", DataType.Bool, true);
        if (failed.Success || probe.Writes != 0 || failed.ErrorMessage?.Contains("ReadPmc") != true)
            throw new InvalidOperationException("PMC read failure must prevent all writes");
        probe.FailRead = false;
        var success = await driver.WriteTagAsync("/CNC/PMC/R1200.0", DataType.Bool, true);
        if (!success.Success || probe.Writes != 1 || !probe.LastWrite.SequenceEqual(new byte[] { 0xA5 })
            || probe.LastArea != 5 || probe.LastStart != 1200 || probe.LastLength != 1 || probe.LastDataType != 0)
            throw new InvalidOperationException("PMC bit write changed neighboring bits");
        var cleared = await driver.WriteTagAsync("R1200.2", DataType.Bool, false);
        if (!cleared.Success || !probe.LastWrite.SequenceEqual(new byte[] { 0xA0 }))
            throw new InvalidOperationException("PMC bit clear changed neighboring bits");
        Console.WriteLine("  FOCAS PMC: failed reads prevent writes and neighboring bits are preserved");
    }
}

public class PmcProbeApi : DispatchProxy
{
    private readonly IFocasApi inner = new SimulatedFocasApi();
    public bool FailRead { get; set; }
    public int Writes { get; private set; }
    public byte[] LastWrite { get; private set; } = [];
    public int LastArea { get; private set; }
    public int LastStart { get; private set; }
    public int LastLength { get; private set; }
    public int LastDataType { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (method.Name is nameof(IFocasApi.ReadPmc) or nameof(IFocasApi.ReadPmcTyped))
        {
            if (FailRead) return -1;
            var buffer = (byte[])arguments[4]!;
            Array.Fill(buffer, (byte)0xA4, 0, (int)arguments[3]!);
            return 0;
        }
        if (method.Name is nameof(IFocasApi.WritePmc) or nameof(IFocasApi.WritePmcTyped))
        {
            Writes++;
            LastWrite = ((byte[])arguments[4]!).ToArray();
            LastArea = (int)arguments[1]!;
            LastStart = (int)arguments[2]!;
            LastLength = (int)arguments[3]!;
            LastDataType = arguments.Length > 5 ? (int)arguments[5]! : 0;
            return 0;
        }
        return method.Invoke(inner, arguments);
    }
}
