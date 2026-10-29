using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using MachineConnectionApi.Models;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private sealed record ProbeSettings(byte Unit = 1, ushort Address = 0, ushort Count = 1, string Path = "/data");
    private static ProbeSettings Settings(string? configuration) => string.IsNullOrWhiteSpace(configuration)
        ? new() : JsonSerializer.Deserialize<ProbeSettings>(configuration, ConfigurationJsonOptions)
            ?? throw new ArgumentException("Probe configuration is empty");

    private async Task<CsProbeResult> ProbeApplicationAsync(CsGateway gateway, string? configuration, int timeoutMs, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var settings = Settings(configuration);
            var detail = gateway.Type.ToUpperInvariant() switch
            {
                "MODBUS" => await ReadModbusAsync(gateway, settings, timeout.Token),
                "REST" => await ReadRestAsync(gateway, settings, timeout.Token),
                "MQTT" => await ReadMqttAsync(gateway, timeout.Token),
                "OPCUA" or "OPC UA" => await ReadOpcUaAsync(gateway, configuration, timeout.Token),
                _ => throw new NotSupportedException($"{gateway.Type} 尚未接入应用协议验证，请使用真实协议适配器；仅TCP建连不能判定通过"),
            };
            return new(true, Math.Round(watch.Elapsed.TotalMilliseconds, 1), detail, Now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return new(false, Math.Round(watch.Elapsed.TotalMilliseconds, 1), $"应用协议验证失败：{error.Message}", Now());
        }
    }

    private static async Task<string> ReadModbusAsync(CsGateway gateway, ProbeSettings settings, CancellationToken ct)
    {
        if (settings.Count is < 1 or > 125 || (int)settings.Address + settings.Count > 65536)
            throw new ArgumentException("Modbus count must be 1..125 and address + count <= 65536");
        using var client = new TcpClient();
        await client.ConnectAsync(gateway.Ip, gateway.Port, ct);
        await using var stream = client.GetStream();
        var transaction = (ushort)Random.Shared.Next(1, 65536);
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        request[5] = 6;
        request[6] = settings.Unit;
        request[7] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), settings.Address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), settings.Count);
        await stream.WriteAsync(request, ct);
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, ct);
        var length = CsModbusProtocol.Read(header, 4);
        if (CsModbusProtocol.Read(header, 0) != transaction || CsModbusProtocol.Read(header, 2) != 0 ||
            header[6] != settings.Unit || length is < 3 or > 254) throw new IOException("Invalid Modbus response header");
        var response = new byte[length - 1];
        await stream.ReadExactlyAsync(response, ct);
        if (response[0] == 0x83) throw new IOException($"Modbus exception {response[1]}");
        if (response[0] != 3 || response.Length != settings.Count * 2 + 2 || response[1] != settings.Count * 2)
            throw new IOException("Invalid Modbus function, length or byte count");
        var values = Enumerable.Range(0, settings.Count).Select(index => CsModbusProtocol.Read(response, index * 2 + 2));
        return $"Modbus FC03 unit={settings.Unit} address={settings.Address} values=[{string.Join(',', values)}]";
    }

    private static async Task<string> ReadRestAsync(CsGateway gateway, ProbeSettings settings, CancellationToken ct)
    {
        if (!settings.Path.StartsWith('/') || settings.Path.StartsWith("//")) throw new ArgumentException("REST path must be a relative absolute-path");
        var uri = new UriBuilder("http", gateway.Ip, gateway.Port) { Path = settings.Path }.Uri;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new IOException("REST response exceeds 1 MiB");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (content.Length + count > 1024 * 1024) throw new IOException("REST response exceeds 1 MiB");
            content.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(content.ToArray());
        if (json.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new IOException("REST response must contain a JSON object or array");
        return $"REST GET {settings.Path}: HTTP {(int)response.StatusCode}, JSON {content.Length} bytes";
    }
}
