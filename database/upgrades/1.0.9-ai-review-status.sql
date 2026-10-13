SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- AI 內容審查排程的執行狀態（單一列）：API 定期回報心跳與目前供應商，後台據此顯示實際運作狀況。
    IF OBJECT_ID(N'social.AiReviewRuntimeStatus', N'U') IS NULL
    BEGIN
        CREATE TABLE [social].[AiReviewRuntimeStatus] (
            [Id] tinyint NOT NULL,
            [Provider] nvarchar(40) NULL,
            [HeartbeatAt] datetime2(3) NOT NULL CONSTRAINT [DF_AiReviewRuntimeStatus_Heartbeat] DEFAULT ((sysutcdatetime())),
            CONSTRAINT [PK_AiReviewRuntimeStatus] PRIMARY KEY ([Id])
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
