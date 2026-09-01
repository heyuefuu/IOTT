namespace IndustrialIoT.Protocols.Gsk;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public sealed partial class GskWebServerDriver
{
    private static readonly TimeSpan RealtimeReconnectBackoff = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RealtimeReceiveTimeout = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _realtimeCts;
    private Task? _realtimeTask;
    private ClientWebSocket? _realtimeWs;
    private JsonDocument? _latestRealtimeDoc;
    private bool _hasRealtimeFrame;
    private string? _lastRealtimeError;
    private string? _lastRealtimeEndpoint;
    private readonly object _realtimeLock = new();
    private TaskCompletionSource<bool> _firstFrameTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void StartRealtimeFeed()
    {
        var options = _options!;
        if (string.IsNullOrWhiteSpace(options.RealtimeWebSocketBaseUri))
        {
            _firstFrameTcs.TrySetCanceled();
            return;
        }

        _firstFrameTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _realtimeCts = new();
        _realtimeTask = Task.Run(() => RealtimeReceiveLoopAsync(_realtimeCts.Token));
    }

    private async Task StopRealtimeFeedAsync()
    {
        try { _realtimeCts?.Cancel(); } catch { }

        var ws = _realtimeWs;
        if (ws is not null && (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived))
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", closeCts.Token);
            }
            catch { }
        }

        if (_realtimeTask is not null)
        {
            try { await _realtimeTask; } catch { }
        }

        _realtimeWs?.Dispose();
        _realtimeWs = null;
        _realtimeCts?.Dispose();
        _realtimeCts = null;
        _realtimeTask = null;

        lock (_realtimeLock)
        {
            _latestRealtimeDoc?.Dispose();
            _latestRealtimeDoc = null;
            _hasRealtimeFrame = false;
            _lastRealtimeError = null;
            _lastRealtimeEndpoint = null;
        }
        _firstFrameTcs.TrySetCanceled();
    }

    private async Task RealtimeReceiveLoopAsync(CancellationToken ct)
    {
        var options = _options!;
        var baseUri = new Uri(options.RealtimeWebSocketBaseUri.TrimEnd('/') + "/");
        var endpoints = BuildRealtimeEndpoints(options, baseUri);
        var index = 0;

        while (!ct.IsCancellationRequested)
        {
            var endpoint = endpoints[index];
            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                _realtimeWs = ws;
                _lastRealtimeEndpoint = endpoint.ToString();
                await ws.ConnectAsync(endpoint, ct);
                _logger.LogInformation("GSK realtime WebSocket connected to {Endpoint}", endpoint);
                _lastRealtimeError = null;
                await ReceiveFramesAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastRealtimeError = ex.Message;
                _logger.LogWarning(ex, "GSK realtime WebSocket loop error ({Endpoint}); will try next endpoint", endpoint);
            }
            finally
            {
                ws?.Dispose();
                if (ReferenceEquals(_realtimeWs, ws)) _realtimeWs = null;
            }

            if (ct.IsCancellationRequested) break;
            index = (index + 1) % endpoints.Count;
            try { await Task.Delay(RealtimeReconnectBackoff, ct); } catch { break; }
        }
    }

    private static IReadOnlyList<Uri> BuildRealtimeEndpoints(GskWebServerOptions options, Uri baseUri)
    {
        var primary = new Uri(baseUri, options.RealtimeWebSocketPath.TrimStart('/'));
        var aggregate = new Uri(baseUri, "ws/");
        return primary == aggregate ? [primary] : [primary, aggregate];
    }

    private async Task ReceiveFramesAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiveCts.CancelAfter(RealtimeReceiveTimeout);
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "server closed", ct);
                    return;
                }
                if (result.Count > 0)
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            if (sb.Length > 0)
                UpdateLatestFrame(sb.ToString());
        }
    }

    private void UpdateLatestFrame(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            JsonDocument? old;
            lock (_realtimeLock)
            {
                old = _latestRealtimeDoc;
                _latestRealtimeDoc = doc;
                _hasRealtimeFrame = true;
            }
            old?.Dispose();
            _firstFrameTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse GSK realtime frame");
        }
    }

    private bool TryGetRealtimeValue(string address, out JsonElement value, out string? error)
    {
        value = default;
        lock (_realtimeLock)
        {
            if (!_hasRealtimeFrame || _latestRealtimeDoc is null)
            {
                var detail = _lastRealtimeError is null
                    ? "frame not yet received"
                    : $"last attempt {_lastRealtimeEndpoint} failed: {_lastRealtimeError}";
                error = $"GSK realtime WebSocket {detail}. " +
                        "Verify RealtimeWebSocketBaseUrl/RealtimeWebSocketPath (default '/ws/{sn}', aggregate '/ws/') " +
                        "and that the device is publishing realtime frames.";
                return false;
            }
            return TryGetRealtimeFieldValue(_latestRealtimeDoc.RootElement, address, out value, out error);
        }
    }

    private async Task<bool> WaitForFirstRealtimeFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _firstFrameTcs;
        if (tcs.Task.IsCompletedSuccessfully) return true;
        try
        {
            await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return tcs.Task.IsCompletedSuccessfully;
        }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return false; }
    }
}
