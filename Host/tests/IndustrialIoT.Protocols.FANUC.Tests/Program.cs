using IndustrialIoT.Protocols.FANUC.Tests;

internal static class Program
{
    private static async Task Main()
    {
        Console.WriteLine("FOCAS batch read regression tests");
        await FocasBatchReadRegressionTests.RunAll();
        Console.WriteLine("All tests passed.");
    }
}
