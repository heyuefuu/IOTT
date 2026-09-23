namespace MachineConnectionApi.Services;

using System.Text.RegularExpressions;
using MachineConnectionApi.Models;
using MachineConnectionApi.Options;
using Microsoft.Extensions.Options;

public sealed class InfluxSettingsStore : IPostConfigureOptions<InfluxDbOptions>
{
    private readonly object _gate = new();
    private readonly IOptionsMonitorCache<InfluxDbOptions> _cache;
    private readonly JsonFileStore<InfluxSettingsRequest> _storage;
    private InfluxSettingsRequest? _saved;

    public InfluxSettingsStore(IOptionsMonitorCache<InfluxDbOptions> cache,
        string fileName = "influx-settings.json")
    {
        _cache = cache;
        _storage = new JsonFileStore<InfluxSettingsRequest>(fileName);
        _saved = _storage.ReadAll().LastOrDefault();
    }

    public void PostConfigure(string? name, InfluxDbOptions options)
    {
        lock (_gate)
        {
            if (_saved is null) return;
            options.Enabled = _saved.Enabled;
            options.Url = _saved.Url;
            options.Org = _saved.Org;
            options.Bucket = _saved.Bucket;
            options.Measurement = _saved.Measurement;
            options.Token = _saved.Token ?? "";
        }
    }

    public InfluxDbOptions Save(InfluxSettingsRequest request, InfluxDbOptions current)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(request.Token) && _saved is not null)
                request = request with { Token = _saved.Token };
            var next = Resolve(request, current);
            var saved = new InfluxSettingsRequest
            {
                Enabled = next.Enabled, Url = next.Url, Org = next.Org,
                Bucket = next.Bucket, Measurement = next.Measurement, Token = next.Token,
            };
            _storage.WriteAll([saved]);
            _saved = saved;
            _cache.Clear();
            return next;
        }
    }

    public static InfluxDbOptions Resolve(InfluxSettingsRequest request, InfluxDbOptions current)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.IsWellFormedOriginalString() ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("历史库地址须为 HTTP(S)，且不能包含用户名密码、查询参数或片段。");

        var org = request.Org?.Trim() ?? "";
        var bucket = request.Bucket?.Trim() ?? "";
        if (org.Length == 0 || bucket.Length == 0)
            throw new ArgumentException("请填写组织和数据库名称。");
        var measurement = request.Measurement?.Trim() ?? "";
        QuoteMeasurement(measurement);
        var token = string.IsNullOrWhiteSpace(request.Token) ? current.Token : request.Token.Trim();
        if (request.Enabled && string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("启用历史库前请填写访问令牌 Token。");
        if (token.Contains('\r') || token.Contains('\n'))
            throw new ArgumentException("访问令牌 Token 不能包含换行符。");

        return new InfluxDbOptions
        {
            Enabled = request.Enabled, Url = uri.AbsoluteUri.TrimEnd('/'),
            Org = org, Bucket = bucket, Measurement = measurement, Token = token,
            WriteTimeoutSeconds = current.WriteTimeoutSeconds,
            WriteRetryCount = current.WriteRetryCount,
            WriteRetryDelayMs = current.WriteRetryDelayMs,
        };
    }

    public static string QuoteMeasurement(string measurement)
    {
        if (measurement.Length > 128 ||
            !Regex.IsMatch(measurement, @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("表名须以字母或下划线开头，仅包含字母、数字和下划线，最长 128 字符。");
        return $"\"{measurement}\"";
    }
}
