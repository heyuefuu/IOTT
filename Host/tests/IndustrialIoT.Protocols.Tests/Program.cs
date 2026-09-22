var checks = new (string Name, Func<Task> Run)[]
{
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
