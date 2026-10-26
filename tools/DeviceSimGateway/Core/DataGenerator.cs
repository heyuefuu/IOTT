namespace DeviceSimGateway.Core;

/// <summary>模拟数据生成器：定时驱动输入寄存器/离散输入（并可选驱动保持寄存器/线圈）。</summary>
public sealed class DataGenerator : IDisposable
{
    private readonly SimDataStore _store;
    private readonly object _sync = new();
    private GeneratorConfig _cfg;
    private Timer? _timer;
    private long _tick;

    public DataGenerator(SimDataStore store, GeneratorConfig cfg)
    {
        _store = store;
        _cfg = cfg.Clone();
    }

    public GeneratorConfig Config { get { lock (_sync) return _cfg.Clone(); } }

    public void Apply(GeneratorConfig cfg)
    {
        lock (_sync) { _cfg = cfg.Clone(); }
        _store.Resize(cfg);
        SeedHolding();
    }

    public void Start()
    {
        Stop();
        int interval = Math.Clamp(_cfg.IntervalMs, 50, 60000);
        _timer = new Timer(_ => Tick(), null, 0, interval);
    }

    public void Stop() { _timer?.Dispose(); _timer = null; }

    /// <summary>上电/应用配置时给保持寄存器填一份种子。</summary>
    public void SeedHolding()
    {
        GeneratorConfig cfg;
        lock (_sync) cfg = _cfg;
        lock (_store.Sync)
        {
            for (int i = 0; i < _store.Holding.Length; i++) _store.Holding[i] = Compute(cfg.HoldingSeed, i, 0);
        }
    }

    private void Tick()
    {
        GeneratorConfig cfg;
        lock (_sync) cfg = _cfg;
        long tick = Interlocked.Increment(ref _tick);
        lock (_store.Sync)
        {
            for (int i = 0; i < _store.Input.Length; i++) _store.Input[i] = Compute(cfg.Input, i, tick);
            if (cfg.DriveHolding)
                for (int i = 0; i < _store.Holding.Length; i++) _store.Holding[i] = Compute(cfg.HoldingSeed, i, tick);
            for (int i = 0; i < _store.Discrete.Length; i++) _store.Discrete[i] = ComputeBit(cfg.Discretes, i, tick);
            for (int i = 0; i < _store.Coils.Length; i++) _store.Coils[i] = ComputeBit(cfg.Coils, i, tick);
        }
        _store.MarkUpdated();
    }

    private static ushort Compute(RegPattern p, int index, long tick)
    {
        double phase = p.PeriodMs > 0 ? (Environment.TickCount64 % (long)Math.Max(1, p.PeriodMs)) / p.PeriodMs : 0;
        double v = p.Mode switch
        {
            RegMode.Constant => p.Base,
            RegMode.Ramp => p.Base + index * p.Step,
            RegMode.Sine => p.Base + p.Amplitude * Math.Sin(2 * Math.PI * phase + index * 0.05),
            RegMode.Random => p.Base + Random.Shared.NextDouble() * p.Amplitude,
            RegMode.Counter => p.Base + tick * p.Step,
            _ => p.Base,
        };
        if (v < p.Min) v = p.Min;
        if (v > p.Max) v = p.Max;
        return (ushort)Math.Round(Math.Clamp(v, 0, 65535));
    }

    private static bool ComputeBit(CoilPattern p, int index, long tick)
    {
        long period = Math.Max(50, (long)p.PeriodMs);
        return p.Mode switch
        {
            CoilMode.Constant => Random.Shared.NextDouble() < p.Density,
            CoilMode.Toggle => ((Environment.TickCount64 / period + index) % 2) == 0,
            CoilMode.Random => Random.Shared.NextDouble() < p.Density,
            _ => false,
        };
    }

    public void Dispose() => Stop();
}
