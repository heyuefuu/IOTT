-- 003_sync_collection_config_columns.sql
-- Keeps existing SQL Server databases aligned with collection configuration entities.

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CollectionProfiles')
   AND COL_LENGTH('dbo.CollectionProfiles', 'IsEnabled') IS NULL
BEGIN
    PRINT 'Adding CollectionProfiles.IsEnabled';
    ALTER TABLE [CollectionProfiles]
        ADD [IsEnabled] BIT NOT NULL
            CONSTRAINT [DF_CollectionProfiles_IsEnabled] DEFAULT 1;
END
ELSE
BEGIN
    PRINT 'CollectionProfiles.IsEnabled already exists (or table absent); skipping.';
END

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TagConfigs')
   AND COL_LENGTH('dbo.TagConfigs', 'ScaleFactor') IS NULL
BEGIN
    PRINT 'Adding TagConfigs.ScaleFactor';
    ALTER TABLE [TagConfigs] ADD [ScaleFactor] FLOAT NULL;
END
ELSE
BEGIN
    PRINT 'TagConfigs.ScaleFactor already exists (or table absent); skipping.';
END

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TagConfigs')
   AND COL_LENGTH('dbo.TagConfigs', 'Offset') IS NULL
BEGIN
    PRINT 'Adding TagConfigs.Offset';
    ALTER TABLE [TagConfigs] ADD [Offset] FLOAT NULL;
END
ELSE
BEGIN
    PRINT 'TagConfigs.Offset already exists (or table absent); skipping.';
END
