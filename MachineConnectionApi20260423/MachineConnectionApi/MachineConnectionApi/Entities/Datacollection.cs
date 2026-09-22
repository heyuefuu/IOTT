namespace MachineConnectionApi.Entities;

public class Datacollection
{
    public int Id { get; set; }

    /// <summary>显示名称（对应界面「显示名称」）</summary>
    public string Name { get; set; } = "";

    /// <summary>点位路径</summary>
    public string Path { get; set; } = "";

    /// <summary>节点类型（对应界面「节点类型」，如 Variable）</summary>
    public string Datatype { get; set; } = "";

    /// <summary>保存日期（库字段类型为 date）</summary>
    public DateTime? Datetime { get; set; }

    /// <summary>采集频率（ms）</summary>
    public int CollectionFrequency { get; set; } = 500;

    /// <summary>所属设备 Id，用于多设备隔离</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Collection route metadata. Legacy NCLinkApi rows also use the Host device driver.</summary>
    public string Protocol { get; set; } = "IndustrialIoT";
}
