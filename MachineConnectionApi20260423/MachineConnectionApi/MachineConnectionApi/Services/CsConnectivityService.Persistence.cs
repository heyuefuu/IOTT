namespace MachineConnectionApi.Services;

using System.Text;
using System.Text.Json;
using MachineConnectionApi.Models;

public sealed partial class CsConnectivityService
{
    private static readonly JsonSerializerOptions ConfigurationJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private void LoadConfiguration()
    {
        lock (_configurationGate)
        {
            if (!File.Exists(_configurationPath)) return;
            try
            {
                var json = File.ReadAllText(_configurationPath, Encoding.UTF8);
                var snapshot = JsonSerializer.Deserialize<CsConfigurationSnapshot>(
                    json, ConfigurationJsonOptions) ?? new();
                foreach (var gateway in snapshot.Gateways.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
                {
                    snapshot.GatewayPasswords.TryGetValue(gateway.Id, out var password);
                    gateway.Password = password;
                    gateway.Status = "停止";
                    _gateways[gateway.Id] = gateway;
                }
                foreach (var dataSource in snapshot.DataSources.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
                {
                    dataSource.Status = "禁用";
                    _dataSources[dataSource.Id] = dataSource;
                }
                foreach (var server in snapshot.Servers.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
                {
                    snapshot.ServerPasswords.TryGetValue(server.Id, out var password);
                    server.Password = password;
                    server.Status = "停止";
                    server.ClientCount = 0;
                    server.MaxClients = NormalizeMaxClients(server.MaxClients);
                    _servers[server.Id] = server;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取 C/S 配置失败：{Path}", _configurationPath);
            }
        }
    }

    private void SaveConfigurationLocked()
    {
        var directory = Path.GetDirectoryName(_configurationPath)
            ?? throw new InvalidOperationException("C/S 配置路径缺少目录");
        Directory.CreateDirectory(directory);
        var snapshot = new CsConfigurationSnapshot
        {
            Gateways = _gateways.Values.ToList(),
            DataSources = _dataSources.Values.ToList(),
            Servers = _servers.Values.ToList(),
            GatewayPasswords = _gateways.Values.ToDictionary(x => x.Id, x => x.Password),
            ServerPasswords = _servers.Values.ToDictionary(x => x.Id, x => x.Password),
        };
        var json = JsonSerializer.Serialize(snapshot, ConfigurationJsonOptions);
        var tempPath = Path.Combine(directory,
            $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, _configurationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private sealed class CsConfigurationSnapshot
    {
        public List<CsGateway> Gateways { get; init; } = [];
        public List<CsDataSource> DataSources { get; init; } = [];
        public List<CsServerService> Servers { get; init; } = [];
        public Dictionary<string, string?> GatewayPasswords { get; init; } = [];
        public Dictionary<string, string?> ServerPasswords { get; init; } = [];
    }
}
