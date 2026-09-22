namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using System.Reflection;
using SMBLibrary;
using SMBLibrary.Client;

public class SmbFileStoreProbe : DispatchProxy
{
    public MemoryStream Contents { get; } = new();
    public int MaxWrite { get; set; } = int.MaxValue;
    public NTStatus ReadStatus { get; set; } = NTStatus.STATUS_SUCCESS;
    public long? ExpectedLength { get; set; }
    public bool EmptySuccessfulRead { get; set; }
    public int CloseCalls { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        switch (method.Name)
        {
            case nameof(ISMBFileStore.CreateFile):
                arguments[0] = new object();
                arguments[1] = Activator.CreateInstance(method.GetParameters()[1].ParameterType.GetElementType()!);
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.CloseFile):
                CloseCalls++;
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.GetFileInformation):
                arguments[0] = new FileStandardInformation { EndOfFile = ExpectedLength ?? Contents.Length };
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.ReadFile):
                arguments[0] = Array.Empty<byte>();
                if (ReadStatus != NTStatus.STATUS_SUCCESS) return ReadStatus;
                if (EmptySuccessfulRead) return NTStatus.STATUS_SUCCESS;
                var offset = (long)arguments[2]!;
                if (offset >= Contents.Length) return NTStatus.STATUS_END_OF_FILE;
                var count = (int)Math.Min((int)arguments[3]!, Contents.Length - offset);
                arguments[0] = Contents.ToArray().AsSpan((int)offset, count).ToArray();
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.WriteFile):
                var data = (byte[])arguments[3]!;
                var accepted = Math.Min(MaxWrite, data.Length);
                arguments[0] = accepted;
                Contents.Position = (long)arguments[2]!;
                Contents.Write(data, 0, accepted);
                return NTStatus.STATUS_SUCCESS;
            case nameof(ISMBFileStore.Disconnect):
                return NTStatus.STATUS_SUCCESS;
            default:
                throw new NotSupportedException(method.Name);
        }
    }
}
