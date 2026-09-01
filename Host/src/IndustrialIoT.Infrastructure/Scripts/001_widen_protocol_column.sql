-- 001_widen_protocol_column.sql
-- Devices.Protocol: nvarchar(20) -> nvarchar(32)
-- Reason: ProtocolType 新增 InovanceSerialOverTcp (21 字符), 原列宽溢出.
-- Target : SQL Server.  Idempotent: 仅当现状为 nvarchar(20) 时执行.

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.tables  t ON c.object_id = t.object_id
    WHERE t.name = 'Devices'
      AND c.name = 'Protocol'
      AND c.system_type_id = TYPE_ID(N'nvarchar')
      AND c.max_length = 40   -- nvarchar(20) = 40 bytes
)
BEGIN
    PRINT 'Widening Devices.Protocol: nvarchar(20) -> nvarchar(32)';
    ALTER TABLE [Devices] ALTER COLUMN [Protocol] NVARCHAR(32) NOT NULL;
END
ELSE
BEGIN
    PRINT 'Devices.Protocol already widened (or table absent); skipping.';
END
