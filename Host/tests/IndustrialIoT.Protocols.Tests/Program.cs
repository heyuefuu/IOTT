var checks = new (string Name, Func<Task> Run)[]
{
    ("Protocol safety: Modbus writes, lengths and point isolation", ModbusSafetyRegressionTests.RunAsync),
    ("Protocol safety: PLC types and Siemens S7 transport", PlcSafetyRegressionTests.RunAsync),
    ("Protocol safety: robot and text data quality", RobotAndTextSafetyRegressionTests.RunAsync),
    ("Protocol safety: SDK values and OPC UA session cleanup", SdkAndSessionSafetyRegressionTests.RunAsync),
    ("PLC collection groups preserve driver values and periods", PlcCollectionRegressionTests.CollectGroupsAsync),
    ("PLC collection survives request cancellation", PlcCollectionRegressionTests.RequestCancellationAsync),
    ("PLC collection waits for reads before releasing", PlcCollectionRegressionTests.StopWaitsForReadAsync),
    ("PLC collection cancels siblings on group initialization failure", PlcCollectionRegressionTests.GroupInitializationFailureAsync),
    ("PLC collection serializes shared device connection lifetimes", PlcCollectionRegressionTests.SharedDeviceLifecycleAsync),
    ("Collection config rejects missing and deleted devices", CollectionConfigRegressionTests.RejectMissingDevicesAsync),
    ("Collection config preserves registered device profiles", CollectionConfigRegressionTests.CreateForRegisteredDeviceAsync),
    ("PLC address browsing rejects synthetic directories", PlcAddressSpaceRegressionTests.RunAsync),
    ("Siemens S7 scalar widths and byte order", SiemensS7RegressionTests.ScalarWidthsAsync),
    ("Siemens S7 TIA addresses and exact integer transport", SiemensS7RegressionTests.AddressAndTransportAsync),
    ("OPC UA scalar, structured values and quality", OpcUaRegressionTests.RunAsync),
    ("Haas ping and macro roundtrip", HaasRegressionTests.MacroRoundtripAsync),
    ("Haas fragmented responses and CRLF", HaasRegressionTests.FragmentedResponseAsync),
    ("Haas asynchronous timeout", HaasRegressionTests.ReadTimeoutAsync),
    ("Haas incomplete response", HaasRegressionTests.PartialResponseTimeoutAsync),
    ("GSK upload and directory errors", CncRegressionTests.GskUploadAsync),
    ("GSK SDK managed port configuration", CncRegressionTests.GskSdkConfigurationAsync),
    ("Transfer configuration clear and partial update", TransferConfigurationRegressionTests.RunAsync),
    ("Batch CNC file routing and duplicate names", BatchProgramTransferRegressionTests.RunAsync),
    ("FANUC CRX capabilities", CncRegressionTests.FanucMetadataAsync),
    ("GSK WebSocket frame timestamps and disconnect", GskRealtimeRegressionTests.RunAsync),
    ("HTTP status, transfers and MTConnect nesting", HttpRegressionTests.RunAsync),
    ("NCLink per-device routing, rejected writes and file keys", NCLinkApiRoutingRegressionTests.RunAsync),
    ("Modbus and Profibus independent wire byte order", ModbusRegressionTests.ByteOrderAsync),
    ("Huazhong robot health address", ModbusRegressionTests.RobotHealthAddressAsync),
    ("HNC and JingDiao IPC directory errors", SdkDirectoryRegressionTests.RunAsync),
};
if (args.Contains("--protocol-safety"))
    checks = checks.Where(check => check.Name.StartsWith("Protocol safety:", StringComparison.Ordinal)).ToArray();
if (args.Contains("--plc-collection"))
    checks = checks.Where(check => check.Name.StartsWith("PLC collection", StringComparison.Ordinal)).ToArray();
if (args.Contains("--collection-config"))
    checks = checks.Where(check => check.Name.StartsWith("Collection config", StringComparison.Ordinal)).ToArray();
if (args.Contains("--siemens-s7"))
    checks = checks.Where(check => check.Name.StartsWith("Siemens S7", StringComparison.Ordinal)).ToArray();
if (args.Contains("--plc-browse"))
    checks = checks.Where(check => check.Name.StartsWith("PLC address browsing", StringComparison.Ordinal)
        || check.Name.StartsWith("Siemens S7", StringComparison.Ordinal)
        || check.Name.StartsWith("OPC UA", StringComparison.Ordinal)
        || check.Name.StartsWith("Modbus and Profibus", StringComparison.Ordinal)).ToArray();
var failures = 0;
foreach (var check in checks)
{
    try { await check.Run(); Console.WriteLine("PASS " + check.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + check.Name + ": " + error); }
}
Console.WriteLine($"RESULT {checks.Length - failures}/{checks.Length} passed");
return failures == 0 ? 0 : 1;

internal static class TestSupport
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
