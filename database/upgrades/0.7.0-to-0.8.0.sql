SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'catalog.KeyTransactions', N'CreatedByAdminUserId') IS NULL
        ALTER TABLE [catalog].[KeyTransactions] ADD [CreatedByAdminUserId] uniqueidentifier NULL;

    IF COL_LENGTH(N'store.PointTransactions', N'CreatedByAdminUserId') IS NULL
        ALTER TABLE [store].[PointTransactions] ADD [CreatedByAdminUserId] uniqueidentifier NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_KeyTransactions_AdminUser')
        ALTER TABLE [catalog].[KeyTransactions] WITH CHECK ADD CONSTRAINT [FK_KeyTransactions_AdminUser]
            FOREIGN KEY ([CreatedByAdminUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_PointTransactions_AdminUser')
        ALTER TABLE [store].[PointTransactions] WITH CHECK ADD CONSTRAINT [FK_PointTransactions_AdminUser]
            FOREIGN KEY ([CreatedByAdminUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'catalog.KeyTransactions') AND [name] = N'IX_KeyTransactions_AdminUser')
        CREATE INDEX [IX_KeyTransactions_AdminUser]
            ON [catalog].[KeyTransactions] ([CreatedByAdminUserId], [CreatedAt] DESC);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'store.PointTransactions') AND [name] = N'IX_PointTransactions_AdminUser')
        CREATE INDEX [IX_PointTransactions_AdminUser]
            ON [store].[PointTransactions] ([CreatedByAdminUserId], [CreatedAt] DESC);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'store.UserCoupons') AND [name] = N'IX_UserCoupons_IssuedByAdmin')
        CREATE INDEX [IX_UserCoupons_IssuedByAdmin]
            ON [store].[UserCoupons] ([IssuedByAdminUserId], [IssuedAt] DESC);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'store.UserCoupons') AND [name] = N'IX_UserCoupons_RevokedByAdmin')
        CREATE INDEX [IX_UserCoupons_RevokedByAdmin]
            ON [store].[UserCoupons] ([RevokedByAdminUserId], [RevokedAt] DESC);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

SELECT
    N'db-v0.8.0' AS [DatabaseVersion],
    N'鑰匙、點數與優惠券管理操作已具備管理員稽核索引' AS [UpgradeResult];
GO
