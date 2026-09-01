namespace IndustrialIoT.HncSdkShim;

using IndustrialIoT.Protocols.HncSdk;

public interface IHncSdkGateway
{
    Task<HncSdkConnectResult> ConnectAsync(HncSdkConnectRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> DisconnectAsync(string sessionId, CancellationToken ct);
    Task<HncSdkIpcResult> PingAsync(string sessionId, CancellationToken ct);
    Task<HncSdkValueResult<object>> ReadAsync(HncSdkReadRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> WriteAsync(HncSdkWriteRequest request, CancellationToken ct);
    Task<HncSdkValueResult<IReadOnlyList<HncSdkFileEntry>>> BrowseFilesAsync(HncSdkBrowseRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> UploadAsync(HncSdkTransferRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> DownloadAsync(HncSdkTransferRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> RemoveAsync(HncSdkRemoveRequest request, CancellationToken ct);
    Task<HncSdkIpcResult> RenameAsync(HncSdkRenameRequest request, CancellationToken ct);
}
