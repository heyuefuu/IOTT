using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Application.Validators;
using IndustrialIoT.Domain.Enums;

internal static class SerialConfigurationRegressionTests
{
    public static Task RunAsync()
    {
        var validator = new CreateDeviceRequestValidator();
        var properties = new Dictionary<string, string>
        {
            ["PortName"] = "COM7", ["BaudRate"] = "19200", ["DataBits"] = "7",
            ["Parity"] = "Even", ["StopBits"] = "Two",
        };
        var request = new CreateDeviceRequest
        {
            Name = "Serial transfer", Type = DeviceType.CNC, Brand = "Serial", Model = "fixture",
            Protocol = ProtocolType.Serial, Host = "localhost", Port = 0, ExtendedProperties = properties,
        };
        TestSupport.Require(validator.Validate(request).IsValid, "Serial port zero and valid settings rejected");
        TestSupport.Require(!validator.Validate(request with { Protocol = ProtocolType.FTP }).IsValid, "FTP port zero accepted");
        var main = request with { Protocol = ProtocolType.MTConnect, Port = 5000, ExtendedProperties = null };
        var transfer = new TransferDeviceRequest
        {
            Protocol = ProtocolType.Serial, Host = "localhost", Port = 0, ExtendedProperties = properties,
        };
        TestSupport.Require(validator.Validate(main with { Transfer = transfer }).IsValid, "Nested serial port zero rejected");
        foreach (var invalid in new (string Key, string Value)[]
        {
            ("PortName", " "), ("BaudRate", "0"), ("BaudRate", "oops"),
            ("DataBits", "9"), ("Parity", "invalid"), ("StopBits", "None"),
        })
        {
            var settings = new Dictionary<string, string>(properties) { [invalid.Key] = invalid.Value };
            TestSupport.Require(!validator.Validate(request with { ExtendedProperties = settings }).IsValid,
                $"Invalid primary serial {invalid.Key} accepted");
            TestSupport.Require(!validator.Validate(main with { Transfer = transfer with { ExtendedProperties = settings } }).IsValid,
                $"Invalid nested serial {invalid.Key} accepted");
        }
        TestSupport.Require(!validator.Validate(request with { ExtendedProperties = null }).IsValid, "Serial without PortName accepted");
        return Task.CompletedTask;
    }
}
