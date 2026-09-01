namespace IndustrialIoT.Protocols.HncSdk;

public sealed record HncSdkConnectRequest(
    string Host, int Port, string LocalIp, int LocalPort, string ClientName, int TimeoutMs);
public sealed record HncSdkSessionRequest(string SessionId);
public sealed record HncSdkReadRequest(string SessionId, string Address, string DataType);
public sealed record HncSdkWriteRequest(string SessionId, string Address, string DataType, string Value);
public sealed record HncSdkBrowseRequest(string SessionId, string? Path);
public sealed record HncSdkTransferRequest(string SessionId, string RemotePath, string LocalPath);
public sealed record HncSdkRemoveRequest(string SessionId, string RemotePath);
public sealed record HncSdkRenameRequest(string SessionId, string RemotePath, string NewName);

public class HncSdkIpcResult
{
    public int ReturnCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class HncSdkConnectResult : HncSdkIpcResult
{
    public string SessionId { get; set; } = "";
}

public sealed class HncSdkValueResult<T> : HncSdkIpcResult
{
    public T? Value { get; set; }
}

public sealed record HncSdkFileEntry(
    string Path, string Name, bool IsDirectory, long? SizeBytes);
