namespace IndustrialIoT.Protocols.Gsk;

/// <summary>
/// GSK RM SDK return codes.
/// Value 0 is always success; non-zero is a library-defined failure.
/// The specific mapping below is best-effort pending the official header —
/// update once we can cross-check against a real SDK sample.
/// </summary>
public static class GskrmErrorCodes
{
    public const int Ok = 0;

    // Codes below are inferred from the typical GSK RM convention and matching
    // FOCAS-style semantics. Treat as [unverified] until confirmed.
    public const int InvalidHandle = -1;
    public const int Timeout = -2;
    public const int NotConnected = -3;
    public const int InvalidArgument = -4;
    public const int NotSupported = -5;
    public const int Busy = -6;
    public const int IoError = -7;

    public static string Describe(int code) => code switch
    {
        Ok => "OK",
        InvalidHandle => "Invalid handle",
        Timeout => "Timeout",
        NotConnected => "Not connected",
        InvalidArgument => "Invalid argument",
        NotSupported => "Not supported",
        Busy => "Busy",
        IoError => "IO error",
        _ => $"GSKRM error {code}"
    };
}
