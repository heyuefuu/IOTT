namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

/// <summary>
/// 网关本地设备注册表（App_Data/devices.json）的唯一入口。
/// 单例注入，避免多个 JsonFileStore 实例各持一把锁造成并发写竞态。
/// </summary>
public interface IDeviceStore
{
    List<MachineDeviceDto> ReadAll();
    void WriteAll(IEnumerable<MachineDeviceDto> items);
    TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update);
}

public sealed class DeviceStore : IDeviceStore
{
    private readonly JsonFileStore<MachineDeviceDto> _store = new("devices.json");

    public List<MachineDeviceDto> ReadAll() => _store.ReadAll();

    public void WriteAll(IEnumerable<MachineDeviceDto> items) => _store.WriteAll(items);

    public TResult Update<TResult>(Func<List<MachineDeviceDto>, TResult> update) =>
        _store.Update(update);
}
