namespace IndustrialIoT.Protocols.Mewtocol;

using System.Text;

/// <summary>
/// Low-level Mewtocol protocol frame builder / parser.
/// Frame format: %{station}#{command}{data}{BCC}\r
/// </summary>
internal static class MewtocolProtocol
{
    private const char FrameStart = '%';
    private const char FrameEnd = '\r';
    private const char CommandMarker = '#';

    // ── Command codes ──────────────────────────────────────────────

    public const string CmdReadData = "RD";      // Read DT data register
    public const string CmdWriteData = "WD";     // Write DT data register
    public const string CmdReadSpecial = "RS";    // Read SR special register
    public const string CmdReadContact = "RCS";   // Read contact (X/Y/R/T/C)
    public const string CmdWriteContact = "WCS";  // Write contact
    public const string CmdReadTimer = "RT";      // Read timer
    public const string CmdWriteTimer = "WT";     // Write timer
    public const string CmdReadCounter = "RC";    // Read counter
    public const string CmdWriteCounter = "WC";   // Write counter

    // ── Response markers ───────────────────────────────────────────

    private const char ResponseOk = '$';   // Normal response uses $
    private const char ResponseErr = '!';  // Error response uses !

    // ── Build ──────────────────────────────────────────────────────

    /// <summary>Build a complete Mewtocol request frame.</summary>
    public static byte[] BuildFrame(int station, string command, string data = "")
    {
        if (station is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(station), "Station must be 1-99.");

        // Body between % and BCC: stationNumber + # + command + data
        string body = $"{station:D2}{CommandMarker}{command}{data}";
        string bcc = ComputeBcc(body);
        string frame = $"{FrameStart}{body}{bcc}{FrameEnd}";
        return Encoding.ASCII.GetBytes(frame);
    }

    /// <summary>Build RD (Read DT register) frame.  Address range: startWord..endWord.</summary>
    public static byte[] BuildReadData(int station, int startWord, int endWord)
    {
        ValidateWordRange(startWord, endWord, 0, 32767, "DT");
        string data = $"D{startWord:D5}{endWord:D5}";
        return BuildFrame(station, CmdReadData, data);
    }

    /// <summary>Build WD (Write DT register) frame.</summary>
    public static byte[] BuildWriteData(int station, int startWord, ushort[] values)
    {
        int endWord = startWord + values.Length - 1;
        ValidateWordRange(startWord, endWord, 0, 32767, "DT");
        var sb = new StringBuilder();
        sb.Append($"D{startWord:D5}{endWord:D5}");
        foreach (var v in values)
            sb.Append(v.ToString("X4"));
        return BuildFrame(station, CmdWriteData, sb.ToString());
    }

    /// <summary>Build RS (Read SR special register) frame.</summary>
    public static byte[] BuildReadSpecial(int station, int startWord, int endWord)
    {
        ValidateWordRange(startWord, endWord, 0, 1023, "SR");
        string data = $"D{startWord:D5}{endWord:D5}";
        return BuildFrame(station, CmdReadSpecial, data);
    }

    /// <summary>Build RCS (Read contact status) frame.</summary>
    public static byte[] BuildReadContact(int station, string contactType, int number)
    {
        ValidateContactType(contactType);
        string data = $"{contactType}{number:D4}";
        return BuildFrame(station, CmdReadContact, data);
    }

    /// <summary>Build WCS (Write contact) frame.</summary>
    public static byte[] BuildWriteContact(int station, string contactType, int number, bool value)
    {
        ValidateContactType(contactType);
        string data = $"{contactType}{number:D4}{(value ? "1" : "0")}";
        return BuildFrame(station, CmdWriteContact, data);
    }

    /// <summary>Build RT (Read timer) frame.</summary>
    public static byte[] BuildReadTimer(int station, int timerNumber)
    {
        if (timerNumber is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(timerNumber));
        return BuildFrame(station, CmdReadTimer, timerNumber.ToString("D2"));
    }

    /// <summary>Build WT (Write timer) frame.</summary>
    public static byte[] BuildWriteTimer(int station, int timerNumber, ushort setValue, ushort elapsedValue)
    {
        if (timerNumber is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(timerNumber));
        string data = $"{timerNumber:D2}{setValue:X4}{elapsedValue:X4}";
        return BuildFrame(station, CmdWriteTimer, data);
    }

    /// <summary>Build RC (Read counter) frame.</summary>
    public static byte[] BuildReadCounter(int station, int counterNumber)
    {
        if (counterNumber is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(counterNumber));
        return BuildFrame(station, CmdReadCounter, counterNumber.ToString("D2"));
    }

    /// <summary>Build WC (Write counter) frame.</summary>
    public static byte[] BuildWriteCounter(int station, int counterNumber, ushort setValue, ushort elapsedValue)
    {
        if (counterNumber is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(counterNumber));
        string data = $"{counterNumber:D2}{setValue:X4}{elapsedValue:X4}";
        return BuildFrame(station, CmdWriteCounter, data);
    }

    // ── Parse ──────────────────────────────────────────────────────

    /// <summary>
    /// Parse a Mewtocol response frame.
    /// Returns the data payload on success; throws on protocol/device errors.
    /// </summary>
    public static string ParseResponse(byte[] buffer, int length)
    {
        string raw = Encoding.ASCII.GetString(buffer, 0, length).TrimEnd('\r', '\n');

        if (raw.Length < 5 || raw[0] != FrameStart)
            throw new MewtocolException("Invalid frame: missing start marker or too short.");

        // Extract BCC (last 2 chars before any trailing CR)
        string receivedBcc = raw[^2..];
        string bodyWithMarker = raw[1..^2]; // everything between % and BCC

        string expectedBcc = ComputeBcc(bodyWithMarker);
        if (!string.Equals(receivedBcc, expectedBcc, StringComparison.OrdinalIgnoreCase))
            throw new MewtocolException($"BCC mismatch: expected {expectedBcc}, received {receivedBcc}.");

        // Station number (2 chars), then response marker
        if (bodyWithMarker.Length < 3)
            throw new MewtocolException("Response too short after BCC validation.");

        char marker = bodyWithMarker[2];

        if (marker == ResponseErr)
        {
            string errorCode = bodyWithMarker.Length > 3 ? bodyWithMarker[3..] : "unknown";
            throw new MewtocolException($"Device returned error: {DecodeErrorCode(errorCode)}");
        }

        if (marker != ResponseOk)
            throw new MewtocolException($"Unexpected response marker: '{marker}'.");

        // Payload is everything after station + '$' + command echo
        // Typical: %01$RDdataBCC  —  skip station(2) + $(1) + command(2+)
        string payload = bodyWithMarker[3..]; // after station + $
        return payload;
    }

    /// <summary>
    /// Extract word values (16-bit unsigned) from a hex-encoded RD/RS response payload.
    /// The payload starts with the echoed command; data follows as groups of 4 hex chars.
    /// </summary>
    public static ushort[] ParseWordValues(string payload)
    {
        // Strip the echoed command prefix (e.g. "RDD00000D00001") — data starts after the register range
        // Find where the pure hex data begins: after the last digit of the end address
        // RD responses echo as: RDD{start5}{end5}{hex data...}
        // RS responses echo as: RSD{start5}{end5}{hex data...}
        int dataOffset = FindHexDataOffset(payload);
        string hexData = payload[dataOffset..];

        if (hexData.Length == 0 || hexData.Length % 4 != 0)
            throw new MewtocolException($"Invalid word data length: {hexData.Length}");

        var values = new ushort[hexData.Length / 4];
        for (int i = 0; i < values.Length; i++)
            values[i] = Convert.ToUInt16(hexData.Substring(i * 4, 4), 16);
        return values;
    }

    /// <summary>Parse a contact (bit) value from an RCS response payload.</summary>
    public static bool ParseContactValue(string payload)
    {
        // RCS response data ends with a single '0' or '1'
        if (payload.Length == 0)
            throw new MewtocolException("Empty contact response.");
        return payload[^1] == '1';
    }

    /// <summary>Parse timer/counter values from RT/RC response payload.  Returns (setValue, elapsedValue).</summary>
    public static (ushort SetValue, ushort ElapsedValue) ParseTimerCounterValue(string payload)
    {
        // Payload ends with 8 hex chars: SSSSEEEEE (set + elapsed)
        if (payload.Length < 8)
            throw new MewtocolException("Timer/counter response too short.");
        string hex = payload[^8..];
        ushort sv = Convert.ToUInt16(hex[..4], 16);
        ushort ev = Convert.ToUInt16(hex[4..], 16);
        return (sv, ev);
    }

    // ── BCC ────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the BCC (Block Check Character) — XOR of all ASCII bytes in the body,
    /// returned as a 2-char upper-case hex string.
    /// </summary>
    public static string ComputeBcc(string body)
    {
        byte xor = 0;
        foreach (char c in body)
            xor ^= (byte)c;
        return xor.ToString("X2");
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static void ValidateWordRange(int start, int end, int min, int max, string prefix)
    {
        if (start < min || start > max)
            throw new ArgumentOutOfRangeException(nameof(start), $"{prefix} start must be {min}-{max}.");
        if (end < start || end > max)
            throw new ArgumentOutOfRangeException(nameof(end), $"{prefix} end must be >= start and <= {max}.");
    }

    private static void ValidateContactType(string contactType)
    {
        if (contactType is not ("X" or "Y" or "R" or "T" or "C"))
            throw new ArgumentException($"Invalid contact type '{contactType}'. Must be X, Y, R, T, or C.");
    }

    /// <summary>
    /// Find where pure hex data begins in a read-word response payload.
    /// Expected formats: RDD00000D00001{hex...} or RSD00000D00001{hex...}
    /// We skip the command echo (2-3 chars) + 'D' + start(5) + end(5) = 13 chars typical.
    /// </summary>
    private static int FindHexDataOffset(string payload)
    {
        // Walk forward: first chars are command echo (RD, RS, etc.), then 'D', then 10 digits, then hex data
        // Robust approach: find the second 'D' separator after command, then skip 5 digits
        int i = 0;
        // Skip command letters (R, W, etc.)
        while (i < payload.Length && char.IsLetter(payload[i])) i++;
        // Skip first address (5 digits)
        while (i < payload.Length && char.IsDigit(payload[i])) i++;
        // Skip second address block separator if present (may be another 'D' or directly digits)
        if (i < payload.Length && payload[i] == 'D') i++;
        while (i < payload.Length && char.IsDigit(payload[i])) i++;
        return i;
    }

    private static string DecodeErrorCode(string code) => code switch
    {
        "21" => "NACK error",
        "22" => "WACK error",
        "23" => "Station number error",
        "24" => "Unit error",
        "25" => "Section designation error",
        "26" => "No contact error",
        "27" => "Connection full",
        "28" => "Timeout",
        "30" => "Monitor registration error",
        "40" => "Command error",
        "41" => "Address error",
        "42" => "Value out of range",
        "43" => "Not executable in RUN mode",
        "50" => "Checksum error",
        _ => $"Unknown error (code={code})"
    };
}

/// <summary>Exception for Mewtocol protocol-level errors.</summary>
public class MewtocolException : Exception
{
    public MewtocolException(string message) : base(message) { }
    public MewtocolException(string message, Exception inner) : base(message, inner) { }
}
