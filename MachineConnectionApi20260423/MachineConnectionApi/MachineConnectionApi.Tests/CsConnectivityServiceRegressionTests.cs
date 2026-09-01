namespace MachineConnectionApi.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CsConnectivityServiceRegressionTests
{
    public static async Task RunAll()
    {
        ConfigurationSurvivesRestartSafely();
        await AnonymousFtpServerIsRejected();
        await UnauthenticatedFtpDeleteIsRejected();
        await ParallelTestsAreRejectedWhileBusy();
        await ParallelTestBudgetIsEnforced();
        await RunningServerUpdateIsRejected();
        DataSourceLifecycleIsSerialized();
    }

    private static void ConfigurationSurvivesRestartSafely()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-config-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "cs.json");
        try
        {
            using (var first = Create(path))
            {
                first.UpsertGateway(new CsGateway
                {
                    Name = "FTP目标", Ip = "127.0.0.1", Port = 21,
                    Type = "FTP", Username = "user", Password = "secret", Status = "运行中",
                });
                first.UpsertDataSource(new CsDataSource
                {
                    Name = "数据源", GatewayId = first.ListGateways().Single().Id,
                    Status = "启用",
                });
                first.UpsertServer(new CsServerService
                {
                    Name = "FTP服务", Type = "FtpServer", Port = 2121,
                    Username = "server", Password = "secret", Status = "运行中",
                    ClientCount = 5,
                });
            }

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (Directory.EnumerateFiles(directory, "*.tmp").Any())
                throw new InvalidOperationException("C/S configuration temp file was not cleaned");
            using var second = Create(path);
            var gateway = second.ListGateways().Single();
            var dataSource = second.ListDataSources().Single();
            var server = second.ListServers().Single();
            AssertEqual("secret", gateway.Password, "GatewayPassword");
            AssertEqual("停止", gateway.Status, "GatewayStatus");
            AssertEqual("禁用", dataSource.Status, "DataSourceStatus");
            AssertEqual("secret", server.Password, "ServerPassword");
            AssertEqual("停止", server.Status, "ServerStatus");
            AssertEqual(0, server.ClientCount, "ServerClientCount");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AnonymousFtpServerIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-ftp-{Guid.NewGuid():N}");
        try
        {
            using var service = Create(Path.Combine(directory, "cs.json"));
            var server = service.UpsertServer(new CsServerService
            {
                Name = "anonymous", Type = "FtpServer", Port = GetFreePort(),
            });
            try
            {
                await service.StartServerAsync(server.Id);
                throw new InvalidOperationException("Anonymous FTP server was accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("匿名登录默认禁用")) { }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task UnauthenticatedFtpDeleteIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-ftp-auth-{Guid.NewGuid():N}");
        try
        {
            using var service = Create(Path.Combine(directory, "cs.json"));
            var server = service.UpsertServer(new CsServerService
            {
                Name = "protected", Type = "FtpServer", Port = GetFreePort(),
                Username = "user", Password = "secret",
            });
            await service.StartServerAsync(server.Id);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            _ = await reader.ReadLineAsync();
            var command = System.Text.Encoding.ASCII.GetBytes("DELE protected.bin\r\n");
            await stream.WriteAsync(command);
            var response = await reader.ReadLineAsync();
            if (response?.StartsWith("530", StringComparison.Ordinal) != true)
                throw new InvalidOperationException($"Unauthenticated DELE response: {response}");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ParallelTestsAreRejectedWhileBusy()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-parallel-{Guid.NewGuid():N}");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var service = Create(Path.Combine(directory, "cs.json"));
            var first = service.RunParallelTestAsync(
                new CsParallelTestRequest("127.0.0.1", port, 1, 1, 1000, 500),
                CancellationToken.None);
            using var accepted = await listener.AcceptTcpClientAsync();
            try
            {
                await service.RunParallelTestAsync(
                    new CsParallelTestRequest("127.0.0.1", 0, 1, 1, 1000),
                    CancellationToken.None);
                throw new InvalidOperationException("Concurrent parallel test was accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("正在运行")) { }
            await first;
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ParallelTestBudgetIsEnforced()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-budget-{Guid.NewGuid():N}");
        try
        {
            using var service = Create(Path.Combine(directory, "cs.json"));
            try
            {
                // 1000 目标 × 并发 1 × 保持 60s：保持时长累计必然超过 120 秒上限，事前拒绝。
                await service.RunParallelTestAsync(
                    new CsParallelTestRequest("127.0.0.1", 1, 1000, 1, 1000, 60_000),
                    CancellationToken.None);
                throw new InvalidOperationException("Oversized parallel test was accepted");
            }
            catch (ArgumentException ex) when (ex.Message.Contains("120 秒")) { }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunningServerUpdateIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-server-{Guid.NewGuid():N}");
        try
        {
            using var service = Create(Path.Combine(directory, "cs.json"));
            var server = service.UpsertServer(new CsServerService
            {
                Name = "running", Type = "ModbusServer", Port = GetFreePort(),
            });
            await service.StartServerAsync(server.Id);
            try
            {
                service.UpsertServer(new CsServerService
                {
                    Id = server.Id, Name = "changed", Type = server.Type,
                    Port = server.Port,
                });
                throw new InvalidOperationException("Running server update was accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("必须先停止")) { }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void DataSourceLifecycleIsSerialized()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-source-{Guid.NewGuid():N}");
        try
        {
            using var service = Create(Path.Combine(directory, "cs.json"));
            var dataSource = service.UpsertDataSource(new CsDataSource
            {
                Name = "source", GatewayId = "missing", UpdateInterval = 60,
            });
            service.EnableDataSource(dataSource.Id);
            var loops = GetDataSourceLoops(service);
            var firstToken = loops[dataSource.Id].Token;
            service.EnableDataSource(dataSource.Id);
            AssertEqual(1, loops.Count, "EnabledDataSourceLoopCount");
            AssertEqual(true, firstToken.IsCancellationRequested, "FirstLoopCancelled");
            service.UpsertDataSource(new CsDataSource
            {
                Id = dataSource.Id, Name = dataSource.Name,
                GatewayId = dataSource.GatewayId, Status = "启用", UpdateInterval = 30,
            });
            AssertEqual(1, loops.Count, "UpdatedDataSourceLoopCount");
            service.DisableDataSource(dataSource.Id);
            AssertEqual(0, loops.Count, "DisabledDataSourceLoopCount");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        GetDataSourceLoops(CsConnectivityService service) =>
        (System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>)
        typeof(CsConnectivityService).GetField("_dataSourceLoops",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;

    private static CsConnectivityService Create(string path) =>
        new(NullLogger<CsConnectivityService>.Instance, path);

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}
