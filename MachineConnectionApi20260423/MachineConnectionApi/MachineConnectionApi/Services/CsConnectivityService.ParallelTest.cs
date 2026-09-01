using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MachineConnectionApi.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;

namespace MachineConnectionApi.Services;

/// <summary>
/// CsConnectivityService 并发压测部分：支持多目标并发 TCP 探测，以及同一目标多路 TCP 建连压测。
/// </summary>
public sealed partial class CsConnectivityService
{
    // 防滥用上限：限制单次规模、连接并发和等待时长。
    // MaxConcurrent 同时是验证指标 max-connections 阶梯压测的规模上限（VerifyAutomationService 引用）。
    private const int MaxDeviceCount = 1000;
    internal const int MaxConcurrent = 100;
    private const int MinTimeoutMs = 100;
    private const int MaxTimeoutMs = 30_000;
    private const int MaxHoldMs = 60_000;
    private const int MaxEstimatedTestDurationMs = 120_000;
    private const int SameTargetHoldMs = 300;

    private static int NormalizeTimeout(int value) =>
        Math.Clamp(value <= 0 ? 3000 : value, MinTimeoutMs, MaxTimeoutMs);

    /// <summary>
    /// 事前只按必然消耗的保持时长拒绝（成功连接必须持有 holdMs）；
    /// 连接超时属于最坏情况，不做最坏估算误杀正常配置，由运行时 120 秒截止兜底。
    /// </summary>
    private static void EnsureTestDuration(int targetCount, int concurrent, int holdMs)
    {
        var waves = (targetCount + concurrent - 1) / concurrent;
        var guaranteedMs = (long)waves * holdMs;
        if (guaranteedMs <= MaxEstimatedTestDurationMs) return;
        throw new ArgumentException(
            $"压测保持时长累计至少 {Math.Ceiling(guaranteedMs / 1000d)} 秒，超过 120 秒资源上限；请减少目标或保持时长，或提高并发数");
    }

    /// <summary>压测总时长运行时兜底：超过 120 秒中止并返回明确失败信息（用户主动取消则原样上抛）。</summary>
    private async Task<CsParallelTestResult> RunWithDeadlineAsync(
        Func<CancellationToken, Task<CsParallelTestResult>> run, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(MaxEstimatedTestDurationMs);
        try
        {
            return await run(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CsParallelTestResult(0, 0, 0, 0, 0, 0,
                new[] { new CsParallelFailure("", "压测超过 120 秒资源上限，已中止", Now()) }, Now());
        }
    }

    public async Task<CsParallelTestResult> RunParallelTestAsync(CsParallelTestRequest request, CancellationToken ct)
    {
        if (!await _parallelTestGate.WaitAsync(0, ct))
            throw new CsParallelTestBusyException();
        try { return await RunWithDeadlineAsync(token => RunParallelTestCoreAsync(request, token), ct); }
        finally { _parallelTestGate.Release(); }
    }

    private async Task<CsParallelTestResult> RunParallelTestCoreAsync(
        CsParallelTestRequest request, CancellationToken ct)
    {
        var deviceCount = Math.Clamp(request.DeviceCount, 1, MaxDeviceCount);
        var concurrent = Math.Clamp(request.ConcurrentCount <= 0 ? 10 : request.ConcurrentCount, 1, MaxConcurrent);
        var port = request.Port;
        var timeoutMs = NormalizeTimeout(request.TimeoutMs);
        var holdMs = Math.Clamp(request.HoldMs, 0, MaxHoldMs);
        var effectiveHoldMs = IsMqtt(request.Protocol) || IsUdp(request.Protocol) ? 0 : holdMs;
        EnsureTestDuration(deviceCount, concurrent, effectiveHoldMs);

        if (port is <= 0 or > 65535)
            return new CsParallelTestResult(0, 0, 0, 0, 0, 0,
                new[] { new CsParallelFailure(request.StartIp ?? "", "端口无效", Now()) }, Now());

        if (IsMqtt(request.Protocol))
            return await RunParallelMqttConnectsAsync(request, port, deviceCount, concurrent, timeoutMs, ct);

        if (IsUdp(request.Protocol))
            return await RunParallelUdpDatagramsAsync(request, port, deviceCount, concurrent, timeoutMs, ct);

        if (!TryParseIpv4(request.StartIp, out var startValue))
            return new CsParallelTestResult(0, 0, 0, 0, 0, 0,
                new[] { new CsParallelFailure(request.StartIp ?? "", "起始 IP 无效", Now()) }, Now());

        var targets = new List<ParallelProbeTarget>(deviceCount);
        for (var i = 0; i < deviceCount; i++)
        {
            var ip = UIntToIpv4(startValue + (uint)i);
            targets.Add(new ParallelProbeTarget(ip, ip, port));
        }

        return holdMs > 0
            ? await RunHeldTcpConnectionsAsync(targets, concurrent, timeoutMs, holdMs, ct)
            : await RunParallelProbeTargetsAsync(targets, concurrent, timeoutMs, ct);
    }

    public async Task<CsParallelTestResult> RunSameTargetParallelTestAsync(
        CsSameTargetParallelTestRequest request, CancellationToken ct, TimeSpan? gateWait = null)
    {
        if (!await _parallelTestGate.WaitAsync(gateWait ?? TimeSpan.Zero, ct))
            throw new CsParallelTestBusyException();
        try { return await RunWithDeadlineAsync(token => RunSameTargetParallelTestCoreAsync(request, token), ct); }
        finally { _parallelTestGate.Release(); }
    }

    private async Task<CsParallelTestResult> RunSameTargetParallelTestCoreAsync(
        CsSameTargetParallelTestRequest request, CancellationToken ct)
    {
        var connectionCount = Math.Clamp(request.ConnectionCount <= 0 ? 1 : request.ConnectionCount, 1, MaxConcurrent);
        var concurrent = Math.Clamp(request.ConcurrentCount <= 0 ? connectionCount : request.ConcurrentCount, 1, MaxConcurrent);
        var timeoutMs = NormalizeTimeout(request.TimeoutMs);
        EnsureTestDuration(connectionCount, concurrent, SameTargetHoldMs);

        if (string.IsNullOrWhiteSpace(request.Host) || request.Port is <= 0 or > 65535)
            return new CsParallelTestResult(0, 0, 0, 0, 0, 0,
                new[] { new CsParallelFailure(request.Host ?? "", "主机或端口无效", Now()) }, Now());

        var targets = Enumerable.Range(1, connectionCount)
            .Select(i => new ParallelProbeTarget($"{request.Host}:{request.Port}#{i}", request.Host, request.Port))
            .ToList();

        return await RunHeldTcpConnectionsAsync(targets, concurrent, timeoutMs, SameTargetHoldMs, ct);
    }

    private async Task<CsParallelTestResult> RunParallelMqttConnectsAsync(
        CsParallelTestRequest request, int port, int connectionCount, int concurrent, int timeoutMs, CancellationToken ct)
    {
        var host = request.StartIp;
        if (string.IsNullOrWhiteSpace(host))
            return new CsParallelTestResult(0, 0, 0, 0, 0, 0,
                new[] { new CsParallelFailure("", "Broker Host 不能为空", Now()) }, Now());

        var targets = Enumerable.Range(1, connectionCount)
            .Select(i => new ParallelProbeTarget($"{host}:{port}#{i}", host.Trim(), port))
            .ToList();
        var rtts = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<CsParallelFailure>();
        var success = 0;
        using var sem = new SemaphoreSlim(concurrent);

        var tasks = targets.Select(async target =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await ProbeMqttAsync(target.Host, target.Port, timeoutMs, request, ct);
                if (r.Success)
                {
                    Interlocked.Increment(ref success);
                    rtts.Add(r.RttMs);
                }
                else
                {
                    failures.Add(new CsParallelFailure(target.Label, r.Message, r.Timestamp));
                }
            }
            catch (Exception ex)
            {
                failures.Add(new CsParallelFailure(target.Label, ex.Message, Now()));
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return BuildParallelResult(targets.Count, success, rtts, failures, concurrent);
    }

    private static Task<CsProbeResult> ProbeMqttAsync(
        string host, int port, int timeoutMs, CsParallelTestRequest request, CancellationToken ct) =>
        ProbeMqttCoreAsync(host, port, timeoutMs, request.MqttUseTls,
            request.MqttUsername, request.MqttPassword, request.MqttClientId, ct);

    /// <summary>真实 MQTT CONNECT/CONNACK 握手探测；压测与网关探测共用。</summary>
    internal static async Task<CsProbeResult> ProbeMqttCoreAsync(
        string host, int port, int timeoutMs, bool useTls,
        string? username, string? password, string? clientId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var client = new MqttFactory().CreateMqttClient();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            var effectiveClientId = string.IsNullOrWhiteSpace(clientId)
                ? $"MachineConnectionApi-Test-{Guid.NewGuid():N}"
                : clientId.Trim();
            var builder = new MqttClientOptionsBuilder()
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithClientId(effectiveClientId)
                .WithTcpServer(host, port);
            if (useTls)
                builder = builder.WithTlsOptions(o => o.UseTls());
            if (!string.IsNullOrWhiteSpace(username))
                builder = builder.WithCredentials(username, password ?? "");
            var options = builder.Build();
            await client.ConnectAsync(options, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            await client.DisconnectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return new CsProbeResult(true, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"MQTT 连接成功 {host}:{port}", Now());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"MQTT 连接超时（>{timeoutMs}ms）", Now());
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"MQTT 连接失败：{ex.Message}", Now());
        }
    }

    private async Task<CsParallelTestResult> RunParallelUdpDatagramsAsync(
        CsParallelTestRequest request, int port, int deviceCount, int concurrent, int timeoutMs, CancellationToken ct)
    {
        if (!TryParseIpv4(request.StartIp, out var startValue))
            return new CsParallelTestResult(0, 0, 0, 0, 0, 0,
                new[] { new CsParallelFailure(request.StartIp ?? "", "起始 IP 无效", Now()) }, Now());

        var targets = new List<ParallelProbeTarget>(deviceCount);
        for (var i = 0; i < deviceCount; i++)
        {
            var ip = UIntToIpv4(startValue + (uint)i);
            targets.Add(new ParallelProbeTarget(ip, ip, port));
        }

        var rtts = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<CsParallelFailure>();
        var success = 0;
        using var sem = new SemaphoreSlim(concurrent);

        var tasks = targets.Select(async target =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await ProbeUdpAsync(target.Host, target.Port, timeoutMs, ct);
                if (r.Success)
                {
                    Interlocked.Increment(ref success);
                    rtts.Add(r.RttMs);
                }
                else
                {
                    failures.Add(new CsParallelFailure(target.Label, r.Message, r.Timestamp));
                }
            }
            catch (Exception ex)
            {
                failures.Add(new CsParallelFailure(target.Label, ex.Message, Now()));
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return BuildParallelResult(targets.Count, success, rtts, failures, concurrent);
    }

    /// <summary>
    /// UDP 无连接，发送成功不代表对端可达。发送后在超时窗口内尝试收回包：
    /// 收到回包 → 确认可达；触发 ICMP 端口不可达（SocketException）→ 判定失败；
    /// 静默超时 → 保守记成功，但消息明确标注"仅确认本地发送，未收到应答"。
    /// </summary>
    private static async Task<CsProbeResult> ProbeUdpAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
            return new CsProbeResult(false, 0, "主机或端口无效", Now());

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            using var udp = new UdpClient();
            udp.Connect(host, port); // Connect 后才能收到 ICMP 端口不可达对应的 SocketException
            var payload = System.Text.Encoding.ASCII.GetBytes("MachineConnectionApi UDP probe");
            await udp.SendAsync(payload, timeoutCts.Token);
            try
            {
                await udp.ReceiveAsync(timeoutCts.Token);
                sw.Stop();
                return new CsProbeResult(true, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                    $"UDP 收到回包，确认 {host}:{port} 可达", Now());
            }
            catch (SocketException se) when (se.SocketErrorCode is SocketError.ConnectionReset)
            {
                sw.Stop();
                return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                    $"UDP 端口不可达（ICMP Port Unreachable）{host}:{port}", Now());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                return new CsProbeResult(true, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                    $"UDP 已发送 {host}:{port}，{timeoutMs}ms 内无应答（仅确认本地发送，无法证实对端接收）", Now());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"UDP 发送超时（>{timeoutMs}ms）", Now());
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CsProbeResult(false, Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                $"UDP 发送失败：{ex.Message}", Now());
        }
    }
    private async Task<CsParallelTestResult> RunParallelProbeTargetsAsync(
        IReadOnlyList<ParallelProbeTarget> targets, int concurrent, int timeoutMs, CancellationToken ct)
    {
        var rtts = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<CsParallelFailure>();
        var success = 0;
        using var sem = new SemaphoreSlim(concurrent);

        var tasks = targets.Select(async target =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await ProbeTcpAsync(target.Host, target.Port, timeoutMs, ct);
                if (r.Success)
                {
                    Interlocked.Increment(ref success);
                    rtts.Add(r.RttMs);
                }
                else
                {
                    failures.Add(new CsParallelFailure(target.Label, r.Message, r.Timestamp));
                }
            }
            catch (Exception ex)
            {
                failures.Add(new CsParallelFailure(target.Label, ex.Message, Now()));
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return BuildParallelResult(targets.Count, success, rtts, failures, concurrent);
    }

    private async Task<CsParallelTestResult> RunHeldTcpConnectionsAsync(
        IReadOnlyList<ParallelProbeTarget> targets, int concurrent, int timeoutMs, int holdMs, CancellationToken ct)
    {
        var rtts = new ConcurrentBag<double>();
        var failures = new ConcurrentBag<CsParallelFailure>();
        var success = 0;
        using var sem = new SemaphoreSlim(concurrent);

        var tasks = targets.Select(async target =>
        {
            await sem.WaitAsync(ct);
            try
            {
                using var client = new TcpClient();
                var sw = Stopwatch.StartNew();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                await client.ConnectAsync(target.Host, target.Port, timeoutCts.Token);
                sw.Stop();
                Interlocked.Increment(ref success);
                rtts.Add(Math.Round(sw.Elapsed.TotalMilliseconds, 1));
                await Task.Delay(holdMs, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failures.Add(new CsParallelFailure(target.Label, $"连接超时（>{timeoutMs}ms）", Now()));
            }
            catch (Exception ex)
            {
                failures.Add(new CsParallelFailure(target.Label, ex.Message, Now()));
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return BuildParallelResult(targets.Count, success, rtts, failures, concurrent);
    }

    private CsParallelTestResult BuildParallelResult(
        int total, int success, ConcurrentBag<double> rtts, ConcurrentBag<CsParallelFailure> failures, int concurrent)
    {
        var failure = total - success;
        var successRate = total > 0 ? (int)Math.Round(success * 100.0 / total) : 0;
        var avgRtt = rtts.IsEmpty ? 0 : Math.Round(rtts.Average(), 1);
        var maxRtt = rtts.IsEmpty ? 0 : Math.Round(rtts.Max(), 1);
        var failureList = failures.OrderBy(f => f.DeviceIp).Take(100).ToList();

        _logger.LogInformation(
            "并发压测完成：目标={Total} 成功={Success} 失败={Failure} 并发={Concurrent}",
            total, success, failure, concurrent);

        return new CsParallelTestResult(
            total, success, failure, successRate, avgRtt, maxRtt, failureList, Now());
    }

    private static bool IsMqtt(string? protocol) =>
        string.Equals(protocol, "MQTT", StringComparison.OrdinalIgnoreCase);

    private static bool IsUdp(string? protocol) =>
        string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseIpv4(string? ip, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(ip, out var addr) ||
            addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var bytes = addr.GetAddressBytes();
        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    private static string UIntToIpv4(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    private sealed record ParallelProbeTarget(string Label, string Host, int Port);
}
