namespace MachineConnectionApi.Services;

using InfluxDB.Client;
using MachineConnectionApi.Options;

internal sealed class InfluxClientPool : IDisposable
{
    private readonly object _gate = new();
    private ClientEntry? _current;
    private bool _disposed;

    public Lease Rent(InfluxDbOptions options)
    {
        var key = (Url: options.Url.Trim(), Token: options.Token,
            Timeout: Math.Max(5, options.WriteTimeoutSeconds));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is null || _current.Key != key)
            {
                var configuration = new InfluxDBClientOptions.Builder()
                    .Url(key.Url)
                    .AuthenticateToken(key.Token)
                    .TimeOut(TimeSpan.FromSeconds(key.Timeout))
                    .Build();
                var next = new ClientEntry(new InfluxDBClient(configuration), key);
                Retire(_current);
                _current = next;
            }
            _current.ActiveWrites++;
            return new Lease(this, _current);
        }
    }

    private static void Retire(ClientEntry? entry)
    {
        if (entry is null) return;
        entry.Retired = true;
        if (entry.ActiveWrites == 0) entry.Client.Dispose();
    }

    private void Release(ClientEntry entry)
    {
        lock (_gate)
        {
            entry.ActiveWrites--;
            if (entry.Retired && entry.ActiveWrites == 0) entry.Client.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Retire(_current);
            _current = null;
        }
    }

    internal sealed class ClientEntry(InfluxDBClient client, (string Url, string Token, int Timeout) key)
    {
        public InfluxDBClient Client { get; } = client;
        public (string Url, string Token, int Timeout) Key { get; } = key;
        public int ActiveWrites { get; set; }
        public bool Retired { get; set; }
    }

    internal sealed class Lease(InfluxClientPool owner, ClientEntry entry) : IDisposable
    {
        private InfluxClientPool? _owner = owner;
        public InfluxDBClient Client => entry.Client;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(entry);
    }
}
