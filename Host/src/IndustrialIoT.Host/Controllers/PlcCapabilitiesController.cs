namespace IndustrialIoT.Host.Controllers;

using IndustrialIoT.Protocols.Models;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/plc/capabilities")]
public sealed class PlcCapabilitiesController : ControllerBase
{
    private static readonly IReadOnlyList<PlcProtocolCapability> Capabilities =
    [
        new()
        {
            Brand = "汇川",
            Models = ["H3U", "H5U", "AM600"],
            Protocols = ["Inovance", "InovanceSerial", "InovanceSerialOverTcp", "ModbusTCP", "ModbusRTU"],
            RequiredProperties = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Inovance"] = ["Series"],
                ["InovanceSerial"] = ["Series", "PortName"],
                ["InovanceSerialOverTcp"] = ["Series"],
                ["ModbusRTU"] = ["PortName"],
            },
            AddressExamples = ["M0", "D100", "HR100", "C0"],
        },
        new()
        {
            Brand = "欧姆龙",
            Models = ["CJ2M", "CP1W", "CP1H", "CP1E-N"],
            Protocols = ["FINS", "OmronHostLink"],
            RequiredProperties = new Dictionary<string, IReadOnlyList<string>>
            {
                ["FINS"] = [],
                ["OmronHostLink"] = ["Mode=Serial 时需要 PortName"],
            },
            AddressExamples = ["D100", "CIO100", "HR100", "A100"],
        },
        new()
        {
            Brand = "松下",
            Models = ["FP"],
            Protocols = ["Mewtocol", "MewtocolSerial"],
            RequiredProperties = new Dictionary<string, IReadOnlyList<string>>
            {
                ["Mewtocol"] = [],
                ["MewtocolSerial"] = ["PortName"],
            },
            AddressExamples = ["DT100", "R0", "X0", "Y0"],
        },
        new()
        {
            Brand = "西门子",
            Models = ["S7-1200", "S7-1500", "S7-300", "S7-400", "S7-200Smart"],
            Protocols = ["SiemensS7", "OpcUa"],
            RequiredProperties = new Dictionary<string, IReadOnlyList<string>>
            {
                ["SiemensS7"] = ["PlcType，可选 Rack/Slot/StringLength"],
                ["OpcUa"] = [],
            },
            AddressExamples = ["M100", "M100.0", "MW100", "DB1.100", "DB1.100.0"],
        },
    ];

    [HttpGet]
    public ActionResult<IReadOnlyList<PlcProtocolCapability>> Get() => Ok(Capabilities);
}
