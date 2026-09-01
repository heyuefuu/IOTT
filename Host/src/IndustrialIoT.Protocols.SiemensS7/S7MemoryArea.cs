namespace IndustrialIoT.Protocols.SiemensS7;

public enum S7MemoryArea
{
    Peripheral = 0x80,
    Input = 0x81,
    Output = 0x82,
    Marker = 0x83,
    DataBlock = 0x84,
    Counter = 0x1E,
    Timer = 0x1F,
    SystemMarker = 0x05,
    AnalogInput = 0x06,
    AnalogOutput = 0x07,
}

public enum SiemensS7PlcType
{
    S1200,
    S1500,
    S300,
    S400,
    S200Smart,
    S200,
}
