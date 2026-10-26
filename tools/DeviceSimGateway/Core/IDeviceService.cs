namespace DeviceSimGateway.Core;

/// <summary>一个可开关的协议服务端（严格按对应数控/PLC 协议实现）。</summary>
public interface IDeviceService
{
    string Key { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>协议默认端口。</summary>
    int DefaultPort { get; }
    /// <summary>本协议是否已实现（未实现者仅作扩展占位，不监听端口）。</summary>
    bool Implemented { get; }
    ServiceState State { get; }
    string Message { get; }
    int Port { get; }

    Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
