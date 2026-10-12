-- Migration: AddBrowserToClickEvents (Scenario 3: reporting)
-- Mirrors src/Migrations/20261010022547_AddBrowserToClickEvents.cs
-- The column is nullable, so existing rows stay valid (reported as "Unknown").

IF COL_LENGTH('dbo.ClickEvents', 'Browser') IS NULL
    ALTER TABLE dbo.ClickEvents ADD Browser varchar(30) NULL;
GO

-- Record the migration so EF Core does not try to apply it again
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261010022547_AddBrowserToClickEvents')
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20261010022547_AddBrowserToClickEvents', N'8.0.0');
GO

-- Rollback
-- ALTER TABLE dbo.ClickEvents DROP COLUMN Browser;
-- DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261010022547_AddBrowserToClickEvents';
