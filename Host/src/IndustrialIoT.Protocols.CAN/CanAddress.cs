namespace IndustrialIoT.Protocols.CAN;

using System.Globalization;

internal readonly record struct CanAddress(bool Extended, uint Id, bool BigEndian)
{
    public (bool Extended, uint Id) Key => (Extended, Id);

    public static CanAddress Parse(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var parts = address.Trim().Split(';');
        var identifier = parts[0].Split(':');
        if (identifier.Length != 2 || !new[] { "STD", "EXT" }.Contains(identifier[0], StringComparer.OrdinalIgnoreCase))
            throw new FormatException("CAN address must be STD:123 or EXT:18FF50E5 (hexadecimal ID).");
        var extended = identifier[0].Equals("EXT", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(identifier[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
            || id > (extended ? 0x1FFFFFFFU : 0x7FFU))
            throw new FormatException("CAN ID is outside the selected 11-bit or 29-bit range.");
        var bigEndian = false;
        if (parts.Length > 2) throw new FormatException("Only one optional endian=LE or endian=BE parameter is supported.");
        if (parts.Length == 2)
        {
            if (parts[1].Equals("endian=BE", StringComparison.OrdinalIgnoreCase)) bigEndian = true;
            else if (!parts[1].Equals("endian=LE", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("CAN address parameter must be endian=LE or endian=BE.");
        }
        return new(extended, id, bigEndian);
    }

    public string FramePrefix => (Extended ? "T" : "t") + Id.ToString(Extended ? "X8" : "X3", CultureInfo.InvariantCulture);
}
