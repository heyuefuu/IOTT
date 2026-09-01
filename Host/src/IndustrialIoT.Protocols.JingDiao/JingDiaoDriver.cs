namespace IndustrialIoT.Protocols.JingDiao;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.JingDiao, "JingDiao", "JD50", "JD60")]
public sealed partial class JingDiaoDriver :
    IProtocolDriver, IAddressSpaceBrowser, IProgramFileBrowser, INCProgramTransfer
{
    private readonly ILogger<JingDiaoDriver> logger;
    private readonly IJingDiaoClient? injectedClient;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IJingDiaoClient? client;
    private JingDiaoShimProcess? shimProcess;
    private JingDiaoOptions? options;
    private string sessionId = "";
    private ConnectionState state = ConnectionState.Disconnected;

    public JingDiaoDriver(ILogger<JingDiaoDriver> logger, IJingDiaoClient? client = null)
    {
        this.logger = logger;
        injectedClient = client;
    }

    public ProtocolType Protocol => ProtocolType.JingDiao;
    public ConnectionState State => state;
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Read | DriverCapabilities.Browse |
        DriverCapabilities.BatchRead | DriverCapabilities.FileTransfer;
    public bool SupportsResume => false;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (state == ConnectionState.Connected) return new() { Success = true };
            options = JingDiaoOptions.From(config);
            SetState(ConnectionState.Connecting);

            if (injectedClient is null && options.AutoStartShim)
            {
                var alive = await ProbeShimAsync(options.ShimBaseUri, TimeSpan.FromSeconds(1), ct);
                if (!alive)
                {
                    var shimPath = options.ShimPath ?? JingDiaoShimProcess.ResolveDefaultPath()
                        ?? throw new InvalidOperationException(
                            $"JingDiao shim is not reachable at {options.ShimBaseUri} and {JingDiaoShimProcess.ExecutableName} was not found.");
                    shimProcess = JingDiaoShimProcess.Start(options.ShimBaseUri.ToString().TrimEnd('/'), shimPath);
                }
            }

            client = injectedClient ?? new JingDiaoIpcClient(options.ShimBaseUri);
            if (injectedClient is null)
                await WaitForShimReadyAsync(options.ShimBaseUri, config.ConnectTimeout, ct);
            var result = await client.ConnectAsync(new(config.Host, options.RpcPort, options.CallbackPort,
                options.FileUploadPort, options.FileDownloadPort, options.TimeoutMs), ct);
            if (result.ReturnCode != 0 || string.IsNullOrWhiteSpace(result.SessionId))
                throw new InvalidOperationException(result.ErrorMessage ?? $"JingDiao connect failed: {result.ReturnCode}");

            sessionId = result.SessionId;
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            var message = $"JingDiao shim connection failed: {ex.Message}";
            logger.LogError(ex, "{Message}", message);
            SetState(ConnectionState.Faulted, message);
            return new() { Success = false, ErrorMessage = message };
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(sessionId) && client is not null)
                await client.DisconnectAsync(sessionId, ct);
            sessionId = "";
            SetState(ConnectionState.Disconnected);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (state != ConnectionState.Connected || client is null || string.IsNullOrEmpty(sessionId))
            return false;
        try { return (await client.PingAsync(sessionId, ct)).ReturnCode == 0; }
        catch { return false; }
    }

    public Task<WriteResult> WriteTagAsync(string address, DataType dataType, object value, CancellationToken ct = default)
        => Task.FromResult(new WriteResult
        {
            Success = false,
            ErrorMessage = "JingDiao first release does not support remote control or tag writes."
        });

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        shimProcess?.Dispose();
        shimProcess = null;
        gate.Dispose();
    }

    private static async Task<bool> ProbeShimAsync(Uri baseUri, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = timeout };
        try
        {
            using var response = await http.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task WaitForShimReadyAsync(Uri baseUri, TimeSpan connectTimeout, CancellationToken ct)
    {
        var timeout = connectTimeout > TimeSpan.Zero ? connectTimeout : TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + timeout;
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromMilliseconds(800) };
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync("/health", ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"JingDiao shim at {baseUri} did not respond on /health: {last?.Message}");
    }

    private void EnsureConnected()
    {
        if (state != ConnectionState.Connected || client is null || string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("JingDiao driver is not connected.");
    }

    private void SetState(ConnectionState next, string? reason = null)
    {
        var old = state;
        if (old == next) return;
        state = next;
        StateChanged?.Invoke(this, new() { OldState = old, NewState = next, Reason = reason });
    }
}
