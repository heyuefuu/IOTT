namespace IndustrialIoT.Protocols.Abstractions;

/// <summary>
/// 可选能力：删除设备侧程序文件。与 INCProgramTransfer 分离，未被某驱动实现即视为不支持删除。
/// </summary>
public interface INCProgramDelete
{
    /// <summary>删除设备侧指定路径的程序文件；返回是否删除成功。</summary>
    Task<bool> DeleteProgramAsync(string remotePath, CancellationToken ct = default);
}
