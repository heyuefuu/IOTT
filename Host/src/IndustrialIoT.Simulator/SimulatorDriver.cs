namespace IndustrialIoT.Simulator;

using System.Text;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;

[ProtocolDriver(ProtocolType.Simulator, "*", Priority = -1000)]
public class SimulatorDriver : IProtocolDriver, IAddressSpaceBrowser, INCProgramTransfer
{
    private readonly ILogger<SimulatorDriver> _logger;
    private readonly SimulationProfile _profile;
    private ConnectionState _state = ConnectionState.Disconnected;
    private readonly Random _rand = new();

    public ProtocolType Protocol => ProtocolType.Simulator;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write
        | DriverCapabilities.Browse | DriverCapabilities.BatchRead | DriverCapabilities.FileTransfer;

    public bool SupportsResume => true;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public SimulatorDriver(ILogger<SimulatorDriver> logger, SimulationProfile? profile = null)
    {
        _logger = logger;
        _profile = profile ?? SimulationProfile.Default;
    }

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        var oldState = _state;
        _state = ConnectionState.Connecting;
        StateChanged?.Invoke(this, new() { OldState = oldState, NewState = _state });

        // Simulate connection latency
        await Task.Delay(_rand.Next(_profile.MinLatencyMs, _profile.MaxLatencyMs), ct);

        // Simulate occasional connection failures
        if (_rand.NextDouble() < _profile.FailureProbability)
        {
            _state = ConnectionState.Faulted;
            StateChanged?.Invoke(this, new() { OldState = ConnectionState.Connecting, NewState = _state, Reason = "Simulated failure" });
            return new() { Success = false, ErrorMessage = "Simulated connection failure" };
        }

        _state = ConnectionState.Connected;
        StateChanged?.Invoke(this, new() { OldState = ConnectionState.Connecting, NewState = _state });
        _logger.LogInformation("Simulator connected to {Host}:{Port}", config.Host, config.Port);
        return new() { Success = true };
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        var old = _state;
        _state = ConnectionState.Disconnected;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = _state });
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct = default) =>
        Task.FromResult(_state == ConnectionState.Connected);

    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        await Task.Delay(_rand.Next(1, _profile.MaxLatencyMs / 2), ct);
        return GenerateTagValue(address, dataType);
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        await Task.Delay(_rand.Next(2, _profile.MaxLatencyMs), ct);
        return requests.Select(r => GenerateTagValue(r.Address, r.DataType)).ToList();
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        await Task.Delay(_rand.Next(1, _profile.MaxLatencyMs / 2), ct);
        _logger.LogDebug("Simulator write: {Address} = {Value} ({DataType})", address, value, dataType);
        return new() { Success = true };
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        IReadOnlyList<AddressNode> nodes = _profile.DeviceType switch
        {
            DeviceType.CNC => GetCNCAddressSpace(parentPath),
            DeviceType.PLC => GetPLCAddressSpace(parentPath),
            DeviceType.Robot => GetRobotAddressSpace(parentPath),
            _ => GetCNCAddressSpace(parentPath)
        };
        return Task.FromResult(nodes);
    }

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,DataType,Readable,Writable");
        var nodes = BrowseAsync(null, ct).Result;
        foreach (var node in FlattenNodes(nodes))
            sb.AppendLine($"{node.Path},{node.DataType},{node.IsReadable},{node.IsWritable}");
        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        return Task.FromResult(stream);
    }

    public async Task<TransferProgressResult> UploadProgramAsync(Stream source, NCProgramMetadata metadata, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var totalBytes = metadata.FileSize ?? source.Length;
        long transferred = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new byte[4096];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            transferred += read;
            await Task.Delay(1, ct); // simulate transfer time
            progress?.Report(new() { BytesTransferred = transferred, TotalBytes = totalBytes });
        }
        sw.Stop();
        return new() { Success = true, TransferId = Guid.NewGuid().ToString("N"), BytesTransferred = transferred, Duration = sw.Elapsed };
    }

    public async Task<TransferProgressResult> DownloadProgramAsync(string remotePath, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var totalBytes = 1024L * _rand.Next(1, 100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = new byte[totalBytes];
        _rand.NextBytes(data);
        for (long i = 0; i < totalBytes; i += 4096)
        {
            var chunk = (int)Math.Min(4096, totalBytes - i);
            await destination.WriteAsync(data.AsMemory((int)i, chunk), ct);
            progress?.Report(new() { BytesTransferred = i + chunk, TotalBytes = totalBytes });
            await Task.Delay(1, ct);
        }
        sw.Stop();
        return new() { Success = true, TransferId = Guid.NewGuid().ToString("N"), BytesTransferred = totalBytes, Duration = sw.Elapsed };
    }

    public Task<TransferProgressResult> ResumeUploadAsync(string transferId, string remotePath, Stream source, long offset, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
        => UploadProgramAsync(source, new() { FileName = "resume", RemotePath = "/resume" }, progress, ct);

    public ValueTask DisposeAsync()
    {
        if (_state == ConnectionState.Connected)
            _state = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    // === Private helpers ===

    private TagValue GenerateTagValue(string address, DataType dataType)
    {
        object value = dataType switch
        {
            DataType.Bool => _rand.Next(2) == 1,
            DataType.Int16 => (short)_rand.Next(-1000, 1000),
            DataType.Int32 => _rand.Next(-100000, 100000),
            DataType.Int64 => (long)_rand.Next(-100000, 100000),
            DataType.UInt16 => (ushort)_rand.Next(0, 65535),
            DataType.UInt32 => (uint)_rand.Next(0, 100000),
            DataType.Float => (float)(_rand.NextDouble() * 1000),
            DataType.Double => _rand.NextDouble() * 10000,
            DataType.String => $"SIM_{address}_{_rand.Next(100)}",
            DataType.ByteArray => new byte[] { (byte)_rand.Next(256), (byte)_rand.Next(256) },
            _ => 0
        };
        return new()
        {
            Address = address, DataType = dataType, Value = value,
            Quality = _rand.NextDouble() < 0.98 ? TagQuality.Good : TagQuality.Uncertain,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private static IReadOnlyList<AddressNode> GetCNCAddressSpace(string? parent) =>
    [
        new() { Path = "Spindle/Speed", DisplayName = "主轴转速", NodeType = AddressNodeType.Variable, DataType = DataType.Float, IsReadable = true, IsWritable = false },
        new() { Path = "Spindle/Load", DisplayName = "主轴负载", NodeType = AddressNodeType.Variable, DataType = DataType.Float, IsReadable = true, IsWritable = false },
        new() { Path = "Axis/X/Position", DisplayName = "X轴位置", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Axis/Y/Position", DisplayName = "Y轴位置", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Axis/Z/Position", DisplayName = "Z轴位置", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Feed/Rate", DisplayName = "进给速度", NodeType = AddressNodeType.Variable, DataType = DataType.Float, IsReadable = true, IsWritable = true },
        new() { Path = "Program/Number", DisplayName = "程序号", NodeType = AddressNodeType.Variable, DataType = DataType.Int32, IsReadable = true, IsWritable = true },
        new() { Path = "Program/Status", DisplayName = "运行状态", NodeType = AddressNodeType.Variable, DataType = DataType.Int16, IsReadable = true, IsWritable = false },
        new() { Path = "Alarm/Active", DisplayName = "报警激活", NodeType = AddressNodeType.Variable, DataType = DataType.Bool, IsReadable = true, IsWritable = false },
        new() { Path = "Alarm/Code", DisplayName = "报警代码", NodeType = AddressNodeType.Variable, DataType = DataType.Int32, IsReadable = true, IsWritable = false },
    ];

    private static IReadOnlyList<AddressNode> GetPLCAddressSpace(string? parent) =>
    [
        new() { Path = "D0", DisplayName = "D0 数据寄存器", NodeType = AddressNodeType.Variable, DataType = DataType.Int16, IsReadable = true, IsWritable = true },
        new() { Path = "D100", DisplayName = "D100 数据寄存器", NodeType = AddressNodeType.Variable, DataType = DataType.Int16, IsReadable = true, IsWritable = true },
        new() { Path = "M0", DisplayName = "M0 辅助继电器", NodeType = AddressNodeType.Variable, DataType = DataType.Bool, IsReadable = true, IsWritable = true },
        new() { Path = "M100", DisplayName = "M100 辅助继电器", NodeType = AddressNodeType.Variable, DataType = DataType.Bool, IsReadable = true, IsWritable = true },
        new() { Path = "W0", DisplayName = "W0 字寄存器", NodeType = AddressNodeType.Variable, DataType = DataType.UInt16, IsReadable = true, IsWritable = true },
        new() { Path = "CIO0", DisplayName = "CIO0 I/O通道", NodeType = AddressNodeType.Variable, DataType = DataType.UInt16, IsReadable = true, IsWritable = false },
    ];

    private static IReadOnlyList<AddressNode> GetRobotAddressSpace(string? parent) =>
    [
        new() { Path = "Joint/J1", DisplayName = "J1关节角度", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Joint/J2", DisplayName = "J2关节角度", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Joint/J3", DisplayName = "J3关节角度", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Joint/J4", DisplayName = "J4关节角度", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Joint/J5", DisplayName = "J5关节角度", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Joint/J6", DisplayName = "J6关节角度", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "TCP/X", DisplayName = "TCP X坐标", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "TCP/Y", DisplayName = "TCP Y坐标", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "TCP/Z", DisplayName = "TCP Z坐标", NodeType = AddressNodeType.Variable, DataType = DataType.Double, IsReadable = true, IsWritable = false },
        new() { Path = "Status/Running", DisplayName = "运行状态", NodeType = AddressNodeType.Variable, DataType = DataType.Bool, IsReadable = true, IsWritable = false },
        new() { Path = "Status/Alarm", DisplayName = "报警信息", NodeType = AddressNodeType.Variable, DataType = DataType.Int32, IsReadable = true, IsWritable = false },
    ];

    private static IEnumerable<AddressNode> FlattenNodes(IReadOnlyList<AddressNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (n.Children != null)
                foreach (var child in FlattenNodes(n.Children))
                    yield return child;
        }
    }
}
