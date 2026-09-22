namespace IndustrialIoT.Protocols.FileTransfer.Tests;

using System.Reflection;

internal static class Regression
{
    public static int Passed { get; private set; }
    public static int Failed { get; private set; }

    public static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test();
            Passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            Failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    public static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { return; }
        throw new InvalidOperationException("Operation unexpectedly accepted");
    }
}
