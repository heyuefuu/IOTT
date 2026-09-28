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
    private JingDiaoShimLease? shimLease;
    private JingDiaoOptions? options;
    private JingDiaoConnectRequest? connectRequest;
    private int disposed;
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
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (state == ConnectionState.Connected) return new() { Success = true };
            await ReleaseConnectionAsync(CancellationToken.None);
            options = JingDiaoOptions.From(config);
            SetState(ConnectionState.Connecting);

            if (injectedClient is null)
                shimLease = await JingDiaoShimLease.AcquireAsync(options.ShimBaseUri, options.ShimPath,
                    config.ConnectTimeout, ct, options.AutoStartShim);

            client = injectedClient ?? new JingDiaoIpcClient(options.ShimBaseUri);
            connectRequest = new(config.Host, options.RpcPort, options.CallbackPort,
                options.FileUploadPort, options.FileDownloadPort, options.TimeoutMs);
            var result = await client.ConnectAsync(connectRequest, ct);
            if (result.ReturnCode != 0 || string.IsNullOrWhiteSpace(result.SessionId))
                throw new InvalidOperationException(result.ErrorMessage ?? $"JingDiao connect failed: {result.ReturnCode}");

            sessionId = result.SessionId;
            SetState(ConnectionState.Connected);
            return new() { Success = true };
        }
        catch (Exception ex)
        {
            await ReleaseConnectionAsync(CancellationToken.None);
            var message = $"JingDiao shim connection failed: {ex.Message}";
            logger.LogError(ex, "{Message}", message);
            SetState(ConnectionState.Faulted, message);
            return new() { Success = false, ErrorMessage = message };
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (disposed == 2) return;
        await gate.WaitAsync(ct);
        try
        {
            await ReleaseConnectionAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (disposed != 0 || state != ConnectionState.Connected || client is null || connectRequest is null)
                return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(connectRequest.TimeoutMs + 5000L));
            try
            {
                if (!string.IsNullOrEmpty(sessionId)
                    && (await client.PingAsync(sessionId, deadline.Token)).ReturnCode == 0) return true;
            }
            catch (HttpRequestException) { }
            return await RestoreSessionAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "JingDiao session recovery failed");
            return false;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposed, 1, 0) != 0) return;
        try { await DisconnectAsync(); }
        finally { Volatile.Write(ref disposed, 2); }
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
