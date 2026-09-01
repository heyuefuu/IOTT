namespace IndustrialIoT.Protocols.Gsk;

public sealed record GskrmCreateInstanceRequest(string Host, int Port, int TimeoutMs);
public sealed record GskrmHandleRequest(int Handle);
public sealed record GskrmTimeoutRequest(int Handle, int TimeoutMs);
public sealed record GskrmAxisRequest(int Handle, int AxisIndex);
public sealed record GskrmIndexedRequest(int Handle, int Index);
public sealed record GskrmPlcReadRequest(int Handle, string Address, int Length);
public sealed record GskrmPlcWriteRequest(int Handle, string Address, byte[] Data);
public sealed record GskrmNumberRequest(int Handle, int Number);
public sealed record GskrmMacroWriteRequest(int Handle, int Number, double Value);
public sealed record GskrmParamRequest(int Handle, int Number, int Axis);
public sealed record GskrmParamWriteRequest(int Handle, int Number, int Axis, int Value);
public sealed record GskrmFileNameRequest(int Handle, string RemoteName);
public sealed record GskrmFileInfoRequest(int Handle, string Name);
public sealed record GskrmReceiveFileRequest(int Handle, string RemoteName, string LocalPath);
public sealed record GskrmSendFileRequest(int Handle, string LocalPath, string RemoteName);

public class GskrmIpcResult
{
    public int ReturnCode { get; set; }
}

public sealed class GskrmCreateInstanceResult : GskrmIpcResult
{
    public int Handle { get; set; }
}

public sealed class GskrmValueResult<T> : GskrmIpcResult
{
    public T? Value { get; set; }
}
