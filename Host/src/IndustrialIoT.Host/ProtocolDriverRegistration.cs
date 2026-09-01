namespace IndustrialIoT.Host;

using IndustrialIoT.Domain.Enums;
using IndustrialIoT.Protocols.FANUC;
using IndustrialIoT.Protocols.FINS;
using IndustrialIoT.Protocols.FileTransfer;
using IndustrialIoT.Protocols.EstunRobot;
using IndustrialIoT.Protocols.Gsk;
using IndustrialIoT.Protocols.Haas;
using IndustrialIoT.Protocols.HncSdk;
using IndustrialIoT.Protocols.HuazhongRobot;
using IndustrialIoT.Protocols.JingDiao;
using IndustrialIoT.Protocols.Inovance;
using IndustrialIoT.Protocols.Mewtocol;
using IndustrialIoT.Protocols.Modbus;
using IndustrialIoT.Protocols.MTConnect;
using IndustrialIoT.Protocols.NCLink;
using IndustrialIoT.Protocols.NCLinkApi;
using IndustrialIoT.Protocols.Registration;
using IndustrialIoT.Protocols.SiemensS7;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class ProtocolDriverRegistration
{
    public static void RegisterRealDrivers(IServiceCollection services, IDriverRegistry registry)
    {
        services.AddTransient<ModbusTcpDriver>();
        registry.Register(typeof(ModbusTcpDriver), ProtocolType.ModbusTCP, ["Inovance", "汇川", "广数", "广州数控", "GSK", "MICRO-T400", "*"]);
        // PROFIBUS-DP 经 PROFIBUS↔以太网网关桥接为 Modbus-TCP 接入（详见 ProfibusDriver 文档）
        services.AddTransient<ProfibusDriver>();
        registry.Register(typeof(ProfibusDriver), ProtocolType.Profibus, ["Profibus", "Profibus-DP", "DP", "*"]);
        services.AddTransient<ModbusRtuDriver>();
        registry.Register(typeof(ModbusRtuDriver), ProtocolType.ModbusRTU, ["ModbusRTU", "Modbus RTU", "PLC", "*"]);
        services.AddTransient<InovanceDriver>();
        registry.Register(typeof(InovanceDriver), ProtocolType.Inovance, ["Inovance", "汇川", "*"]);
        services.AddTransient<InovanceSerialDriver>();
        registry.Register(typeof(InovanceSerialDriver), ProtocolType.InovanceSerial, ["Inovance", "汇川", "Serial", "RS485"]);
        services.AddTransient<InovanceSerialOverTcpDriver>();
        registry.Register(typeof(InovanceSerialOverTcpDriver), ProtocolType.InovanceSerialOverTcp, ["Inovance", "汇川", "SerialOverTcp", "RS485"]);
        services.AddTransient<FinsDriver>();
        registry.Register(typeof(FinsDriver), ProtocolType.FINS, ["Omron", "欧姆龙", "CJ2M", "CP1W", "CP1H", "CP1E-N"]);
        services.AddTransient<OmronHostLinkDriver>();
        registry.Register(typeof(OmronHostLinkDriver), ProtocolType.OmronHostLink, ["Omron", "欧姆龙", "HostLink", "CJ2M", "CP1W", "CP1H", "CP1E-N"]);
        services.AddTransient<MewtocolDriver>();
        registry.Register(typeof(MewtocolDriver), ProtocolType.Mewtocol, ["Panasonic", "松下", "FP"]);
        services.AddTransient<MewtocolSerialDriver>();
        registry.Register(typeof(MewtocolSerialDriver), ProtocolType.MewtocolSerial, ["Panasonic", "松下", "FP", "Serial", "RS232"]);
        services.AddTransient<NCLinkDriver>();
        registry.Register(typeof(NCLinkDriver), ProtocolType.NCLink, ["华中数控"]);
        services.AddTransient<NCLinkApiDriver>();
        registry.Register(typeof(NCLinkApiDriver), ProtocolType.NCLinkApi,
            ["华中数控", "HNC", "HNC-8", "HNC-808", "HNC-818", "HNC-848", "HNC-848Di", "HNC-9", "HNC-10", "NCLink", "*"]);
        services.AddTransient<OpcUaDriver>();
        registry.Register(typeof(OpcUaDriver), ProtocolType.OpcUa, ["Siemens", "西门子", "840Dsl", "HNC", "HNC-848Di", "华中数控", "*"]);
        services.AddTransient<SiemensS7Driver>();
        registry.Register(typeof(SiemensS7Driver), ProtocolType.SiemensS7, ["Siemens", "西门子", "S7-1200", "S7-1500", "S7-300", "S7-400", "S7-200Smart", "*"]);
        services.AddSingleton<IFocasApi, NativeFocasApi>();
        services.AddTransient<FocasDriver>();
        registry.Register(typeof(FocasDriver), ProtocolType.FOCAS, ["FANUC", "发那科", "Makino", "牧野", "0i-MF", "0i-D", "30i", "31i", "32i"]);
        services.AddTransient<FanucRobotDriver>();
        registry.Register(typeof(FanucRobotDriver), ProtocolType.FanucRobot, ["FANUC", "发那科", "Robot", "机器人", "CRX", "M-", "R-", "LR"]);
        services.AddTransient<MTConnectDriver>();
        registry.Register(typeof(MTConnectDriver), ProtocolType.MTConnect, ["Mazak", "马扎克", "Brother", "兄弟", "MTConnect", "*"]);
        services.AddTransient<HaasMdcDriver>();
        registry.Register(typeof(HaasMdcDriver), ProtocolType.HaasMdc, ["Haas", "哈斯", "HaasNGC", "MDC"]);
        services.AddTransient<HuazhongRobotDriver>();
        // 华中机器人地址映射：默认无内置点位，从 appsettings.json 的 "RobotAddressMaps:Huazhong:Nodes" 节点加载。
        // 配置为空时仅支持原始 Modbus 地址直传（如 "1000;float"、"0x0040"、"100"）。
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var nodes = config.GetSection("RobotAddressMaps:Huazhong:Nodes")
                .Get<List<HuazhongRobotAddressSpace.Node>>() ?? new List<HuazhongRobotAddressSpace.Node>();
            return new HuazhongRobotAddressSpace(nodes);
        });
        registry.Register(typeof(HuazhongRobotDriver), ProtocolType.HuazhongRobot, ["华中数控", "华中机器人", "HSR", "HR", "HC", "HNC-Robot"]);
        // 埃斯顿机器人：地址映射由 HslCommunication EstunTcpNet.ReadRobotData() 内置，无需站点配置
        services.AddTransient<EstunRobotDriver>();
        registry.Register(typeof(EstunRobotDriver), ProtocolType.EstunRobot, ["埃斯顿", "ESTUN", "Estun", "ER", "ProNet"]);
        services.AddTransient<HncSdkDriver>();
        services.AddTransient<JingDiaoDriver>();
        registry.Register(typeof(HncSdkDriver), ProtocolType.HncSdk, ["华中数控", "HNC", "HNC-8", "HNC-808", "HNC-818", "HNC-848", "HNC-848Di"]);
        registry.Register(typeof(JingDiaoDriver), ProtocolType.JingDiao, ["精雕", "北京精雕", "JingDiao", "JD50", "JD60"]);
        services.AddSingleton<IGskrmApi>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var mode = config["Gskrm:Mode"] ?? "Ipc";
            if (mode.Equals("Native", StringComparison.OrdinalIgnoreCase))
                return new NativeGskrmApi();

            var baseUrl = config["Gskrm:IpcBaseUrl"] ?? "http://127.0.0.1:39123";
            return new GskrmIpcClient(new HttpClient { BaseAddress = new Uri(baseUrl) });
        });
        services.AddTransient<GskrmDriver>();
        registry.Register(typeof(GskrmDriver), ProtocolType.Gskrm, ["广数", "广州数控", "GSK", "MICRO-T400", "980", "25i"]);
        services.AddTransient<GskrmTransferDriver>();
        registry.Register(typeof(GskrmTransferDriver), ProtocolType.GskrmFileTransfer, ["广数", "广州数控", "GSK", "MICRO-T400"]);
        services.AddTransient<GskWebServerDriver>();
        registry.Register(typeof(GskWebServerDriver), ProtocolType.GskWebServer, ["GSK", "G3IOT", "GSK-WebServer", "GSK WebServer", "广数", "广州数控", "*"]);
        services.AddTransient<FtpTransferDriver>();
        registry.Register(typeof(FtpTransferDriver), ProtocolType.FTP, ["FTP", "*"]);
        services.AddTransient<SmbTransferDriver>();
        registry.Register(typeof(SmbTransferDriver), ProtocolType.SMB, ["SMB", "*"]);
        services.AddTransient<SerialTransferDriver>();
        registry.Register(typeof(SerialTransferDriver), ProtocolType.Serial, ["Serial", "*"]);
        services.AddTransient<NfsTransferDriver>();
        registry.Register(typeof(NfsTransferDriver), ProtocolType.NFS, ["NFS", "FANUC", "Makino", "*"]);
    }
}
