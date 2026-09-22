using IndustrialIoT.Protocols.FileTransfer.Tests;

Console.WriteLine("File transfer regression tests");
await NfsRegressionTests.RunAll();
await SmbRegressionTests.RunAll();
if (args.Length == 2 && args[0] == "--ftp")
    await FtpRegressionTests.RunAll(int.Parse(args[1]));
Console.WriteLine($"Results: {Regression.Passed} passed, {Regression.Failed} failed");
Environment.ExitCode = Regression.Failed == 0 ? 0 : 1;
