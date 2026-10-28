namespace IndustrialIoT.Protocols.CAN;

using System.Threading.Channels;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Domain.ValueObjects;
using IndustrialIoT.Protocols.Abstractions;
using IndustrialIoT.Protocols.Models;
using IndustrialIoT.Protocols.Registration;
using Microsoft.Extensions.Logging;
using ProtocolType = IndustrialIoT.Domain.Enums.ProtocolType;

[ProtocolDriver(ProtocolType.CAN, "CAN", "SLCAN", "*")]
public sealed partial class CanDriver : IProtocolDriver
{
    private readonly ILogger<CanDriver> _logger;
    private readonly Func<ISlcanTransport> _transportFactory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private ISlcanTransport? _transport;
    private CancellationTokenSource? _session;
    private Task? _receiver;
    private Channel<string>? _responses;
    private TimeSpan _readTimeout;
    private CanOptions? _options;
    private bool _disposed;

    public CanDriver(ILogger<CanDriver> logger) : this(logger, () => new SerialSlcanTransport()) { }
    public CanDriver(ILogger<CanDriver> logger, ISlcanTransport transport) : this(logger, () => transport) { }
    private CanDriver(ILogger<CanDriver> logger, Func<ISlcanTransport> factory)
    { _logger = logger; _transportFactory = factory; }

    public ProtocolType Protocol => ProtocolType.CAN;
    public ConnectionState State => _state;
    public DriverCapabilities Capabilities => DriverCapabilities.Read | DriverCapabilities.Write | DriverCapabilities.BatchRead;
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public async Task<ConnectionResult> ConnectAsync(DeviceConnectionConfig config, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == ConnectionState.Connected) return new() { Success = true };
            await CleanupAsync();
            SetState(ConnectionState.Connecting);
            try
            {
                _options = CanOptions.Parse(config);
                _readTimeout = config.ReadTimeout;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(config.ConnectTimeout);
                _transport = _transportFactory();
                await _transport.OpenAsync(config, timeout.Token);
                _session = new();
                _responses = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
                _receiver = ReceiveLoopAsync(_session.Token);
                await CommandAsync("C", timeout.Token, allowRejection: true);
                await CommandAsync($"S{_options.SpeedIndex}", timeout.Token);
                await CommandAsync("O", timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (_state == ConnectionState.Faulted) throw new IOException("SLCAN receiver stopped during initialization.");
                SetState(ConnectionState.Connected);
                _logger.LogInformation("SLCAN connected on {Port} at CAN speed index {Speed}", _options.PortName, _options.SpeedIndex);
                return new() { Success = true };
            }
            catch (Exception ex)
            {
                await CleanupAsync();
                SetState(ConnectionState.Faulted, ex.Message);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                return new() { Success = false, ErrorMessage = ex is OperationCanceledException ? "SLCAN initialization timed out." : ex.Message };
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            var wasConnected = _state == ConnectionState.Connected;
            SetState(ConnectionState.Disconnected);
            ClearFrames();
            if (wasConnected)
            {
                try { await CommandAsync("C", ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "SLCAN close command failed; disposing serial transport"); }
            }
            await CleanupAsync();
            SetState(ConnectionState.Disconnected);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task CleanupAsync()
    {
        _session?.Cancel();
        _responses?.Writer.TryComplete();
        if (_transport is not null) await _transport.DisposeAsync();
        if (_receiver is not null)
        {
            try { await _receiver; }
            catch (OperationCanceledException) { }
        }
        // An old command must finish its cancellation/fault handling before a new session is installed.
        await _commands.WaitAsync();
        try
        {
            _transport = null;
            _receiver = null;
            _responses = null;
            _session?.Dispose();
            _session = null;
            ClearFrames();
        }
        finally { _commands.Release(); }
    }

    private void SetState(ConnectionState state, string? reason = null)
    {
        var previous = _state;
        _state = state;
        if (previous != state) StateChanged?.Invoke(this, new() { OldState = previous, NewState = state, Reason = reason });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}
