namespace IndustrialIoT.Protocols.Gsk;

using System.Text.Json;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

public sealed partial class GskWebServerDriver
{
    private static TagValue ToTagValue(string address, DataType dataType, JsonElement value)
    {
        var ok = TryConvertValue(value, dataType, out var converted, out var error);
        return new()
        {
            Address = address,
            DataType = dataType,
            Value = converted,
            Quality = ok ? TagQuality.Good : TagQuality.Bad,
            Timestamp = DateTimeOffset.UtcNow,
            ErrorMessage = error
        };
    }

    private static bool TryConvertValue(JsonElement value, DataType dataType, out object converted, out string? error)
    {
        try
        {
            converted = ConvertValue(value, dataType);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            converted = value.ToString();
            error = ex.Message;
            return false;
        }
    }

    private static JsonElement ExtractAddressValue(JsonElement root, string address)
    {
        if (address.StartsWith("Tool:", StringComparison.OrdinalIgnoreCase) &&
            TryGetToolOffsetValue(root, address, out var rootToolValue))
            return rootToolValue;

        var payload = Unwrap(root);
        if (payload.ValueKind == JsonValueKind.Array && payload.GetArrayLength() > 0)
            payload = payload[0];
        if (TryGetStaticValue(payload, address, out var staticValue)) return staticValue;
        if (address.StartsWith("Realtime.", StringComparison.OrdinalIgnoreCase) &&
            TryGetRealtimeFieldValue(payload, address, out var realtimeValue, out _))
            return realtimeValue;
        if (address.StartsWith("Tool:", StringComparison.OrdinalIgnoreCase) &&
            TryGetToolOffsetValue(payload, address, out var toolValue))
            return toolValue;
        if (TryGetProperty(payload, Tail(address), out var exact)) return exact;
        if (TryGetProperty(payload, address, out exact)) return exact;
        if (TryGetProperty(payload, "value", out exact)) return exact;
        return payload;
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (TryGetProperty(root, "data", out var data)) return data;
        if (TryGetProperty(root, "result", out var result)) return result;
        if (TryGetProperty(root, "value", out var value)) return value;
        return root;
    }

    private static bool TryGetStaticValue(JsonElement payload, string address, out JsonElement value)
    {
        value = default;
        if (!address.StartsWith("Static.", StringComparison.OrdinalIgnoreCase)) return false;
        return Tail(address).ToLowerInvariant() switch
        {
            "systemtype" => TryGetProperty(payload, "model", out value),
            "axiscount" => TryGetFirstArrayObjectProperty(payload, "axes", "totalcount", out value),
            "spindlecount" => TryGetFirstArrayObjectProperty(payload, "spindle", "totalcount", out value),
            "pathcount" => TryGetProperty(payload, "pathcount", out value),
            _ when TryGetStaticAxisValue(payload, address, out value) => true,
            _ => false
        };
    }

    private static bool TryGetStaticAxisValue(JsonElement payload, string address, out JsonElement value)
    {
        value = default;
        var parts = Tail(address).Split(['.', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        var section = parts[0].ToLowerInvariant() switch
        {
            "axisname" => "names",
            "axisdecimal" => "decimal",
            "axisunit" => "unit",
            _ => ""
        };
        return section.Length > 0 &&
            int.TryParse(parts[2], out var index) &&
            TryGetFirstAxesNestedArrayValue(payload, section, parts[1], index, out value);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        var found = false;
        value = default;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) ||
                    property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool TryGetFirstArrayObjectProperty(
        JsonElement element, string arrayName, string propertyName, out JsonElement value)
    {
        value = default;
        if (!TryGetProperty(element, arrayName, out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0)
            return false;

        return TryGetProperty(array[0], propertyName, out value);
    }

    private static bool TryGetFirstAxesNestedArrayValue(
        JsonElement element, string sectionName, string arrayName, int index, out JsonElement value)
    {
        value = default;
        if (!TryGetProperty(element, "axes", out var axes) ||
            axes.ValueKind != JsonValueKind.Array ||
            axes.GetArrayLength() == 0 ||
            !TryGetProperty(axes[0], sectionName, out var section) ||
            !TryGetProperty(section, arrayName, out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            index < 0 || index >= array.GetArrayLength())
            return false;

        value = array[index];
        return true;
    }

    private static readonly JsonElement TrueElement = ParseBoolElement(true);
    private static readonly JsonElement FalseElement = ParseBoolElement(false);

    private static JsonElement ParseBoolElement(bool v)
    {
        using var doc = JsonDocument.Parse(v ? "true" : "false");
        return doc.RootElement.Clone();
    }

    private static bool TryGetRealtimeFieldValue(JsonElement root, string address, out JsonElement value, out string? error)
    {
        error = null;
        value = default;

        var frame = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            ? root[0]
            : root;

        var parts = address.Split(['.', ':'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 &&
            parts[1].Equals("Cord", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts.Length > 3 ? parts[3] : "0", out var cordIndex))
        {
            if (TryGetNestedArray(frame, "cord", parts[2], cordIndex, out value)) return true;
            error = $"Realtime cord field '{parts[2]}' not present in latest WebSocket frame.";
            return false;
        }

        var name = parts.Length > 1 ? parts[1].ToLowerInvariant() : TailLast(address).ToLowerInvariant();
        var arrayIndex = parts.Length > 2 && int.TryParse(parts[^1], out var idx) ? idx : 0;

        if (name == "running")
        {
            if (TryGetProperty(frame, "state", out var stateField) &&
                stateField.ValueKind == JsonValueKind.Number &&
                stateField.TryGetInt32(out var stateNum))
            {
                value = stateNum == 2 ? TrueElement : FalseElement;
                return true;
            }
            value = FalseElement;
            return true;
        }

        bool found;
        switch (name)
        {
            case "mode": found = TryGetProperty(frame, "mode", out value); break;
            case "state": found = TryGetProperty(frame, "state", out value); break;
            case "programname": found = TryGetNested(frame, "gcode", "name", out value); break;
            case "lineno": found = TryGetNested(frame, "gcode", "line", out value); break;
            case "feedrate": found = TryGetNested(frame, "speed", "actual", out value); break;
            case "programfeedrate": found = TryGetNested(frame, "speed", "programe", out value); break;
            case "spindlespeed": found = TryGetNestedArray(frame, "spindle", "actspeed", arrayIndex, out value); break;
            case "spindlecmdspeed": found = TryGetNestedArray(frame, "spindle", "cmdspeed", arrayIndex, out value); break;
            case "feedoverride": found = TryGetNested(frame, "override", "feed", out value); break;
            case "spindleoverride": found = TryGetNested(frame, "override", "spindle", out value); break;
            case "rapidoverride": found = TryGetNested(frame, "override", "rappid", out value); break;
            case "jogoverride": found = TryGetNested(frame, "override", "jog", out value); break;
            case "mpgoverride": found = TryGetNested(frame, "override", "mpg", out value); break;
            case "esp": found = TryGetNested(frame, "event", "esp", out value); break;
            case "alm": found = TryGetNested(frame, "event", "alm", out value); break;
            case "partstarget": found = TryGetNested(frame, "parts", "target", out value); break;
            case "partscutted": found = TryGetNested(frame, "parts", "cutted", out value); break;
            case "runtime": found = TryGetNested(frame, "time", "run", out value); break;
            case "cuttime": found = TryGetNested(frame, "time", "cut", out value); break;
            case "toolno": found = TryGetNested(frame, "tool", "toolno", out value); break;
            case "offsetno": found = TryGetNested(frame, "tool", "offsetno", out value); break;
            default:
                error = $"Unknown realtime field '{name}'. See GSK WebServer SDK manual for available NC data fields.";
                return false;
        }

        if (!found)
            error = $"Realtime field '{name}' not present in latest WebSocket frame.";
        return found;
    }

    private static bool TryGetNested(JsonElement frame, string parentName, string childName, out JsonElement value)
    {
        value = default;
        return TryGetProperty(frame, parentName, out var parent) && TryGetProperty(parent, childName, out value);
    }

    private static bool TryGetNestedArrayFirst(JsonElement frame, string parentName, string arrayName, out JsonElement value)
        => TryGetNestedArray(frame, parentName, arrayName, 0, out value);

    private static bool TryGetNestedArray(
        JsonElement frame, string parentName, string arrayName, int index, out JsonElement value)
    {
        value = default;
        if (!TryGetProperty(frame, parentName, out var parent)) return false;
        if (!TryGetProperty(parent, arrayName, out var arr) ||
            arr.ValueKind != JsonValueKind.Array ||
            index < 0 || index >= arr.GetArrayLength())
            return false;
        value = arr[index];
        return true;
    }

    private static bool TryGetToolOffsetValue(JsonElement payload, string address, out JsonElement value)
    {
        value = default;
        var parts = Tail(address).Split([':', '.'], StringSplitOptions.RemoveEmptyEntries);
        var path = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 0;
        var type = parts.Length > 2 && int.TryParse(parts[2], out var t) ? t : 0;
        var axis = parts.Length > 3 && int.TryParse(parts[3], out var a) ? a : 0;
        return TryGetProperty(payload, "value", out var paths) &&
            paths.ValueKind == JsonValueKind.Array &&
            path >= 0 && path < paths.GetArrayLength() &&
            TryGetToolOffsetType(paths[path], type, axis, out value);
    }

    private static bool TryGetToolOffsetType(JsonElement pathValue, int type, int axis, out JsonElement value)
    {
        value = default;
        if (type is 0 or 1)
        {
            return TryGetProperty(pathValue, "axis", out var axes) &&
                axes.ValueKind == JsonValueKind.Array &&
                axis >= 0 && axis < axes.GetArrayLength() &&
                axes[axis].ValueKind == JsonValueKind.Array &&
                type < axes[axis].GetArrayLength() &&
                (value = axes[axis][type]).ValueKind != JsonValueKind.Undefined;
        }

        if (type is 2 or 3)
        {
            return TryGetProperty(pathValue, "r", out var r) &&
                r.ValueKind == JsonValueKind.Array &&
                type - 2 < r.GetArrayLength() &&
                (value = r[type - 2]).ValueKind != JsonValueKind.Undefined;
        }
        return TryGetProperty(pathValue, "t", out value);
    }

    private static string TailLast(string address)
    {
        var parts = address.Split(['.', ':'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : address;
    }

    private static object ConvertValue(JsonElement value, DataType dataType) => dataType switch
    {
        DataType.Bool => value.ValueKind == JsonValueKind.True ||
                         value.ValueKind == JsonValueKind.String && bool.Parse(value.GetString() ?? "false") ||
                         value.ValueKind == JsonValueKind.Number && value.GetInt32() != 0,
        DataType.Int16 => value.GetInt16(),
        DataType.Int32 => value.GetInt32(),
        DataType.Int64 => value.GetInt64(),
        DataType.UInt16 => value.GetUInt16(),
        DataType.UInt32 => value.GetUInt32(),
        DataType.Float => value.GetSingle(),
        DataType.Double => value.GetDouble(),
        DataType.String => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString(),
        DataType.ByteArray => ConvertByteArray(value),
        _ => value.ToString()
    };

    private static byte[] ConvertByteArray(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return Convert.FromBase64String(value.GetString() ?? "");
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(x => x.GetByte()).ToArray();
        return [];
    }

    private static IReadOnlyList<ProgramFileEntry> ParseProgramFiles(JsonElement root)
    {
        var payload = Unwrap(root);
        if (payload.ValueKind != JsonValueKind.Array) return [];

        var files = new List<ProgramFileEntry>();
        foreach (var item in payload.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var fileName = item.GetString() ?? "";
                files.Add(File(fileName, fileName, null));
                continue;
            }

            var name = ReadString(item, "name") ?? ReadString(item, "fileName") ?? "";
            var path = ReadString(item, "path") ?? ReadString(item, "remotePath") ?? name;
            var isDirectory = ReadBool(item, "isDirectory") ?? ReadBool(item, "directory") ?? false;
            files.Add(new()
            {
                Path = path,
                Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(path) : name,
                IsDirectory = isDirectory,
                SizeBytes = ReadLong(item, "size") ?? ReadLong(item, "sizeBytes"),
                ModifiedAt = ReadDate(item, "modifiedAt") ?? ReadDate(item, "modified"),
                CanDownload = !isDirectory,
                CanUpload = true,
                HasChildren = isDirectory
            });
        }

        return files;
    }

    private static ProgramFileEntry File(string path, string name, long? size) => new()
    {
        Path = path,
        Name = name,
        IsDirectory = false,
        SizeBytes = size,
        CanDownload = true,
        CanUpload = true,
        HasChildren = false
    };

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null
        };
    }

    private static long? ReadLong(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) &&
        DateTimeOffset.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;
}
