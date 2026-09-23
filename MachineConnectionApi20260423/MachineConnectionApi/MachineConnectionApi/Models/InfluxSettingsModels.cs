namespace MachineConnectionApi.Models;

using MachineConnectionApi.Options;

public sealed record InfluxSettingsRequest
{
    public bool Enabled { get; init; }
    public string Url { get; init; } = "";
    public string Org { get; init; } = "";
    public string Bucket { get; init; } = "";
    public string Measurement { get; init; } = "datapoint";
    public string? Token { get; init; }
}

public sealed record InfluxSettingsResponse(
    bool Enabled, string Url, string Org, string Bucket, string Measurement, bool HasToken)
{
    public static InfluxSettingsResponse From(InfluxDbOptions options) => new(
        options.Enabled, options.Url, options.Org, options.Bucket, options.Measurement,
        !string.IsNullOrWhiteSpace(options.Token));
}

public sealed record InfluxConnectionTestResult(bool Success, string Message);
