using System.Buffers.Binary;

namespace MachineConnectionApi.Services;

internal static class CsModbusProtocol
{
    public static byte[] Respond(byte[] request, ushort[] registers, bool[] coils, ushort[] inputRegisters, bool[] discreteInputs)
    {
        if (request.Length == 0) return Error(0, 3);
        if (request.Length < 2) return Error(request[0], 3);
        var function = request[0];
        if (function is not (1 or 2 or 3 or 4 or 5 or 6 or 15 or 16)) return Error(function, 1);
        if (request.Length < 5) return Error(function, 3);
        var address = Read(request, 1);
        var count = Read(request, 3);
        lock (registers)
        {
            if (function is 5 or 6)
            {
                if (request.Length != 5 || (function == 5 && count is not (0 or 0xFF00))) return Error(function, 3);
                if (function == 5) coils[address] = count == 0xFF00;
                else registers[address] = count;
                return request.ToArray();
            }
            var maximum = function is 1 or 2 ? 2000 : function == 15 ? 1968 : function == 16 ? 123 : 125;
            if (count == 0 || count > maximum) return Error(function, 3);
            if ((int)address + count > 65536) return Error(function, 2);
            if (function is 15 or 16) return WriteMany(request, registers, coils, address, count);
            if (request.Length != 5) return Error(function, 3);
            var byteCount = function is 1 or 2 ? (count + 7) / 8 : count * 2;
            var response = new byte[byteCount + 2];
            response[0] = function;
            response[1] = (byte)byteCount;
            for (var index = 0; index < count; index++)
            {
                if (function is 1 or 2)
                {
                    var bits = function == 1 ? coils : discreteInputs;
                    if (bits[address + index]) response[2 + index / 8] |= (byte)(1 << (index % 8));
                }
                else BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + index * 2),
                    (function == 3 ? registers : inputRegisters)[address + index]);
            }
            return response;
        }
    }

    private static byte[] WriteMany(byte[] request, ushort[] registers, bool[] coils, ushort address, ushort count)
    {
        var expected = request[0] == 15 ? (count + 7) / 8 : count * 2;
        if (request.Length != 6 + expected || request[5] != expected) return Error(request[0], 3);
        for (var index = 0; index < count; index++)
        {
            if (request[0] == 15) coils[address + index] = (request[6 + index / 8] & (1 << (index % 8))) != 0;
            else registers[address + index] = Read(request, 6 + index * 2);
        }
        return request[..5];
    }

    internal static ushort Read(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static byte[] Error(byte function, byte code) => [(byte)(function | 0x80), code];
}
