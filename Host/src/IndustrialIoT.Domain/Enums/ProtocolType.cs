namespace IndustrialIoT.Domain.Enums;
public enum ProtocolType
{
    // Numeric values are persisted in ConnectionConfig.Transfer.Protocol JSON.
    NCLink = 0, CAN = 1, Profibus = 2, ModbusTCP = 3, ModbusRTU = 4,
    Inovance = 5, FINS = 6, Mewtocol = 7, FOCAS = 8, NFS = 9,
    SMB = 10, FTP = 11, Serial = 12, OpcUa = 13, Simulator = 14,
    InovanceSerial = 15, InovanceSerialOverTcp = 16, OmronHostLink = 17,
    MewtocolSerial = 18, FanucRobot = 19, MTConnect = 20, HaasMdc = 21,
    HuazhongRobot = 22, Gskrm = 23, GskrmFileTransfer = 24,
    GskWebServer = 25, HncSdk = 26, NCLinkApi = 27, JingDiao = 28,
    SiemensS7 = 29, EstunRobot = 30
}
