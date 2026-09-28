namespace IndustrialIoT.Protocols.JingDiao;

using System.Text.Json;

public sealed class JingDiaoShimLease : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, SharedProcess> Processes = new(StringComparer.Ordinal);
    private static readonly HttpClient HealthClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string key;
    private int disposed;

    private JingDiaoShimLease(string key) => this.key = key;

    public static async Task<JingDiaoShimLease> AcquireAsync(
        Uri baseUri, string? executablePath, TimeSpan timeout, CancellationToken ct, bool autoStart = true)
    {
        var normalizedUri = Normalize(baseUri);
        var key = normalizedUri.AbsoluteUri;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Processes.TryGetValue(key, out var existing))
            {
                await EnsureReadyAsync(existing, ct, autoStart).ConfigureAwait(false);
                existing.References++;
                return new JingDiaoShimLease(key);
            }
            var shared = new SharedProcess
            {
                BaseUri = normalizedUri, Timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(10), ExecutablePath = executablePath,
                References = 1,
            };
            try { await EnsureReadyAsync(shared, ct, autoStart).ConfigureAwait(false); }
            catch
            {
                shared.Process?.Dispose();
                shared.Stop.Dispose();
                throw;
            }
            Processes.Add(key, shared);
            _ = MonitorAsync(shared);
            return new JingDiaoShimLease(key);
        }
        finally { Gate.Release(); }
    }

    private static Uri Normalize(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttp || !baseUri.IsLoopback
            || baseUri.AbsolutePath != "/" || baseUri.Query.Length != 0
            || baseUri.Fragment.Length != 0 || baseUri.UserInfo.Length != 0)
            throw new ArgumentException("JingDiao shim requires a loopback HTTP origin.", nameof(baseUri));
        return new UriBuilder(baseUri) { Host = baseUri.Host == "localhost" ? "127.0.0.1" : baseUri.Host }.Uri;
    }

    private static async Task EnsureReadyAsync(SharedProcess shared, CancellationToken ct, bool autoStart = true)
    {
        if (shared.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shared.Timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(shared.Timeout);
        if (await ProbeAsync(shared.BaseUri, deadline.Token).ConfigureAwait(false)) return;
        if (!autoStart && shared.Process is null)
        {
            await WaitForReadyAsync(shared.BaseUri, shared.Timeout, ct).ConfigureAwait(false);
            return;
        }
        if (shared.Process is null || shared.Process.HasExited)
        {
            var executablePath = shared.ExecutablePath ?? JingDiaoShimProcess.ResolveDefaultPath()
                ?? throw new FileNotFoundException("JingDiao shim executable was not found. Configure ShimPath.");
            shared.Process?.Dispose();
            shared.Process = JingDiaoShimProcess.Start(shared.BaseUri.GetLeftPart(UriPartial.Authority), executablePath);
        }
        await WaitForReadyAsync(shared.BaseUri, shared.Timeout, ct).ConfigureAwait(false);
    }

    public static async Task WaitForReadyAsync(Uri baseUri, TimeSpan timeout, CancellationToken ct)
    {
        var normalizedUri = Normalize(baseUri);
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromSeconds(10);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (!await ProbeAsync(normalizedUri, deadline.Token).ConfigureAwait(false))
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"JingDiao shim at '{normalizedUri}' did not become ready within {timeout}.");
        }
    }

    private static async Task<bool> ProbeAsync(Uri baseUri, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        HttpResponseMessage response;
        try { response = await HealthClient.GetAsync(new Uri(baseUri, "health"), deadline.Token).ConfigureAwait(false); }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"JingDiao SDK health failed ({(int)response.StatusCode}): {body}");
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.GetProperty("service").GetString() == "jingdiao"
                    && root.GetProperty("protocolVersion").GetInt32() == 1
                    && root.GetProperty("architecture").GetString() == "x86"
                    && root.GetProperty("status").GetString() == "ok") return true;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new InvalidOperationException("Incompatible JingDiao shim health response. Update the host and shim together.");
        }
    }

    private static async Task MonitorAsync(SharedProcess shared)
    {
        var delay = TimeSpan.FromSeconds(2);
        try
        {
            while (true)
            {
                await Task.Delay(delay, shared.Stop.Token).ConfigureAwait(false);
                await Gate.WaitAsync(shared.Stop.Token).ConfigureAwait(false);
                try
                {
                    if (shared.References == 0) return;
                    if (shared.Process is not null && shared.Process.HasExited)
                        await EnsureReadyAsync(shared, shared.Stop.Token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(2);
                }
                catch (OperationCanceledException) when (shared.Stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("JingDiao shim restart failed: {0}", error);
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
                finally { Gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (shared.Stop.IsCancellationRequested) { }
        finally { shared.Stop.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Gate.Wait();
        try
        {
            var shared = Processes[key];
            if (--shared.References != 0) return;
            Processes.Remove(key);
            shared.Stop.Cancel();
            shared.Process?.Dispose();
        }
        finally { Gate.Release(); }
    }

    private sealed class SharedProcess
    {
        public required Uri BaseUri { get; init; }
        public required TimeSpan Timeout { get; init; }
        public string? ExecutablePath { get; init; }
        public JingDiaoShimProcess? Process { get; set; }
        public CancellationTokenSource Stop { get; } = new();
        public int References { get; set; }
    }
}
