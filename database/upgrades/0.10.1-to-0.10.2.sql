SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- 洗版防治的 SimHash 設定（比對天數／相似度門檻）改成後台可調整的單一設定列，
    -- 取代原本寫死在 ContentSimilarityService 裡的常數。
    IF OBJECT_ID(N'social.ContentModerationSettings', N'U') IS NULL
    BEGIN
        CREATE TABLE [social].[ContentModerationSettings] (
            [Id] tinyint NOT NULL,
            [SimHashWindowDays] int NOT NULL CONSTRAINT [DF_ContentModerationSettings_WindowDays] DEFAULT ((7)),
            [SimHashHammingThreshold] int NOT NULL CONSTRAINT [DF_ContentModerationSettings_HammingThreshold] DEFAULT ((8)),
            [UpdatedByUserId] uniqueidentifier NULL,
            [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_ContentModerationSettings_Updated] DEFAULT ((sysutcdatetime())),
            [RowVersion] rowversion NOT NULL,
            CONSTRAINT [PK_ContentModerationSettings] PRIMARY KEY ([Id]),
            CONSTRAINT [CK_ContentModerationSettings_Values] CHECK (([SimHashWindowDays] BETWEEN 1 AND 90 AND [SimHashHammingThreshold] BETWEEN 0 AND 64))
        );

        ALTER TABLE [social].[ContentModerationSettings] ADD CONSTRAINT [FK_ContentModerationSettings_UpdatedByUser]
            FOREIGN KEY ([UpdatedByUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

        INSERT INTO [social].[ContentModerationSettings] ([Id], [SimHashWindowDays], [SimHashHammingThreshold])
        VALUES (1, 7, 8);
    END

    -- AI 內容審查（折衷策略：規則沒抓到但看起來可疑的文字、以及全部圖片）由背景排程處理，
    -- 用這三個欄位追蹤進度：null 代表還沒審查，避免同一筆內容被重複送去問 AI。
    IF COL_LENGTH(N'social.SocialPosts', N'AiReviewedAt') IS NULL
        ALTER TABLE [social].[SocialPosts] ADD [AiReviewedAt] datetime2(3) NULL;

    IF COL_LENGTH(N'social.SocialComments', N'AiReviewedAt') IS NULL
        ALTER TABLE [social].[SocialComments] ADD [AiReviewedAt] datetime2(3) NULL;

    IF COL_LENGTH(N'social.MediaAssets', N'AiReviewedAt') IS NULL
        ALTER TABLE [social].[MediaAssets] ADD [AiReviewedAt] datetime2(3) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

SELECT
    N'db-v0.10.2' AS [DatabaseVersion],
    N'新增 ContentModerationSettings（SimHash 比對天數／相似度門檻可調整）；貼文/留言/圖片新增 AiReviewedAt 供 AI 內容審查背景排程追蹤進度' AS [UpgradeResult];
GO
