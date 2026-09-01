namespace IndustrialIoT.Application.Validators;

using System.Text;
using FluentValidation;
using IndustrialIoT.Application.DTOs;
using IndustrialIoT.Domain.Enums;

public class CreateDeviceRequestValidator : AbstractValidator<CreateDeviceRequest>
{
    public CreateDeviceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Brand).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Model).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Host).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.ConnectTimeoutMs).GreaterThan(0).When(x => x.ConnectTimeoutMs.HasValue);
        RuleFor(x => x.ReadTimeoutMs).GreaterThan(0).When(x => x.ReadTimeoutMs.HasValue);
        RuleFor(x => x.Protocol).IsInEnum();
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Transfer).SetValidator(new TransferDeviceRequestValidator()!).When(x => x.Transfer is not null);

        // SMB requires ShareName
        When(x => x.Protocol == ProtocolType.SMB, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .NotNull().WithMessage("SMB protocol requires ExtendedProperties with 'ShareName'")
                .Must(p => p != null && p.ContainsKey("ShareName"))
                .WithMessage("SMB protocol requires 'ShareName' in ExtendedProperties");
        });

        // Serial (and serial variants) require PortName
        When(x => x.Protocol is ProtocolType.Serial or ProtocolType.ModbusRTU or ProtocolType.InovanceSerial or ProtocolType.MewtocolSerial, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .NotNull().WithMessage("Serial protocol requires ExtendedProperties with 'PortName'")
                .Must(p => p != null && p.ContainsKey("PortName"))
                .WithMessage("Serial protocol requires 'PortName' in ExtendedProperties");
        });

        When(x => x.Protocol == ProtocolType.OmronHostLink && IsSerialMode(x.ExtendedProperties), () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .NotNull().WithMessage("Omron HostLink serial mode requires ExtendedProperties with 'PortName'")
                .Must(p => p != null && p.ContainsKey("PortName"))
                .WithMessage("Omron HostLink serial mode requires 'PortName' in ExtendedProperties");
        });

        When(x => x.Protocol is ProtocolType.Inovance or ProtocolType.InovanceSerial or ProtocolType.InovanceSerialOverTcp, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .NotNull().WithMessage("Inovance protocol requires ExtendedProperties with 'Series'")
                // 只查 ContainsKey 会让 Series="" 通过创建、直到连接时才失败，故要求非空白
                .Must(p => p != null && p.TryGetValue("Series", out var s) && !string.IsNullOrWhiteSpace(s))
                .WithMessage("汇川协议必须提供非空的 'Series'（H3U / H5U / AM / Easy）");
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("Station", out var st) || (byte.TryParse(st, out var v) && v > 0))
                .WithMessage("汇川协议 'Station' 必须是 1~255 的整数（Modbus 站号）");
        });

        // FANUC 机器人：可选 Encoding（默认 UTF-8，中文报警建议 GBK）+ 可选 PingTimeout（默认 3000ms）
        When(x => x.Protocol == ProtocolType.FanucRobot, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("Encoding", out var enc) || IsValidEncoding(enc))
                .WithMessage("FANUC 机器人 'Encoding' 必须是合法的字符编码名（如 GBK / UTF-8 / GB2312）");
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("PingTimeout", out var pt) || (int.TryParse(pt, out var v) && v > 0 && v <= 60000))
                .WithMessage("FANUC 机器人 'PingTimeout' 必须是 1~60000 的整数（毫秒）");
        });

        // 华中机器人：可选 Station（默认 1）
        When(x => x.Protocol == ProtocolType.HuazhongRobot, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("Station", out var s) || (byte.TryParse(s, out var v) && v > 0))
                .WithMessage("华中机器人 'Station' 必须是 1~255 的整数（Modbus 站号）");
        });

        // Modbus TCP：可选从站号，'UnitId' 与 'Station' 等价（驱动两个键都认）
        When(x => x.Protocol == ProtocolType.ModbusTCP, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("UnitId", out var u) || (byte.TryParse(u, out var v) && v > 0))
                .WithMessage("Modbus TCP 'UnitId' 必须是 1~255 的整数（Modbus 从站号）");
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("Station", out var s) || (byte.TryParse(s, out var v) && v > 0))
                .WithMessage("Modbus TCP 'Station' 必须是 1~255 的整数（Modbus 从站号）");
        });

        // 埃斯顿机器人：可选 Station（默认 1）+ PingTimeout（默认 3000ms）+ SnapshotTtlMs（默认 200ms）
        When(x => x.Protocol == ProtocolType.EstunRobot, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("Station", out var s) || (byte.TryParse(s, out var v) && v > 0))
                .WithMessage("埃斯顿机器人 'Station' 必须是 1~255 的整数（Modbus 站号）");
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("PingTimeout", out var pt) || (int.TryParse(pt, out var v) && v > 0 && v <= 60000))
                .WithMessage("埃斯顿机器人 'PingTimeout' 必须是 1~60000 的整数（毫秒）");
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("SnapshotTtlMs", out var ttl) || (int.TryParse(ttl, out var v) && v > 0 && v <= 60000))
                .WithMessage("埃斯顿机器人 'SnapshotTtlMs' 必须是 1~60000 的整数（毫秒，快照缓存有效期）");
            RuleFor(x => x.ExtendedProperties)
                .Must(p => p == null || !p.TryGetValue("SnapshotTimeoutMs", out var t) || (int.TryParse(t, out var v) && v > 0 && v <= 120000))
                .WithMessage("埃斯顿机器人 'SnapshotTimeoutMs' 必须是 1~120000 的整数（毫秒，单次快照读取硬超时）");
        });
    }

    private static bool IsValidEncoding(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try { Encoding.GetEncoding(name); return true; }
        catch { return false; }
    }

    private static bool IsSerialMode(Dictionary<string, string>? properties) =>
        properties is not null &&
        properties.TryGetValue("Mode", out var mode) &&
        mode.Equals("Serial", StringComparison.OrdinalIgnoreCase);
}

internal sealed class TransferDeviceRequestValidator : AbstractValidator<TransferDeviceRequest>
{
    public TransferDeviceRequestValidator()
    {
        RuleFor(x => x.Host).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.ConnectTimeoutMs).GreaterThan(0).When(x => x.ConnectTimeoutMs.HasValue);
        RuleFor(x => x.ReadTimeoutMs).GreaterThan(0).When(x => x.ReadTimeoutMs.HasValue);
        RuleFor(x => x.Protocol).IsInEnum();

        When(x => x.Protocol == ProtocolType.SMB, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .NotNull().WithMessage("SMB transfer requires ExtendedProperties with 'ShareName'")
                .Must(p => p != null && p.ContainsKey("ShareName"))
                .WithMessage("SMB transfer requires 'ShareName' in ExtendedProperties");
        });

        When(x => x.Protocol is ProtocolType.Serial or ProtocolType.ModbusRTU or ProtocolType.InovanceSerial or ProtocolType.MewtocolSerial, () =>
        {
            RuleFor(x => x.ExtendedProperties)
                .NotNull().WithMessage("Serial transfer requires ExtendedProperties with 'PortName'")
                .Must(p => p != null && p.ContainsKey("PortName"))
                .WithMessage("Serial transfer requires 'PortName' in ExtendedProperties");
        });
    }

}
