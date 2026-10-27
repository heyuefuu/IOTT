namespace IndustrialIoT.Protocols.Addressing;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;

public sealed partial class PlcAddressRangeBrowser : IAddressSpaceBrowser
{
    public const int PageSize = 128;
    private const int MaximumPoints = 1_000_000;
    private readonly IReadOnlyList<PlcAddressArea> _areas;
    private readonly string _source;
    private readonly string _description;

    public PlcAddressRangeBrowser(IReadOnlyList<PlcAddressArea> areas, string source, string description)
    {
        ValidateAreas(areas);
        _areas = areas.ToArray();
        _source = source;
        _description = description;
    }

    public Task<IReadOnlyList<AddressNode>> BrowseAsync(string? parentPath = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(parentPath) || parentPath == "/")
            return Task.FromResult<IReadOnlyList<AddressNode>>(_areas.Select(area => Folder(area.Id,
                $"{area.DisplayName} ({area.Address(area.Start)}–{area.Address(area.End)})")).ToArray());

        var segments = parentPath.Trim('/').Split('/');
        var area = _areas.FirstOrDefault(candidate => candidate.Id.Equals(segments[0], StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("未知地址区域，请重新加载地址目录。", nameof(parentPath));
        var start = area.Start;
        var end = area.End;
        if (segments.Length > 2 || (segments.Length == 2 && !TryRange(segments[1], out start, out end))
            || start < area.Start || end > area.End || start > end)
            throw new ArgumentException("地址分页范围无效或超出已配置区域。", nameof(parentPath));

        var nodes = new List<AddressNode>();
        var count = (long)end - start + 1;
        long span = 1;
        while ((count + span - 1) / span > PageSize) span *= PageSize;
        for (long index = start; index <= end; index += span)
        {
            ct.ThrowIfCancellationRequested();
            var last = (int)Math.Min(index + span - 1, end);
            nodes.Add(span == 1 ? Variable(area, (int)index) : Folder(
                $"{area.Id}/{index}-{last}", $"{area.Address((int)index)}–{area.Address(last)}"));
        }
        return Task.FromResult<IReadOnlyList<AddressNode>>(nodes);
    }

    public async Task<Stream> ExportAddressSpaceAsync(ExportFormat format, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (format is not (ExportFormat.CSV or ExportFormat.JSON))
            throw new NotSupportedException("PLC 地址范围目录支持 CSV 或 JSON 导出，请选择这两种格式。 ");

        // Keep complete exports bounded in memory; ASP.NET disposes the returned stream after sending it.
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), $"plc-address-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            if (format == ExportFormat.JSON)
            {
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                options.Converters.Add(new JsonStringEnumConverter());
                await JsonSerializer.SerializeAsync(stream, Enumerate(ct), options, ct);
            }
            else
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true);
                await writer.WriteLineAsync("Path,DisplayName,DataType,IsReadable,IsWritable,Source,Description".AsMemory(), ct);
                foreach (var node in Enumerate(ct))
                {
                    var row = string.Join(",", new[] { node.Path, node.DisplayName, node.DataType.ToString(),
                        node.IsReadable.ToString(), node.IsWritable.ToString(), node.Source, node.Description }.Select(Csv));
                    await writer.WriteLineAsync(row.AsMemory(), ct);
                }
                await writer.FlushAsync(ct);
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private IEnumerable<AddressNode> Enumerate(CancellationToken ct)
    {
        foreach (var area in _areas)
            for (var index = area.Start; index <= area.End; index++)
            {
                ct.ThrowIfCancellationRequested();
                yield return Variable(area, index);
            }
    }

    private AddressNode Folder(string path, string name) => new()
    {
        Path = path, DisplayName = name, NodeType = AddressNodeType.Folder,
        Source = _source, Description = _description,
    };

    private AddressNode Variable(PlcAddressArea area, int index) => new()
    {
        Path = area.Address(index), DisplayName = area.Address(index), NodeType = AddressNodeType.Variable,
        DataType = area.DataType, IsReadable = true, IsWritable = area.IsWritable,
        Source = _source, Description = _description,
    };

    private static bool TryRange(string path, out int start, out int end)
    {
        start = end = 0;
        var parts = path.Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end);
    }

    private static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private static void ValidateAreas(IReadOnlyList<PlcAddressArea> areas)
    {
        if (areas.Count is 0 or > PageSize || areas.Select(area => area.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != areas.Count
            || areas.Any(area => !Regex.IsMatch(area.Id, "^[A-Z][A-Z0-9_]*$") || area.Start < 0 || area.End < area.Start || area.End == int.MaxValue)
            || areas.Sum(area => (long)area.End - area.Start + 1) > MaximumPoints)
            throw new AddressSpaceConfigurationException("地址区域无效：区域须唯一、索引非负且有序，总点数最多 1000000。 ");
    }
}
