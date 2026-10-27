namespace IndustrialIoT.Protocols.Addressing;

using System.Text.Json;
using System.Text.Json.Serialization;
using IndustrialIoT.Domain.ValueObjects;

public sealed partial class PlcAddressRangeBrowser
{
    public static IReadOnlyList<PlcAddressArea> Configure(
        IReadOnlyList<PlcAddressArea> definitions, DeviceConnectionConfig config, bool requireExplicitRanges = false)
    {
        var raw = config.ExtendedProperties.FirstOrDefault(pair =>
            pair.Key.Equals("AddressSpaceRanges", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (requireExplicitRanges)
                throw new AddressSpaceConfigurationException(
                    "当前型号尚无已核验的完整地址范围。请在地址浏览页配置区域和起止索引，或在设备扩展属性 AddressSpaceRanges 中填写 JSON 数组。可选区域："
                    + string.Join(", ", definitions.Select(area => area.Id)));
            ValidateAreas(definitions);
            return definitions.ToArray();
        }

        if (raw.Length > 65536)
            throw new AddressSpaceConfigurationException("AddressSpaceRanges 配置过长，最多 65536 个字符。 ");
        ConfiguredRange[] ranges;
        try
        {
            ranges = JsonSerializer.Deserialize<ConfiguredRange[]>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                NumberHandling = JsonNumberHandling.Strict,
            }) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new AddressSpaceConfigurationException(
                "AddressSpaceRanges 必须是 JSON 数组，每项包含 area、整数 start 和 end，例如 [{\"area\":\"DM\",\"start\":0,\"end\":127}]。");
        }

        var result = new List<PlcAddressArea>();
        foreach (var range in ranges)
        {
            var definition = definitions.FirstOrDefault(area => area.Id.Equals(range?.Area, StringComparison.OrdinalIgnoreCase));
            if (definition is null || range?.Start is not { } start || range.End is not { } end)
                throw new AddressSpaceConfigurationException("地址范围区域未知或缺少起止索引。可选区域：" + string.Join(", ", definitions.Select(area => area.Id)));
            if (start < definition.Start || end > definition.End || end < start)
                throw new AddressSpaceConfigurationException($"{definition.Id} 的连续索引范围须在 {definition.Start}–{definition.End} 内，且结束索引不能小于起始索引。");
            result.Add(definition with { Start = start, End = end });
        }
        ValidateAreas(result);
        return result;
    }

    private sealed record ConfiguredRange(string? Area, int? Start, int? End);
}
