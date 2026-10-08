SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- 貼文圖片排版：SECONDARY（預設）文字為主；PRIMARY 圖片為主。
    IF COL_LENGTH(N'social.SocialPosts', N'MediaLayout') IS NULL
    BEGIN
        ALTER TABLE [social].[SocialPosts] ADD [MediaLayout] nvarchar(20) NOT NULL
            CONSTRAINT [DF_SocialPosts_MediaLayout] DEFAULT N'SECONDARY';
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SocialPosts_MediaLayout')
    ALTER TABLE [social].[SocialPosts] ADD CONSTRAINT [CK_SocialPosts_MediaLayout]
        CHECK ([MediaLayout] = N'PRIMARY' OR [MediaLayout] = N'SECONDARY');
GO

-- 社群圖片改放進各貼文專屬資料夾：social/posts/{PostId}/{流水號}.ext（相對於媒體根目錄）。
-- 以 / 開頭的是其他來源（例如圖鑑）的路徑，不動。實體檔案請同步搬移（QMAH.Media/media/social/posts/）。
UPDATE [social].[MediaAssets]
SET [StoredPath] = N'social/posts/' + LOWER(CONVERT(nvarchar(36), [PostId])) + N'/' + [StoredPath]
WHERE [PostId] IS NOT NULL AND [StoredPath] NOT LIKE N'/%' AND [StoredPath] NOT LIKE N'social/%';
GO

UPDATE [social].[MediaAssets]
SET [StoredPath] = N'social/pending/' + [StoredPath]
WHERE [PostId] IS NULL AND [StoredPath] NOT LIKE N'/%' AND [StoredPath] NOT LIKE N'social/%' AND [StoredPath] <> N'pending';
GO

SELECT
    N'db-v0.12.2' AS [DatabaseVersion],
    N'SocialPosts 新增 MediaLayout（圖片排版）；社群圖片路徑改為各貼文專屬資料夾' AS [UpgradeResult];
GO
