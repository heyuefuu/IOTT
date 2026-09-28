using Microsoft.Data.SqlClient;
using MySqlConnector;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class TableCopy(string sourceConnection, string database, string table)
{
    private string Target => $"{Quote(database)}.{Quote(table)}";
    private static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public async Task RequireEmptyAsync(MySqlConnection target, MySqlTransaction transaction)
    {
        await using var command = new MySqlCommand($"SELECT COUNT(*) FROM {Target}", target, transaction);
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException($"{database}.{table} is not empty. Migration never overwrites target data.");
    }

    public async Task<TableReport> InspectAsync()
    {
        var rows = await ReadSourceAsync();
        return new(database, table, rows.Values.Count, Digest(rows.Values), false);
    }

    public async Task<TableReport> CopyAsync(MySqlConnection target, MySqlTransaction transaction)
    {
        var source = await ReadSourceAsync();
        await using var schema = new MySqlCommand($"SELECT * FROM {Target} LIMIT 0", target, transaction);
        await using (var schemaReader = await schema.ExecuteReaderAsync())
        {
            var targetColumns = Enumerable.Range(0, schemaReader.FieldCount).Select(schemaReader.GetName).ToHashSet();
            if (!targetColumns.SetEquals(source.Columns))
                throw new InvalidOperationException($"Column mismatch for {database}.{table}; inspect schema before migration.");
        }
        var columns = string.Join(',', source.Columns.Select(Quote));
        var parameters = string.Join(',', source.Columns.Select((_, index) => $"@value{index}"));
        await using var insert = new MySqlCommand($"INSERT INTO {Target} ({columns}) VALUES ({parameters})", target, transaction);
        for (var index = 0; index < source.Columns.Length; index++)
            insert.Parameters.Add(new MySqlParameter($"@value{index}", DBNull.Value));
        foreach (var row in source.Values)
        {
            for (var index = 0; index < row.Length; index++) insert.Parameters[index].Value = row[index];
            await insert.ExecuteNonQueryAsync();
        }
        return await CompareAsync(source, target, transaction);
    }

    public async Task<TableReport> VerifyAsync(MySqlConnection target, MySqlTransaction transaction)
        => await CompareAsync(await ReadSourceAsync(), target, transaction);

    private async Task<TableReport> CompareAsync(Rows source, MySqlConnection target, MySqlTransaction transaction)
    {
        var columns = string.Join(',', source.Columns.Select(Quote));
        await using var command = new MySqlCommand($"SELECT {columns} FROM {Target}", target, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var copied = await ReadRowsAsync(reader);
        var sourceHash = Digest(source.Values);
        if (source.Values.Count != copied.Values.Count || sourceHash != Digest(copied.Values))
            throw new InvalidOperationException($"Row count/content checksum mismatch for {database}.{table}.");
        return new(database, table, source.Values.Count, sourceHash, true);
    }

    private async Task<Rows> ReadSourceAsync()
    {
        await using var source = new SqlConnection(sourceConnection);
        await source.OpenAsync();
        await using var command = new SqlCommand($"SELECT * FROM [dbo].[{table}]", source) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync();
        return await ReadRowsAsync(reader);
    }

    private static async Task<Rows> ReadRowsAsync(DbDataReader reader)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var values = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            for (var index = 0; index < row.Length; index++) row[index] = Normalize(reader.GetValue(index));
            values.Add(row);
        }
        return new(columns, values);
    }

    private static object Normalize(object value)
    {
        if (value is DateTimeOffset offset) value = offset.UtcDateTime;
        if (value is DateTime date) return new DateTime(date.Ticks - date.Ticks % 10, DateTimeKind.Utc);
        return value;
    }

    private static string Digest(List<object[]> rows)
    {
        string? Canonical(object value) => value switch
        {
            DBNull => null,
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "1" : "0",
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
        var hashes = rows.Select(row => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row.Select(Canonical)))))).Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes))));
    }

    private sealed record Rows(string[] Columns, List<object[]> Values);
}
