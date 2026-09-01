namespace IndustrialIoT.Domain.Entities;
using IndustrialIoT.Domain.Enums;

public class NCProgram
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string DeviceId { get; set; }
    public required string FileName { get; set; }
    public required string RemotePath { get; set; }
    /// <summary>Local file path used for upload (needed for resume)</summary>
    public string? LocalFilePath { get; set; }
    /// <summary>Protocol-level transfer ID returned by the driver (needed for resume)</summary>
    public string? ProtocolTransferId { get; set; }
    public long FileSize { get; set; }
    public string? Checksum { get; set; }
    public TransferDirection Direction { get; set; }
    public TransferStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long BytesTransferred { get; set; }
    public string? ErrorMessage { get; set; }
}
