namespace IndustrialIoT.Protocols.FANUC;

using System.Diagnostics;
using System.Text;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// Simulated FOCAS2 API implementation for development and testing.
/// Returns physically plausible data without requiring FANUC native DLLs.
/// </summary>
public sealed class SimulatedFocasApi : IFocasApi
{
    private readonly Random _rng = new();
    private readonly object _lock = new();
    private int _nextHandle = 1000;
    private readonly HashSet<int> _activeHandles = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // ── Simulated robot state ───────────────────────────────────────

    private bool _running = true;
    private int _mode = 1; // 0=TEACH, 1=AUTO
    private int _programNumber = 1001;
    private int _mainProgramNumber = 1000;
    private int _programLine = 1;
    private string _programBlock = "G01 X100.0 Y50.0 F1200";
    private string _programName = "O01001";
    private int _spindleSpeed = 1500;
    private int _actualFeed = 1180;
    private double _actualFeedRate = 1180;
    private double _commandedFeedRate = 1200;
    private int _feedOverride = 100;
    private int _toolId = 1;
    private int _maxToolGroup = 12;
    private bool _alarmActive;
    private int _alarmCode;
    private string _alarmMessage = string.Empty;
    private int _alarmStatus;
    private int _partCountCurrent = 27;
    private int _partCountTotal = 120;
    private int _cuttingTimeMilliseconds = 45_000;
    private int _cuttingTimeMinutes = 2;
    private int _workingTimeMilliseconds = 30_000;
    private int _workingTimeMinutes = 1;
    private int _powerOnTimeMinutes = 120;
    private readonly Dictionary<short, byte[]> _programStore = [];
    private readonly Dictionary<string, SimulatedCncFile> _pathProgramStore = new(StringComparer.OrdinalIgnoreCase);
    private MemoryStream? _programDownloadBuffer;
    private byte[]? _programUploadBuffer;
    private int _programUploadOffset;
    private string? _programDownloadTargetDirectory;

    // Digital I/O: 16 inputs + 16 outputs + 8 robot inputs + 8 robot outputs
    private readonly bool[] _digitalInputs = new bool[16];
    private readonly bool[] _digitalOutputs = new bool[16];
    private readonly bool[] _robotInputs = new bool[8];
    private readonly bool[] _robotOutputs = new bool[8];

    public SimulatedFocasApi()
    {
        // Seed some initial I/O states
        _digitalInputs[0] = true;  // Home sensor
        _digitalInputs[1] = true;  // Safety gate closed
        _robotOutputs[0] = true;   // Gripper closed
        _alarmStatus = 0;
        _programStore[(short)_programNumber] = Encoding.ASCII.GetBytes($"%\n{_programName}\n{_programBlock}\nM30\n%");
        _pathProgramStore["//CNC_MEM/USER/PATH1/O1001.nc"] = new(_programStore[(short)_programNumber], DateTimeOffset.UtcNow, "Sample O1001");
        _pathProgramStore["//CNC_MEM/USER/PATH2/O2001.nc"] = new(Encoding.ASCII.GetBytes("%\nO2001\nM30\n%"), DateTimeOffset.UtcNow, "Sample O2001");
    }

    public int Connect(string host, int port, int timeout, out int handle)
    {
        lock (_lock)
        {
            handle = _nextHandle++;
            _activeHandles.Add(handle);
        }
        return 0; // EW_OK
    }

    public int Disconnect(int handle)
    {
        lock (_lock)
        {
            return _activeHandles.Remove(handle) ? 0 : -1;
        }
    }

    public int ReadActPos(int handle, out double[] positions)
    {
        if (!IsValidHandle(handle)) { positions = []; return -1; }

        // Generate sinusoidally varying joint positions (degrees)
        double t = _clock.Elapsed.TotalSeconds;
        positions =
        [
            30.0  * Math.Sin(t * 0.5),          // J1: +-30 deg oscillation
            -45.0 + 15.0 * Math.Sin(t * 0.3),   // J2: around -45 deg
            90.0  + 10.0 * Math.Cos(t * 0.4),   // J3: around 90 deg
            5.0   * Math.Sin(t * 0.7),           // J4: small wrist rotation
            -30.0 + 8.0  * Math.Sin(t * 0.6),   // J5: around -30 deg
            45.0  * Math.Sin(t * 0.8),           // J6: wrist roll
        ];

        return 0;
    }

    public int ReadAxisPositions(int handle, out FocasAxisPosition[] positions)
    {
        if (ReadActPos(handle, out var absolute) != 0)
        {
            positions = [];
            return -1;
        }

        positions = absolute.Select((value, index) => new FocasAxisPosition
        {
            Absolute = value,
            Relative = value - ((index + 1) * 100.0)
        }).ToArray();
        return 0;
    }

    public int ReadSpindleSpeed(int handle, out int speed)
    {
        if (!IsValidHandle(handle)) { speed = 0; return -1; }

        // Slight random fluctuation around base speed
        lock (_lock)
        {
            speed = _running ? _spindleSpeed + _rng.Next(-20, 21) : 0;
        }
        return 0;
    }

    public int ReadActualFeed(int handle, out int feed)
    {
        if (!IsValidHandle(handle)) { feed = 0; return -1; }
        lock (_lock)
        {
            _actualFeed = _running ? 1234 : 0;
            feed = _actualFeed;
        }
        return 0;
    }

    public int ReadFeedOverride(int handle, out int overridePercent)
    {
        if (!IsValidHandle(handle)) { overridePercent = 0; return -1; }
        lock (_lock) { overridePercent = _feedOverride; }
        return 0;
    }

    public int ReadSysInfo(int handle, out FocasSystemInfo info)
    {
        if (!IsValidHandle(handle)) { info = new(); return -1; }
        info = new() { MaxAxis = 8, CncTypeCode = "31", Series = "B055", Version = "A123", AxisCount = 6 };
        return 0;
    }

    public int ReadAlarm(int handle, out int alarmNo, out string message)
    {
        if (!IsValidHandle(handle)) { alarmNo = 0; message = string.Empty; return -1; }

        lock (_lock)
        {
            // ~1% chance of triggering an alarm each read cycle
            if (!_alarmActive && _rng.NextDouble() < 0.01)
            {
                _alarmActive = true;
                _alarmCode = _rng.Next(1, 50) * 100; // e.g. 100, 200, ... 4900
                _alarmMessage = _alarmCode switch
                {
                    100  => "SRVO-001 Operator panel E-stop",
                    200  => "SRVO-002 Teach pendant E-stop",
                    300  => "SRVO-003 Deadman switch released",
                    400  => "SRVO-004 Fence open",
                    500  => "SRVO-006 Hand broken",
                    1000 => "SRVO-010 Collision detection",
                    2000 => "SRVO-023 Servo motor overheat",
                    3000 => "SRVO-037 IMSTP input",
                    _    => $"SRVO-{_alarmCode / 100:D3} General servo alarm"
                };
            }
            else if (_alarmActive && _rng.NextDouble() < 0.05)
            {
                // 5% chance of clearing alarm
                _alarmActive = false;
                _alarmCode = 0;
                _alarmMessage = string.Empty;
            }

            alarmNo = _alarmCode;
            message = _alarmMessage;
        }
        return 0;
    }

    public int ReadRunStatus(int handle, out int status)
    {
        if (!IsValidHandle(handle)) { status = 0; return -1; }

        lock (_lock)
        {
            // Occasionally toggle running state (~2% chance)
            if (_rng.NextDouble() < 0.02)
                _running = !_running;

            // Stop if alarm active
            if (_alarmActive) _running = false;

            status = _running ? 1 : 0;
        }
        return 0;
    }

    public int ReadProgramNumber(int handle, out int progNum)
    {
        if (!IsValidHandle(handle)) { progNum = 0; return -1; }

        lock (_lock)
        {
            // Advance program line when running
            if (_running)
            {
                _programLine++;
                if (_programLine > 200)
                {
                    _programLine = 1;
                    // Occasionally switch programs
                    if (_rng.NextDouble() < 0.1)
                        _programNumber = 1000 + _rng.Next(1, 20);
                }
            }
            progNum = _programNumber;
        }
        return 0;
    }

    public int ReadProgramInfo(int handle, out FocasProgramInfo program)
    {
        if (!IsValidHandle(handle)) { program = new(); return -1; }
        lock (_lock)
        {
            program = new()
            {
                MainNumber = _mainProgramNumber,
                RunningNumber = _programNumber,
                Name = _programName
            };
        }
        return 0;
    }

    public int ReadProgramName(int handle, out string programName)
    {
        if (!IsValidHandle(handle)) { programName = string.Empty; return -1; }
        lock (_lock) { programName = _programName; }
        return 0;
    }

    public int ReadStatusInfo(int handle, out FocasStatusInfo status)
    {
        if (!IsValidHandle(handle)) { status = new(); return -1; }
        lock (_lock)
        {
            status = new()
            {
                Run = _running ? 3 : 1,
                Auto = _mode == 1 ? 1 : 0,
                Emergency = _alarmActive ? 1 : 0,
                Alarm = _alarmActive ? 1 : 0,
                Motion = _running ? 1 : 0
            };
        }
        return 0;
    }

    public int ReadProgramBlock(int handle, out string block)
    {
        if (!IsValidHandle(handle)) { block = string.Empty; return -1; }
        lock (_lock) { block = _programBlock; }
        return 0;
    }

    public int ReadActualFeedRate(int handle, out double feedRate)
    {
        if (!IsValidHandle(handle)) { feedRate = 0; return -1; }
        lock (_lock)
        {
            _actualFeedRate = _running ? _commandedFeedRate - _rng.Next(0, 30) : 0;
            feedRate = _actualFeedRate;
        }
        return 0;
    }

    public int ReadCommandedFeedRate(int handle, out double feedRate)
    {
        if (!IsValidHandle(handle)) { feedRate = 0; return -1; }
        lock (_lock) { feedRate = _commandedFeedRate; }
        return 0;
    }

    public int ReadToolId(int handle, out int toolId)
    {
        if (!IsValidHandle(handle)) { toolId = 0; return -1; }
        lock (_lock) { toolId = _toolId; }
        return 0;
    }

    public int ReadMaxToolGroup(int handle, out int maxGroup)
    {
        if (!IsValidHandle(handle)) { maxGroup = 0; return -1; }
        lock (_lock) { maxGroup = _maxToolGroup; }
        return 0;
    }

    public int ReadMacroVariable(int handle, int number, out double value)
    {
        if (!IsValidHandle(handle)) { value = 0; return -1; }
        value = number == 3901 ? _partCountCurrent : 0;
        return 0;
    }

    public int ReadParameter(int handle, int number, out int value)
    {
        if (!IsValidHandle(handle)) { value = 0; return -1; }
        value = number switch
        {
            6712 => _partCountTotal,
            6750 => _powerOnTimeMinutes,
            6751 => _workingTimeMilliseconds,
            6752 => _workingTimeMinutes,
            6753 => _cuttingTimeMilliseconds,
            6754 => _cuttingTimeMinutes,
            _ => 0
        };
        return 0;
    }

    public int ReadAlarmStatus(int handle, out int statusCode, out string message)
    {
        if (!IsValidHandle(handle)) { statusCode = 0; message = string.Empty; return -1; }
        lock (_lock)
        {
            statusCode = _alarmStatus;
            message = statusCode == 0 ? "参数开启（SW）" : "未知错误";
        }
        return 0;
    }

    public int StartProgramDownload(int handle)
    {
        if (!IsValidHandle(handle)) return -1;
        lock (_lock)
        {
            _programDownloadBuffer?.Dispose();
            _programDownloadBuffer = new MemoryStream();
        }
        return 0;
    }

    public int StartProgramDownloadAtPath(int handle, string destinationPath)
    {
        var rc = StartProgramDownload(handle);
        if (rc == 0)
            _programDownloadTargetDirectory = NormalizeCncDirectoryPath(destinationPath);
        return rc;
    }

    public int DownloadProgramChunk(int handle, byte[] data, int length)
    {
        if (!IsValidHandle(handle)) return -1;
        if (length <= 0 || length > 256 || data.Length < length) return -2;
        lock (_lock)
        {
            if (_programDownloadBuffer is null) return -3;
            _programDownloadBuffer.Write(data, 0, length);
        }
        return 0;
    }

    public int DownloadProgramChunkAtPath(int handle, byte[] data, int length, out int acceptedLength)
    {
        acceptedLength = length;
        return DownloadProgramChunk(handle, data, length);
    }

    public int EndProgramDownload(int handle)
    {
        if (!IsValidHandle(handle)) return -1;
        lock (_lock)
        {
            if (_programDownloadBuffer is null) return -3;
            var programText = Encoding.ASCII.GetString(_programDownloadBuffer.ToArray());
            var programNumber = ExtractProgramNumber(programText);
            if (programNumber is null) return -4;
            _programStore[programNumber.Value] = Encoding.ASCII.GetBytes(NormalizeProgramForUpload(programText));
            _programDownloadBuffer.Dispose();
            _programDownloadBuffer = null;
        }
        return 0;
    }

    public int EndProgramDownloadAtPath(int handle)
    {
        if (!string.IsNullOrWhiteSpace(_programDownloadTargetDirectory))
            SaveBufferedProgramToPath(_programDownloadTargetDirectory);
        var rc = EndProgramDownload(handle);
        _programDownloadTargetDirectory = null;
        return rc;
    }

    public int StartProgramUpload(int handle, short programNumber)
    {
        if (!IsValidHandle(handle)) return -1;
        lock (_lock)
        {
            if (!_programStore.TryGetValue(programNumber, out _programUploadBuffer))
                return -4;
            _programUploadOffset = 0;
        }
        return 0;
    }

    public int StartProgramUploadFromPath(int handle, string sourcePath)
    {
        if (!IsValidHandle(handle)) return -1;
        lock (_lock)
        {
            var filePath = ResolveStoredFilePath(sourcePath);
            if (filePath is null || !_pathProgramStore.TryGetValue(filePath, out var file))
                return -4;
            _programUploadBuffer = file.Content;
            _programUploadOffset = 0;
        }
        return 0;
    }

    public int UploadProgramChunk(int handle, byte[] buffer, out int actualLength)
    {
        actualLength = 0;
        if (!IsValidHandle(handle)) return -1;
        lock (_lock)
        {
            if (_programUploadBuffer is null) return -3;
            actualLength = Math.Min(Math.Min(buffer.Length, 256), _programUploadBuffer.Length - _programUploadOffset);
            if (actualLength > 0)
            {
                Array.Copy(_programUploadBuffer, _programUploadOffset, buffer, 0, actualLength);
                _programUploadOffset += actualLength;
            }
        }
        return 0;
    }

    public int EndProgramUpload(int handle)
    {
        if (!IsValidHandle(handle)) return -1;
        lock (_lock)
        {
            _programUploadBuffer = null;
            _programUploadOffset = 0;
        }
        return 0;
    }

    public int UploadProgramChunkFromPath(int handle, byte[] buffer, out int actualLength) => UploadProgramChunk(handle, buffer, out actualLength);
    public int EndProgramUploadFromPath(int handle) => EndProgramUpload(handle);

    public int ReadProgramDirectory(int handle, out IReadOnlyList<FocasProgramDirectoryEntry> entries)
    {
        entries = [];
        if (!IsValidHandle(handle)) return -1;
        lock (_lock)
        {
            entries = _programStore.Select(kvp => new FocasProgramDirectoryEntry
            {
                Number = kvp.Key,
                Size = kvp.Value.Length,
                Comment = $"O{kvp.Key:D4}",
                ModifiedDate = DateTimeOffset.UtcNow
            }).OrderBy(e => e.Number).ToList();
        }
        return 0;
    }

    public int ReadCncMemoryDirectory(int handle, string path, out IReadOnlyList<ProgramFileEntry> entries)
    {
        entries = [];
        if (!IsValidHandle(handle)) return -1;
        var directoryPath = NormalizeCncDirectoryPath(path);
        lock (_lock)
        {
            var directories = EnumerateDirectoryNodes(directoryPath);
            var files = _pathProgramStore
                .Where(entry => string.Equals(Path.GetDirectoryName(entry.Key)?.Replace('\\', '/'), directoryPath, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new ProgramFileEntry
                {
                    Path = entry.Key,
                    Name = Path.GetFileName(entry.Key),
                    IsDirectory = false,
                    SizeBytes = entry.Value.Content.LongLength,
                    ModifiedAt = entry.Value.ModifiedAt,
                    CanDownload = true,
                    CanUpload = false,
                    HasChildren = false,
                    Comment = entry.Value.Comment
                });
            entries = directories.Concat(files).OrderBy(entry => entry.IsDirectory ? 0 : 1).ThenBy(entry => entry.Path).ToArray();
        }
        return 0;
    }

    public int ReadPmc(int handle, int adrType, int startAddr, int length, byte[] data)
    {
        if (!IsValidHandle(handle)) return -1;
        if (data.Length < length) return -2;

        lock (_lock)
        {
            // PMC address type 0 = DI, 1 = DO, 5 = RI, 6 = RO
            bool[] source = adrType switch
            {
                0 => _digitalInputs,
                1 => _digitalOutputs,
                5 => _robotInputs,
                6 => _robotOutputs,
                _ => []
            };

            if (source.Length == 0) return -3;

            // Pack bools into bytes (8 bits each)
            Array.Clear(data, 0, length);
            for (int i = 0; i < Math.Min(length * 8, source.Length); i++)
            {
                if (startAddr + i < source.Length && source[startAddr + i])
                    data[i / 8] |= (byte)(1 << (i % 8));
            }

            // Simulate some random DI flickering
            if (adrType == 0 && _rng.NextDouble() < 0.05)
            {
                int bit = _rng.Next(2, _digitalInputs.Length);
                _digitalInputs[bit] = !_digitalInputs[bit];
            }
        }
        return 0;
    }

    public int WritePmc(int handle, int adrType, int startAddr, int length, byte[] data)
    {
        if (!IsValidHandle(handle)) return -1;

        lock (_lock)
        {
            // Only DO and RO are writable
            bool[] target = adrType switch
            {
                1 => _digitalOutputs,
                6 => _robotOutputs,
                _ => []
            };

            if (target.Length == 0) return -3; // Read-only area

            for (int i = 0; i < Math.Min(length * 8, target.Length); i++)
            {
                if (startAddr + i < target.Length)
                    target[startAddr + i] = (data[i / 8] & (1 << (i % 8))) != 0;
            }
        }
        return 0;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Current simulated program line (exposed for address mapper).</summary>
    internal int CurrentProgramLine
    {
        get { lock (_lock) { return _programLine; } }
    }

    /// <summary>Current speed override % (exposed for address mapper).</summary>
    internal float SpeedOverride
    {
        get { lock (_lock) { return _running ? 80f + (float)(_rng.NextDouble() * 20.0) : 0f; } }
    }

    private static short? ExtractProgramNumber(string programText)
    {
        var markerIndex = programText.IndexOf('O');
        if (markerIndex < 0) return null;
        var digits = new string(programText.Skip(markerIndex + 1).TakeWhile(char.IsDigit).ToArray());
        return short.TryParse(digits, out var programNumber) ? programNumber : null;
    }

    private static string NormalizeProgramForUpload(string programText)
    {
        if (programText.StartsWith("%", StringComparison.Ordinal))
            return programText;

        return $"%{programText}";
    }

    private void SaveBufferedProgramToPath(string directoryPath)
    {
        if (_programDownloadBuffer is null)
            return;
        var programText = Encoding.ASCII.GetString(_programDownloadBuffer.ToArray());
        var programNumber = ExtractProgramNumber(programText);
        if (programNumber is null)
            return;
        var fileName = $"O{programNumber.Value:D4}.nc";
        var normalizedBytes = Encoding.ASCII.GetBytes(NormalizeProgramForUpload(programText));
        _pathProgramStore[$"{directoryPath}/{fileName}"] = new(normalizedBytes, DateTimeOffset.UtcNow, fileName);
    }

    private string? ResolveStoredFilePath(string sourcePath)
    {
        var normalizedPath = sourcePath.Replace('\\', '/').Trim();
        if (_pathProgramStore.ContainsKey(normalizedPath))
            return normalizedPath;
        var withExtension = normalizedPath.EndsWith(".nc", StringComparison.OrdinalIgnoreCase) ? normalizedPath : $"{normalizedPath}.nc";
        return _pathProgramStore.ContainsKey(withExtension) ? withExtension : null;
    }

    private static string NormalizeCncDirectoryPath(string path)
        => path.Replace('\\', '/').Trim().TrimEnd('/');

    private IReadOnlyList<ProgramFileEntry> EnumerateDirectoryNodes(string directoryPath)
    {
        return _pathProgramStore.Keys
            .Where(path => path.StartsWith($"{directoryPath}/", StringComparison.OrdinalIgnoreCase))
            .Select(path => path[(directoryPath.Length + 1)..].Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => $"{directoryPath}/{name}")
            .Where(path => !_pathProgramStore.ContainsKey(path))
            .OrderBy(path => path)
            .Select(path => new ProgramFileEntry
            {
                Path = path,
                Name = path[(path.LastIndexOf('/') + 1)..],
                IsDirectory = true,
                CanDownload = false,
                CanUpload = true,
                HasChildren = true
            })
            .ToArray();
    }

    private bool IsValidHandle(int handle)
    {
        lock (_lock) { return _activeHandles.Contains(handle); }
    }
}

public sealed class FocasStatusInfo
{
    public int Run { get; init; }
    public int Auto { get; init; }
    public int Motion { get; init; }
    public int Mstb { get; init; }
    public int Emergency { get; init; }
    public int Alarm { get; init; }
    public int Edit { get; init; }
}

public sealed class FocasAxisPosition
{
    public double Absolute { get; init; }
    public double Relative { get; init; }
}

public sealed class FocasProgramInfo
{
    public int MainNumber { get; init; }
    public int RunningNumber { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class FocasSystemInfo
{
    public int MaxAxis { get; init; }
    public string CncTypeCode { get; init; } = string.Empty;
    public string Series { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public int AxisCount { get; init; }
}

public sealed record FocasProgramDirectoryEntry
{
    public int Number { get; init; }
    public int Size { get; init; }
    public string Comment { get; init; } = string.Empty;
    public DateTimeOffset? ModifiedDate { get; init; }
}

public sealed record FocasDetailError(short ErrorNumber, short DataNumber);

sealed record SimulatedCncFile(byte[] Content, DateTimeOffset ModifiedAt, string Comment);

/// <summary>
/// Abstraction layer over FANUC FOCAS2 native DLL calls.
/// Implementations: <see cref="SimulatedFocasApi"/> (dev/test), or a real P/Invoke wrapper.
/// </summary>
public interface IFocasApi
{
    /// <summary>Connect to CNC. Returns 0 (EW_OK) on success.</summary>
    int Connect(string host, int port, int timeout, out int handle);

    /// <summary>Disconnect from CNC.</summary>
    int Disconnect(int handle);

    /// <summary>Read PMC data (I/O, counters, timers). adrType: 0=DI,1=DO,5=RI,6=RO.</summary>
    int ReadPmc(int handle, int adrType, int startAddr, int length, byte[] data);

    /// <summary>Write PMC data. Only writable areas (DO, RO).</summary>
    int WritePmc(int handle, int adrType, int startAddr, int length, byte[] data);

    /// <summary>Read actual axis positions (joint angles in degrees).</summary>
    int ReadActPos(int handle, out double[] positions);

    int ReadAxisPositions(int handle, out FocasAxisPosition[] positions);

    /// <summary>Read spindle speed (RPM).</summary>
    int ReadSpindleSpeed(int handle, out int speed);

    int ReadActualFeed(int handle, out int feed);
    int ReadFeedOverride(int handle, out int overridePercent);
    int ReadSysInfo(int handle, out FocasSystemInfo info);

    /// <summary>Read active alarm.</summary>
    int ReadAlarm(int handle, out int alarmNo, out string message);

    int ReadAlarmStatus(int handle, out int statusCode, out string message);

    /// <summary>Read run/stop status. 0=stopped, 1=running.</summary>
    int ReadRunStatus(int handle, out int status);

    /// <summary>Read currently executing program number.</summary>
    int ReadProgramNumber(int handle, out int progNum);
    int ReadProgramInfo(int handle, out FocasProgramInfo program);
    int ReadProgramName(int handle, out string programName);

    int ReadStatusInfo(int handle, out FocasStatusInfo status);
    int ReadProgramBlock(int handle, out string block);
    int ReadActualFeedRate(int handle, out double feedRate);
    int ReadCommandedFeedRate(int handle, out double feedRate);
    int ReadToolId(int handle, out int toolId);
    int ReadMaxToolGroup(int handle, out int maxGroup);
    int ReadMacroVariable(int handle, int number, out double value);
    int ReadParameter(int handle, int number, out int value);
    int StartProgramDownload(int handle);
    int DownloadProgramChunk(int handle, byte[] data, int length);
    int EndProgramDownload(int handle);
    int StartProgramDownloadAtPath(int handle, string destinationPath);
    int DownloadProgramChunkAtPath(int handle, byte[] data, int length, out int acceptedLength);
    int EndProgramDownloadAtPath(int handle);
    int StartProgramUpload(int handle, short programNumber);
    int UploadProgramChunk(int handle, byte[] buffer, out int actualLength);
    int EndProgramUpload(int handle);
    int StartProgramUploadFromPath(int handle, string sourcePath);
    int UploadProgramChunkFromPath(int handle, byte[] buffer, out int actualLength);
    int EndProgramUploadFromPath(int handle);
    /// <summary>Read program directory listing from CNC memory. Returns 0 (EW_OK) on success.</summary>
    int ReadProgramDirectory(int handle, out IReadOnlyList<FocasProgramDirectoryEntry> entries);
    bool SupportsProgramBlockRead => true;
    bool SupportsModalReads => true;
    FocasDetailError? ReadDetailError(int handle) => null;
    int ReadCncMemoryDirectory(int handle, string path, out IReadOnlyList<ProgramFileEntry> entries)
    {
        entries = [];
        throw new NotSupportedException("FOCAS API does not implement CNC memory directory browsing.");
    }
}
