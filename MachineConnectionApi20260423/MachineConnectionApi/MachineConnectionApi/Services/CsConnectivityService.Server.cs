using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

/// <summary>CsConnectivityService 服务端部分：真实 TcpListener 监听、接受连接、回显并统计。</summary>
public sealed partial class CsConnectivityService
{
    private const int MaxServerClients = 200;
    private const int MaxFtpFileCountPerServer = 100;
    private const int MaxFtpFileBytes = 32 * 1024 * 1024;
    private const long MaxFtpStoredBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan FtpControlIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FtpDataTransferTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FtpAuthFailureDelay = TimeSpan.FromSeconds(2);

    private static int NormalizeMaxClients(int value) =>
        Math.Clamp(value <= 0 ? 100 : value, 1, MaxServerClients);

    private static bool IsFtpServer(string? type) =>
        string.Equals(type, "FtpServer", StringComparison.OrdinalIgnoreCase);

    /// <summary>单个服务端运行态：监听器、取消源与活动连接表。</summary>
    private sealed class ServerRuntime
    {
        public required TcpListener Listener { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required SemaphoreSlim ConnectionSlots { get; init; }
        public ConcurrentDictionary<string, CsServerConnection> Connections { get; } = new();
        /// <summary>FtpServer 模式的内存文件区（文件名→内容）。</summary>
        public ConcurrentDictionary<string, byte[]> FtpFiles { get; } = new();
    }

    public IReadOnlyList<CsServerService> ListServers() => _servers.Values.OrderBy(s => s.Name).ToList();

    public CsServerService UpsertServer(CsServerService server)
    {
        lock (_configurationGate)
        {
            if (string.IsNullOrWhiteSpace(server.Id)) server.Id = NewId("svc");
            if (_serverRuntimes.ContainsKey(server.Id))
                throw new InvalidOperationException("运行中的服务端必须先停止，再修改配置");
            if (_servers.TryGetValue(server.Id, out var current) &&
                string.IsNullOrWhiteSpace(server.Password))
                server.Password = current.Password;
            server.MaxClients = NormalizeMaxClients(server.MaxClients);
            server.Status = "停止";
            server.ClientCount = 0;
            _servers[server.Id] = server;
            SaveConfigurationLocked();
            return server;
        }
    }

    public bool DeleteServer(string id)
    {
        // 停止与删配置同锁完成，避免与 StartServerAsync 交错产生"配置已删但监听器仍在跑"的孤儿。
        lock (_configurationGate)
        {
            StopServer(id);
            var removed = _servers.TryRemove(id, out _);
            if (removed) SaveConfigurationLocked();
            return removed;
        }
    }

    public async Task<bool> StartServerAsync(string id)
    {
        // 校验配置存在 → 建监听器 → 登记运行态需要原子完成（与 Upsert/Delete 互斥）。
        lock (_configurationGate)
        {
            if (!_servers.TryGetValue(id, out var svc)) return false;
            if (IsFtpServer(svc.Type) &&
                (string.IsNullOrWhiteSpace(svc.Username) || string.IsNullOrEmpty(svc.Password)))
                throw new InvalidOperationException(
                    "FTP 服务端必须配置非空用户名和密码，匿名登录默认禁用");
            svc.MaxClients = NormalizeMaxClients(svc.MaxClients);
            if (_serverRuntimes.ContainsKey(id)) return true; // 已在运行

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Any, svc.Port);
                listener.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "服务端 {Id} 启动失败 端口={Port}", id, svc.Port);
                svc.Status = "停止";
                throw new InvalidOperationException($"端口 {svc.Port} 监听失败：{ex.Message}", ex);
            }

            var runtime = new ServerRuntime
            {
                Listener = listener,
                Cts = new CancellationTokenSource(),
                ConnectionSlots = new SemaphoreSlim(svc.MaxClients, svc.MaxClients),
            };
            _serverRuntimes[id] = runtime;
            svc.Status = "运行中";
            svc.ClientCount = 0;
            if (IsFtpServer(svc.Type))
                _ = FtpAcceptLoopAsync(svc, runtime);
            else
                _ = AcceptLoopAsync(svc, runtime);
        }
        await Task.CompletedTask;
        return true;
    }

    public bool StopServer(string id)
    {
        if (_serverRuntimes.TryRemove(id, out var rt))
        {
            rt.Cts.Cancel();
            lock (_ftpStorageGate)
            {
                var released = rt.FtpFiles.Values.Sum(x => x.LongLength);
                rt.FtpFiles.Clear();
                _ftpStoredBytes = Math.Max(0, _ftpStoredBytes - released);
            }
            try { rt.Listener.Stop(); } catch { /* ignore */ }
            rt.Cts.Dispose();
        }
        if (_servers.TryGetValue(id, out var svc))
        {
            svc.Status = "停止";
            svc.ClientCount = 0;
        }
        return true;
    }

    public IReadOnlyList<CsServerConnection> GetServerConnections(string id) =>
        _serverRuntimes.TryGetValue(id, out var rt)
            ? rt.Connections.Values.ToList()
            : Array.Empty<CsServerConnection>();

    private async Task AcceptLoopAsync(CsServerService svc, ServerRuntime runtime)
    {
        var ct = runtime.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await runtime.Listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "服务端 {Id} 接受连接异常", svc.Id);
                break;
            }

            if (!runtime.ConnectionSlots.Wait(0))
            {
                client.Close();
                continue;
            }
            _ = HandleClientAsync(svc, runtime, client, ct);
        }
    }

    private async Task HandleClientAsync(CsServerService svc, ServerRuntime runtime, TcpClient client, CancellationToken ct)
    {
        var connId = Guid.NewGuid().ToString("N");
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connectedAt = Now();
        runtime.Connections[connId] = new CsServerConnection(remote, connectedAt, 0);
        svc.ClientCount = runtime.Connections.Count;
        svc.LastAccess = connectedAt;

        var buffer = new byte[4096];
        long total = 0;
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    runtime.Connections[connId] = new CsServerConnection(remote, connectedAt, total);
                    await stream.WriteAsync(buffer.AsMemory(0, n), ct); // echo 回显，便于客户端验证往返
                }
            }
        }
        catch (OperationCanceledException) { /* 停止或客户端断开 */ }
        catch (Exception ex) { _logger.LogDebug(ex, "服务端 {Id} 连接 {Remote} 处理结束", svc.Id, remote); }
        finally
        {
            runtime.Connections.TryRemove(connId, out _);
            svc.ClientCount = runtime.Connections.Count;
            runtime.ConnectionSlots.Release();
        }
    }

    private void StopAllServers()
    {
        foreach (var id in _serverRuntimes.Keys.ToList()) StopServer(id);
    }
}
