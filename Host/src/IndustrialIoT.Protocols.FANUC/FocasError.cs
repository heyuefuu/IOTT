namespace IndustrialIoT.Protocols.FANUC;

/// <summary>
/// Thrown when a FOCAS2 API call returns a non-zero code, carrying the raw code so callers can
/// decide between "retry", "reconnect then retry" and "give up".
/// </summary>
internal sealed class FocasApiException : InvalidOperationException
{
    public int ReturnCode { get; }
    public string ApiName { get; }

    public FocasApiException(string apiName, int returnCode)
        : base(FocasError.Describe($"FOCAS API {apiName} failed", returnCode))
    {
        ApiName = apiName;
        ReturnCode = returnCode;
    }
}

/// <summary>
/// FOCAS2 return-code names and their retry semantics.
/// <para>
/// FANUC embedded Ethernet routinely answers a perfectly healthy request with a transient code —
/// the CNC is servicing the MDI panel, another FOCAS client holds the internal buffer, or the
/// socket was reset by the controller's idle timer. Treating every non-zero code as a hard failure
/// is what makes collection look like "sometimes works, sometimes fails": one unlucky code fails
/// the whole batch, and the next poll succeeds again.
/// </para>
/// </summary>
internal static class FocasError
{
    /// <summary>Formats "<paramref name="operation"/>: -8 (EW_HANDLE)" for logs and UI error text.</summary>
    public static string Describe(string operation, int code)
    {
        var name = GetName(code);
        return name is null ? $"{operation}: {code}" : $"{operation}: {code} ({name})";
    }

    public static string? GetName(int code) => code switch
    {
        -17 => "EW_NODLL",
        -16 => "EW_BUS",
        -15 => "EW_HSSB",
        -11 => "EW_MMCSYS",
        -10 => "EW_SYSTEM2",
        -9 => "EW_VERSION",
        -8 => "EW_HANDLE",
        -7 => "EW_UNEXP",
        -6 => "EW_SYSTEM",
        -5 => "EW_PARITY",
        -4 => "EW_OVERRUN",
        -3 => "EW_FRAMING",
        -2 => "EW_RESET",
        -1 => "EW_BUSY",
        1 => "EW_FUNC",
        2 => "EW_LENGTH",
        3 => "EW_NUMBER",
        4 => "EW_RANGE",
        5 => "EW_DATA",
        6 => "EW_NOOPT",
        7 => "EW_PROT",
        8 => "EW_OVRFLOW",
        9 => "EW_PARAM",
        10 => "EW_BUFFER",
        11 => "EW_PATH",
        12 => "EW_MODE",
        13 => "EW_REJECT",
        14 => "EW_DTSRVR",
        15 => "EW_ALARM",
        16 => "EW_STOP",
        17 => "EW_PASSWD",
        _ => null
    };

    /// <summary>
    /// The controller is momentarily unable to answer, but the handle and socket are still good —
    /// retrying the same call after a short pause is expected to succeed.
    /// </summary>
    public static bool IsTransient(int code) => code switch
    {
        -1 => true,  // EW_BUSY    — CNC busy with another request
        10 => true,  // EW_BUFFER  — internal transfer buffer full/empty
        13 => true,  // EW_REJECT  — CNC refused the request in its current state
        16 => true,  // EW_STOP    — operation stopped, safe to re-issue
        _ => false
    };

    /// <summary>
    /// The library handle or the underlying socket is broken. Retrying the same call is pointless;
    /// the driver must drop the handle, re-run <c>cnc_allclibhndl3</c> and try again.
    /// </summary>
    public static bool RequiresReconnect(int code) => code switch
    {
        -8 => true,   // EW_HANDLE  — handle no longer valid
        -16 => true,  // EW_BUS     — bus/socket error
        -7 => true,   // EW_UNEXP   — unexpected library state
        -6 => true,   // EW_SYSTEM  — system error in the library
        -2 => true,   // EW_RESET   — connection reset by the controller
        -3 => true,   // EW_FRAMING
        -4 => true,   // EW_OVERRUN
        -5 => true,   // EW_PARITY
        _ => false
    };

    public static bool IsRecoverable(int code) => IsTransient(code) || RequiresReconnect(code);
}
