using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace IottDevMock.Mock;

// 阶段 3：验证专用证据链（配置快照 + FTP 独占创建/回读往返）。
// 说明：证据为“调试用伪造”，会与本轮冻结目标自洽，但不代表真实设备。
public sealed partial class HostApi
{
    private void MapP3(WebApplication app)
    {
        app.MapGet("/api/program-transfer/{deviceId}/verification/snapshot", (string deviceId) =>
            Find(deviceId) is { } d
                ? Results.Ok(Snapshot(d))
                : Results.NotFound(new { error = "Device/transfer target not found." }));

        app.MapPost("/api/program-transfer/{deviceId}/verification/round-trip", async (string deviceId, HttpRequest req) =>
        {
            if (Find(deviceId) is not { } d) return Results.NotFound(new { error = "Device not found." });

            var form = await req.ReadFormAsync();
            if (!string.Equals(form["allowWrites"].ToString(), "true", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Explicit verification file-write permission is required." });

            var snapshotToken = form["snapshotToken"].ToString();
            var directory = form["directory"].ToString();
            var remoteName = form["remoteName"].ToString();
            var expectedBytes = long.TryParse(form["expectedBytes"].ToString(), out var eb) ? eb : -1;
            var file = form.Files.GetFile("file");
            if (file is null || file.Length != expectedBytes || expectedBytes <= 0)
                return Results.BadRequest(new { error = "Exactly one bounded verification file with matching filename and length is required." });

            byte[] bytes;
            await using (var source = file.OpenReadStream())
            {
                using var ms = new MemoryStream();
                await source.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var seconds = Math.Max(0.01, bytes.Length / (50.0 * 1024 * 1024));
            var transferProtocol = d.Transfer?.Protocol ?? d.Protocol;

            return Results.Ok(new
            {
                supported = true,
                hasEvidence = true,
                success = true,
                integrityVerified = true,
                configurationChanged = false,
                deviceId = d.Id,
                snapshotToken,
                protocol = transferProtocol,
                remoteName,
                remotePath = directory.TrimEnd('/') + "/" + remoteName,
                uploadedBytes = (long)bytes.Length,
                downloadedBytes = (long)bytes.Length,
                uploadSeconds = seconds,
                sourceSha256 = hash,
                readBackSha256 = hash,
                detail = $"IottDevMock 虚拟 {transferProtocol} 独占创建+回读校验完成（{bytes.Length} 字节）",
            });
        });
    }

    private static object Snapshot(MockDevice d)
    {
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("iott-snapshot|" + d.Id)));
        var transferProtocol = d.Transfer?.Protocol ?? d.Protocol;
        return new
        {
            deviceId = d.Id,
            token,
            supported = d.Transfer is not null,
            protocol = transferProtocol,
            primary = new
            {
                protocol = d.Protocol,
                host = d.Host,
                port = d.Port,
                brand = d.Brand,
                model = d.Model,
                username = d.Username,
                connectTimeoutMs = d.ConnectTimeoutMs,
                readTimeoutMs = d.ReadTimeoutMs,
                extendedProperties = d.ExtendedProperties,
                transfer = d.Transfer is null ? null : new
                {
                    protocol = d.Transfer.Protocol,
                    host = d.Transfer.Host,
                    port = d.Transfer.Port,
                    username = (string?)null,
                    connectTimeoutMs = 10000,
                    readTimeoutMs = 5000,
                    extendedProperties = d.Transfer.ExtendedProperties,
                },
            },
        };
    }
}
