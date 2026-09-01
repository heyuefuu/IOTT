namespace IndustrialIoT.Protocols.Modbus;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

/// <summary>
/// PROFIBUS-DP 通讯验证驱动 —— 经 PROFIBUS↔以太网网关接入。
///
/// PROFIBUS-DP 是 RS-485 现场总线，无法纯软件直连；现场标准做法是用
/// PROFIBUS-to-Ethernet 网关（如 Hilscher netTAP / 西门子 IE-PB-Link /
/// Anybus 等）把 DP 从站的 I/O 数据镜像到 Modbus-TCP 寄存器区，上位机经
/// 网关的以太网口用 Modbus-TCP 读写。本驱动即面向这一部署模型：复用经过
/// 验证的 Modbus-TCP 栈（HslCommunication），对外标识为 PROFIBUS 协议。
///
/// 部署前提：
///   1. 现场已用 PROFIBUS 网关把目标 DP 从站桥接为 Modbus-TCP（默认端口 502）；
///   2. <see cref="DeviceConnectionConfig.Host"/>/<c>Port</c> 指向网关以太网口；
///   3. 地址沿用 Modbus 寻址（HR/IR/C/DI + 偏移），具体寄存器映射由网关的
///      DP 从站组态决定，需与网关配置表一致。
///
/// 若现场网关桥接的是 PROFINET 而非 Modbus-TCP，则不适用本驱动，应另走
/// OPC UA / PROFINET 路径。
/// </summary>
[ProtocolDriver(ProtocolType.Profibus, "Profibus", "Profibus-DP", "DP")]
public sealed class ProfibusDriver : IProtocolDriver, IAddressSpaceBrowser
{
    private readonly ModbusTcpDriver _inner;
    private readonly ILogger<ProfibusDriver> _logger;

    public ProfibusDriver(ILogger<ProfibusDriver> logger, ILogger<ModbusTcpDriver> innerLogger)
    {
        _logger = logger;
        _inner = new ModbusTcpDriver(innerLogger);
        _inner.StateChanged += OnInnerStateChanged;
    }

    public ProtocolType Protocol => ProtocolType.Profibus;
    public ConnectionState State => _inner.State;
    public DriverCapabilities Capabilities => _inner.Capabilities;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "PROFIBUS-DP via Modbus-TCP gateway at {Host}:{Port}", config.Host, config.Port);
        return _inner.ConnectAsync(config, ct);
    }

    public Task DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);

    public Task<bool> PingAsync(CancellationToken ct = default) => _inner.PingAsync(ct);

    public Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
        => _inner.ReadTagAsync(address, dataType, ct);

    public Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
        => _inner.ReadTagsAsync(requests, ct);

    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        => _inner.WriteTagAsync(address, dataType, value, ct);

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
        => _inner.BrowseAsync(parentPath, ct);

    public Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
        => _inner.ExportAddressSpaceAsync(format, ct);

    public async ValueTask DisposeAsync()
    {
        _inner.StateChanged -= OnInnerStateChanged;
        await _inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void OnInnerStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        => StateChanged?.Invoke(this, e);
}
