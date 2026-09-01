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
