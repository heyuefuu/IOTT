using System.Text.Json;
using IottDevMock.Mock;

namespace IottDevMock.Seed;

/// <summary>生成网关 App_Data/devices.json（与网关 DeviceSnapshot 结构一致）。</summary>
public static class SeedWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(ArgMap opt)
    {
        var root = ResolveRoot(opt.Get("root", ""));
        if (root is null) { Console.Error.WriteLine("未找到仓库根（应含 Host/IndustrialIoT.sln）。用 --root 指定。"); return 1; }

        var seed = opt.GetInt("seed", 42);
        var count = opt.GetInt("devices", 30);
        var scenario = opt.Get("scenario", "Normal");
        var devices = Catalog.Build(seed, count, scenario).Select(ToSeed).ToList();
        var targets = Targets(root).ToList();

        if (opt.Has("dry-run"))
        {
            Console.WriteLine($"[dry-run] {devices.Count} 台设备（seed={seed}）将写入:");
            foreach (var t in targets) Console.WriteLine($"  - {t}");
            return 0;
        }

        foreach (var target in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.Copy(target, target + ".bak", overwrite: true);
            File.WriteAllText(target, JsonSerializer.Serialize(devices, Json));
            Console.WriteLine($"已写入 {target}");
        }
        Console.WriteLine($"  {devices.Count} 台设备（seed={seed}，原文件备份为 devices.json.bak）");
        Console.WriteLine("  提示：把网关 appsettings.Local.json 的 IndustrialIoT:BaseUrl 指向 Mock Host，或先启动 serve。");
        if (opt.Has("verify"))
            VerifySeed.WriteAll(root, opt.Has("large"));
        return 0;
    }

    public static int Clean(ArgMap opt)
    {
        var root = ResolveRoot(opt.Get("root", ""));
        if (root is null) { Console.Error.WriteLine("未找到仓库根。"); return 1; }
        var removed = 0;
        foreach (var target in Targets(root))
        {
            if (File.Exists(target)) { File.Delete(target); Console.WriteLine($"已删除 {target}"); removed++; }
        }
        if (removed == 0) Console.WriteLine("无需删除（未找到生成的 devices.json）");
        Console.WriteLine("提示：如需恢复，可把同目录的 devices.json.bak 改名回 devices.json。");
        return 0;
    }

    private static IEnumerable<string> Targets(string root)
    {
        var gwDir = Path.Combine(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi");
        yield return Path.Combine(gwDir, "App_Data", "devices.json");
        var bin = Path.Combine(gwDir, "bin");
        if (Directory.Exists(bin))
            foreach (var dir in Directory.GetDirectories(bin, "App_Data", SearchOption.AllDirectories))
                yield return Path.Combine(dir, "devices.json");
    }

    private static string? ResolveRoot(string hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && Directory.Exists(hint)) return Path.GetFullPath(hint);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Host", "IndustrialIoT.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    private static SeedDevice ToSeed(MockDevice d) => new(
        d.Id, d.Name, d.Type, d.Brand, d.Model, d.Protocol, "Offline", d.Host, d.Port, d.Username,
        d.ConnectTimeoutMs, d.ReadTimeoutMs, d.ExtendedProperties,
        d.Transfer is null ? null : new SeedTransfer(d.Transfer.Protocol, d.Transfer.Host, d.Transfer.Port, null, 10000, 5000, d.Transfer.ExtendedProperties),
        d.CreatedAt, null, true, null, false);

    private sealed record SeedDevice(
        string Id, string Name, string Type, string Brand, string Model, string Protocol, string Status,
        string Host, int Port, string? Username, int ConnectTimeoutMs, int ReadTimeoutMs,
        Dictionary<string, string> ExtendedProperties, SeedTransfer? Transfer,
        DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool? UpstreamSynced, string? UpstreamError, bool RestoredFromUpstream);

    private sealed record SeedTransfer(
        string Protocol, string Host, int Port, string? Username, int ConnectTimeoutMs, int ReadTimeoutMs,
        Dictionary<string, string> ExtendedProperties);
}
