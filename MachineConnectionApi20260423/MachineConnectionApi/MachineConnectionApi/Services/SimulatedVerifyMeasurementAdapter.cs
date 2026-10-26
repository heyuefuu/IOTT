namespace MachineConnectionApi.Services;

using MachineConnectionApi.Models;

/// <summary>
/// 测试用测量适配器：不做真实通讯，直接返回"成功观测"，使 7 个指标在本机无硬件/无 Host 真机读时
/// 也能跑通"规则 → 测量 → 评分 → 达标判定"的完整链路，用于验证模块的完整性与逻辑正确性。
/// 仅当配置 Verify:Simulate=true 时由 <see cref="VerifyAutomationService"/> 选用；
/// 生产环境务必保持 false（走 HttpVerifyMeasurementAdapter，拒绝模拟证据）。
/// </summary>
public sealed class SimulatedVerifyMeasurementAdapter : IVerifyMeasurementAdapter
{
    public Task<VerifyReadObservation> ReadAsync(VerifyMeasurementTarget target, string address, string dataType, int timeoutMs, CancellationToken ct)
        => Task.FromResult(new VerifyReadObservation(true, true, true,
            $"模拟测试：应用读成功（{target.Protocol} {address}/{dataType}）",
            EvaluationAutomationRules.NormalizeProtocol(target.Protocol)));

    public Task<VerifyReadObservation> ReadTransferDirectoryAsync(VerifyMeasurementTarget target, string directory, int timeoutMs, CancellationToken ct)
        => Task.FromResult(new VerifyReadObservation(true, true, true,
            $"模拟测试：目录 {directory} 读取成功",
            EvaluationAutomationRules.NormalizeProtocol(target.ResolvedTransferProtocol ?? target.Transfer?.Protocol ?? target.Protocol)));

    public Task<VerifyAttachmentValidation> ValidateAttachmentAsync(EvaluationFile file, CancellationToken ct)
        => Task.FromResult(new VerifyAttachmentValidation(true, $"模拟测试：附件 {file.Name} 校验通过"));

    public Task<VerifyTransferObservation> TransferAsync(VerifyMeasurementTarget target, EvaluationFile file, string directory,
        string remoteName, Action<double> reportFraction, CancellationToken ct)
    {
        var bytes = file.SizeBytes ?? 1;
        var seconds = Math.Max(0.001, bytes / 1024d / 1024d / 50d); // 名义 50 MiB/s，便于考核速度档位
        reportFraction(1);
        return Task.FromResult(new VerifyTransferObservation(true, true, true, true, bytes, seconds,
            $"模拟测试：上传 {remoteName} 并回读校验通过（{bytes} 字节）"));
    }

    public Task<VerifyConnectionsObservation> MeasureConnectionsAsync(VerifyMeasurementTarget target, int count,
        string address, string dataType, int timeoutMs, CancellationToken ct)
        => Task.FromResult(new VerifyConnectionsObservation(true, true, count, count,
            $"simulated: {count}/{count} independent connections"));

    public Task<VerifyTransferSnapshot> GetTransferSnapshotAsync(VerifyMeasurementTarget target, CancellationToken ct)
        => Task.FromResult(new VerifyTransferSnapshot(true,
            string.IsNullOrWhiteSpace(target.Transfer?.Protocol) ? target.Protocol : target.Transfer!.Protocol,
            "simulated snapshot"));
}
