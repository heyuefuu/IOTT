namespace IndustrialIoT.Domain.Exceptions;

public class DomainException : Exception
{
    public string Code { get; }
    public DomainException(string code, string message) : base(message) => Code = code;
    public DomainException(string code, string message, Exception inner) : base(message, inner) => Code = code;
}

public class DeviceNotFoundException : DomainException
{
    public DeviceNotFoundException(string deviceId) : base("DEVICE_NOT_FOUND", $"Device '{deviceId}' not found") { }
}

public class DeviceConnectionException : DomainException
{
    public DeviceConnectionException(string deviceId, string message, Exception? inner = null)
        : base("DEVICE_CONNECTION_ERROR", $"Connection error for device '{deviceId}': {message}", inner ?? new Exception()) { }
}
