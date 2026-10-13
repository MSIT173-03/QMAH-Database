SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- 貼文收藏：每位會員對每篇貼文最多一筆。貼文或會員刪除時一併清掉。
    IF OBJECT_ID(N'social.PostBookmarks', N'U') IS NULL
    BEGIN
        CREATE TABLE [social].[PostBookmarks] (
            [UserId] uniqueidentifier NOT NULL,
            [PostId] uniqueidentifier NOT NULL,
            [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_PostBookmarks_Created] DEFAULT ((sysutcdatetime())),
            CONSTRAINT [PK_PostBookmarks] PRIMARY KEY ([UserId], [PostId])
        );

        ALTER TABLE [social].[PostBookmarks] ADD CONSTRAINT [FK_PostBookmarks_Post]
            FOREIGN KEY ([PostId]) REFERENCES [social].[SocialPosts] ([Id]) ON DELETE CASCADE;

        ALTER TABLE [social].[PostBookmarks] ADD CONSTRAINT [FK_PostBookmarks_User]
            FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]) ON DELETE CASCADE;

        CREATE INDEX [IX_PostBookmarks_Post] ON [social].[PostBookmarks] ([PostId]);
    END;

    -- 會員之間的私訊：一則訊息一列，對話由兩端會員組合出來，不另建對話表。
    -- 寄件與收件兩個 FK 都不做連鎖刪除（兩條連鎖路徑會被 SQL Server 拒絕）。
    IF OBJECT_ID(N'social.DirectMessages', N'U') IS NULL
    BEGIN
        CREATE TABLE [social].[DirectMessages] (
            [Id] uniqueidentifier NOT NULL,
            [SenderUserId] uniqueidentifier NOT NULL,
            [RecipientUserId] uniqueidentifier NOT NULL,
            [Content] nvarchar(1000) NOT NULL,
            [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_DirectMessages_Created] DEFAULT ((sysutcdatetime())),
            [ReadAt] datetime2(3) NULL,
            CONSTRAINT [PK_DirectMessages] PRIMARY KEY ([Id]),
            CONSTRAINT [CK_DirectMessages_NotSelf] CHECK (([SenderUserId]<>[RecipientUserId]))
        );

        ALTER TABLE [social].[DirectMessages] ADD CONSTRAINT [FK_DirectMessages_Sender]
            FOREIGN KEY ([SenderUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

        ALTER TABLE [social].[DirectMessages] ADD CONSTRAINT [FK_DirectMessages_Recipient]
            FOREIGN KEY ([RecipientUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

        CREATE INDEX [IX_DirectMessages_Pair] ON [social].[DirectMessages] ([SenderUserId], [RecipientUserId], [CreatedAt] DESC);
        CREATE INDEX [IX_DirectMessages_Inbox] ON [social].[DirectMessages] ([RecipientUserId], [ReadAt], [CreatedAt] DESC);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
