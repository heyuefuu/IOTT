namespace IndustrialIoT.Protocols.NCLink;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>
/// NC-Link 国标协议消息构建器与解析器。
/// 基于 MQTT pub/sub，消息格式遵循 NC-Link 标准（非 JSON-RPC）。
/// </summary>
public static class NCLinkProtocol
{
    private static int _messageSeq;

    // ── Topic 模板 ──────────────────────────────────────────────────────

    public static string QueryRequestTopic(string guid) => $"Query/Request/{guid}";
    public static string QueryResponseTopic(string guid) => $"Query/Response/{guid}";
    public static string SetRequestTopic(string guid) => $"Set/Request/{guid}";
    public static string SetResponseTopic(string guid) => $"Set/Response/{guid}";
    public static string ProbeQueryRequestTopic(string guid) => $"Probe/Query/Request/{guid}";
    public static string ProbeQueryResponseTopic(string guid) => $"Probe/Query/Response/{guid}";
    public static string ProbeSetRequestTopic(string guid) => $"Probe/Set/Request/{guid}";
    public static string ProbeSetResponseTopic(string guid) => $"Probe/Set/Response/{guid}";
    public static string ProbeVersionTopic(string guid) => $"Probe/Version/{guid}";
    public static string ProbeVersionResponseTopic(string guid) => $"Probe/Version/Response/{guid}";
    public static string RegisterRequestTopic(string guid) => $"Register/Request/{guid}";
    public static string RegisterResponseTopic(string guid) => $"Register/Response/{guid}";
    public static string SampleTopic(string guid, string channelId) => $"Sample/{guid}/{channelId}";

    /// <summary>返回连接后需订阅的所有响应 Topic。</summary>
    public static string[] GetResponseTopics(string guid) =>
    [
        QueryResponseTopic(guid),
        SetResponseTopic(guid),
        ProbeQueryResponseTopic(guid),
        ProbeSetResponseTopic(guid),
        ProbeVersionResponseTopic(guid),
        RegisterResponseTopic(guid),
    ];

    // ── 消息 ID 生成 ────────────────────────────────────────────────────

    public static string NextMessageId()
    {
        var seq = Interlocked.Increment(ref _messageSeq);
        return $"msg-{seq:D6}";
    }

    // ── 消息构建 ────────────────────────────────────────────────────────

    /// <summary>
    /// 构建 Query 请求 — 读取指定 DataItem。
    /// 发布到 Query/Request/{guid}
    /// </summary>
    public static string BuildQueryRequest(string messageId, IEnumerable<string> dataItemIds)
    {
        var obj = new JsonObject
        {
            ["@id"] = messageId,
            ["ids"] = new JsonArray(
                dataItemIds.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray())
        };
        return obj.ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// 构建 Set 请求 — 写入指定 DataItem。
    /// 发布到 Set/Request/{guid}
    /// </summary>
    public static string BuildSetRequest(string messageId, IEnumerable<SetItem> items)
    {
        var obj = new JsonObject
        {
            ["@id"] = messageId,
            ["items"] = new JsonArray(
                items.Select(i =>
                {
                    var item = new JsonObject
                    {
                        ["id"] = i.Id,
                        ["operation"] = i.Operation,
                    };
                    item["value"] = JsonSerializer.SerializeToNode(i.Value, SerializerOptions);
                    return (JsonNode)item;
                }).ToArray())
        };
        return obj.ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// 构建 Probe/Query 请求 — 获取设备模型。
    /// 发布到 Probe/Query/Request/{guid}
    /// </summary>
    public static string BuildProbeQueryRequest(string messageId)
    {
        var obj = new JsonObject { ["@id"] = messageId };
        return obj.ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// 构建 Probe/Version 请求 — 查询设备模型版本。
    /// 发布到 Probe/Version/{guid}
    /// </summary>
    public static string BuildProbeVersionRequest(string messageId, string? currentVersion = null)
    {
        var obj = new JsonObject { ["@id"] = messageId };
        if (currentVersion is not null)
            obj["version"] = currentVersion;
        return obj.ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// 构建 Register 请求 — 设备注册。
    /// 发布到 Register/Request/{guid}
    /// </summary>
    public static string BuildRegisterRequest(string messageId, string guid)
    {
        var obj = new JsonObject
        {
            ["@id"] = messageId,
            ["guid"] = guid,
        };
        return obj.ToJsonString(SerializerOptions);
    }

    // ── 响应解析 ────────────────────────────────────────────────────────

    /// <summary>解析通用响应，提取 @id 和 code。</summary>
    public static NCLinkResponse ParseResponse(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new NCLinkProtocolException("Failed to parse NC-Link response");
        return new NCLinkResponse
        {
            MessageId = node["@id"]?.GetValue<string>() ?? "",
            Code = node["code"]?.GetValue<string>() ?? "",
            Raw = node,
        };
    }

    /// <summary>解析 Query 响应，提取 values 数组。</summary>
    public static IReadOnlyList<QueryResultValue> ParseQueryResponse(JsonObject raw)
    {
        var values = raw["values"]?.AsArray();
        if (values is null) return [];

        return values.Select(v =>
        {
            var obj = v!.AsObject();
            return new QueryResultValue
            {
                Id = obj["id"]?.GetValue<string>() ?? "",
                Value = obj["value"],
                Quality = obj["quality"]?.GetValue<string>(),
                Timestamp = obj["timestamp"]?.GetValue<long>(),
            };
        }).ToList();
    }

    /// <summary>解析 Probe 响应，提取设备模型。</summary>
    public static NCLinkDeviceModel? ParseProbeResponse(JsonObject raw)
    {
        // Probe 响应可能直接是设备对象，也可能包含在 devices 数组中
        JsonObject? deviceNode = null;

        if (raw["devices"]?.AsArray() is { Count: > 0 } devices)
            deviceNode = devices[0]!.AsObject();
        else if (raw["id"] is not null || raw["guid"] is not null)
            deviceNode = raw;

        if (deviceNode is null) return null;

        var model = new NCLinkDeviceModel
        {
            Id = deviceNode["id"]?.GetValue<string>() ?? "",
            Guid = deviceNode["guid"]?.GetValue<string>() ?? "",
            Version = deviceNode["version"]?.GetValue<string>(),
        };

        // 解析 dataItems
        if (deviceNode["dataItems"]?.AsArray() is JsonArray dataItems)
        {
            foreach (var di in dataItems)
            {
                var diObj = di!.AsObject();
                model.DataItems.Add(new NCLinkDataItem
                {
                    Id = diObj["id"]?.GetValue<string>() ?? "",
                    Name = diObj["name"]?.GetValue<string>() ?? "",
                    Type = diObj["type"]?.GetValue<string>() ?? "",
                    Settable = diObj["settable"]?.GetValue<bool>() ?? false,
                    Unit = diObj["unit"]?.GetValue<string>(),
                });
            }
        }

        // 递归解析 components
        if (deviceNode["components"]?.AsArray() is JsonArray components)
        {
            foreach (var comp in components)
                ParseComponentRecursive(comp!.AsObject(), model.DataItems);
        }

        // 解析 sample channels
        if (deviceNode["configs"]?.AsArray() is JsonArray configs)
        {
            foreach (var cfg in configs)
            {
                var cfgObj = cfg!.AsObject();
                if (cfgObj["type"]?.GetValue<string>() == "SAMPLE_CHANNEL")
                {
                    model.SampleChannels.Add(new NCLinkSampleChannel
                    {
                        Id = cfgObj["id"]?.GetValue<string>() ?? "",
                        SampleIntervalMs = cfgObj["sampleInterval"]?.GetValue<int>() ?? 1000,
                        UploadIntervalMs = cfgObj["uploadInterval"]?.GetValue<int>() ?? 1000,
                        DataItemIds = cfgObj["ids"]?.AsArray()
                            ?.Select(x => x!.AsObject()["id"]?.GetValue<string>() ?? "")
                            .ToList() ?? [],
                    });
                }
            }
        }

        return model;
    }

    /// <summary>解析 Sample 消息中的数据值。</summary>
    public static IReadOnlyList<QueryResultValue> ParseSampleData(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node is null) return [];

        var values = node["values"]?.AsArray();
        if (values is null) return [];

        return values.Select(v =>
        {
            var obj = v!.AsObject();
            return new QueryResultValue
            {
                Id = obj["id"]?.GetValue<string>() ?? "",
                Value = obj["value"],
                Quality = obj["quality"]?.GetValue<string>(),
                Timestamp = obj["timestamp"]?.GetValue<long>(),
            };
        }).ToList();
    }

    // ── 文件传输消息构建 ────────────────────────────────────────────────

    /// <summary>构建文件上传分块请求（Set/Request）。</summary>
    public static string BuildFileUploadChunkRequest(
        string messageId, string fileDataItemId, string remoteKey,
        long offset, int length, byte[] data)
    {
        return BuildSetRequest(messageId, [new(fileDataItemId, "set_value",
            new Dictionary<string, object>
            {
                ["key"] = remoteKey, ["offset"] = offset,
                ["length"] = length, ["value"] = Convert.ToBase64String(data),
            })]);
    }

    /// <summary>构建文件下载分块请求（Set/Request + get_value）。</summary>
    public static string BuildFileDownloadChunkRequest(
        string messageId, string fileDataItemId, string remoteKey,
        long offset, int length)
    {
        return BuildSetRequest(messageId, [new(fileDataItemId, "get_value",
            new Dictionary<string, object>
            {
                ["key"] = remoteKey, ["offset"] = offset, ["length"] = length,
            })]);
    }

    /// <summary>构建文件列表请求。</summary>
    public static string BuildFileListRequest(
        string messageId, string fileDataItemId, string directoryKey)
    {
        return BuildSetRequest(messageId, [new(fileDataItemId, "get_keys",
            new Dictionary<string, object> { ["key"] = directoryKey })]);
    }

    /// <summary>构建创建目录请求。</summary>
    public static string BuildFileCreateDirectoryRequest(
        string messageId, string fileDataItemId, string directoryKey)
    {
        return BuildSetRequest(messageId, [new(fileDataItemId, "add",
            new Dictionary<string, object> { ["key"] = directoryKey })]);
    }

    /// <summary>从 Set 响应解析文件传输分块结果。</summary>
    public static FileChunkResponse? ParseFileChunkResponse(JsonObject raw)
    {
        var values = raw["values"]?.AsArray();
        if (values is null or { Count: 0 }) return null;

        var valueNode = values[0]!.AsObject()["value"];
        if (valueNode is not JsonObject vo) return new FileChunkResponse();

        return new FileChunkResponse
        {
            TransmittedLength = vo["TransmitedLength"]?.GetValue<long>()
                ?? vo["transmitedLength"]?.GetValue<long>() ?? 0,
            FileTotalSize = vo["FileTotalSize"]?.GetValue<long>()
                ?? vo["fileTotalSize"]?.GetValue<long>(),
            Data = vo["value"]?.GetValue<string>(),
            Offset = vo["offset"]?.GetValue<long>() ?? 0,
            Length = vo["length"]?.GetValue<int>() ?? 0,
        };
    }

    // ── 辅助 ────────────────────────────────────────────────────────────

    private static void ParseComponentRecursive(JsonObject comp, List<NCLinkDataItem> items)
    {
        if (comp["dataItems"]?.AsArray() is JsonArray dataItems)
        {
            foreach (var di in dataItems)
            {
                var diObj = di!.AsObject();
                items.Add(new NCLinkDataItem
                {
                    Id = diObj["id"]?.GetValue<string>() ?? "",
                    Name = diObj["name"]?.GetValue<string>() ?? "",
                    Type = diObj["type"]?.GetValue<string>() ?? "",
                    Settable = diObj["settable"]?.GetValue<bool>() ?? false,
                    Unit = diObj["unit"]?.GetValue<string>(),
                    ComponentPath = comp["id"]?.GetValue<string>(),
                });
            }
        }

        if (comp["components"]?.AsArray() is JsonArray subComps)
        {
            foreach (var sub in subComps)
                ParseComponentRecursive(sub!.AsObject(), items);
        }
    }

    public static void ThrowIfError(NCLinkResponse response)
    {
        if (!string.IsNullOrEmpty(response.Code)
            && !response.Code.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new NCLinkProtocolException($"NC-Link error: {response.Code}");
        }

        var itemError = response.Raw["results"]?.AsArray()?
            .Select(x => x?.AsObject())
            .FirstOrDefault(x =>
                x?["code"]?.GetValue<string>() is { } code &&
                !code.Equals("OK", StringComparison.OrdinalIgnoreCase));
        if (itemError is null) return;

        var itemId = itemError["id"]?.GetValue<string>() ?? "";
        var itemCode = itemError["code"]?.GetValue<string>() ?? "UNKNOWN";
        throw new NCLinkProtocolException(
            string.IsNullOrEmpty(itemId)
                ? $"NC-Link item error: {itemCode}"
                : $"NC-Link item error: {itemId}={itemCode}");
    }

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

// ── 数据模型 ────────────────────────────────────────────────────────────

public record SetItem(string Id, string Operation, object Value);

public class NCLinkResponse
{
    public required string MessageId { get; init; }
    public required string Code { get; init; }
    public required JsonObject Raw { get; init; }
}

public class QueryResultValue
{
    public required string Id { get; init; }
    public JsonNode? Value { get; init; }
    public string? Quality { get; init; }
    public long? Timestamp { get; init; }
}

public class NCLinkDeviceModel
{
    public string Id { get; set; } = "";
    public string Guid { get; set; } = "";
    public string? Version { get; set; }
    public List<NCLinkDataItem> DataItems { get; set; } = [];
    public List<NCLinkSampleChannel> SampleChannels { get; set; } = [];
}

public class NCLinkDataItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Settable { get; init; }
    public string? Unit { get; init; }
    public string? ComponentPath { get; init; }
}

public class NCLinkSampleChannel
{
    public required string Id { get; init; }
    public int SampleIntervalMs { get; init; }
    public int UploadIntervalMs { get; init; }
    public List<string> DataItemIds { get; init; } = [];
}

/// <summary>Protocol-level exception for NC-Link communication errors.</summary>
public class NCLinkProtocolException : Exception
{
    public NCLinkProtocolException(string message) : base(message) { }
    public NCLinkProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

public class FileChunkResponse
{
    public long TransmittedLength { get; init; }
    public long? FileTotalSize { get; init; }
    public string? Data { get; init; }
    public long Offset { get; init; }
    public int Length { get; init; }
}
