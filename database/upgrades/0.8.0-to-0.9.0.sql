SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @fixtureNormalId uniqueidentifier = 'c0416075-b472-eeaa-d50f-3d6c38387b71';
    DECLARE @fixtureReferences int = 0;

    IF NOT EXISTS (
        SELECT 1
        FROM [catalog].[KeyDefinitions]
        WHERE [Code] = N'KEY-NORMAL'
          AND [ScopeType] = N'NORMAL')
        THROW 51001, N'找不到 KEY-NORMAL 一般鑰匙，無法完成 0.9.0 升級。', 1;

    SELECT @fixtureReferences =
        (SELECT COUNT(*) FROM [catalog].[KeyTransactions] WHERE [KeyDefinitionId] = @fixtureNormalId)
        + (SELECT COUNT(*) FROM [catalog].[UserKeyBalances] WHERE [KeyDefinitionId] = @fixtureNormalId)
        + (SELECT COUNT(*) FROM [catalog].[KeyExchangeRules] WHERE [SourceKeyDefinitionId] = @fixtureNormalId OR [TargetKeyDefinitionId] = @fixtureNormalId)
        + (SELECT COUNT(*) FROM [admin].[CommunityRewardCampaigns] WHERE [KeyDefinitionId] = @fixtureNormalId)
        + (SELECT COUNT(*) FROM [social].[EventRegistrations] WHERE [RewardKeyDefinitionId] = @fixtureNormalId)
        + (SELECT COUNT(*) FROM [game].[GameRoomInvitations] WHERE [RewardKeyDefinitionId] = @fixtureNormalId);

    IF @fixtureReferences > 0
        THROW 51002, N'FIXTURE-NORMAL 仍有資料參照，請先人工確認並轉移參照後再升級。', 1;

    DELETE FROM [catalog].[KeyDefinitions]
    WHERE [Id] = @fixtureNormalId
      AND [Code] = N'FIXTURE-NORMAL';

    IF NOT EXISTS (
        SELECT 1
        FROM [catalog].[KeyDefinitions]
        WHERE [Code] = N'KEY-ERA-WESTERN_XIA'
          AND [ScopeType] = N'ERA')
        THROW 51003, N'找不到西夏年代鑰匙，無法完成 0.9.0 升級。', 1;

    UPDATE [catalog].[KeyDefinitions]
    SET [IsActive] = 1
    WHERE [Code] = N'KEY-ERA-WESTERN_XIA'
      AND [ScopeType] = N'ERA';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

SELECT
    N'db-v0.9.0' AS [DatabaseVersion],
    N'移除重複的一般鑰匙並啟用西夏年代鑰匙' AS [UpgradeResult];
GO
