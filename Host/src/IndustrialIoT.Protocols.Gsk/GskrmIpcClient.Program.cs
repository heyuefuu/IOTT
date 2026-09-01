namespace IndustrialIoT.Protocols.Gsk;

public sealed partial class GskrmIpcClient
{
    public int GetPartCount(int handle, out int count)
        => PostInt("api/gskrm/get-part-count", new GskrmHandleRequest(handle), out count);

    public int GetCutTime(int handle, out TimeSpan elapsed)
        => PostTimeSpan("api/gskrm/get-cut-time", new GskrmHandleRequest(handle), out elapsed);

    public int GetRunTime(int handle, out TimeSpan elapsed)
        => PostTimeSpan("api/gskrm/get-run-time", new GskrmHandleRequest(handle), out elapsed);

    public int GetCNCFileCount(int handle, out int count)
        => PostInt("api/gskrm/get-cnc-file-count", new GskrmHandleRequest(handle), out count);

    public int GetCNCFileList(int handle, out IReadOnlyList<GskrmCncFileEntry> entries)
    {
        int rc = PostValue<List<GskrmCncFileEntry>>("api/gskrm/get-cnc-file-list", new GskrmHandleRequest(handle), out var value);
        entries = value ?? [];
        return rc;
    }

    public int GetCNCFileInfo(int handle, string name, out GskrmCncFileEntry entry)
    {
        int rc = PostValue<GskrmCncFileEntry>("api/gskrm/get-cnc-file-info",
            new GskrmFileInfoRequest(handle, name), out var value);
        entry = value ?? new GskrmCncFileEntry { Name = name };
        return rc;
    }

    public int ReceiveCNCFile(int handle, string remoteName, string localPath, IProgress<long>? progress = null)
    {
        int rc = PostCode("api/gskrm/receive-cnc-file", new GskrmReceiveFileRequest(handle, remoteName, localPath));
        if (rc == GskrmErrorCodes.Ok && File.Exists(localPath))
            progress?.Report(new FileInfo(localPath).Length);
        return rc;
    }

    public int SendCNCFile(int handle, string localPath, string remoteName, IProgress<long>? progress = null)
    {
        int rc = PostCode("api/gskrm/send-cnc-file", new GskrmSendFileRequest(handle, localPath, remoteName));
        if (rc == GskrmErrorCodes.Ok && File.Exists(localPath))
            progress?.Report(new FileInfo(localPath).Length);
        return rc;
    }

    public int DeleteCNCFile(int handle, string remoteName)
        => PostCode("api/gskrm/delete-cnc-file", new GskrmFileNameRequest(handle, remoteName));

    public int ProgInstall(int handle, string remoteName)
        => PostCode("api/gskrm/prog-install", new GskrmFileNameRequest(handle, remoteName));

    public int ProgUninstall(int handle, string remoteName)
        => PostCode("api/gskrm/prog-uninstall", new GskrmFileNameRequest(handle, remoteName));

    private int PostTimeSpan(string path, object request, out TimeSpan elapsed)
    {
        int rc = PostValue<double>(path, request, out var seconds);
        elapsed = TimeSpan.FromSeconds(seconds);
        return rc;
    }
}
