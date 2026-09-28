using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.JingDiao;
using IndustrialIoT.Protocols.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class JingDiaoRegressionTests
{
    public static async Task RunAsync()
    {
        var port = TestSupport.FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var writes = new List<JingDiaoMacroWriteRequest>();
        var failure = false;
        var directoryFailure = false;
        string? uploadedName = null, uploadedDirectory = null;
        app.MapPost("/api/jingdiao/connect", () => Results.Json(new { returnCode = 0, sessionId = "fixture" }));
        app.MapPost("/api/jingdiao/disconnect", () => Results.Json(new { returnCode = 0 }));
        app.MapPost("/api/jingdiao/set-macro", (JingDiaoMacroWriteRequest request) =>
        {
            writes.Add(request);
            return Results.Json(new { returnCode = failure ? 23 : 0, errorMessage = failure ? "macro refused" : null });
        });
        app.MapPost("/api/jingdiao/list-files", () => Results.Json(new { returnCode = directoryFailure ? 31 : 0,
            errorMessage = directoryFailure ? "directory offline" : null,
            value = new[] { new JingDiaoFileEntry("/NC/O1.nc", "O1.nc", false, 42) } }));
        app.MapPost("/api/jingdiao/send-nc-file", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            uploadedName = form.Files.Single().FileName;
            uploadedDirectory = form["directory"].ToString();
            return Results.Json(new { returnCode = 0 });
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await using var driver = new JingDiaoDriver(NullLogger<JingDiaoDriver>.Instance, new JingDiaoIpcClient(http));
            TestSupport.Require((await driver.ConnectAsync(new DeviceConnectionConfig { Host = "127.0.0.1", Port = 89 })).Success,
                "JingDiao fixture did not connect");
            foreach (var item in new (DataType Type, object Value, double Expected)[]
                { (DataType.Bool, true, 1), (DataType.Bool, "false", 0), (DataType.Int16, -123, -123),
                  (DataType.UInt32, uint.MaxValue, uint.MaxValue), (DataType.Float, 1.25f, 1.25), (DataType.Double, -2.5d, -2.5) })
            {
                var result = await driver.WriteTagAsync("Macro:100", item.Type, item.Value);
                TestSupport.Require(result.Success && writes.Last().Number == 100 && writes.Last().Value == item.Expected,
                    "JingDiao macro value/type was lost over IPC");
            }
            var count = writes.Count;
            foreach (var item in new (string Address, DataType Type, object Value)[]
                { ("State:Prog", DataType.Int32, 1), ("Macro:-1", DataType.Int32, 1), ("Macro:1:2", DataType.Int32, 1),
                  ("Macro:1", DataType.Int16, 40000), ("Macro:1", DataType.Int32, 1.5), ("Macro:1", DataType.Bool, 2),
                  ("Macro:1", DataType.Double, double.NaN), ("Macro:1", DataType.Float, double.MaxValue),
                  ("Macro:1", DataType.Int64, long.MaxValue), ("Macro:1", DataType.String, "1") })
                TestSupport.Require(!(await driver.WriteTagAsync(item.Address, item.Type, item.Value)).Success,
                    "Invalid macro write was accepted");
            TestSupport.Require(writes.Count == count, "Invalid macro write reached the controller");
            failure = true;
            var failed = await driver.WriteTagAsync("Macro:100", DataType.Int32, 1);
            TestSupport.Require(!failed.Success && failed.ErrorMessage == "macro refused", "SDK write error was lost");
            TestSupport.Require((await driver.BrowseAsync("/Macro")).Single().IsWritable,
                "Writable macro was not advertised");
            var files = await driver.ReadTagAsync("Program.Files", DataType.String);
            using var parsedFiles = JsonDocument.Parse((string)files.Value!);
            TestSupport.Require(files.Quality == TagQuality.Good && parsedFiles.RootElement[0].GetProperty("SizeBytes").GetInt64() == 42,
                "Program.Files did not return readable file metadata");
            directoryFailure = true;
            var badFiles = await driver.ReadTagAsync("Program.Files", DataType.String);
            TestSupport.Require(badFiles.Quality == TagQuality.Bad && badFiles.ErrorMessage!.Contains("directory offline"),
                "Program.Files directory failure must return Bad quality");
            TestSupport.Require(JingDiaoProgramPath.Resolve("O1.nc", "C:/O1.nc").Directory == "C:/",
                "Windows drive root was lost in the target path");
            await using var exported = await driver.ExportAddressSpaceAsync(ExportFormat.JSON);
            using var nodes = await JsonDocument.ParseAsync(exported);
            TestSupport.Require(nodes.RootElement.GetArrayLength() == 28, "JSON address export is invalid/incomplete");
            var upload = await driver.UploadProgramAsync(new MemoryStream([1, 2, 3]),
                new NCProgramMetadata { FileName = "local.nc", RemotePath = "/NC/SUB/O1234.nc" });
            TestSupport.Require(upload.Success && uploadedDirectory == "/NC/SUB" && uploadedName == "O1234.nc",
                "Upload lost target directory or target file name");
            var invalidUpload = await driver.UploadProgramAsync(new MemoryStream([1]),
                new NCProgramMetadata { FileName = "file.nc", RemotePath = "/NC/../file.nc" });
            TestSupport.Require(!invalidUpload.Success, "Upload accepted a traversing target path");
            TestSupport.Require(!driver.SupportsResume, "Unimplemented resume was advertised");
        }
        finally { await app.StopAsync(); }
    }
}
