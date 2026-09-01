namespace IndustrialIoT.Protocols.JingDiao;

public interface IJdMonApi
{
    IntPtr Create();
    void Delete(ref IntPtr handle);
    bool Connect(IntPtr handle, string host, int rpcPort, int callbackPort, int uploadPort, int downloadPort);
    bool Disconnect(IntPtr handle);
    bool IsConnected(IntPtr handle);
    void SetConnectionTimeout(IntPtr handle, int timeoutMs);
    void SetRpcTimeout(IntPtr handle, int timeoutMs);
    uint GetLastError(IntPtr handle);
    bool GetMachPos(IntPtr handle, double[] machine, double[] absolute, double[] relative);
    bool GetProgState(IntPtr handle, out int state);
    bool GetAlarm(IntPtr handle, out int alarm);
    bool GetBasicModal(IntPtr handle, out JingDiaoModalSnapshot value);
    bool GetSpindle(IntPtr handle, double[] spindle);
    bool GetRate(IntPtr handle, int[] rates);
    bool GetMacro(IntPtr handle, int number, out double value);
    bool GetLineNo(IntPtr handle, out int lineNo);
    bool GetPartCount(IntPtr handle, out int count);
    bool GetMachFileList(IntPtr handle, string directory, int bufferSize, out string fileList);
    bool SendNcFile(IntPtr handle, string localPath, bool addToTask, bool setMainProgram);
    bool ReceiveFile(IntPtr handle, string remotePath, string localPath);
    bool DeleteFile(IntPtr handle, string directory, string fileName);
}
