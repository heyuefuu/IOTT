namespace DeviceSimGateway.Services;

using DeviceSimGateway.Core;

/// <summary>尚未实现的协议占位（仅出现在列表中，标明扩展点，不监听端口）。</summary>
public sealed class PlaceholderService : IDeviceService
{
    public string Key { get; }
    public string Name { get; }
    public string Description { get; }
    public int DefaultPort { get; }
    public bool Implemented => false;
    public ServiceState State => ServiceState.NotImplemented;
    public string Message => "未实现（扩展占位）";
    public int Port { get; private set; }

    public PlaceholderService(string key, string name, string description, int defaultPort)
    {
        Key = key; Name = name; Description = description; DefaultPort = defaultPort; Port = defaultPort;
    }

    public Task StartAsync(int port, SimDataStore data, GeneratorConfig gen, CancellationToken ct)
    {
        Port = port; return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
