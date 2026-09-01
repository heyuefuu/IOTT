namespace IndustrialIoT.Protocols.FANUC;

using System.Text.RegularExpressions;
using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.Models;

/// <summary>
/// Maps hierarchical address paths (e.g. "/Robot/Joint/J1") to FOCAS API calls
/// and converts raw FOCAS data into <see cref="TagValue"/> results.
/// </summary>
internal sealed partial class FocasAddressMapper
{
    private delegate int FanucIntReader(int handle, out int value);

    private readonly IFocasApi _api;
    private int _handle;
    private string[] _axisLabels = ["X", "Y", "Z", "A", "B", "C"];

    // PMC address type constants matching FOCAS2 conventions
    private const int PmcDI = 0; // Digital Input
    private const int PmcDO = 1; // Digital Output
    private const int PmcRI = 5; // Robot Input
    private const int PmcRO = 6; // Robot Output

    private sealed class BatchReadCache
    {
        public double[]? ActPositions { get; set; }
        public FocasAxisPosition[]? AxisPositions { get; set; }
        public FocasSystemInfo? SystemInfo { get; set; }
        public FocasStatusInfo? StatusInfo { get; set; }
        public FocasProgramInfo? ProgramInfo { get; set; }
        public Dictionary<int, byte[]> PmcBytes { get; } = [];
        public string? ProgramBlock { get; set; }
        public (int StatusCode, string Message)? AlarmStatusInfo { get; set; }
    }

    internal FocasAddressMapper(IFocasApi api) => _api = api;

    internal void SetHandle(int handle) => _handle = handle;

    internal void ConfigureAxisLabels(string? axisLabels)
    {
        if (string.IsNullOrWhiteSpace(axisLabels))
        {
            _axisLabels = ["X", "Y", "Z", "A", "B", "C"];
            return;
        }

        var labels = axisLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length > 0)
            _axisLabels = labels.Select(label => label.ToUpperInvariant()).ToArray();
    }

    // ── Read ─────────────────────────────────────────────────────────

    internal TagValue Read(string address, DataType dataType)
        => Read(address, dataType, cache: null);

    internal IReadOnlyList<TagValue> ReadBatch(IReadOnlyList<TagReadRequest> requests)
    {
        var cache = new BatchReadCache();
        var results = new TagValue[requests.Count];
        for (var i = 0; i < requests.Count; i++)
            results[i] = Read(requests[i].Address, requests[i].DataType, cache);
        return results;
    }

    private TagValue Read(string address, DataType dataType, BatchReadCache? cache)
    {
        try
        {
            var (value, actualType) = ReadCore(address, dataType, cache);
            return new TagValue
            {
                Address = address,
                DataType = actualType,
                Value = value,
                Quality = TagQuality.Good,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new TagValue
            {
                Address = address,
                DataType = dataType,
                Value = GetDefaultValue(dataType),
                Quality = TagQuality.Bad,
                Timestamp = DateTimeOffset.UtcNow,
                ErrorMessage = ex.Message
            };
        }
    }

    // ── Write ────────────────────────────────────────────────────────

    internal WriteResult Write(string address, DataType dataType, object value)
    {
        try
        {
            WriteCore(address, dataType, value);
            return new WriteResult { Success = true };
        }
        catch (Exception ex)
        {
            return new WriteResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ── Address resolution ───────────────────────────────────────────

    private (object Value, DataType Type) ReadCore(string address, DataType requestedType, BatchReadCache? cache)
    {
        // Normalize: trim, ensure leading slash, case-insensitive compare
        string path = NormalizePath(address);

        if (path.StartsWith("/cnc/fanuc/", StringComparison.OrdinalIgnoreCase))
            return ReadFanucPath(path, cache);

        // /CNC/Axis/{Label}[/Position]
        if (path.StartsWith("/cnc/axis/", StringComparison.OrdinalIgnoreCase))
        {
            int axis = GetAxisIndex(path);
            var positions = GetActPositions(cache);
            if (axis < 1 || axis > positions.Length)
                throw new ArgumentOutOfRangeException(nameof(address), $"Axis {axis} out of range (1-{positions.Length})");
            return (positions[axis - 1], DataType.Double);
        }

        // /Robot/TCP/{component}
        if (TcpRegex().Match(path) is { Success: true } tm)
        {
            string comp = tm.Groups[1].Value.ToUpperInvariant();
            var positions = GetActPositions(cache);
            // TCP: X,Y,Z derived from forward kinematics approximation (use J1-J3)
            // W,P,R from J4-J6 as Euler angles
            int idx = comp switch
            {
                "X" => 0, "Y" => 1, "Z" => 2,
                "W" => 3, "P" => 4, "R" => 5,
                _ => throw new ArgumentException($"Unknown TCP component '{comp}'. Expected X,Y,Z,W,P,R")
            };
            // Simulated TCP: scale joint angles into mm/deg range
            double tcpValue = idx < 3
                ? positions[idx] * 10.0 + 500.0  // XYZ in mm
                : positions[idx];                  // WPR in degrees
            return (tcpValue, DataType.Double);
        }

        // /CNC/Status/Running
        if (path.Equals("/cnc/status/running", StringComparison.OrdinalIgnoreCase))
        {
            int rc = _api.ReadRunStatus(_handle, out int status);
            ThrowIfError(rc, "ReadRunStatus");
            return (status != 0, DataType.Bool);
        }

        // /CNC/Status/Mode
        if (path.Equals("/cnc/status/mode", StringComparison.OrdinalIgnoreCase))
        {
            var status = GetStatusInfo(cache);
            return ((short)status.Auto, DataType.Int16);
        }

        // /CNC/Spindle/Speed
        if (path.Equals("/cnc/spindle/speed", StringComparison.OrdinalIgnoreCase))
        {
            int rc = _api.ReadSpindleSpeed(_handle, out int speed);
            ThrowIfError(rc, "ReadSpindleSpeed");
            return (speed, DataType.Int32);
        }

        // /CNC/Alarm/Active
        if (path.Equals("/cnc/alarm/active", StringComparison.OrdinalIgnoreCase))
        {
            var (statusCode, _) = GetAlarmStatusInfo(cache);
            return (statusCode != 0, DataType.Bool);
        }

        // /CNC/Alarm/Code
        if (path.Equals("/cnc/alarm/code", StringComparison.OrdinalIgnoreCase))
        {
            var (statusCode, _) = GetAlarmStatusInfo(cache);
            return (statusCode, DataType.Int32);
        }

        // /CNC/Alarm/Message
        if (path.Equals("/cnc/alarm/message", StringComparison.OrdinalIgnoreCase))
        {
            var (_, msg) = GetAlarmStatusInfo(cache);
            return (msg, DataType.String);
        }

        // /CNC/Program/Number
        if (path.Equals("/cnc/program/number", StringComparison.OrdinalIgnoreCase))
            return (GetProgramInfo(cache).RunningNumber, DataType.Int32);

        // /CNC/Program/Line
        if (path.Equals("/cnc/program/line", StringComparison.OrdinalIgnoreCase))
        {
            if (!_api.SupportsProgramBlockRead)
                throw new NotSupportedException("FOCAS program line read is not supported on this controller.");
            int line = _api is SimulatedFocasApi s
                ? s.CurrentProgramLine
                : ReadProgramLineFromBlock(cache);
            return (line, DataType.Int32);
        }

        if (path.Equals("/cnc/path/execution", StringComparison.OrdinalIgnoreCase))
        {
            var status = GetStatusInfo(cache);
            return (MapExecution(status), DataType.String);
        }

        if (path.Equals("/cnc/path/controllermode", StringComparison.OrdinalIgnoreCase))
        {
            var status = GetStatusInfo(cache);
            return (MapControllerMode(status), DataType.String);
        }

        if (path.Equals("/cnc/program/block", StringComparison.OrdinalIgnoreCase))
        {
            if (!_api.SupportsProgramBlockRead)
                throw new NotSupportedException("FOCAS program block read is not supported on this controller.");
            return (GetProgramBlock(cache), DataType.String);
        }

        if (path.Equals("/cnc/feed/actual", StringComparison.OrdinalIgnoreCase))
        {
            int rc = _api.ReadActualFeedRate(_handle, out var feedRate);
            ThrowIfError(rc, "ReadActualFeedRate");
            return (feedRate, DataType.Double);
        }

        if (path.Equals("/cnc/feed/commanded", StringComparison.OrdinalIgnoreCase))
        {
            if (!_api.SupportsModalReads)
                throw new NotSupportedException("FOCAS commanded feed read is not supported on this controller.");
            int rc = _api.ReadCommandedFeedRate(_handle, out var feedRate);
            ThrowIfError(rc, "ReadCommandedFeedRate");
            return (feedRate, DataType.Double);
        }

        if (path.Equals("/cnc/tool/id", StringComparison.OrdinalIgnoreCase))
        {
            if (!_api.SupportsModalReads)
                throw new NotSupportedException("FOCAS tool id read is not supported on this controller.");
            int rc = _api.ReadToolId(_handle, out var toolId);
            ThrowIfError(rc, "ReadToolId");
            return (toolId, DataType.Int32);
        }

        // /CNC/PMC/DI[n], /CNC/PMC/DO[n], /CNC/PMC/RI[n], /CNC/PMC/RO[n]
        if (PmcRegex().Match(path) is { Success: true } iom)
        {
            string ioType = iom.Groups[1].Value.ToUpperInvariant();
            int index = int.Parse(iom.Groups[2].Value);
            return ReadIoBit(ioType, index, cache);
        }

        throw new ArgumentException($"Unknown FANUC address: '{address}'");
    }

    private void WriteCore(string address, DataType dataType, object value)
    {
        string path = NormalizePath(address);

        // Only PMC output areas are writable on FANUC CNC.
        if (PmcRegex().Match(path) is { Success: true } iom)
        {
            string ioType = iom.Groups[1].Value.ToUpperInvariant();
            int index = int.Parse(iom.Groups[2].Value);

            if (ioType is "DI" or "RI")
                throw new InvalidOperationException($"{ioType} is read-only (input). Use {ioType[0]}O for outputs.");

            int pmcType = ioType == "DO" ? PmcDO : PmcRO;
            int maxIndex = ioType == "DO" ? 15 : 7;
            if (index < 0 || index > maxIndex)
                throw new ArgumentOutOfRangeException(nameof(address), $"{ioType} index must be 0-{maxIndex}");

            bool boolVal = Convert.ToBoolean(value);

            // Read current byte, set/clear the target bit, write back
            byte[] data = new byte[2]; // Read 2 bytes to cover the range
            _api.ReadPmc(_handle, pmcType, 0, data.Length, data);

            if (boolVal)
                data[index / 8] |= (byte)(1 << (index % 8));
            else
                data[index / 8] &= (byte)~(1 << (index % 8));

            int rc = _api.WritePmc(_handle, pmcType, 0, data.Length, data);
            ThrowIfError(rc, "WritePmc");
            return;
        }

        throw new InvalidOperationException(
            $"Address '{address}' is read-only or not writable. Only DO[n] and RO[n] outputs support writes.");
    }

    // ── I/O helpers ──────────────────────────────────────────────────

    private double[] GetActPositions(BatchReadCache? cache)
    {
        if (cache?.ActPositions is { } positions)
            return positions;

        int rc = _api.ReadActPos(_handle, out positions);
        ThrowIfError(rc, "ReadActPos");
        if (cache is not null)
            cache.ActPositions = positions;
        return positions;
    }

    private FocasSystemInfo GetSystemInfo(BatchReadCache? cache)
    {
        if (cache?.SystemInfo is { } info)
            return info;

        int rc = _api.ReadSysInfo(_handle, out info);
        ThrowIfError(rc, "ReadSysInfo");
        if (cache is not null)
            cache.SystemInfo = info;
        return info;
    }

    private FocasAxisPosition[] GetAxisPositions(BatchReadCache? cache)
    {
        if (cache?.AxisPositions is { } positions)
            return positions;

        int rc = _api.ReadAxisPositions(_handle, out positions);
        ThrowIfError(rc, "ReadAxisPositions");
        if (cache is not null)
            cache.AxisPositions = positions;
        return positions;
    }

    private FocasStatusInfo GetStatusInfo(BatchReadCache? cache)
    {
        if (cache?.StatusInfo is { } status)
            return status;

        int rc = _api.ReadStatusInfo(_handle, out status);
        ThrowIfError(rc, "ReadStatusInfo");
        if (cache is not null)
            cache.StatusInfo = status;
        return status;
    }

    private FocasProgramInfo GetProgramInfo(BatchReadCache? cache)
    {
        if (cache?.ProgramInfo is { } program)
            return program;

        int rc = _api.ReadProgramInfo(_handle, out program);
        ThrowIfError(rc, "ReadProgramInfo");
        if (cache is not null)
            cache.ProgramInfo = program;
        return program;
    }

    private byte[] GetPmcBytes(int pmcType, BatchReadCache? cache)
    {
        if (cache is not null && cache.PmcBytes.TryGetValue(pmcType, out var data))
            return data;

        data = new byte[2];
        int rc = _api.ReadPmc(_handle, pmcType, 0, data.Length, data);
        ThrowIfError(rc, "ReadPmc");
        if (cache is not null)
            cache.PmcBytes[pmcType] = data;
        return data;
    }

    private string GetProgramBlock(BatchReadCache? cache)
    {
        if (cache?.ProgramBlock is { } block)
            return block;

        int rc = _api.ReadProgramBlock(_handle, out block);
        ThrowIfError(rc, "ReadProgramBlock");
        if (cache is not null)
            cache.ProgramBlock = block;
        return block;
    }

    private (int StatusCode, string Message) GetAlarmStatusInfo(BatchReadCache? cache)
    {
        if (cache?.AlarmStatusInfo is { } alarm)
            return alarm;

        int rc = _api.ReadAlarmStatus(_handle, out var statusCode, out var message);
        ThrowIfError(rc, "ReadAlarmStatus");
        alarm = (statusCode, message);
        if (cache is not null)
            cache.AlarmStatusInfo = alarm;
        return alarm;
    }

    private (object Value, DataType Type) ReadFanucPath(string path, BatchReadCache? cache)
    {
        if (path.StartsWith("/cnc/fanuc/axis/", StringComparison.OrdinalIgnoreCase))
            return ReadFanucAxisPath(path, cache);

        return path switch
        {
            "/cnc/fanuc/system/maxaxis" => ReadFanucSystemValue(info => info.MaxAxis, cache),
            "/cnc/fanuc/system/cnctypecode" => ReadFanucSystemString(info => info.CncTypeCode, cache),
            "/cnc/fanuc/system/cnctypename" => ReadFanucSystemString(info => MapCncTypeName(info.CncTypeCode), cache),
            "/cnc/fanuc/system/series" => ReadFanucSystemString(info => info.Series, cache),
            "/cnc/fanuc/system/version" => ReadFanucSystemString(info => info.Version, cache),
            "/cnc/fanuc/system/axiscount" => ReadFanucSystemValue(info => info.AxisCount, cache),
            "/cnc/fanuc/status/run" => ReadFanucStatusValue(status => status.Run, cache),
            "/cnc/fanuc/status/alarm" => ReadFanucStatusValue(status => status.Alarm, cache),
            "/cnc/fanuc/status/automodecode" => ReadFanucStatusValue(status => status.Auto, cache),
            "/cnc/fanuc/status/automodename" => ReadFanucStatusString(status => MapFanucAutoMode(status.Auto), cache),
            "/cnc/fanuc/feed/actual" => ReadFanucInt(_api.ReadActualFeed, "ReadActualFeed"),
            "/cnc/fanuc/feed/override" => ReadFanucInt(_api.ReadFeedOverride, "ReadFeedOverride"),
            "/cnc/fanuc/program/mainnumber" => ReadFanucProgramValue(program => program.MainNumber, cache),
            "/cnc/fanuc/program/subnumber" => ReadFanucProgramValue(program => program.RunningNumber, cache),
            "/cnc/fanuc/program/name" => ReadFanucProgramString(program => program.Name, cache),
            "/cnc/fanuc/tool/maxgroup" => ReadFanucInt(_api.ReadMaxToolGroup, "ReadMaxToolGroup"),
            "/cnc/fanuc/production/partcountcurrent" => ReadFanucPartCountCurrent(),
            "/cnc/fanuc/production/partcounttotal" => ReadFanucParameter(6712),
            "/cnc/fanuc/time/cuttingseconds" => ReadFanucDuration(6753, 6754),
            "/cnc/fanuc/time/workingseconds" => ReadFanucDuration(6751, 6752),
            "/cnc/fanuc/time/poweronseconds" => ReadFanucPowerOnSeconds(),
            "/cnc/fanuc/alarm/status" => ReadFanucAlarmValue(alarm => alarm.StatusCode),
            "/cnc/fanuc/alarm/message" => ReadFanucAlarmString(alarm => alarm.Message),
            _ => throw new ArgumentException($"Unknown FANUC address: '{path}'")
        };
    }

    private (object Value, DataType Type) ReadIoBit(string ioType, int index, BatchReadCache? cache)
    {
        int pmcType = ioType switch
        {
            "DI" => PmcDI,
            "DO" => PmcDO,
            "RI" => PmcRI,
            "RO" => PmcRO,
            _ => throw new ArgumentException($"Unknown I/O type: {ioType}")
        };

        int maxIndex = ioType is "DI" or "DO" ? 15 : 7;
        if (index < 0 || index > maxIndex)
            throw new ArgumentOutOfRangeException(nameof(index), $"{ioType} index must be 0-{maxIndex}");

        var data = GetPmcBytes(pmcType, cache);

        bool bitValue = (data[index / 8] & (1 << (index % 8))) != 0;
        return (bitValue, DataType.Bool);
    }

    private (object Value, DataType Type) ReadFanucAxisPath(string path, BatchReadCache? cache)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new ArgumentException($"Unknown FANUC axis address: '{path}'");

        int axis = GetAxisIndex($"/cnc/axis/{parts[3]}");
        var positions = GetAxisPositions(cache);
        if (axis < 1 || axis > positions.Length)
            throw new ArgumentOutOfRangeException(nameof(path), $"Axis {axis} out of range (1-{positions.Length})");

        var position = positions[axis - 1];
        return parts[4] switch
        {
            "absolute" => (position.Absolute, DataType.Double),
            "relative" => (position.Relative, DataType.Double),
            _ => throw new ArgumentException($"Unknown FANUC axis address: '{path}'")
        };
    }

    private (object Value, DataType Type) ReadFanucSystemValue(Func<FocasSystemInfo, int> selector, BatchReadCache? cache)
        => (selector(GetSystemInfo(cache)), DataType.Int32);

    private (object Value, DataType Type) ReadFanucSystemString(Func<FocasSystemInfo, string> selector, BatchReadCache? cache)
        => (selector(GetSystemInfo(cache)), DataType.String);

    private (object Value, DataType Type) ReadFanucStatusValue(Func<FocasStatusInfo, int> selector, BatchReadCache? cache)
        => (selector(GetStatusInfo(cache)), DataType.Int32);

    private (object Value, DataType Type) ReadFanucStatusString(Func<FocasStatusInfo, string> selector, BatchReadCache? cache)
        => (selector(GetStatusInfo(cache)), DataType.String);

    private (object Value, DataType Type) ReadFanucProgramValue(Func<FocasProgramInfo, int> selector, BatchReadCache? cache)
        => (selector(GetProgramInfo(cache)), DataType.Int32);

    private (object Value, DataType Type) ReadFanucProgramString(Func<FocasProgramInfo, string> selector, BatchReadCache? cache)
        => (selector(GetProgramInfo(cache)), DataType.String);

    private (object Value, DataType Type) ReadFanucInt(FanucIntReader reader, string apiName)
    {
        int rc = reader(_handle, out var value);
        ThrowIfError(rc, apiName);
        return (value, DataType.Int32);
    }

    private (object Value, DataType Type) ReadFanucParameter(int number)
    {
        int rc = _api.ReadParameter(_handle, number, out var value);
        ThrowIfError(rc, "ReadParameter");
        return (value, DataType.Int32);
    }

    private (object Value, DataType Type) ReadFanucPartCountCurrent()
    {
        int rc = _api.ReadMacroVariable(_handle, 3901, out var value);
        ThrowIfError(rc, "ReadMacroVariable");
        return ((int)Math.Round(value), DataType.Int32);
    }

    private (object Value, DataType Type) ReadFanucDuration(int millisecondParam, int minuteParam)
    {
        int rc = _api.ReadParameter(_handle, millisecondParam, out var milliseconds);
        ThrowIfError(rc, "ReadParameter");
        rc = _api.ReadParameter(_handle, minuteParam, out var minutes);
        ThrowIfError(rc, "ReadParameter");
        return (minutes * 60 + (milliseconds / 1000), DataType.Int32);
    }

    private (object Value, DataType Type) ReadFanucPowerOnSeconds()
    {
        int rc = _api.ReadParameter(_handle, 6750, out var minutes);
        ThrowIfError(rc, "ReadParameter");
        return (minutes * 60, DataType.Int32);
    }

    private (object Value, DataType Type) ReadFanucAlarmValue(Func<(int StatusCode, string Message), int> selector)
    {
        int rc = _api.ReadAlarmStatus(_handle, out var statusCode, out var message);
        ThrowIfError(rc, "ReadAlarmStatus");
        return (selector((statusCode, message)), DataType.Int32);
    }

    private (object Value, DataType Type) ReadFanucAlarmString(Func<(int StatusCode, string Message), string> selector)
    {
        int rc = _api.ReadAlarmStatus(_handle, out var statusCode, out var message);
        ThrowIfError(rc, "ReadAlarmStatus");
        return (selector((statusCode, message)), DataType.String);
    }

    // ── Address space definition (for browser) ───────────────────────

    internal IReadOnlyList<AddressNode> GetAddressSpace(string? parentPath)
    {
        string parent = NormalizePath(parentPath ?? string.Empty);

        return parent switch
        {
            "" or "/" => [MakeFolder("/CNC", "CNC"), MakeFolder("/Programs", "NC Programs")],

            "/cnc" =>
            [
                MakeFolder("/CNC/Axis", "Axis"),
                MakeFolder("/CNC/Fanuc", "Fanuc"),
                MakeFolder("/CNC/Path", "Path"),
                MakeFolder("/CNC/Spindle", "Spindle"),
                MakeFolder("/CNC/Status", "Status"),
                MakeFolder("/CNC/Alarm", "Alarm"),
                MakeFolder("/CNC/Program", "Program"),
                MakeFolder("/CNC/Feed", "Feed"),
                MakeFolder("/CNC/Tool", "Tool"),
                MakeFolder("/CNC/PMC", "PMC"),
            ],

            "/cnc/axis" => MakeAxisNodes(),
            "/cnc/fanuc" =>
            [
                MakeFolder("/CNC/Fanuc/System", "System"),
                MakeFolder("/CNC/Fanuc/Status", "Status"),
                MakeFolder("/CNC/Fanuc/Feed", "Feed"),
                MakeFolder("/CNC/Fanuc/Program", "Program"),
                MakeFolder("/CNC/Fanuc/Tool", "Tool"),
                MakeFolder("/CNC/Fanuc/Production", "Production"),
                MakeFolder("/CNC/Fanuc/Time", "Time"),
                MakeFolder("/CNC/Fanuc/Axis", "Axis"),
                MakeFolder("/CNC/Fanuc/Alarm", "Alarm"),
            ],
            "/cnc/path" =>
            [
                MakeVar("/CNC/Path/Execution", "Execution", DataType.String, true, false),
                MakeVar("/CNC/Path/ControllerMode", "Controller Mode", DataType.String, true, false),
            ],
            "/cnc/spindle" => [MakeVar("/CNC/Spindle/Speed", "Spindle Speed (RPM)", DataType.Int32, true, false)],
            "/cnc/status" =>
            [
                MakeVar("/CNC/Status/Running", "Running", DataType.Bool, true, false),
                MakeVar("/CNC/Status/Mode", "Mode (0=STOP,1=RUN)", DataType.Int16, true, false),
            ],
            "/cnc/alarm" =>
            [
                MakeVar("/CNC/Alarm/Active", "Alarm Active", DataType.Bool, true, false),
                MakeVar("/CNC/Alarm/Code", "Alarm Code", DataType.Int32, true, false),
                MakeVar("/CNC/Alarm/Message", "Alarm Message", DataType.String, true, false),
            ],
            "/cnc/program" =>
            [
                MakeVar("/CNC/Program/Number", "Program Number", DataType.Int32, true, false),
                MakeVar("/CNC/Program/Line", "Program Line", DataType.Int32, true, false),
                MakeVar("/CNC/Program/Block", "Current Program Block", DataType.String, true, false),
            ],
            "/cnc/feed" =>
            [
                MakeVar("/CNC/Feed/Actual", "Actual Feedrate", DataType.Double, true, false),
                MakeVar("/CNC/Feed/Commanded", "Commanded Feedrate", DataType.Double, true, false),
            ],
            "/cnc/tool" => [MakeVar("/CNC/Tool/Id", "Tool Id", DataType.Int32, true, false)],
            "/cnc/fanuc/system" => MakeFanucSystemNodes(),
            "/cnc/fanuc/status" => MakeFanucStatusNodes(),
            "/cnc/fanuc/feed" => MakeFanucFeedNodes(),
            "/cnc/fanuc/program" => MakeFanucProgramNodes(),
            "/cnc/fanuc/tool" => [MakeVar("/CNC/Fanuc/Tool/MaxGroup", "Max Tool Group", DataType.Int32, true, false)],
            "/cnc/fanuc/production" => MakeFanucProductionNodes(),
            "/cnc/fanuc/time" => MakeFanucTimeNodes(),
            "/cnc/fanuc/axis" => MakeFanucAxisNodes(),
            "/cnc/fanuc/alarm" => MakeFanucAlarmNodes(),
            "/cnc/pmc" =>
            [
                MakeFolder("/CNC/PMC/DI", "Digital Inputs (0-15)"),
                MakeFolder("/CNC/PMC/DO", "Digital Outputs (0-15)"),
                MakeFolder("/CNC/PMC/RI", "Robot Inputs (0-7)"),
                MakeFolder("/CNC/PMC/RO", "Robot Outputs (0-7)"),
            ],
            "/cnc/pmc/di" => MakeIoNodes("DI", 16, writable: false),
            "/cnc/pmc/do" => MakeIoNodes("DO", 16, writable: true),
            "/cnc/pmc/ri" => MakeIoNodes("RI", 8,  writable: false),
            "/cnc/pmc/ro" => MakeIoNodes("RO", 8,  writable: true),

            _ => GetSpecialChildren(parent) ?? GetAxisChildren(parent)
        };
    }

    // ── Static node builders ─────────────────────────────────────────

    private IReadOnlyList<AddressNode> MakeAxisNodes()
    {
        var list = new AddressNode[_axisLabels.Length];
        for (int i = 0; i < _axisLabels.Length; i++)
            list[i] = MakeFolder($"/CNC/Axis/{_axisLabels[i]}", $"{_axisLabels[i]} Axis");
        return list;
    }

    private IReadOnlyList<AddressNode> MakeFanucAxisNodes()
    {
        var list = new AddressNode[_axisLabels.Length];
        for (int i = 0; i < _axisLabels.Length; i++)
            list[i] = MakeFolder($"/CNC/Fanuc/Axis/{_axisLabels[i]}", $"{_axisLabels[i]} Axis");
        return list;
    }

    private static IReadOnlyList<AddressNode> MakeFanucSystemNodes() =>
    [
        MakeVar("/CNC/Fanuc/System/MaxAxis", "Max Axis", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/System/CncTypeCode", "CNC Type Code", DataType.String, true, false),
        MakeVar("/CNC/Fanuc/System/CncTypeName", "CNC Type Name", DataType.String, true, false),
        MakeVar("/CNC/Fanuc/System/Series", "Series", DataType.String, true, false),
        MakeVar("/CNC/Fanuc/System/Version", "Version", DataType.String, true, false),
        MakeVar("/CNC/Fanuc/System/AxisCount", "Axis Count", DataType.Int32, true, false),
    ];

    private static IReadOnlyList<AddressNode> MakeFanucStatusNodes() =>
    [
        MakeVar("/CNC/Fanuc/Status/Run", "Run Code", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Status/Alarm", "Alarm Flag", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Status/AutoModeCode", "Auto Mode Code", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Status/AutoModeName", "Auto Mode Name", DataType.String, true, false),
    ];

    private static IReadOnlyList<AddressNode> MakeFanucFeedNodes() =>
    [
        MakeVar("/CNC/Fanuc/Feed/Actual", "Actual Feed", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Feed/Override", "Feed Override", DataType.Int32, true, false),
    ];

    private static IReadOnlyList<AddressNode> MakeFanucProgramNodes() =>
    [
        MakeVar("/CNC/Fanuc/Program/MainNumber", "Main Program Number", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Program/SubNumber", "Sub Program Number", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Program/Name", "Program Name", DataType.String, true, false),
    ];

    private static IReadOnlyList<AddressNode> MakeFanucProductionNodes() =>
    [
        MakeVar("/CNC/Fanuc/Production/PartCountCurrent", "Current Part Count", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Production/PartCountTotal", "Total Part Count", DataType.Int32, true, false),
    ];

    private static IReadOnlyList<AddressNode> MakeFanucTimeNodes() =>
    [
        MakeVar("/CNC/Fanuc/Time/CuttingSeconds", "Cutting Seconds", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Time/WorkingSeconds", "Working Seconds", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Time/PowerOnSeconds", "Power On Seconds", DataType.Int32, true, false),
    ];

    private static IReadOnlyList<AddressNode> MakeFanucAlarmNodes() =>
    [
        MakeVar("/CNC/Fanuc/Alarm/Status", "Alarm Status", DataType.Int32, true, false),
        MakeVar("/CNC/Fanuc/Alarm/Message", "Alarm Message", DataType.String, true, false),
    ];

    private static IReadOnlyList<AddressNode> MakeIoNodes(string prefix, int count, bool writable)
    {
        var list = new AddressNode[count];
        for (int i = 0; i < count; i++)
            list[i] = MakeVar($"/CNC/PMC/{prefix}[{i}]", $"{prefix}[{i}]", DataType.Bool, true, writable);
        return list;
    }

    private IReadOnlyList<AddressNode> GetAxisChildren(string parent)
    {
        var axisPath = TryNormalizeAxisPath(parent);
        if (axisPath is null || axisPath.Equals("/cnc/axis", StringComparison.OrdinalIgnoreCase))
            return [];

        var axisName = axisPath["/cnc/axis/".Length..].ToUpperInvariant();
        return [MakeVar($"/CNC/Axis/{axisName}/Position", "Position", DataType.Double, true, false)];
    }

    private IReadOnlyList<AddressNode>? GetSpecialChildren(string parent)
    {
        if (!parent.StartsWith("/cnc/fanuc/axis/", StringComparison.OrdinalIgnoreCase))
            return null;

        var axisName = parent["/cnc/fanuc/axis/".Length..].ToUpperInvariant();
        if (Array.FindIndex(_axisLabels, label => label.Equals(axisName, StringComparison.OrdinalIgnoreCase)) < 0)
            return [];

        return
        [
            MakeVar($"/CNC/Fanuc/Axis/{axisName}/Absolute", "Absolute", DataType.Double, true, false),
            MakeVar($"/CNC/Fanuc/Axis/{axisName}/Relative", "Relative", DataType.Double, true, false),
        ];
    }

    private static AddressNode MakeFolder(string path, string name) =>
        new() { Path = path, DisplayName = name, NodeType = AddressNodeType.Folder, IsReadable = false, IsWritable = false };

    private static AddressNode MakeVar(string path, string name, DataType dt, bool read, bool write) =>
        new() { Path = path, DisplayName = name, NodeType = AddressNodeType.Variable, DataType = dt, IsReadable = read, IsWritable = write };

    // ── Utilities ────────────────────────────────────────────────────

    private string NormalizePath(string path)
    {
        path = path.Trim().Replace('\\', '/');
        if (!path.StartsWith('/') && path.Length > 0)
            path = "/" + path;
        path = path.TrimEnd('/').ToLowerInvariant();
        var jointMatch = JointRegex().Match(path);
        if (jointMatch.Success)
            return $"/cnc/axis/axis{jointMatch.Groups[1].Value}";
        if (path == "/robot")
            return "/cnc";
        if (path == "/robot/joint")
            return "/cnc/axis";
        if (path == "/robot/io")
            return "/cnc/pmc";
        if (path == "/robot/status")
            return "/cnc/status";
        if (path == "/robot/alarm")
            return "/cnc/alarm";
        if (path == "/robot/program")
            return "/cnc/program";
        if (path == "/robot/tcp")
            return "/cnc/axis";
        if (path == "/robot/status/running")
            return "/cnc/status/running";
        if (path == "/robot/status/mode")
            return "/cnc/status/mode";
        if (path == "/robot/status/speed")
            return "/cnc/spindle/speed";
        if (path.StartsWith("/robot/alarm/"))
            return path.Replace("/robot/alarm/", "/cnc/alarm/");
        if (path.StartsWith("/robot/program/"))
            return path.Replace("/robot/program/", "/cnc/program/");
        if (path.StartsWith("/robot/io/"))
            return path.Replace("/robot/io/", "/cnc/pmc/");
        var axisPath = TryNormalizeAxisPath(path);
        if (axisPath is not null)
            return axisPath;
        return path;
    }

    private static void ThrowIfError(int returnCode, string apiName)
    {
        if (returnCode != 0)
            throw new InvalidOperationException($"FOCAS API {apiName} failed with code {returnCode}");
    }

    private string? TryNormalizeAxisPath(string path)
    {
        if (!path.StartsWith("/cnc/axis/"))
            return null;

        var segment = path["/cnc/axis/".Length..].ToUpperInvariant();
        if (segment.EndsWith("/POSITION", StringComparison.OrdinalIgnoreCase))
            segment = segment[..^"/POSITION".Length];
        if (segment.StartsWith("AXIS") && int.TryParse(segment[4..], out var axisNo))
            return axisNo >= 1 && axisNo <= _axisLabels.Length ? $"/cnc/axis/{_axisLabels[axisNo - 1].ToLowerInvariant()}" : null;

        var axisIndex = Array.FindIndex(_axisLabels, label => label.Equals(segment, StringComparison.OrdinalIgnoreCase));
        return axisIndex >= 0 ? $"/cnc/axis/{_axisLabels[axisIndex].ToLowerInvariant()}" : null;
    }

    private int GetAxisIndex(string normalizedAxisPath)
    {
        var segment = normalizedAxisPath["/cnc/axis/".Length..].ToUpperInvariant();
        var axisIndex = Array.FindIndex(_axisLabels, label => label.Equals(segment, StringComparison.OrdinalIgnoreCase));
        if (axisIndex < 0)
            throw new ArgumentException($"Unknown CNC axis '{segment}'");
        return axisIndex + 1;
    }

    private int ReadProgramLineFromBlock(BatchReadCache? cache)
    {
        var block = GetProgramBlock(cache);
        var match = ProgramLineRegex().Match(block);
        return match.Success && int.TryParse(match.Groups[1].Value, out var line) ? line : 0;
    }

    private static string MapExecution(FocasStatusInfo status) => status.Run switch
    {
        >= 3 => "ACTIVE",
        2 => "INTERRUPTED",
        _ when status.Alarm != 0 || status.Emergency != 0 => "STOPPED",
        _ => "READY"
    };

    private static string MapControllerMode(FocasStatusInfo status) => status.Auto switch
    {
        1 => "AUTOMATIC",
        0 => "MANUAL_DATA_INPUT",
        3 => "EDIT",
        4 or 5 or 6 => "MANUAL",
        _ => "UNKNOWN"
    };

    private static string MapFanucAutoMode(int autoMode) => autoMode switch
    {
        0 => "MDI",
        1 => "MEMory",
        2 => "Not Defined",
        3 => "EDIT",
        4 => "h",
        5 => "JOG",
        6 => "Teach in JOG",
        7 => "Teach in h",
        8 => "INC·feed",
        9 => "REFerence",
        10 => "ReMoTe",
        _ => "others mode"
    };

    private static string MapCncTypeName(string cncTypeCode) => cncTypeCode switch
    {
        "15" => "Series 15/15i",
        "16" => "Series 16/16i",
        "18" => "Series 18/18i",
        "21" => "Series 21/21i",
        "30" => "Series 30i",
        "31" => "Series 31i",
        "32" => "Series 32i",
        "35" => "Series 35i",
        "0" or " 0" => "Series 0i",
        "PD" => "Power Mate i-D",
        "PH" => "Power Mate i-H",
        "PM" => "Power Motion i",
        _ => "其它类型"
    };

    private static object GetDefaultValue(DataType dt) => dt switch
    {
        DataType.Bool   => false,
        DataType.Int16  => (short)0,
        DataType.Int32  => 0,
        DataType.Int64  => 0L,
        DataType.UInt16 => (ushort)0,
        DataType.UInt32 => 0u,
        DataType.Float  => 0f,
        DataType.Double => 0.0,
        DataType.String => string.Empty,
        _ => 0
    };

    // ── Compiled regex patterns ──────────────────────────────────────

    [GeneratedRegex(@"^/robot/joint/j(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex JointRegex();

    [GeneratedRegex(@"^/cnc/axis/axis(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AxisRegex();

    [GeneratedRegex(@"^/robot/tcp/([xyzwpr])$", RegexOptions.IgnoreCase)]
    private static partial Regex TcpRegex();

    [GeneratedRegex(@"^/cnc/pmc/(di|do|ri|ro)\[(\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex PmcRegex();

    [GeneratedRegex(@"\bN(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramLineRegex();
}
