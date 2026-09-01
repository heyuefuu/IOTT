-- 002_create_realtime_data_records.sql
-- Adds durable realtime data output storage for existing SQL Server databases.

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = 'RealtimeDataRecords'
)
BEGIN
    CREATE TABLE [RealtimeDataRecords] (
        [Id] NVARCHAR(32) NOT NULL CONSTRAINT [PK_RealtimeDataRecords] PRIMARY KEY,
        [DeviceId] NVARCHAR(32) NOT NULL,
        [GroupName] NVARCHAR(100) NOT NULL,
        [PayloadJson] NVARCHAR(MAX) NOT NULL,
        [CollectedAt] DATETIMEOFFSET NOT NULL,
        [StoredAt] DATETIMEOFFSET NOT NULL,
        [ValueCount] INT NOT NULL,
        [CollectionDurationMs] FLOAT NOT NULL
    );

    CREATE INDEX [IX_RealtimeDataRecords_DeviceId_CollectedAt]
        ON [RealtimeDataRecords] ([DeviceId], [CollectedAt]);
END
