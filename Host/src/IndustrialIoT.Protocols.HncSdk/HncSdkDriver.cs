namespace IndustrialIoT.Protocols.HncSdk;

using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.HncSdk, "华中数控", "HNC", "HNC-8", "HNC-808", "HNC-818", "HNC-848", "HNC-848Di")]
public sealed class HncSdkDriver : IProtocolDriver, IAddressSpaceBrowser, IProgramFileBrowser, INCProgramTransfer
{
    private readonly ILogger<HncSdkDriver> logger;
    private readonly IHncSdkClient? injectedClient;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IHncSdkClient? client;
    private HncSdkShimProcess? shimProcess;
    private HncStaticMetadata? staticMetadata;
    private DeviceConnectionConfig? config;
    private string sessionId = "";
    private ConnectionState state = ConnectionState.Disconnected;

    public HncSdkDriver(ILogger<HncSdkDriver> logger, IHncSdkClient? client = null)
    {
        this.logger = logger;
        injectedClient = client;
    }

    public ProtocolType Protocol => ProtocolType.HncSdk;
    public ConnectionState State => state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Write |
        DriverCapabilities.Browse | DriverCapabilities.BatchRead |
        DriverCapabilities.FileTransfer;
    public bool SupportsResume => false;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (state == ConnectionState.Connected) return new() { Success = true };
            this.config = config;
            SetState(ConnectionState.Connecting);

            var baseUrl = config.ExtendedProperties.GetValueOrDefault("ShimBaseUrl") ?? HncSdkShimProcess.DefaultBaseUrl;
            if (injectedClient is null && ShouldAutoStartShim(config))
            {
                // 先快速探测：Shim 可能已被外部托管（service / 手动启动 / 上次没退干净）。
                var alreadyAlive = await ProbeShimAsync(baseUrl, TimeSpan.FromSeconds(1), ct);
                if (!alreadyAlive)
                {
                    var shimPath = ResolveShimPath(config) ?? HncSdkShimProcess.ResolveDefaultPath()
                        ?? throw new InvalidOperationException(
                            $"HNC SDK shim is not reachable at {baseUrl} and {HncSdkShimProcess.ExecutableName} was not found in conventional locations. " +
                            $"Set ExtendedProperties[\"ShimPath\"] to the shim executable, publish the shim next to the host, " +
                            $"or set AutoStartShim=false and start the shim externally on {baseUrl}.");
                    shimProcess = HncSdkShimProcess.Start(baseUrl, shimPath);
                }
            }

            client = injectedClient ?? new HncSdkIpcClient(new Uri(baseUrl));
            await WaitForShimReadyAsync(baseUrl, config.ConnectTimeout, ct);

            var adapterIp = config.ExtendedProperties.GetValueOrDefault("LocalIp") ?? "127.0.0.1";
            var adapterPort = TryOptInt(config, "LocalPort") ?? 10001;

            var request = new HncSdkConnectRequest(
                config.Host,
                config.Port > 0 ? config.Port : 10001,
                adapterIp,
                adapterPort,
                config.ExtendedProperties.GetValueOrDefault("ClientName") ?? "IndustrialIoT",
                (int)config.ConnectTimeout.TotalMilliseconds);

            var result = await client.ConnectAsync(request, ct);
            if (result.ReturnCode != 0 || string.IsNullOrEmpty(result.SessionId))
                throw new InvalidOperationException(result.ErrorMessage ?? $"HNC SDK connect failed: {result.ReturnCode}");

            sessionId = result.SessionId;
            await TryLoadStaticMetadataAsync(ct);
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            var message = $"HNC SDK shim connection failed: {ex.Message}";
            logger.LogError(ex, "{Message}", message);
            SetState(ConnectionState.Faulted, message);
            return new() { Success = false, ErrorMessage = message };
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(sessionId) && client is not null)
                await client.DisconnectAsync(sessionId, ct);
            sessionId = "";
            SetState(ConnectionState.Disconnected);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (state != ConnectionState.Connected || client is null) return false;
        try { return (await client.PingAsync(sessionId, ct)).ReturnCode == 0; }
        catch { return false; }
    }

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        EnsureConnected();
        var result = await client!.ReadAsync(new(sessionId, address, dataType.ToString()), ct);
        if (result.ReturnCode != 0)
            return Bad(address, dataType, result.ErrorMessage ?? $"HNC SDK read failed: {result.ReturnCode}");

        return Good(address, dataType, DecodeValue(result.Value, dataType));
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        var values = new List<TagValue>(requests.Count);
        foreach (var request in requests)
            values.Add(await ReadTagAsync(request.Address, request.DataType, ct));
        return values;
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        var result = await client!.WriteAsync(new(sessionId, address, dataType.ToString(), Convert.ToString(value) ?? ""), ct);
        return new() { Success = result.ReturnCode == 0, ErrorMessage = result.ErrorMessage };
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
        => Task.FromResult(HncSdkAddressSpace.Browse(parentPath, staticMetadata));

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder("Path,DisplayName,DataType,Readable,Writable\n");
        foreach (var group in HncSdkAddressSpace.Browse(null, staticMetadata))
        foreach (var node in HncSdkAddressSpace.Browse(group.Path, staticMetadata))
            sb.AppendLine($"{node.Path},{node.DisplayName},{node.DataType},{node.IsReadable},{node.IsWritable}");
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public async Task<IReadOnlyList<ProgramFileEntry>> BrowseFilesAsync(string? path = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var result = await client!.BrowseFilesAsync(new(sessionId, path), ct);
        return result.Value?.Select(x => new ProgramFileEntry
        {
            Path = x.Path, Name = x.Name, IsDirectory = x.IsDirectory,
            SizeBytes = x.SizeBytes, CanDownload = !x.IsDirectory,
            CanUpload = x.IsDirectory, HasChildren = x.IsDirectory,
        }).ToArray() ?? [];
    }

    public async Task<TransferProgressResult> UploadProgramAsync(Stream source, NCProgramMetadata metadata, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var id = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var temp = Path.GetTempFileName();
        try
        {
            await using (var file = File.Create(temp)) await source.CopyToAsync(file, ct);
            var remote = BuildRemotePath(metadata.RemotePath, metadata.FileName);
            var result = await client!.UploadAsync(new(sessionId, remote, temp), ct);
            var bytes = new FileInfo(temp).Length;
            progress?.Report(new() { BytesTransferred = bytes, TotalBytes = metadata.FileSize ?? bytes });
            return new() { Success = result.ReturnCode == 0, TransferId = id, BytesTransferred = bytes,
                Duration = sw.Elapsed, ErrorMessage = result.ErrorMessage };
        }
        catch (Exception ex) { return new() { Success = false, TransferId = id, Duration = sw.Elapsed, ErrorMessage = ex.Message }; }
        finally { TryDelete(temp); }
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(string remotePath, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var id = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var temp = Path.GetTempFileName();
        try
        {
            var result = await client!.DownloadAsync(new(sessionId, remotePath, temp), ct);
            if (result.ReturnCode != 0) return new() { Success = false, TransferId = id, Duration = sw.Elapsed, ErrorMessage = result.ErrorMessage };
            await using var file = File.OpenRead(temp);
            await file.CopyToAsync(destination, ct);
            progress?.Report(new() { BytesTransferred = file.Length, TotalBytes = file.Length });
            return new() { Success = true, TransferId = id, BytesTransferred = file.Length, Duration = sw.Elapsed };
        }
        catch (Exception ex) { return new() { Success = false, TransferId = id, Duration = sw.Elapsed, ErrorMessage = ex.Message }; }
        finally { TryDelete(temp); }
    }

    public Task<TransferProgressResult> ResumeUploadAsync(string transferId, string remotePath, Stream source, long offset, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => Task.FromResult(new TransferProgressResult { Success = false, TransferId = transferId, BytesTransferred = offset, ErrorMessage = "HNC SDK FTP transfer does not support resume upload." });

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        shimProcess?.Dispose();
        shimProcess = null;
        staticMetadata = null;
        gate.Dispose();
    }

    private static async Task<bool> ProbeShimAsync(string baseUrl, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = timeout };
        try
        {
            using var rsp = await http.GetAsync("/health", ct);
            return rsp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task WaitForShimReadyAsync(string baseUrl, TimeSpan connectTimeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + (connectTimeout > TimeSpan.Zero ? connectTimeout : TimeSpan.FromSeconds(10));
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMilliseconds(800) };
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var rsp = await http.GetAsync("/health", ct);
                if (rsp.IsSuccessStatusCode) return;
            }
            catch (Exception ex) { last = ex; }
            try { await Task.Delay(200, ct); } catch { return; }
        }
        throw new TimeoutException($"HNC SDK shim at {baseUrl} did not respond on /health within {deadline - DateTime.UtcNow + TimeSpan.FromSeconds(10)}: {last?.Message}");
    }

    private async Task TryLoadStaticMetadataAsync(CancellationToken ct)
    {
        try
        {
            var chanNum = Math.Max(1, await ReadIntMetaAsync("sys:CHAN_NUM", 1, ct));
            var axisCounts = new List<int>(chanNum);
            for (var ch = 0; ch < chanNum; ch++)
            {
                var maskLo = await ReadIntMetaAsync($"chan:AXES_MASK:{ch}:0", 0, ct);
                var maskHi = await ReadIntMetaAsync($"chan:AXES_MASK1:{ch}:0", 0, ct);
                var count = BitOperations.PopCount((uint)maskLo) + BitOperations.PopCount((uint)maskHi);
                axisCounts.Add(Math.Max(1, count));
            }
            staticMetadata = new HncStaticMetadata(chanNum, axisCounts);
            logger.LogInformation("HNC static metadata loaded: pathCount={Path} axes=[{Axes}]",
                chanNum, string.Join(",", axisCounts));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HNC static metadata load failed; address browse falls back to single-channel defaults");
            staticMetadata = null;
        }
    }

    private async Task<int> ReadIntMetaAsync(string address, int fallback, CancellationToken ct)
    {
        try
        {
            var result = await client!.ReadAsync(new(sessionId, address, "Int32"), ct);
            return result.ReturnCode == 0 ? AsInt(result.Value, fallback) : fallback;
        }
        catch { return fallback; }
    }

    private static int AsInt(object? raw, int fallback)
    {
        if (raw is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) return v;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
            return fallback;
        }
        return raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var v) => v,
            _ => fallback,
        };
    }

    private static object DecodeValue(object? raw, DataType dataType)
    {
        if (raw is null) return DefaultForType(dataType);
        if (raw is not JsonElement el) return raw;

        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return DefaultForType(dataType);
            case JsonValueKind.True:
                return dataType == DataType.String ? "true" : (object)true;
            case JsonValueKind.False:
                return dataType == DataType.String ? "false" : (object)false;
            case JsonValueKind.String:
                return ConvertString(el.GetString() ?? "", dataType);
            case JsonValueKind.Number:
                return ConvertNumber(el, dataType);
            default:
                return el.ToString();
        }
    }

    private static object DefaultForType(DataType dataType) => dataType == DataType.String ? "" : 0;

    private static object ConvertString(string s, DataType dataType) => dataType switch
    {
        DataType.String => s,
        DataType.Bool => bool.TryParse(s, out var b) ? b : s == "1",
        DataType.Int16 => short.TryParse(s, out var v) ? v : (short)0,
        DataType.Int32 => int.TryParse(s, out var v) ? v : 0,
        DataType.Int64 => long.TryParse(s, out var v) ? v : 0L,
        DataType.UInt16 => ushort.TryParse(s, out var v) ? v : (ushort)0,
        DataType.UInt32 => uint.TryParse(s, out var v) ? v : 0u,
        DataType.Float => float.TryParse(s, out var v) ? v : 0f,
        DataType.Double => double.TryParse(s, out var v) ? v : 0d,
        _ => s,
    };

    private static object ConvertNumber(JsonElement el, DataType dataType) => dataType switch
    {
        DataType.String => el.ToString(),
        DataType.Bool => el.TryGetInt64(out var b) && b != 0,
        DataType.Int16 => el.TryGetInt16(out var v) ? v : (short)el.GetDouble(),
        DataType.Int32 => el.TryGetInt32(out var v) ? v : (int)el.GetDouble(),
        DataType.Int64 => el.TryGetInt64(out var v) ? v : (long)el.GetDouble(),
        DataType.UInt16 => el.TryGetUInt16(out var v) ? v : (ushort)el.GetDouble(),
        DataType.UInt32 => el.TryGetUInt32(out var v) ? v : (uint)el.GetDouble(),
        DataType.Float => el.TryGetSingle(out var v) ? v : (float)el.GetDouble(),
        DataType.Double => el.GetDouble(),
        _ => el.ToString(),
    };

    private static bool ShouldAutoStartShim(DeviceConnectionConfig config)
        => !config.ExtendedProperties.TryGetValue("AutoStartShim", out var value)
           || !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    private static string? ResolveShimPath(DeviceConnectionConfig config)
        => config.ExtendedProperties.GetValueOrDefault("ShimPath");
    private static int? TryOptInt(DeviceConnectionConfig config, string key)
        => config.ExtendedProperties.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : null;
    private void EnsureConnected()
    {
        if (state != ConnectionState.Connected || client is null || string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("HNC SDK driver is not connected.");
    }
    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = state; if (old == next) return;
        state = next;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = next, Reason = reason });
    }
    private static TagValue Good(string address, DataType dataType, object value)
        => new() { Address = address, DataType = dataType, Value = value, Quality = TagQuality.Good, Timestamp = DateTimeOffset.UtcNow };
    private static TagValue Bad(string address, DataType dataType, string error)
        => new() { Address = address, DataType = dataType, Value = dataType == DataType.String ? "" : 0, Quality = TagQuality.Bad, Timestamp = DateTimeOffset.UtcNow, ErrorMessage = error };
    private static string BuildRemotePath(string remotePath, string fileName)
        => string.IsNullOrWhiteSpace(remotePath) ? fileName : $"{remotePath.TrimEnd('/', '\\')}/{fileName}";
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
