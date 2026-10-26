using System.Text.Json;
using System.Text.Json.Nodes;

namespace IottDevMock.Seed;

/// <summary>
/// P4 种子：为网关 /verify 端到端跑分生成 verify-tasks.json、评价指标补丁（automation+files）、
/// 测试附件（evaluation-attachments）与 cs-connectivity.json。
/// </summary>
public static class VerifySeed
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed record SeedFile(string Id, string Name, string Size, long SizeBytes);

    public static void WriteAll(string root, bool large)
    {
        var dirs = AppDataDirs(root).ToList();
        if (dirs.Count == 0) { Console.WriteLine("[verify] 未找到网关 App_Data 目录，跳过"); return; }

        var sizes = new List<double> { 0.1, 1, 10, 20 };
        if (large) sizes.Add(200);
        var files = new List<SeedFile>();
        foreach (var size in sizes.Distinct())
            files.Add(CreateAttachment(dirs, size));

        PatchIndicators(root, dirs, files);
        WriteAll(dirs, "verify-tasks.json", BuildTasks(files));
        WriteAll(dirs, "cs-connectivity.json", BuildCsConfig());

        Console.WriteLine($"[verify] 已写入 verify-tasks.json / evaluation-indicators.json / cs-connectivity.json 与 {files.Count} 个附件" + (large ? "（含 200MiB）" : "（--large 可加 200MiB）"));
    }

    // ---------- 附件 ----------

    private static SeedFile CreateAttachment(List<string> dirs, double sizeMiB)
    {
        var sizeBytes = (long)Math.Round(sizeMiB * 1024 * 1024);
        var id = Guid.NewGuid().ToString("N");
        var name = $"verify-{sizeMiB:0.###}MiB.nc";
        var display = $"{sizeMiB:0.##} MiB";

        var buffer = new byte[8192];
        new Random(unchecked((int)(sizeBytes & 0x7fffffff)) ^ 0x5A5A).NextBytes(buffer);
        var written = 0L;
        foreach (var dir in dirs)
        {
            var attachDir = Path.Combine(dir, "evaluation-attachments");
            Directory.CreateDirectory(attachDir);
            var binPath = Path.Combine(attachDir, id + ".bin");
            using (var fs = new FileStream(binPath, FileMode.Create, FileAccess.Write))
            {
                while (written < sizeBytes)
                {
                    var chunk = (int)Math.Min(buffer.Length, sizeBytes - written);
                    fs.Write(buffer, 0, chunk);
                    written += chunk;
                }
            }
            written = 0;
            var meta = new { id, name, size = display, sizeBytes };
            File.WriteAllText(Path.Combine(attachDir, id + ".json"), JsonSerializer.Serialize(meta, Json));
        }
        return new SeedFile(id, name, display, sizeBytes);
    }

    // ---------- 指标补丁 ----------

    private static void PatchIndicators(string root, List<string> dirs, List<SeedFile> files)
    {
        var source = Path.Combine(dirs[0], "evaluation-indicators.json");
        if (!File.Exists(source))
        {
            var seed = Path.Combine(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi", "SeedData", "evaluation-indicators.json");
            if (!File.Exists(seed)) { Console.WriteLine($"[verify] 未找到指标源文件，跳过补丁: {seed}"); return; }
            source = seed;
        }

        var array = JsonNode.Parse(File.ReadAllText(source))!.AsArray();
        foreach (var cfg in array)
        {
            foreach (var section in cfg?["indicators"]?.AsArray() ?? new JsonArray())
                foreach (var sub in section?["children"]?.AsArray() ?? new JsonArray())
                    foreach (var item in sub?["items"]?.AsArray() ?? new JsonArray())
                    {
                        if (item is not JsonObject obj) continue;
                        var metricId = obj["metricId"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(metricId)) continue;
                        PatchItem(obj, metricId!, files);
                    }
        }
        WriteAll(dirs, "evaluation-indicators.json", array);
    }

    private static void PatchItem(JsonObject item, string metricId, List<SeedFile> files)
    {
        var automation = AutomationFor(metricId);
        var test = automation["test"]!.AsObject();
        switch (metricId)
        {
            case "industrial-protocol":
                item["protocols"] = new JsonArray("OpcUa");
                test["readAddress"] = "/Objects/CNC/Static/AxisCount";
                test["readDataType"] = "Int32";
                break;
            case "communication-stability":
                test["durationMinutes"] = 1;
                test["sampleIntervalSeconds"] = 5;
                test["readAddress"] = "/Objects/CNC/Static/AxisCount";
                test["readDataType"] = "Int32";
                break;
            case "max-connections":
                test["maxConnections"] = 4;
                test["concurrencyMode"] = "devices";
                test["readAddress"] = "/Objects/CNC/Static/AxisCount";
                test["readDataType"] = "Int32";
                break;
            case "transfer-protocol":
                item["protocols"] = new JsonArray("FTP");
                test["targetDirectory"] = "/Programs";
                break;
            case "file-integrity":
                item["files"] = FilesNode(files, [0.1, 1, 10, 20]);
                test["targetDirectory"] = "/Programs";
                break;
            case "transfer-speed":
                item["files"] = FilesNode(files, [10]);
                test["targetDirectory"] = "/Programs";
                break;
            case "file-size":
                item["files"] = FilesNode(files, [1, 10, 200]);
                test["targetDirectory"] = "/Programs";
                break;
        }
        item["automation"] = automation;
    }

    private static JsonArray FilesNode(List<SeedFile> files, double[] sizesMiB)
    {
        var array = new JsonArray();
        foreach (var f in files)
        {
            var size = Math.Round(f.SizeBytes / 1024d / 1024d, 3);
            if (!sizesMiB.Any(s => Math.Abs(s - size) <= 0.001)) continue;
            array.Add(new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["size"] = f.Size, ["sizeBytes"] = f.SizeBytes });
        }
        return array;
    }

    /// <summary>镜像 EvaluationAutomationRules.DefaultFor。</summary>
    private static JsonObject AutomationFor(string metricId)
    {
        var rule = metricId switch
        {
            "industrial-protocol" => Rule("linear", "%", []),
            "communication-stability" => Rule("bands", "min", [(30, 100), (20, 80), (10, 60), (0, 0)]),
            "max-connections" => Rule("bands", "count", [(4, 100), (3, 80), (2, 60), (0, 0)]),
            "transfer-protocol" => Rule("bands", "count", [(2, 100), (1, 60), (0, 0)]),
            "file-integrity" => Rule("bands", "%", [(100, 100), (75, 80), (50, 60), (0, 0)]),
            "transfer-speed" => Rule("bands", "MB/s", [(10, 100), (5, 80), (2, 60), (0, 0)]),
            "file-size" => Rule("bands", "MB", [(200, 100), (10, 80), (1, 60), (0, 0)]),
            _ => Rule("bands", "count", [(0, 0)]),
        };
        return rule;
    }

    private static JsonObject Rule(string scoringMode, string unit, (double Min, double Score)[] bands)
    {
        var scoreBands = new JsonArray();
        foreach (var (min, score) in bands)
            scoreBands.Add(new JsonObject { ["min"] = min, ["score"] = score });
        return new JsonObject
        {
            ["unit"] = unit,
            ["scoringMode"] = scoringMode,
            ["scoreBands"] = scoreBands,
            ["test"] = new JsonObject
            {
                ["durationMinutes"] = 30,
                ["sampleIntervalSeconds"] = 5,
                ["maxConnections"] = 4,
                ["failureLimit"] = 3,
                ["concurrencyMode"] = "devices",
                ["readAddress"] = "",
                ["readDataType"] = "String",
                ["targetDirectory"] = "/",
            },
        };
    }

    // ---------- verify-tasks ----------

    private static object[] BuildTasks(List<SeedFile> files)
    {
        var now = DateTimeOffset.UtcNow;
        object Task(string id, string name, string[] metricIds, int concurrency) => new
        {
            id,
            name,
            type = "automation",
            status = "pending",
            priority = "中",
            deviceId = "cnc-opcua-01",
            machineId = "cnc-opcua-01",
            evaluationCategory = "machine",
            metricIds,
            options = new
            {
                communicationRounds = 3,
                probeTimeoutMs = 3000,
                maxParallelTargets = 50,
                requiredMinConcurrentSuccess = 0,
                concurrentDeviceIds = new[] { "cnc-opcua-01", "plc-opcua-01", "plc-modbus-01", "plc-s7-01" }
                    .Take(concurrency).ToArray(),
                allowFileWrites = true,
            },
            currentRunJson = "",
            activeRunId = (string?)null,
            @params = "",
            description = "IottDevMock 生成的验证任务",
            createdAt = now,
            completedAt = (DateTimeOffset?)null,
            executionTime = "",
            result = "",
            detail = "",
            scheduleType = "none",
            scheduleTime = "",
            lastAutoRunAt = (DateTimeOffset?)null,
            lastRunJson = "",
        };

        return
        [
            Task("mock-comm", "Mock 通讯能力验证", ["industrial-protocol", "communication-stability", "max-connections", "transfer-protocol"], 4),
            Task("mock-files", "Mock 文件传输验证", ["file-integrity", "transfer-speed", "file-size"], 1),
        ];
    }

    // ---------- cs-connectivity ----------

    private static object BuildCsConfig() => new
    {
        gateways = new[]
        {
            new { id = "mock-gw-1", name = "Mock网关", ip = "127.0.0.1", port = 502, type = "Modbus",
                  username = (string?)null, remotePath = "/", description = "IottDevMock", status = "停止", lastHeartbeat = (DateTimeOffset?)null },
        },
        dataSources = new[]
        {
            new { id = "mock-ds-1", name = "Mock采集", type = "Modbus", gatewayId = "mock-gw-1",
                  config = "", updateInterval = 5, status = "禁用", lastUpdate = (DateTimeOffset?)null, lastResult = (string?)null },
        },
        servers = new[]
        {
            new { id = "mock-srv-1", name = "Mock服务", type = "ModbusServer", rootDirectory = "/",
                  username = (string?)null, port = 502, description = "IottDevMock", maxClients = 100, status = "停止",
                  clientCount = 0, lastAccess = (DateTimeOffset?)null },
        },
        gatewayPasswords = new Dictionary<string, string?> { ["mock-gw-1"] = null },
        serverPasswords = new Dictionary<string, string?> { ["mock-srv-1"] = null },
    };

    // ---------- 落盘 ----------

    private static IEnumerable<string> AppDataDirs(string root)
    {
        var gwDir = Path.Combine(root, "MachineConnectionApi20260423", "MachineConnectionApi", "MachineConnectionApi");
        yield return Path.Combine(gwDir, "App_Data");
        var bin = Path.Combine(gwDir, "bin");
        if (Directory.Exists(bin))
            foreach (var dir in Directory.GetDirectories(bin, "App_Data", SearchOption.AllDirectories))
                yield return dir;
    }

    private static void WriteAll(List<string> dirs, string fileName, object content)
    {
        var text = JsonSerializer.Serialize(content, Json);
        foreach (var dir in dirs)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path, text);
        }
    }
}
