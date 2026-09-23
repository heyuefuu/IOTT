namespace MachineConnectionApi.Services;

/// <summary>
/// 网关启动后对账设备；本地列表为空时从 Industrial IoT 恢复已有设备。
/// 上游可能晚于网关启动，因此带间隔重试；配置 IndustrialIoT:AutoSyncDevices=false 可关闭。
/// </summary>
public sealed class DeviceUpstreamSyncHostedService : BackgroundService
{
    private static readonly int[] RetryDelaysSeconds = [5, 15, 30, 60];

    private readonly IDeviceUpstreamSyncService _sync;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeviceUpstreamSyncHostedService> _logger;

    public DeviceUpstreamSyncHostedService(
        IDeviceUpstreamSyncService sync,
        IConfiguration configuration,
        ILogger<DeviceUpstreamSyncHostedService> logger)
    {
        _sync = sync;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_configuration.GetValue<bool?>("IndustrialIoT:AutoSyncDevices") == false)
            return;

        foreach (var delaySeconds in RetryDelaysSeconds)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                var report = await _sync.SyncAllAsync(stoppingToken);
                if (report.Failed == 0)
                {
                    _logger.LogInformation(
                        "设备对账完成：恢复 {Restored}，保留 {Skipped}，新建 {Created}，更新 {Updated}，共 {Total}",
                        report.Restored, report.Skipped, report.Created, report.Updated, report.Total);
                    return;
                }
                _logger.LogWarning(
                    "设备上游对账未全部成功：失败 {Failed}/{Total}（{FirstError}），稍后重试",
                    report.Failed, report.Total, report.Errors.Count > 0 ? report.Errors[0].Error : "");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "设备上游对账异常，稍后重试");
            }
        }
    }
}
