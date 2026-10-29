namespace IndustrialIoT.Protocols.Addressing;

using System.Text.RegularExpressions;
using HslCommunication;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

/// <summary>Byte 0 is the numeric low byte of a PLC word; byte 1 is its high byte.</summary>
public static class HslWordByteAccess
{
    public static (string WordAddress, int Shift) Parse(string address)
    {
        var parts = address.Trim().Split(";byte=", StringSplitOptions.None);
        if (parts.Length > 2 || parts.Length == 2 && parts[1] is not ("0" or "1")
            || !Regex.IsMatch(parts[0], @"^(?:s=\d+;)?[A-Za-z]+\d+$", RegexOptions.IgnoreCase))
            throw new FormatException("8-bit access requires a word address with optional ;byte=0 (low) or ;byte=1 (high).");
        return (parts[0], parts.Length == 2 && parts[1] == "1" ? 8 : 0);
    }

    public static async Task<TagValue> ReadAsync(Func<string, Task<OperateResult<ushort>>> read, SemaphoreSlim gate,
        string address, DataType type, Func<string, string> map, CancellationToken ct)
    {
        try
        {
            var (wordAddress, shift) = Parse(address);
            var mappedAddress = map(wordAddress);
            await gate.WaitAsync(ct);
            try
            {
                var result = await read(mappedAddress);
                ct.ThrowIfCancellationRequested();
                if (!result.IsSuccess) return Tag(address, type, null, result.Message ?? "PLC word read failed.");
                var unsigned = (byte)(result.Content >> shift);
                object value = type == DataType.Int8 ? (object)unchecked((sbyte)unsigned) : unsigned;
                return Tag(address, type, value, null);
            }
            finally { gate.Release(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return Tag(address, type, null, error.Message); }
    }

    public static async Task<WriteResult> WriteAsync(Func<string, Task<OperateResult<ushort>>> read,
        Func<string, ushort, Task<OperateResult>> write, SemaphoreSlim gate,
        string address, DataType type, object value, Func<string, string> map, CancellationToken ct)
    {
        try
        {
            var (wordAddress, shift) = Parse(address);
            var mappedAddress = map(wordAddress);
            var scalar = TagValueConversion.ConvertScalar(value, type);
            var converted = type == DataType.Int8 ? unchecked((byte)(sbyte)scalar) : (byte)scalar;
            await gate.WaitAsync(ct);
            try
            {
                var previous = await read(mappedAddress);
                if (!previous.IsSuccess) return new() { Success = false, ErrorMessage = previous.Message };
                ct.ThrowIfCancellationRequested();
                // The caller's connection gate covers the complete read/modify/write transaction.
                var merged = (ushort)((previous.Content & ~(0xff << shift)) | (converted << shift));
                var result = await write(mappedAddress, merged);
                return new() { Success = result.IsSuccess, ErrorMessage = result.IsSuccess ? null : result.Message };
            }
            finally { gate.Release(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new() { Success = false, ErrorMessage = error.Message }; }
    }

    private static TagValue Tag(string address, DataType type, object? value, string? error) => new()
    {
        Address = address, DataType = type, Value = value ?? 0,
        Quality = error is null ? TagQuality.Good : TagQuality.Bad,
        ErrorMessage = error, Timestamp = DateTimeOffset.UtcNow,
    };
}
