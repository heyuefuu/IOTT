namespace IndustrialIoT.JingDiaoShim;

using IndustrialIoT.Protocols.JingDiao;

public static class JingDiaoFileTransfer
{
    public static async Task<JingDiaoIpcResult> UploadAsync(IJdMonApi api, JingDiaoSessionStore store,
        string sessionId, Stream source, string fileName, string directory, bool addToTask, bool setMainProgram,
        CancellationToken ct)
    {
        JingDiaoProgramPath.Validate(fileName, directory);
        var temporaryDirectory = Directory.CreateTempSubdirectory("IndustrialIoT.JingDiao-");
        var temporaryFile = Path.Combine(temporaryDirectory.FullName, fileName);
        try
        {
            await using (var output = File.Create(temporaryFile)) await source.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            return store.TryUse(sessionId, handle =>
            {
                ct.ThrowIfCancellationRequested();
                if (directory.Length > 0)
                {
                    var folder = Result(api.SetReceiveFolder(handle, directory), api, handle);
                    if (folder.ReturnCode != 0) return folder;
                }
                return Result(api.SendNcFile(handle, temporaryFile, addToTask, setMainProgram), api, handle);
            }, out var result) ? result : new() { ReturnCode = -404, ErrorMessage = "Unknown session." };
        }
        finally
        {
            File.Delete(temporaryFile);
            temporaryDirectory.Delete();
        }
    }

    public static JingDiaoValueResult<IReadOnlyList<JingDiaoFileEntry>> Browse(IJdMonApi api, IntPtr handle, string? directory)
    {
        var basePath = directory ?? "";
        var status = Result(api.GetMachFileList(handle, basePath, 102400, out var fileList), api, handle);
        if (status.ReturnCode != 0)
            return new() { ReturnCode = status.ReturnCode, ErrorMessage = status.ErrorMessage };
        var files = new List<JingDiaoFileEntry>();
        foreach (var entry in fileList.Split(["\r\n", "\n", "\r", ";", "|"], StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim()).Where(entry => entry.Length > 0))
        {
            var name = entry.TrimEnd('/', '\\').Replace('\\', '/').Split('/').Last();
            var path = string.IsNullOrWhiteSpace(basePath) ? entry : $"{basePath.TrimEnd('/', '\\')}/{name}";
            var attributes = Result(api.GetFileAttribute(handle, path, out var isDirectory, out var actualSize), api, handle);
            if (attributes.ReturnCode != 0)
                return new() { ReturnCode = attributes.ReturnCode,
                    ErrorMessage = $"File attributes unavailable for '{path}': {attributes.ErrorMessage}" };
            long? size = !isDirectory && actualSize >= 0 ? actualSize : null;
            files.Add(new JingDiaoFileEntry(path, name, isDirectory, size));
        }
        return new() { Value = files };
    }

    public static JingDiaoIpcResult Result(bool ok, IJdMonApi api, IntPtr handle)
    {
        var error = ok ? 0 : api.GetLastError(handle);
        return new() { ReturnCode = ok ? 0 : (int)(error == 0 ? 1 : error),
            ErrorMessage = ok ? null : $"NcMonIO call failed: {error}" };
    }
}
