using System.Net;
using System.Net.Sockets;
using System.Text;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static async Task Main()
    {
        await MachineConnectionApi.Tests.VerifyTaskRunnerRegressionTests.ConcurrentExecutionIsRejected();
        await MachineConnectionApi.Tests.VerifyTaskRunnerRegressionTests.ExecutionFailureReleasesTaskForRetry();
        await MachineConnectionApi.Tests.JsonFileStoreRegressionTests.ConcurrentUpdatesDoNotLoseRows();
        await MachineConnectionApi.Tests.DeviceUpsertRegressionTests.RunAll();
        await MachineConnectionApi.Tests.CsConnectivityServiceRegressionTests.RunAll();
        MachineConnectionApi.Tests.TelemetryInfluxSqlRegressionTests.HistoryPagingUsesStableOrdering();
        await SameTargetParallelTestOpensRequestedConnections();
        await ParallelTestKeepsLongConnectionsOpen();
        await ParallelMqttTestPerformsMqttHandshake();
        await ParallelMqttTestUsesRequestClientId();
        await ParallelMqttTestUsesRequestCredentials();
        await ParallelUdpTestSendsUdpDatagrams();
        ParallelReportServiceGeneratesHtmlReport();
        ParallelReportServiceGeneratesPdfReport();
        Console.WriteLine("All tests passed.");
    }

    private static async Task SameTargetParallelTestOpensRequestedConnections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = AcceptConnectionsAsync(listener, 5, cts.Token, TimeSpan.FromMilliseconds(150));

        using var service = new CsConnectivityService(NullLogger<CsConnectivityService>.Instance);
        var result = await service.RunSameTargetParallelTestAsync(
            new CsSameTargetParallelTestRequest("127.0.0.1", port, 5, 5, 3000), cts.Token);

        var maxSimultaneous = await accepted;
        AssertEqual(5, result.Total, "Total");
        AssertEqual(5, result.Success, "Success");
        AssertEqual(0, result.Failure, "Failure");
        AssertEqual(5, maxSimultaneous, "MaxSimultaneous");
    }

    private static async Task ParallelTestKeepsLongConnectionsOpen()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = AcceptConnectionsAsync(listener, 1, cts.Token, TimeSpan.FromMilliseconds(500));

        using var service = new CsConnectivityService(NullLogger<CsConnectivityService>.Instance);
        var result = await service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", port, 1, 1, 3000, 1000), cts.Token);

        var maxSimultaneous = await accepted;
        AssertEqual(1, result.Total, "LongTotal");
        AssertEqual(1, result.Success, "LongSuccess");
        AssertEqual(1, maxSimultaneous, "LongMaxSimultaneous");
    }

    private static async Task ParallelMqttTestPerformsMqttHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var broker = AcceptMqttConnectsAsync(listener, 2, cts.Token);

        using var service = new CsConnectivityService(NullLogger<CsConnectivityService>.Instance);
        var result = await service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", port, 2, 2, 3000, 0, "MQTT"), cts.Token);

        var connectCount = await broker;
        AssertEqual(2, result.Total, "MqttTotal");
        AssertEqual(2, result.Success, "MqttSuccess");
        AssertEqual(0, result.Failure, "MqttFailure");
        AssertEqual(2, connectCount, "MqttConnectPackets");
    }


    private static async Task ParallelMqttTestUsesRequestClientId()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var broker = AcceptMqttConnectsAsync(listener, 1, cts.Token, "ui-client");

        using var service = new CsConnectivityService(NullLogger<CsConnectivityService>.Instance);
        var result = await service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", port, 1, 1, 3000, 0, "MQTT", MqttClientId: "ui-client"), cts.Token);

        var connectCount = await broker;
        AssertEqual(1, result.Success, "MqttRequestClientIdSuccess");
        AssertEqual(1, connectCount, "MqttRequestClientIdConnectPackets");
    }

    private static async Task ParallelMqttTestUsesRequestCredentials()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var broker = AcceptMqttConnectsAsync(listener, 1, cts.Token, "ui-user", "ui-pass");

        using var service = new CsConnectivityService(NullLogger<CsConnectivityService>.Instance);
        var result = await service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", port, 1, 1, 3000, 0, "MQTT",
                MqttUsername: "ui-user", MqttPassword: "ui-pass"), cts.Token);

        var connectCount = await broker;
        AssertEqual(1, result.Success, "MqttRequestCredentialsSuccess");
        AssertEqual(1, connectCount, "MqttRequestCredentialsConnectPackets");
    }

    private static async Task ParallelUdpTestSendsUdpDatagrams()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = ReceiveUdpDatagramsAsync(udp, 1, cts.Token);

        using var service = new CsConnectivityService(NullLogger<CsConnectivityService>.Instance);
        var result = await service.RunParallelTestAsync(
            new CsParallelTestRequest("127.0.0.1", port, 1, 1, 3000, 1000, "UDP"), cts.Token);

        AssertEqual(1, await received, "UdpDatagramsReceived");
        AssertEqual(1, result.Total, "UdpTotal");
        AssertEqual(1, result.Success, "UdpSuccess");
        AssertEqual(0, result.Failure, "UdpFailure");
    }
    private static void ParallelReportServiceGeneratesHtmlReport()
    {
        var service = new CsParallelReportService();
        var report = service.Generate(BuildReportRequest(), "html");
        var html = Encoding.UTF8.GetString(report.Content);

        AssertEqual("text/html; charset=utf-8", report.ContentType, "HtmlContentType");
        AssertContains("<!doctype html>", html, "HtmlDoctype");
        AssertContains("并行连接验证报告", html, "HtmlTitle");
        AssertContains("192.168.1.10", html, "HtmlHost");
        AssertContains("连接拒绝", html, "HtmlFailure");
    }

    private static void ParallelReportServiceGeneratesPdfReport()
    {
        var service = new CsParallelReportService();
        var report = service.Generate(BuildReportRequest(), "pdf");
        var signature = Encoding.ASCII.GetString(report.Content, 0, 8);

        AssertEqual("application/pdf", report.ContentType, "PdfContentType");
        AssertEqual("%PDF-1.4", signature, "PdfSignature");
        var pdfText = Encoding.ASCII.GetString(report.Content);
        AssertContains("5E76884C8FDE63A59A8C8BC162A5544A", pdfText, "PdfChineseTitle");
        AssertContains("/FontFile2", pdfText, "PdfEmbeddedChineseFont");
        AssertNotContains("/STSong-Light", pdfText, "PdfNoExternalCjkFont");
        if (report.Content.Length < 500)
            throw new InvalidOperationException($"PdfSize: expected >= 500, actual {report.Content.Length}");
    }

    private static CsParallelReportRequest BuildReportRequest() => new(
        new CsParallelTestRequest("192.168.1.10", 502, 3, 3, 3000, 0, "ModbusTCP"),
        new CsParallelTestResult(3, 2, 1, 67, 12.5, 25.2,
            new[] { new CsParallelFailure("192.168.1.12", "连接拒绝", "2026-06-30 10:00:00") },
            "2026-06-30 10:00:01"),
        "短连接",
        60,
        "2026-06-30 10:00:01");
    private static async Task<int> ReceiveUdpDatagramsAsync(UdpClient udp, int count, CancellationToken ct)
    {
        var received = 0;
        while (received < count)
        {
            await udp.ReceiveAsync(ct);
            received++;
        }
        return received;
    }
    private static async Task<int> AcceptMqttConnectsAsync(TcpListener listener, int count, CancellationToken ct, params string[] expectedPayloadParts)
    {
        var clients = new List<TcpClient>();
        var connects = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                clients.Add(client);
                var stream = client.GetStream();
                var fixedHeader = stream.ReadByte();
                if ((fixedHeader & 0xF0) != 0x10)
                    throw new InvalidOperationException($"Expected MQTT CONNECT, got 0x{fixedHeader:X2}");
                var multiplier = 1;
                var remainingLength = 0;
                int encoded;
                do
                {
                    encoded = stream.ReadByte();
                    if (encoded < 0) throw new EndOfStreamException();
                    remainingLength += (encoded & 127) * multiplier;
                    multiplier *= 128;
                } while ((encoded & 128) != 0);
                var payload = new byte[remainingLength];
                await stream.ReadExactlyAsync(payload, ct);
                var payloadText = Encoding.ASCII.GetString(payload);
                if (!payloadText.Contains("MQTT"))
                    throw new InvalidOperationException("MQTT protocol name missing");
                foreach (var part in expectedPayloadParts)
                    if (!payloadText.Contains(part))
                        throw new InvalidOperationException($"MQTT CONNECT payload missing {part}");
                await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, 0x00 }, ct);
                connects++;
            }
            return connects;
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }
    private static async Task<int> AcceptConnectionsAsync(
        TcpListener listener, int count, CancellationToken ct, TimeSpan holdBeforeDispose)
    {
        var clients = new List<TcpClient>();
        var maxSimultaneous = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                clients.Add(await listener.AcceptTcpClientAsync(ct));
                maxSimultaneous = Math.Max(maxSimultaneous, clients.Count(x => x.Connected));
            }
            await Task.Delay(holdBeforeDispose, ct);
            maxSimultaneous = Math.Max(maxSimultaneous, clients.Count(x => x.Connected));
            return maxSimultaneous;
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }

    private static void AssertContains(string expected, string actual, string name)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: expected content to contain {expected}");
    }

    private static void AssertNotContains(string unexpected, string actual, string name)
    {
        if (actual.Contains(unexpected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: expected content not to contain {unexpected}");
    }
}
