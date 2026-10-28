namespace IndustrialIoT.Protocols.CAN;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;
using Microsoft.Extensions.Logging;

public sealed partial class CanDriver
{
    public async Task<TagValue> ReadTagAsync(string address, DataType dataType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var parsed = CanAddress.Parse(address);
            var frame = await ReadFrameAsync(parsed, ct);
            return new()
            {
                Address = address, DataType = dataType, Value = CanPayloadCodec.Decode(frame.Payload, dataType, parsed.BigEndian),
                Quality = TagQuality.Good, Timestamp = frame.Timestamp,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new()
            {
                Address = address, DataType = dataType, Value = Array.Empty<byte>(), Quality = TagQuality.Bad,
                Timestamp = DateTimeOffset.UtcNow, ErrorMessage = ex.Message,
            };
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(IReadOnlyList<TagReadRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return await Task.WhenAll(requests.Select(request => ReadTagAsync(request.Address, request.DataType, ct)));
    }

    public async Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var parsed = CanAddress.Parse(address);
            var bytes = CanPayloadCodec.Encode(value, dataType, parsed.BigEndian);
            EnsureConnected();
            var command = parsed.FramePrefix + bytes.Length + Convert.ToHexString(bytes);
            await CommandAsync(command, ct, requireConnected: true);
            return new() { Success = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "CAN frame write failed at {Address}", address);
            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }
}
