namespace IndustrialIoT.Protocols.FANUC;

internal static class FocasPmcBuffer
{
    public const int Capacity = 256;
    public const int HeaderLength = 8;

    public static int Validate(int area, int startAddr, int length, byte[]? buffer, int dataType)
    {
        if ((area is < 0 or > 9 && area != 12) || dataType is < 0 or > 2)
            return 4; // EW_ATTRIB
        if (startAddr is < 0 or > ushort.MaxValue)
            return 3; // EW_RANGE
        if (length is < 1 or > Capacity || length % (1 << dataType) != 0)
            return 2; // EW_LENGTH
        if (length > ushort.MaxValue - startAddr + 1)
            return 3; // EW_RANGE
        if (buffer is null)
            return 5; // EW_DATA
        if (buffer.Length < length)
            return 2; // EW_LENGTH
        return 0;
    }

    public static bool IsReadOnly(int area) => area is 1 or 3; // F and X
}
