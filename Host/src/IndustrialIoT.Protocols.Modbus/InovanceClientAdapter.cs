namespace IndustrialIoT.Protocols.Inovance;

using System.Text;
using HslCommunication;
using HslCommunication.Core;
using HslCommunication.Profinet.Inovance;

public interface IInovanceClient : IDisposable
{
    Task<OperateResult> ConnectServerAsync();
    Task ConnectCloseAsync();
    Task<OperateResult<byte[]>> ReadAsync(string address, ushort length);
    Task<OperateResult<bool>> ReadBoolAsync(string address);
    Task<OperateResult<short>> ReadInt16Async(string address);
    Task<OperateResult<ushort>> ReadUInt16Async(string address);
    Task<OperateResult<int>> ReadInt32Async(string address);
    Task<OperateResult<uint>> ReadUInt32Async(string address);
    Task<OperateResult<long>> ReadInt64Async(string address);
    Task<OperateResult<double>> ReadDoubleAsync(string address);
    Task<OperateResult<float>> ReadFloatAsync(string address);
    Task<OperateResult<string>> ReadStringAsync(string address, ushort length, Encoding encoding);
    Task<OperateResult> WriteAsync(string address, bool value);
    Task<OperateResult> WriteAsync(string address, short value);
    Task<OperateResult> WriteAsync(string address, ushort value);
    Task<OperateResult> WriteAsync(string address, int value);
    Task<OperateResult> WriteAsync(string address, uint value);
    Task<OperateResult> WriteAsync(string address, long value);
    Task<OperateResult> WriteAsync(string address, double value);
    Task<OperateResult> WriteAsync(string address, float value);
    Task<OperateResult> WriteAsync(string address, byte[] value);
    Task<OperateResult> WriteAsync(string address, string value, int length, Encoding encoding);
}

internal sealed class HslInovanceClientAdapter : IInovanceClient
{
    public InovanceTcpNet Inner { get; }

    public HslInovanceClientAdapter(InovanceTcpNet inner) => Inner = inner;

    public Task<OperateResult> ConnectServerAsync() => Inner.ConnectServerAsync();
    public Task ConnectCloseAsync() => Inner.ConnectCloseAsync();
    public Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) => Inner.ReadAsync(address, length);
    public Task<OperateResult<bool>> ReadBoolAsync(string address) => Inner.ReadBoolAsync(address);
    public Task<OperateResult<short>> ReadInt16Async(string address) => Inner.ReadInt16Async(address);
    public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Inner.ReadUInt16Async(address);
    public Task<OperateResult<int>> ReadInt32Async(string address) => Inner.ReadInt32Async(address);
    public Task<OperateResult<uint>> ReadUInt32Async(string address) => Inner.ReadUInt32Async(address);
    public Task<OperateResult<long>> ReadInt64Async(string address) => Inner.ReadInt64Async(address);
    public Task<OperateResult<double>> ReadDoubleAsync(string address) => Inner.ReadDoubleAsync(address);
    public Task<OperateResult<float>> ReadFloatAsync(string address) => Inner.ReadFloatAsync(address);
    public Task<OperateResult<string>> ReadStringAsync(string address, ushort length, Encoding encoding) => Inner.ReadStringAsync(address, length, encoding);
    public Task<OperateResult> WriteAsync(string address, bool value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, short value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, ushort value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, int value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, uint value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, long value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, double value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, float value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, byte[] value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, string value, int length, Encoding encoding) => Inner.WriteAsync(address, value, length, encoding);
    public void Dispose() => Inner.Dispose();
}

internal sealed class HslInovanceSerialClientAdapter : IInovanceClient
{
    public InovanceSerial Inner { get; }

    public HslInovanceSerialClientAdapter(InovanceSerial inner) => Inner = inner;

    public Task<OperateResult> ConnectServerAsync() => Task.FromResult(Inner.Open());
    public Task ConnectCloseAsync() { Inner.Close(); return Task.CompletedTask; }
    public Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) => Inner.ReadAsync(address, length);
    public Task<OperateResult<bool>> ReadBoolAsync(string address) => Inner.ReadBoolAsync(address);
    public Task<OperateResult<short>> ReadInt16Async(string address) => Inner.ReadInt16Async(address);
    public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Inner.ReadUInt16Async(address);
    public Task<OperateResult<int>> ReadInt32Async(string address) => Inner.ReadInt32Async(address);
    public Task<OperateResult<uint>> ReadUInt32Async(string address) => Inner.ReadUInt32Async(address);
    public Task<OperateResult<long>> ReadInt64Async(string address) => Inner.ReadInt64Async(address);
    public Task<OperateResult<double>> ReadDoubleAsync(string address) => Inner.ReadDoubleAsync(address);
    public Task<OperateResult<float>> ReadFloatAsync(string address) => Inner.ReadFloatAsync(address);
    public Task<OperateResult<string>> ReadStringAsync(string address, ushort length, Encoding encoding) => Inner.ReadStringAsync(address, length, encoding);
    public Task<OperateResult> WriteAsync(string address, bool value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, short value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, ushort value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, int value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, uint value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, long value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, double value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, float value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, byte[] value) => Inner.WriteAsync(address, value);
    public Task<OperateResult> WriteAsync(string address, string value, int length, Encoding encoding) => Inner.WriteAsync(address, value, length, encoding);
    public void Dispose() => Inner.Dispose();
}
