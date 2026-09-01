namespace IndustrialIoT.Host.Services;

using Microsoft.VisualBasic.FileIO;

public sealed record TagImportCsvRow(
    string Address,
    string DataType,
    string GroupName,
    int IntervalMs,
    string? DisplayName,
    string? Unit);

public static class TagImportCsvParser
{
    public static Task<IReadOnlyList<TagImportCsvRow>> ParseAsync(
        TextReader reader, CancellationToken ct = default)
    {
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true,
        };
        parser.SetDelimiters(",");

        var headers = parser.ReadFields() ?? [];
        var columns = headers.Select((name, index) => new { name, index })
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var rows = new List<TagImportCsvRow>();

        while (!parser.EndOfData)
        {
            ct.ThrowIfCancellationRequested();
            var fields = parser.ReadFields();
            if (fields is null || fields.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(new(
                Get(fields, columns, "Address"),
                Get(fields, columns, "DataType"),
                Get(fields, columns, "GroupName"),
                int.TryParse(Get(fields, columns, "IntervalMs"), out var ms) ? ms : 0,
                GetOptional(fields, columns, "DisplayName"),
                GetOptional(fields, columns, "Unit")));
        }

        return Task.FromResult<IReadOnlyList<TagImportCsvRow>>(rows);
    }

    private static string Get(string[] fields, Dictionary<string, int> columns, string name) =>
        columns.TryGetValue(name, out var i) && i < fields.Length ? fields[i].Trim() : "";

    private static string? GetOptional(string[] fields, Dictionary<string, int> columns, string name)
    {
        var value = Get(fields, columns, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
