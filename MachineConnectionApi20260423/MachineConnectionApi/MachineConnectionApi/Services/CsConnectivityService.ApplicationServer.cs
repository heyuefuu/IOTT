using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MachineConnectionApi.Services;

public sealed partial class CsConnectivityService
{
    private static async Task ServeModbusAsync(NetworkStream stream, ServerRuntime runtime, CancellationToken ct)
    {
        var header = new byte[7];
        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, ct);
            var length = CsModbusProtocol.Read(header, 4);
            if (CsModbusProtocol.Read(header, 2) != 0 || length is < 2 or > 254)
                throw new IOException("Invalid Modbus MBAP header");
            var request = new byte[length - 1];
            await stream.ReadExactlyAsync(request, ct);
            var response = CsModbusProtocol.Respond(request, runtime.Registers, runtime.Coils, runtime.InputRegisters, runtime.DiscreteInputs);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)(response.Length + 1));
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(response, ct);
        }
    }

    private static async Task ServeRestAsync(NetworkStream stream, ServerRuntime runtime, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        ct = timeout.Token;
        var header = new List<byte>();
        var next = new byte[1];
        var complete = false;
        while (header.Count < 16384)
        {
            await stream.ReadExactlyAsync(next, ct);
            header.Add(next[0]);
            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) { complete = true; break; }
        }
        if (!complete) throw new IOException("HTTP headers incomplete or exceed 16 KiB");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || !request[1].StartsWith('/') || request[2] is not ("HTTP/1.0" or "HTTP/1.1"))
            throw new IOException("Invalid HTTP request");
        var uri = new Uri("http://localhost" + request[1]);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var status = "200 OK";
        object payload;
        lock (runtime.Registers)
        {
            if (request[0] == "GET" && uri.AbsolutePath == "/health") payload = new { status = "ok", protocol = "REST" };
            else if (request[0] == "GET" && uri.AbsolutePath == "/data")
                payload = new { registers = runtime.Registers.Take(128).ToArray(), timestamp = DateTimeOffset.UtcNow };
            else if (request[0] == "PUT" && uri.AbsolutePath == "/data" &&
                ushort.TryParse(query["address"], out var address) && ushort.TryParse(query["value"], out var value))
            {
                runtime.Registers[address] = value;
                payload = new { address, value };
            }
            else { status = "400 Bad Request"; payload = new { error = "Use GET /data, GET /health or PUT /data?address=0&value=1" }; }
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, ct);
        await stream.WriteAsync(body, ct);
    }
}
