namespace IndustrialIoT.Protocols.Gsk;

/// <summary>
/// Abstraction layer over the <c>gskrm.dll</c> native surface. The GSK RM SDK
/// exports ~154 `GSKRM_*` functions; this interface only exposes the subset
/// the driver uses today. Extend deliberately — callers should rely on stable
/// high-level primitives, not raw P/Invoke shapes.
///
/// Implementations:
///   — <see cref="NativeGskrmApi"/>: real P/Invoke, in-process (needs matching-arch DLL).
///   — (pending) SimulatedGskrmApi: deterministic stub for dev/test.
///   — (pending) GskrmIpcClient: wire protocol to an out-of-process x86 shim.
/// </summary>
public interface IGskrmApi
{
    // ── Connection lifecycle ──────────────────────────────────────────
    /// <summary>
    /// <c>GSKRM_CreateInstance</c> — allocate an RM session against a CNC.
    /// Returns a non-negative handle on success, or a negative error code.
    /// </summary>
    int CreateInstance(string host, int port, int timeoutMs, out int handle);
    int CloseInstance(int handle);
    int GetConnectState(int handle, out bool connected);
    int SetOvertime(int handle, int timeoutMs);

    // ── System identity ───────────────────────────────────────────────
    int GetCncInfo(int handle, out GskrmCncInfo info);
    int GetCncTypeName(int handle, out string name);
    int GetAvailAxisCount(int handle, out int count);
    int GetAvailAxisName(int handle, int axisIndex, out string name);
    int GetAvailAxisUnits(int handle, int axisIndex, out string unit);

    // ── Runtime status ────────────────────────────────────────────────
    int GetCncState(int handle, out GskrmCncState state);
    int GetStatus(int handle, out int statusCode);
    int GetWorkMode(int handle, out string mode);
    int GetRunCncProgName(int handle, out string programName);
    int GetMainCncProgName(int handle, out string programName);
    int GetRunLineNo(int handle, out int lineNo);
    int GetEspState(int handle, out bool estop);

    // ── Geometry / kinematics ─────────────────────────────────────────
    int GetPosition(int handle, int axisIndex, out GskrmPosition position);
    int GetFecPoint(int handle, int axisIndex, out double value);

    // ── Speed & override ──────────────────────────────────────────────
    int GetFeedSpeedAct(int handle, out int feedMmPerMin);
    int GetFeedSpeedProg(int handle, out int feedMmPerMin);
    int GetSpindleSpeedAct(int handle, out int rpm);
    int GetSpindleSpeedProg(int handle, out int rpm);
    int GetAllRateInfo(int handle, out GskrmRateInfo rates);

    // ── Alarms ────────────────────────────────────────────────────────
    int GetAlarmCount(int handle, out int count);
    int GetAlarmInfo(int handle, int index, out GskrmAlarm alarm);

    // ── PLC / macro / parameter ───────────────────────────────────────
    int GetPlcData(int handle, string address, int length, byte[] buffer);
    int SetPlcData(int handle, string address, byte[] data);
    int GetMacroValue(int handle, int number, out double value);
    int SetMacroValue(int handle, int number, double value);
    int GetParamValue(int handle, int number, int axis, out int value);
    int SetParamValue(int handle, int number, int axis, int value);

    // ── Tool offsets (minimal surface) ────────────────────────────────
    int GetToolOffsetCount(int handle, out int count);
    int GetToolOffsetValue(int handle, int index, out double value);

    // ── Part count / cutting time ─────────────────────────────────────
    int GetPartCount(int handle, out int count);
    int GetCutTime(int handle, out TimeSpan elapsed);
    int GetRunTime(int handle, out TimeSpan elapsed);

    // ── NC program file transfer ──────────────────────────────────────
    int GetCNCFileCount(int handle, out int count);
    int GetCNCFileList(int handle, out IReadOnlyList<GskrmCncFileEntry> entries);
    int GetCNCFileInfo(int handle, string name, out GskrmCncFileEntry entry);
    /// <summary><c>GSKRM_ReceiveCNCFile</c> — pull a program from the CNC to local disk.</summary>
    int ReceiveCNCFile(int handle, string remoteName, string localPath, IProgress<long>? progress = null);
    /// <summary><c>GSKRM_SendCNCFile</c> — push a local program to the CNC.</summary>
    int SendCNCFile(int handle, string localPath, string remoteName, IProgress<long>? progress = null);
    int DeleteCNCFile(int handle, string remoteName);
    int ProgInstall(int handle, string remoteName);
    int ProgUninstall(int handle, string remoteName);
}
