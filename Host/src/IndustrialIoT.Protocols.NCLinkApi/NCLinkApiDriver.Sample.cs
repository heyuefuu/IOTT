namespace IndustrialIoT.Protocols.NCLinkApi;

using System.Threading.Channels;
using Microsoft.Extensions.Logging;

/// <summary>
/// 驱动级别的采样便捷接口 — 包装 NCLinkApiSampleSession 的六步法，
/// 并提供后台轮询 + Channel 推送。
/// </summary>
public sealed partial class NCLinkApiDriver
{
    /// <summary>
    /// 一次性建立采样会话；返回 NCLinkApiSampleSession，调用方负责后续 SetPeriod/Register/Start/Fetch。
    /// </summary>
    public NCLinkApiSampleSession CreateSampleSession(string channelKey)
    {
        EnsureConnected();
        return new NCLinkApiSampleSession(_client!, _deviceId, channelKey, _logger);
    }

    /// <summary>
    /// 启动一次完整采样：完成六步法 ①~④ + ⑤ 轮询，把数据写入返回的 Channel。
    /// 调用方读取 Channel 后通过 cancellation 停止，会自动 ⑥ 注销。
    /// </summary>
    public async Task<(ChannelReader<IReadOnlyList<int>> Reader, NCLinkApiSampleSession Session)>
        StartSamplingAsync(
            string channelKey,
            int periodMs,
            IReadOnlyList<NCLinkSampleItem> items,
            int batchLength,
            TimeSpan pollInterval,
            bool enablePlcFlag = true,
            CancellationToken ct = default)
    {
        EnsureConnected();
        var session = new NCLinkApiSampleSession(_client!, _deviceId, channelKey, _logger);

        var channel = Channel.CreateBounded<IReadOnlyList<int>>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true,
            });

        await session.SetPeriodAsync(periodMs, ct).ConfigureAwait(false);
        await session.RegisterAsync(items, ct).ConfigureAwait(false);
        await session.StartAsync(ct).ConfigureAwait(false);
        if (enablePlcFlag)
            await session.EnablePlcFlagAsync(ct: ct).ConfigureAwait(false);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var batch = await session.FetchAsync(batchLength, ct).ConfigureAwait(false);
                        if (batch.Count > 0) channel.Writer.TryWrite(batch);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "NC-Link sample fetch failed, retry");
                    }
                    await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            finally
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
                channel.Writer.TryComplete();
            }
        }, ct);

        return (channel.Reader, session);
    }
}
