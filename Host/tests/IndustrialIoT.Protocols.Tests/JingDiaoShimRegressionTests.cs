using System.Collections.Concurrent;
using IndustrialIoT.JingDiaoShim;
using IndustrialIoT.Protocols.JingDiao;

internal static class JingDiaoShimRegressionTests
{
    public static async Task RunAsync()
    {
        var api = new FakeApi();
        using var store = new JingDiaoSessionStore(api);
        var session = store.Add(new IntPtr(1));
        async Task<JingDiaoIpcResult> Upload(string directory) => await JingDiaoFileTransfer.UploadAsync(api, store,
            session, new MemoryStream([1, 2, 3]), "O1234.nc", directory, false, false, CancellationToken.None);
        var result = await Upload("/NC/SUB");
        var sent = api.Sent.Single();
        TestSupport.Require(result.ReturnCode == 0 && Path.GetFileName(sent.Path) == "O1234.nc" && sent.Directory == "/NC/SUB",
            "SDK upload received the wrong basename/directory");
        TestSupport.Require(!File.Exists(sent.Path) && !Directory.Exists(Path.GetDirectoryName(sent.Path)),
            "SDK upload temporary files were not cleaned");
        api.FailFolder = true;
        TestSupport.Require((await Upload("/NC/FAIL")).ReturnCode == 27 && api.Sent.Count == 1,
            "Upload proceeded after setting its target directory failed");
        api.FailFolder = false;
        api.FailSend = true;
        var threw = false;
        try { await Upload("/NC/THROW"); } catch (IOException) { threw = true; }
        TestSupport.Require(threw && !Directory.Exists(Path.GetDirectoryName(api.Sent.Last().Path)),
            "SDK exception left the temporary program behind");
        api.FailSend = false;
        api.BlockNextSend = true;
        var first = Task.Run(() => Upload("/NC/ONE"));
        TestSupport.Require(api.Entered.Wait(TimeSpan.FromSeconds(5)), "First concurrent upload did not enter SDK");
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () => { secondStarted.SetResult(); return await Upload("/NC/TWO"); });
        await secondStarted.Task;
        api.Release.Set();
        var concurrent = await Task.WhenAll(first, second);
        TestSupport.Require(concurrent.All(item => item.ReturnCode == 0)
            && api.Sent.TakeLast(2).Select(item => item.Directory).SequenceEqual(["/NC/ONE", "/NC/TWO"]),
            "Concurrent uploads interleaved the target directory and SDK send");
        var files = JingDiaoFileTransfer.Browse(api, new IntPtr(1), "/NC");
        TestSupport.Require(files.Value![0].SizeBytes == 42 && files.Value[1].IsDirectory,
            "SDK file attributes were not reflected in directory metadata");
        api.FailAttributes = true;
        var failedAttributes = JingDiaoFileTransfer.Browse(api, new IntPtr(1), "/NC");
        TestSupport.Require(failedAttributes.ReturnCode == 27 && failedAttributes.Value is null,
            "Missing SDK file attributes were advertised as a downloadable file");
        TestSupport.Require(store.Close(session).ReturnCode == 0 && api.Deleted
            && !store.TryUse(session, _ => true, out _), "Closed session still exposes its native handle");
        api.Entered.Dispose();
        api.Release.Dispose();
    }

    private sealed class FakeApi : IJdMonApi
    {
        public ConcurrentQueue<(string Path, string Directory)> Sent { get; } = new();
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool FailFolder, FailSend, FailAttributes, BlockNextSend, Deleted;
        private string directory = "";
        public IntPtr Create() => new(1);
        public void Delete(ref IntPtr handle) { Deleted = true; handle = IntPtr.Zero; }
        public bool Connect(IntPtr handle, string host, int rpcPort, int callbackPort, int uploadPort, int downloadPort) => true;
        public bool Disconnect(IntPtr handle) => true;
        public bool IsConnected(IntPtr handle) => !Deleted;
        public void SetConnectionTimeout(IntPtr handle, int timeoutMs) { }
        public void SetRpcTimeout(IntPtr handle, int timeoutMs) { }
        public uint GetLastError(IntPtr handle) => 27;
        public bool GetMachPos(IntPtr handle, double[] machine, double[] absolute, double[] relative) => true;
        public bool GetProgState(IntPtr handle, out int state) { state = 0; return true; }
        public bool GetAlarm(IntPtr handle, out int alarm) { alarm = 0; return true; }
        public bool GetBasicModal(IntPtr handle, out JingDiaoModalSnapshot value) { value = new(0, 0, 0, 0, 0, 0, 0); return true; }
        public bool GetSpindle(IntPtr handle, double[] spindle) => true;
        public bool GetRate(IntPtr handle, int[] rates) => true;
        public bool GetMacro(IntPtr handle, int number, out double value) { value = 0; return true; }
        public bool SetMacro(IntPtr handle, int number, double value) => true;
        public bool GetLineNo(IntPtr handle, out int lineNo) { lineNo = 0; return true; }
        public bool GetPartCount(IntPtr handle, out int count) { count = 0; return true; }
        public bool GetMachFileList(IntPtr handle, string directory, int bufferSize, out string fileList)
        { fileList = "O1.nc\nSUB\n"; return true; }
        public bool GetFileAttribute(IntPtr handle, string path, out bool isDirectory, out long size)
        { isDirectory = path.EndsWith("SUB"); size = 42; return !FailAttributes; }
        public bool SetReceiveFolder(IntPtr handle, string target)
        { if (FailFolder) return false; directory = target; return true; }
        public bool SendNcFile(IntPtr handle, string localPath, bool addToTask, bool setMainProgram)
        {
            TestSupport.Require(!Deleted && File.ReadAllBytes(localPath).Length == 3, "Invalid SDK handle/temp program");
            if (BlockNextSend)
            {
                BlockNextSend = false;
                Entered.Set();
                TestSupport.Require(Release.Wait(TimeSpan.FromSeconds(5)), "Concurrent SDK test timed out");
            }
            Sent.Enqueue((localPath, directory));
            if (FailSend) throw new IOException("SDK send fixture failure");
            return true;
        }
        public bool ReceiveFile(IntPtr handle, string remotePath, string localPath) => true;
        public bool DeleteFile(IntPtr handle, string directory, string fileName) => true;
    }
}
