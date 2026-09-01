using System.Net.Http.Json;
using System.Text;
using MachineConnectionApi.Models;
using MachineConnectionApi.Services;
using Microsoft.AspNetCore.Mvc;
using MachineConnectionApi.Proxy;

namespace MachineConnectionApi.Controllers;

/// <summary>
/// 设备管理 Web API：本地 devices.json 为 UI 主数据，增删改同步镜像到上游 Industrial IoT 设备库
/// （上游按 deviceId 解析驱动，两侧必须一致）；连接测试优先走上游驱动级握手，失败回退 TCP 端口探测。
/// </summary>
[ApiController]
[Route("api/devices")]
public class DevicesController : IndustrialIoTProxyControllerBase
{
    private readonly IDeviceStore _store;
    private readonly IDeviceUpstreamSyncService _sync;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISystemActivityLog _activityLog;
    private readonly ILogger<DevicesController> _logger;

    public DevicesController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IDeviceStore store,
        IDeviceUpstreamSyncService sync,
        ISystemActivityLog activityLog,
        ILogger<DevicesController> logger)
        : base(httpClientFactory, logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _store = store;
        _sync = sync;
        _activityLog = activityLog;
        _logger = logger;
    }

    private string DevicesPath => _configuration["IndustrialIoT:DevicesPath"] ?? "api/Devices";

    /// <summary>获取设备列表，可按 type 过滤（CNC / PLC / Robot）</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<MachineDeviceDto>> List([FromQuery] string? type)
    {
        var rows = _store.ReadAll();
        if (!string.IsNullOrWhiteSpace(type))
            rows = rows.Where(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        return Ok(rows.OrderByDescending(x => x.CreatedAt).ToList());
    }

    /// <summary>获取单个设备</summary>
    [HttpGet("{id}")]
    public ActionResult<MachineDeviceDto> GetById(string id)
    {
        var item = _store.ReadAll().FirstOrDefault(x => x.Id == id);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>创建设备（本地保存并同步注册到上游）</summary>
    [HttpPost]
    public async Task<ActionResult<MachineDeviceDto>> Create([FromBody] MachineDeviceUpsertRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return BadRequest(new { error = "Name 不能为空" });
        if (string.IsNullOrWhiteSpace(input.Type)) return BadRequest(new { error = "Type 不能为空（CNC / PLC / Robot）" });
        if (string.IsNullOrWhiteSpace(input.Protocol)) return BadRequest(new { error = "Protocol 不能为空" });
        if (string.IsNullOrWhiteSpace(input.Host)) return BadRequest(new { error = "Host 不能为空" });
        if (input.Port is not (> 0 and <= 65535)) return BadRequest(new { error = "Port 必须是 1~65535" });

        var item = new MachineDeviceDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = input.Name,
            Type = input.Type,
            Brand = input.Brand ?? string.Empty,
            Model = input.Model ?? string.Empty,
            Protocol = input.Protocol,
            Status = "Offline",
            Host = input.Host,
            Port = input.Port.Value,
            Username = input.Username,
            ConnectTimeoutMs = input.ConnectTimeoutMs ?? 10000,
            ReadTimeoutMs = input.ReadTimeoutMs ?? 5000,
            ExtendedProperties = input.ExtendedProperties ?? [],
            Transfer = input.Transfer,
            CreatedAt = DateTimeOffset.Now,
        };
        var sync = await _sync.UpsertAsync(item, ct);
        item = item with { UpstreamSynced = sync.Success, UpstreamError = sync.Error };
        _store.Update(rows => { rows.Add(item); return 0; });
        _activityLog.Write("operation", "创建设备",
            $"{item.Name}（{item.Type} · {item.Protocol} · {item.Host}:{item.Port}）{(sync.Success ? "已同步上游" : "上游同步失败")}");
        return Ok(item);
    }

    /// <summary>更新设备（本地保存并同步到上游）。未提供的字段沿用现有值。</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<MachineDeviceDto>> Update(string id, [FromBody] MachineDeviceUpsertRequest input, CancellationToken ct)
    {
        var current = _store.ReadAll().FirstOrDefault(x => x.Id == id);
        if (current is null) return NotFound();
        if (input.Port is not null && input.Port is not (> 0 and <= 65535)) return BadRequest(new { error = "Port 必须是 1~65535" });

        // Id 与 CreatedAt 始终保留服务端原值，其余字段"给了才改"
        var candidate = current with
        {
            Name = input.Name ?? current.Name,
            Type = input.Type ?? current.Type,
            Brand = input.Brand ?? current.Brand,
            Model = input.Model ?? current.Model,
            Protocol = input.Protocol ?? current.Protocol,
            Host = input.Host ?? current.Host,
            Port = input.Port ?? current.Port,
            Username = input.Username ?? current.Username,
            ConnectTimeoutMs = input.ConnectTimeoutMs ?? current.ConnectTimeoutMs,
            ReadTimeoutMs = input.ReadTimeoutMs ?? current.ReadTimeoutMs,
            ExtendedProperties = input.ExtendedProperties ?? current.ExtendedProperties,
            Transfer = input.Transfer ?? current.Transfer,
        };
        var sync = await _sync.UpsertAsync(candidate, ct);
        var item = _store.Update<MachineDeviceDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            rows[index] = candidate with
            {
                CreatedAt = rows[index].CreatedAt,
                UpstreamSynced = sync.Success, UpstreamError = sync.Error,
            };
            return rows[index];
        });
        if (item is null)
        {
            // 上游 Upsert 与本地落盘之间设备被并发删除：撤销刚重建的上游记录，避免两侧不一致。
            var rollback = await _sync.DeleteAsync(id, ct);
            if (!rollback.Success)
                _logger.LogWarning("设备 {DeviceId} 更新期间被删除，回滚上游记录失败：{Error}", id, rollback.Error);
            return NotFound();
        }
        _activityLog.Write("operation", "更新设备", $"{item.Name}（{item.Host}:{item.Port}）");
        return Ok(item);
    }

    /// <summary>删除设备（本地删除并同步删除上游记录）</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var target = _store.Update<MachineDeviceDto?>(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index < 0) return null;
            var item = rows[index];
            rows.RemoveAt(index);
            return item;
        });
        if (target is null) return NotFound();

        var sync = await _sync.DeleteAsync(id, ct);
        if (!sync.Success)
            _logger.LogWarning("设备 {DeviceId} 上游删除未成功：{Error}", id, sync.Error);
        _activityLog.Write("operation", "删除设备", target.Name);
        return NoContent();
    }

    /// <summary>将本地设备注册表全量对账同步到上游（新建缺失、更新已有），返回同步报告。</summary>
    [HttpPost("sync-upstream")]
    public async Task<ActionResult<UpstreamSyncReport>> SyncUpstream(CancellationToken ct)
    {
        var report = await _sync.SyncAllAsync(ct);
        _activityLog.Write(report.Failed == 0 ? "operation" : "warning", "设备上游对账",
            $"共 {report.Total} 台：新建 {report.Created}，更新 {report.Updated}，失败 {report.Failed}");
        return Ok(report);
    }

    /// <summary>测试单设备连接：优先上游驱动级握手（FOCAS/Modbus/S7 等），上游不可用时回退 TCP 端口探测</summary>
    [HttpPost("{id}/test-connection")]
    public async Task<IActionResult> TestConnection(string id, CancellationToken ct)
    {
        var item = _store.ReadAll().FirstOrDefault(x => x.Id == id);
        if (item is null) return NotFound();

        var driver = await TryUpstreamDriverTestAsync(item, ct);
        if (driver is not null)
        {
            if (driver.Success)
                MarkOnline(id);
            var latencyText = driver.Latency is { } latency ? $"{latency.TotalMilliseconds:N0} ms" : null;
            return Ok(new { success = driver.Success, latency = latencyText, errorMessage = driver.ErrorMessage, mode = "driver" });
        }

        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var start = DateTimeOffset.Now;
            await tcp.ConnectAsync(item.Host, item.Port, ct);
            var elapsed = DateTimeOffset.Now - start;
            MarkOnline(id);
            return Ok(new { success = true, latency = $"{elapsed.TotalMilliseconds:N0} ms", mode = "tcp" });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, errorMessage = ex.Message, mode = "tcp" });
        }
    }

    private void MarkOnline(string id)
    {
        _store.Update(rows =>
        {
            var index = rows.FindIndex(x => x.Id == id);
            if (index >= 0)
            {
                rows[index] = rows[index] with
                {
                    Status = "Online", LastSeenAt = DateTimeOffset.Now,
                };
            }
            return 0;
        });
    }

    /// <summary>
    /// 上游驱动级连接测试。设备尚未同步到上游（404）时先补一次注册再重试。
    /// 返回 null 表示上游不可用，调用方回退本地 TCP 探测。
    /// </summary>
    private async Task<UpstreamConnectionTestResult?> TryUpstreamDriverTestAsync(MachineDeviceDto item, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("IndustrialIoT");
            var path = $"{DevicesPath}/{Uri.EscapeDataString(item.Id)}/test-connection";
            using var first = await client.PostAsync(path, content: null, ct);
            if (first.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var sync = await _sync.UpsertAsync(item, ct);
                if (!sync.Success) return null;
                using var retry = await client.PostAsync(path, content: null, ct);
                return await ParseTestResultAsync(retry, ct);
            }
            return await ParseTestResultAsync(first, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上游驱动级连接测试不可用，回退 TCP 探测：{DeviceId}", item.Id);
            return null;
        }
    }

    private static async Task<UpstreamConnectionTestResult?> ParseTestResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<UpstreamConnectionTestResult>(cancellationToken: ct);
    }

    private sealed record UpstreamConnectionTestResult(bool Success, string? ErrorMessage, TimeSpan? Latency);

    [HttpGet("template")]
    public IActionResult DownloadTemplate()
    {
        const string csv = "name,type,brand,model,protocol,host,port,username,password,connectTimeoutMs,readTimeoutMs\n" +
                           "PLC-001,PLC,Siemens,S7-1500,ModbusTCP,192.168.1.10,502,,,10000,5000\n";
        return File(Encoding.UTF8.GetBytes("\uFEFF" + csv), "text/csv", "device-import-template.csv");
    }

    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<DeviceImportResult>> Import(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) return BadRequest(new { error = "导入文件为空" });
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rows = ParseCsv(await reader.ReadToEndAsync(ct));
        if (rows.Count < 2) return BadRequest(new { error = "CSV 至少需要表头和一行设备数据" });

        var result = ImportRows(rows, out var imported);

        // 导入成功的设备逐个同步到上游；失败不影响本地导入结果，但记入 Errors 提示用户
        if (imported.Count > 0)
        {
            var syncErrors = new List<string>();
            var syncResults = new Dictionary<string, UpstreamSyncResult>();
            foreach (var device in imported)
            {
                var sync = await _sync.UpsertAsync(device, ct);
                syncResults[device.Id] = sync;
                if (!sync.Success)
                    syncErrors.Add($"{device.Name}: 已导入本地，但同步上游失败（{sync.Error}）");
            }
            _store.Update(all =>
            {
                for (var index = 0; index < all.Count; index++)
                {
                    if (!syncResults.TryGetValue(all[index].Id, out var sync)) continue;
                    all[index] = all[index] with
                    {
                        UpstreamSynced = sync.Success, UpstreamError = sync.Error,
                    };
                }
                return 0;
            });
            if (syncErrors.Count > 0)
                result = result with { Errors = [.. result.Errors, .. syncErrors] };
        }

        _activityLog.Write("operation", "批量导入设备", $"成功 {result.Success}/{result.Total}，失败 {result.Failed}");
        return Ok(result);
    }

    private DeviceImportResult ImportRows(List<List<string>> rows, out List<MachineDeviceDto> imported)
    {
        var headers = rows[0].Select(x => x.Trim().TrimStart('\uFEFF')).ToList();
        var errors = new List<string>();
        var importedRows = new List<MachineDeviceDto>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = ToDictionary(headers, rows[i]);
            if (!row.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"第 {i + 1} 行缺少 name");
                continue;
            }
            var device = BuildDevice(row);
            importedRows.Add(device);
        }
        _store.Update(devices =>
        {
            devices.AddRange(importedRows);
            return 0;
        });
        imported = importedRows;
        return new DeviceImportResult(rows.Count - 1, imported.Count, errors.Count, errors);
    }

    private static MachineDeviceDto BuildDevice(IReadOnlyDictionary<string, string> row) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Get(row, "name"),
        Type = Get(row, "type", "CNC"),
        Brand = Get(row, "brand", "Unknown"),
        Model = Get(row, "model", "Unknown"),
        Protocol = Get(row, "protocol", "ModbusTCP"),
        Status = "Offline",
        Host = Get(row, "host", "127.0.0.1"),
        Port = int.TryParse(Get(row, "port"), out var port) ? port : 502,
        Username = EmptyToNull(Get(row, "username")),
        ConnectTimeoutMs = int.TryParse(Get(row, "connectTimeoutMs"), out var connectTimeout) ? connectTimeout : 10000,
        ReadTimeoutMs = int.TryParse(Get(row, "readTimeoutMs"), out var readTimeout) ? readTimeout : 5000,
        ExtendedProperties = [],
        Transfer = null,
        CreatedAt = DateTimeOffset.Now,
        LastSeenAt = null,
    };

    private static string Get(IReadOnlyDictionary<string, string> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string> ToDictionary(IReadOnlyList<string> headers, IReadOnlyList<string> values) =>
        headers.Select((header, index) => new { header, value = index < values.Count ? values[index] : "" })
            .Where(x => !string.IsNullOrWhiteSpace(x.header))
            .ToDictionary(x => x.header, x => x.value, StringComparer.OrdinalIgnoreCase);

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (ch == '"' && quoted && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
            else if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else if ((ch == '\n' || ch == '\r') && !quoted)
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
                row = [];
            }
            else cell.Append(ch);
        }
        row.Add(cell.ToString());
        if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
        return rows;
    }
}
