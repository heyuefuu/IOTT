namespace IndustrialIoT.HncSdkShim;

using System.Runtime.InteropServices;
using System.Text;

internal static class HncSdkFtpApi
{
    private const int MaxDisplayNum = 512;
    private const int MaxLineSize = 128;

    private static readonly Encoding Gbk = RegisterAndResolveGbk();

    private static Encoding RegisterAndResolveGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // 华中 NC 文件名实际是 GBK；GB2312 是其子集，会把生僻字解成 '?'。
        return Encoding.GetEncoding(936);
    }

    [DllImport("ftp.dll", EntryPoint = "UploadFile", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int UploadFile(string remotePath, string localPath, string ipaddr);

    [DllImport("ftp.dll", EntryPoint = "DownloadFile", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int DownloadFile(string remotePath, string localPath, string ipaddr);

    [DllImport("ftp.dll", EntryPoint = "GetDirInfo", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GetDirInfoNative(string remotePath, string ipaddr, byte[] info);

    [DllImport("ftp.dll", EntryPoint = "RemoveFile", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int RemoveFile(string remotePath, string ipaddr);

    [DllImport("ftp.dll", EntryPoint = "RenameFile", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int RenameFile(string remotePath, string newName, string ipaddr);

    public static int GetDirInfo(string remotePath, string ipaddr, List<string> info)
    {
        var data = new byte[MaxDisplayNum * MaxLineSize];
        var ret = GetDirInfoNative(remotePath, ipaddr, data);
        if (ret != 0) return ret;

        var content = Gbk.GetString(data).Trim('\0');
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            info.Add(line);
        return 0;
    }
}
