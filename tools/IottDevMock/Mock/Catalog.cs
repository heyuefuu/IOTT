namespace IottDevMock.Mock;

/// <summary>确定性设备台账：serve 与 seed 共用同一份数据。</summary>
public static class Catalog
{
    private sealed record Spec(
        string Slug, string Name, string Type, string Brand, string Model, string Protocol,
        bool Browsable, string? Series = null, string? PortName = null);

    private static readonly Spec[] Templates =
    [
        // ---- CNC ----
        new("cnc-opcua-01",   "CNC-OPCUA 加工中心", "CNC", "通用", "OPC-UA CNC", "OpcUa", true),
        new("cnc-focas-01",   "发那科 0i-MF",       "CNC", "发那科", "0i-MF", "FOCAS", true),
        new("cnc-nclink-01",  "华中数控 818",        "CNC", "华中数控", "HNC-818", "NCLink", true),
        new("cnc-nclinkapi-01","华中数控 848",       "CNC", "华中数控", "HNC-848", "NCLinkApi", true),
        new("cnc-mtconnect-01","马扎克 Nexus",       "CNC", "马扎克", "Nexus 6800", "MTConnect", false),
        new("cnc-haas-01",    "哈斯 VF-2",           "CNC", "哈斯", "VF-2", "HaasMdc", false),
        new("cnc-gskws-01",   "广数 928",            "CNC", "广州数控", "GSK-928", "GskWebServer", true),
        new("cnc-jingdiao-01","精雕 JD50",           "CNC", "精雕", "JD50", "JingDiao", true),
        new("cnc-hncsdk-01",  "华中数控 HNC-8",      "CNC", "华中数控", "HNC-8", "HncSdk", true),
        new("cnc-s7-01",      "西门子 828D",         "CNC", "西门子", "828D", "SiemensS7", true),

        // ---- PLC ----
        new("plc-modbus-01",  "西门子 S7-1500(Modbus)", "PLC", "西门子", "S7-1500", "ModbusTCP", true),
        new("plc-modbusrtu-01","台达 RTU",            "PLC", "台达", "DVP", "ModbusRTU", true, PortName: "COM3"),
        new("plc-s7-01",      "西门子 S7-1200",       "PLC", "西门子", "S7-1200", "SiemensS7", true),
        new("plc-fins-01",    "欧姆龙 CJ2M",          "PLC", "欧姆龙", "CJ2M", "FINS", true),
        new("plc-hostlink-01","欧姆龙 CP1W",          "PLC", "欧姆龙", "CP1W", "OmronHostLink", true),
        new("plc-mew-01",     "松下 FP7",             "PLC", "松下", "FP7", "Mewtocol", true),
        new("plc-mewserial-01","松下 FP0R",           "PLC", "松下", "FP0R", "MewtocolSerial", true, PortName: "COM4"),
        new("plc-inovance-01","汇川 H3U",             "PLC", "汇川", "H3U", "Inovance", true, Series: "H3U"),
        new("plc-inovance-02","汇川 H5U",             "PLC", "汇川", "H5U", "InovanceSerial", true, Series: "H5U", PortName: "COM5"),
        new("plc-inovance-03","汇川 AM600",           "PLC", "汇川", "AM600", "InovanceSerialOverTcp", true, Series: "AM600"),
        new("plc-opcua-01",   "倍福 OPC-UA",          "PLC", "倍福", "CX5140", "OpcUa", true),
        new("plc-modbus-02",  "施耐德 M241",          "PLC", "施耐德", "M241", "ModbusTCP", true),

        // ---- Robot ----
        new("robot-fanuc-01", "发那科 R-2000iC",      "Robot", "发那科", "R-2000iC", "FanucRobot", false),
        new("robot-estun-01", "埃斯顿 ER20",          "Robot", "埃斯顿", "ER20", "EstunRobot", false),
        new("robot-huazhong-01","华数 HSR",           "Robot", "华数", "HSR-JR605", "HuazhongRobot", false),
        new("robot-fanuc-02", "发那科 M-10iD",        "Robot", "发那科", "M-10iD", "FanucRobot", false),
    ];

    public static List<MockDevice> Build(int seed, int count, string scenario)
    {
        var rnd = new Random(seed);
        var list = new List<MockDevice>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < count; i++)
        {
            var spec = Templates[i % Templates.Length];
            var round = i / Templates.Length;
            var id = round == 0 ? spec.Slug : $"{spec.Slug}-{round + 1}";
            if (!usedIds.Add(id)) { i--; continue; }

            var host = $"192.168.{(i / 250) + 1}.{(i % 250) + 10}";
            if (spec.PortName is not null) host = "127.0.0.1"; // 串口设备 host 占位
            var port = DefaultPort(spec.Protocol);
            var ext = BuildExtensions(spec);
            var transfer = BuildTransfer(spec, rnd);

            list.Add(new MockDevice
            {
                Id = id,
                Name = round == 0 ? spec.Name : $"{spec.Name} #{round + 1}",
                Type = spec.Type,
                Brand = spec.Brand,
                Model = spec.Model,
                Protocol = spec.Protocol,
                Host = host,
                Port = port,
                ExtendedProperties = ext,
                Transfer = transfer,
                Browsable = spec.Browsable,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-(i % 30)),
            });
        }
        return list;
    }

    private static int DefaultPort(string protocol) => protocol switch
    {
        "ModbusTCP" or "HuazhongRobot" or "EstunRobot" => 502,
        "SiemensS7" => 102,
        "FINS" or "OmronHostLink" => 9600,
        "Mewtocol" or "MewtocolSerial" => 5000,
        "OpcUa" => 4840,
        "FOCAS" => 8193,
        "MTConnect" => 5000,
        "HaasMdc" => 9999,
        "NCLinkApi" => 19001,
        "GskWebServer" => 11520,
        "Gskrm" => 39123,
        "JingDiao" => 39125,
        "HncSdk" => 39210,
        "FanucRobot" => 60008,
        "NCLink" => 1883,
        _ => 502,
    };

    private static Dictionary<string, string> BuildExtensions(Spec spec)
    {
        var ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeviceCode"] = $"{spec.Type}-{spec.Slug.ToUpperInvariant()}",
            ["uiBrandKey"] = spec.Brand,
            ["description"] = "IottDevMock 虚拟设备",
        };
        if (spec.Series is not null)
        {
            ext["Series"] = spec.Series;
            ext["DataFormat"] = "ABCD";
            ext["AddressStartWithZero"] = "true";
        }
        if (spec.PortName is not null)
        {
            ext["PortName"] = spec.PortName;
            ext["BaudRate"] = "9600";
            ext["DataBits"] = "8";
            ext["StopBits"] = "One";
            ext["Parity"] = "None";
        }
        switch (spec.Protocol)
        {
            case "NCLink": ext["DeviceGuid"] = $"guid-{spec.Slug}"; ext["MqttBrokerHost"] = "127.0.0.1"; break;
            case "NCLinkApi": ext["DeviceId"] = spec.Slug.ToUpperInvariant(); break;
            case "GskWebServer": ext["DeviceSn"] = spec.Slug.ToUpperInvariant(); break;
            case "FOCAS": case "SiemensS7": ext["AxisLabels"] = "X,Y,Z"; break;
        }
        return ext;
    }

    private static MockTransfer? BuildTransfer(Spec spec, Random rnd)
    {
        // 仅 CNC 具备程序传输通道（与前端门控一致）
        if (spec.Type != "CNC") return null;
        var protocol = spec.Slug switch
        {
            "cnc-focas-01" => "FTP",
            "cnc-s7-01" => "SMB",
            "cnc-nclink-01" => "FTP",
            _ => "FTP",
        };
        var ext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (protocol == "SMB") ext["ShareName"] = "ncshare";
        return new MockTransfer
        {
            Protocol = protocol,
            Host = spec.Slug == "cnc-s7-01" ? "192.168.1.30" : "192.168.1.40",
            Port = protocol == "FTP" ? 21 : 445,
            ExtendedProperties = ext,
        };
    }
}
