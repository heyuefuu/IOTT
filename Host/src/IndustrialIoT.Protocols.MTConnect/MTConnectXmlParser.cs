namespace IndustrialIoT.Protocols.MTConnect;

using System.Globalization;
using System.Xml.Linq;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// MTConnect XML 解析器 — 解析 /probe（设备/组件树）与 /current（实时快照）。
/// /probe 根元素 MTConnectDevices，含 Devices/Device/(DataItems/DataItem | Components/.../DataItems)
/// /current 根元素 MTConnectStreams，含 Streams/DeviceStream/ComponentStream/(Samples|Events|Condition)/*
/// </summary>
internal static class MTConnectXmlParser
{
    public sealed record CurrentValue(string Raw, DateTimeOffset Timestamp, string ElementName);

    /// <summary>解析 /probe XML → 地址空间树。Path 使用 DataItem.id（MTConnect 标准路由键）。</summary>
    public static IReadOnlyList<AddressNode> ParseProbe(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("Empty XML");
        var ns = root.GetDefaultNamespace();

        var devices = root.Element(ns + "Devices")?.Elements(ns + "Device") ?? [];
        return devices.Select(d => BuildComponentNode(d, ns)).ToList();
    }

    private static AddressNode BuildComponentNode(XElement compOrDevice, XNamespace ns)
    {
        var name = compOrDevice.Attribute("name")?.Value
            ?? compOrDevice.Attribute("id")?.Value
            ?? compOrDevice.Name.LocalName;

        var children = new List<AddressNode>();

        // DataItems 直接子节点
        foreach (var di in compOrDevice.Element(ns + "DataItems")?.Elements(ns + "DataItem") ?? [])
            children.Add(BuildDataItemNode(di));

        // Components 嵌套 — 每种组件（Axes/Controller/Path/Rotary/Linear...）展平递归
        foreach (var comp in compOrDevice.Element(ns + "Components")?.Elements() ?? [])
            foreach (var sub in comp.Elements())
                children.Add(BuildComponentNode(sub, ns));

        return new AddressNode
        {
            Path = compOrDevice.Attribute("id")?.Value ?? name,
            DisplayName = name,
            NodeType = AddressNodeType.Folder,
            IsReadable = false,
            Children = children,
        };
    }

    private static AddressNode BuildDataItemNode(XElement dataItem)
    {
        var id = dataItem.Attribute("id")?.Value ?? "";
        var name = dataItem.Attribute("name")?.Value ?? id;
        var type = dataItem.Attribute("type")?.Value ?? "";
        return new AddressNode
        {
            Path = id,
            DisplayName = string.IsNullOrEmpty(name) ? id : $"{name} ({type})",
            NodeType = AddressNodeType.Variable,
            DataType = ResolveDataType(type, dataItem.Attribute("category")?.Value),
            IsReadable = true,
            IsWritable = false,
        };
    }

    /// <summary>解析 /current XML → DataItemId → 最新值快照。</summary>
    public static Dictionary<string, CurrentValue> ParseCurrent(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("Empty XML");
        var ns = root.GetDefaultNamespace();
        var map = new Dictionary<string, CurrentValue>(StringComparer.OrdinalIgnoreCase);

        // 三类叶子元素：Samples/Events/Condition 下的任意子元素都是 DataItem 的值
        var leafContainers = root.Descendants(ns + "Samples")
            .Concat(root.Descendants(ns + "Events"))
            .Concat(root.Descendants(ns + "Condition"));

        foreach (var container in leafContainers)
            foreach (var leaf in container.Elements())
            {
                var id = leaf.Attribute("dataItemId")?.Value;
                if (string.IsNullOrEmpty(id)) continue;
                var tsAttr = leaf.Attribute("timestamp")?.Value;
                var ts = DateTimeOffset.TryParse(tsAttr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed : DateTimeOffset.UtcNow;
                map[id] = new CurrentValue(leaf.Value, ts, leaf.Name.LocalName);
            }

        return map;
    }

    public static DataType ResolveDataType(string mtconnectType, string? category = null)
    {
        if (string.Equals(category, "EVENT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "CONDITION", StringComparison.OrdinalIgnoreCase))
            return DataType.String;

        return mtconnectType.ToUpperInvariant() switch
        {
            "PART_COUNT" or "LINE_NUMBER" or "ACCUMULATED_TIME" => DataType.Int32,
            "POSITION" or "LOAD" or "TEMPERATURE" or "ANGLE" or
            "ROTARY_VELOCITY" or "PATH_FEEDRATE" or "AXIS_FEEDRATE" or
            "SPINDLE_SPEED" or "VOLTAGE" or "AMPERAGE" or "PRESSURE" => DataType.Float,
            _ => DataType.String,
        };
    }

    public static object CoerceValue(string raw, DataType target) => target switch
    {
        DataType.Bool => bool.TryParse(raw, out var b) ? b : raw,
        DataType.Int16 => short.TryParse(raw, CultureInfo.InvariantCulture, out var i) ? i : (object)raw,
        DataType.Int32 => int.TryParse(raw, CultureInfo.InvariantCulture, out var i) ? i : (object)raw,
        DataType.Int64 => long.TryParse(raw, CultureInfo.InvariantCulture, out var i) ? i : (object)raw,
        DataType.Float => float.TryParse(raw, CultureInfo.InvariantCulture, out var f) ? f : (object)raw,
        DataType.Double => double.TryParse(raw, CultureInfo.InvariantCulture, out var d) ? d : (object)raw,
        _ => raw,
    };
}
