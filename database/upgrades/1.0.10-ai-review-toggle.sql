SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- 後台可切換的 AI 內容審查總開關，預設開啟，維持升級前的行為。
    IF COL_LENGTH(N'social.ContentModerationSettings', N'AiReviewEnabled') IS NULL
    BEGIN
        ALTER TABLE [social].[ContentModerationSettings]
            ADD [AiReviewEnabled] bit NOT NULL CONSTRAINT [DF_ContentModerationSettings_AiReviewEnabled] DEFAULT ((1));
    END;

    -- 每次 AI 判斷的審查紀錄（含通過的），後台據此列出審查結果。
    IF OBJECT_ID(N'social.AiReviewLogs', N'U') IS NULL
    BEGIN
        CREATE TABLE [social].[AiReviewLogs] (
            [Id] bigint IDENTITY(1,1) NOT NULL,
            [ReviewedAt] datetime2(3) NOT NULL CONSTRAINT [DF_AiReviewLogs_ReviewedAt] DEFAULT ((sysutcdatetime())),
            [TargetType] nvarchar(10) NOT NULL,
            [TargetId] uniqueidentifier NOT NULL,
            [Provider] nvarchar(40) NOT NULL,
            [Flagged] bit NOT NULL,
            [AutoHidden] bit NOT NULL,
            [Category] nvarchar(60) NULL,
            [Score] float NULL,
            [Summary] nvarchar(400) NULL,
            CONSTRAINT [PK_AiReviewLogs] PRIMARY KEY ([Id])
        );
        CREATE INDEX [IX_AiReviewLogs_ReviewedAt] ON [social].[AiReviewLogs] ([ReviewedAt] DESC);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
