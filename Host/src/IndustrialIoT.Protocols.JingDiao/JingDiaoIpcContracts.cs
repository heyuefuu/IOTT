namespace IndustrialIoT.Protocols.JingDiao;

public sealed record JingDiaoConnectRequest(
    string Host, int RpcPort, int CallbackPort, int FileUploadPort, int FileDownloadPort, int TimeoutMs);

public sealed record JingDiaoSessionRequest(string SessionId);
public sealed record JingDiaoMacroRequest(string SessionId, int Number);
public sealed record JingDiaoBrowseFilesRequest(string SessionId, string? Path);
public sealed record JingDiaoDownloadRequest(string SessionId, string RemotePath);
public sealed record JingDiaoDeleteFileRequest(string SessionId, string Directory, string FileName);

public class JingDiaoIpcResult
{
    public int ReturnCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class JingDiaoConnectResult : JingDiaoIpcResult
{
    public string SessionId { get; set; } = "";
}

public sealed class JingDiaoValueResult<T> : JingDiaoIpcResult
{
    public T? Value { get; set; }
}

public sealed record JingDiaoPositionSnapshot(double[] Machine, double[] Absolute, double[] Relative);
public sealed record JingDiaoModalSnapshot(
    int WorkCoordinate, float Feedrate, int SpindleSpeed, int ToolNo,
    float MachiningTimeMinutes, int ProgramNo, int MainProgramNo);
public sealed record JingDiaoSpindleSnapshot(double Current, double Torque, double Power);
public sealed record JingDiaoRateSnapshot(int Spindle, int Feed);
public sealed record JingDiaoFileEntry(string Path, string Name, bool IsDirectory, long? SizeBytes);
