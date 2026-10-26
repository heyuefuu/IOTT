namespace DeviceSimGateway.Core;

/// <summary>所有协议服务共享的数据区（线程安全）。Modbus 与 MTConnect 等读到的是同一份数据。</summary>
public sealed class SimDataStore
{
    private readonly object _sync = new();
    public ushort[] Holding { get; private set; } = Array.Empty<ushort>();
    public ushort[] Input { get; private set; } = Array.Empty<ushort>();
    public bool[] Coils { get; private set; } = Array.Empty<bool>();
    public bool[] Discrete { get; private set; } = Array.Empty<bool>();
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.Now;

    /// <summary>随机失败注入策略（各协议服务共享）。</summary>
    public FailurePolicy Failure { get; } = new();

    public SimDataStore(GeneratorConfig cfg) => Resize(cfg);

    public void Resize(GeneratorConfig cfg)
    {
        lock (_sync)
        {
            Holding = new ushort[Math.Clamp(cfg.HoldingSize, 1, 65536)];
            Input = new ushort[Math.Clamp(cfg.InputSize, 1, 65536)];
            Coils = new bool[Math.Clamp(cfg.CoilCount, 1, 65536)];
            Discrete = new bool[Math.Clamp(cfg.DiscreteCount, 1, 65536)];
        }
    }

    public object Sync => _sync;

    public bool ReadRegisters(bool holding, int start, int qty, out ushort[] values)
    {
        values = Array.Empty<ushort>();
        lock (_sync)
        {
            var arr = holding ? Holding : Input;
            if (start < 0 || qty < 1 || start + qty > arr.Length) return false;
            values = new ushort[qty];
            Array.Copy(arr, start, values, 0, qty);
            return true;
        }
    }

    public bool ReadBits(bool coil, int start, int qty, out bool[] values)
    {
        values = Array.Empty<bool>();
        lock (_sync)
        {
            var arr = coil ? Coils : Discrete;
            if (start < 0 || qty < 1 || start + qty > arr.Length) return false;
            values = new bool[qty];
            Array.Copy(arr, start, values, 0, qty);
            return true;
        }
    }

    public bool WriteRegister(int addr, ushort value)
    {
        lock (_sync) { if (addr < 0 || addr >= Holding.Length) return false; Holding[addr] = value; return true; }
    }

    public bool WriteRegisters(int start, ushort[] values)
    {
        lock (_sync)
        {
            if (start < 0 || start + values.Length > Holding.Length) return false;
            Array.Copy(values, 0, Holding, start, values.Length);
            return true;
        }
    }

    public bool WriteCoil(int addr, bool value)
    {
        lock (_sync) { if (addr < 0 || addr >= Coils.Length) return false; Coils[addr] = value; return true; }
    }

    public bool WriteCoils(int start, bool[] values)
    {
        lock (_sync)
        {
            if (start < 0 || start + values.Length > Coils.Length) return false;
            Array.Copy(values, 0, Coils, start, values.Length);
            return true;
        }
    }

    public void MarkUpdated() => UpdatedAt = DateTimeOffset.Now;

    public object Snapshot(int count)
    {
        lock (_sync)
        {
            count = Math.Clamp(count, 1, 256);
            int h = Math.Min(count, Holding.Length);
            int i = Math.Min(count, Input.Length);
            int c = Math.Min(count, Coils.Length);
            int d = Math.Min(count, Discrete.Length);
            return new
            {
                updatedAt = UpdatedAt,
                holding = Holding.Take(h).ToArray(),
                input = Input.Take(i).ToArray(),
                coils = Coils.Take(c).ToArray(),
                discrete = Discrete.Take(d).ToArray(),
                sizes = new { holding = Holding.Length, input = Input.Length, coils = Coils.Length, discrete = Discrete.Length },
            };
        }
    }
}
