namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

/// <summary>指标库（App_Data/metrics.json）唯一入口：指标管理 CRUD 与自动验证判定共用。</summary>
public interface IMetricStore
{
    List<MetricDto> ReadAll();
    void WriteAll(IEnumerable<MetricDto> items);
    TResult Update<TResult>(Func<List<MetricDto>, TResult> update);
}

public sealed class MetricStore : IMetricStore
{
    private readonly JsonFileStore<MetricDto> _store = new("metrics.json");

    public MetricStore()
    {
        _store.Update(rows =>
        {
            var definitions = new[]
            {
                ("industrial-protocol", "5.2.1", "工控协议覆盖率", "compatibility", "种", "统计已配置协议；正式验收需验证至少 3 类协议实际收发。"),
                ("communication-stability", "5.2.2", "通讯稳定性", "stability", "%", "当前为短时连通性探测，不能替代连续 2 小时通讯验收。"),
                ("max-connections", "5.2.3", "最大并发连接数", "performance", "台", "当前自动压测上限 100，尚未满足至少 200 台的验收要求。"),
                ("transfer-protocol", "5.2.4", "传输协议覆盖率", "compatibility", "种", "统计传输协议配置；正式验收需验证至少 3 类协议上下传。"),
                ("file-integrity", "5.2.5", "文件完整性", "function", "%", "当前主动测试仅覆盖两档文件，尚未覆盖 0.1/1/10/20MB 四档。"),
                ("transfer-speed", "5.2.6", "传输速度", "performance", "MB/s", "当前统计历史记录，不能替代约 100MB 文件达到 50MB/s 的实测。"),
                ("file-size", "5.2.7", "文件大小", "performance", "MB", "当前统计历史最大文件，尚需完成至少 100MB 文件实际传输验收。"),
            };
            var createdAt = DateTimeOffset.Now;
            foreach (var (id, code, name, category, unit, description) in definitions)
            {
                if (rows.Any(metric => string.Equals(metric.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new MetricDto
                {
                    Id = id, Code = code, Name = name, Category = category, Unit = unit,
                    StatusLabel = "待验证", StatusType = "info", Description = description,
                    CreatedAt = createdAt,
                });
            }
            return rows.Count;
        });
    }

    public List<MetricDto> ReadAll() => _store.ReadAll();

    public void WriteAll(IEnumerable<MetricDto> items) => _store.WriteAll(items);

    public TResult Update<TResult>(Func<List<MetricDto>, TResult> update) => _store.Update(update);
}

/// <summary>验证任务库（App_Data/verify-tasks.json）唯一入口：任务 CRUD、手动运行与定时调度共用。</summary>
public interface IVerifyTaskStore
{
    List<VerifyTaskDto> ReadAll();
    void WriteAll(IEnumerable<VerifyTaskDto> items);
    TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update);
}

public sealed class VerifyTaskStore : IVerifyTaskStore
{
    private readonly JsonFileStore<VerifyTaskDto> _store = new("verify-tasks.json");

    public List<VerifyTaskDto> ReadAll() => _store.ReadAll();

    public void WriteAll(IEnumerable<VerifyTaskDto> items) => _store.WriteAll(items);

    public TResult Update<TResult>(Func<List<VerifyTaskDto>, TResult> update) => _store.Update(update);
}
