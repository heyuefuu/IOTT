namespace IndustrialIoT.Domain.Enums;
public enum DataType { Bool, Int16, Int32, Int64, UInt16, UInt32, Float, Double, String, ByteArray }
public enum TagQuality { Good, Bad, Uncertain }
public enum TransferDirection { Upload, Download }
public enum TransferStatus { Pending, InProgress, Paused, Completed, Failed }
public enum ExportFormat { CSV, JSON, Excel }
