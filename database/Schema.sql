SET ANSI_NULLS ON;
GO

SET QUOTED_IDENTIFIER ON;
GO

BEGIN TRANSACTION;
    IF SCHEMA_ID(N'user') IS NULL EXEC(N'CREATE SCHEMA [user];');

    IF SCHEMA_ID(N'catalog') IS NULL EXEC(N'CREATE SCHEMA [catalog];');

    IF SCHEMA_ID(N'game') IS NULL EXEC(N'CREATE SCHEMA [game];');

    IF SCHEMA_ID(N'store') IS NULL EXEC(N'CREATE SCHEMA [store];');

    IF SCHEMA_ID(N'social') IS NULL EXEC(N'CREATE SCHEMA [social];');

    IF SCHEMA_ID(N'admin') IS NULL EXEC(N'CREATE SCHEMA [admin];');

    IF SCHEMA_ID(N'common') IS NULL EXEC(N'CREATE SCHEMA [common];');

    CREATE TABLE [user].[Achievements] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(80) NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [Title] nvarchar(120) NOT NULL,
        [Description] nvarchar(500) NULL,
        [IconPath] nvarchar(1024) NULL,
        [ConditionType] nvarchar(40) NOT NULL,
        [ThresholdValue] bigint NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_Achievements_Status] DEFAULT N'ACTIVE',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Achievements_CreatedAt] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Achievements_UpdatedAt] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Achievements] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Achievements_Code_NotBlank] CHECK ((len(ltrim(rtrim([Code])))>(0))),
        CONSTRAINT [CK_Achievements_ConditionType_NotBlank] CHECK ((len(ltrim(rtrim([ConditionType])))>(0))),
        CONSTRAINT [CK_Achievements_Name_NotBlank] CHECK ((len(ltrim(rtrim([Name])))>(0))),
        CONSTRAINT [CK_Achievements_Status] CHECK (([Status]=N'INACTIVE' OR [Status]=N'ACTIVE')),
        CONSTRAINT [CK_Achievements_ThresholdValue] CHECK (([ThresholdValue]>(0))),
        CONSTRAINT [CK_Achievements_Title_NotBlank] CHECK ((len(ltrim(rtrim([Title])))>(0))),
        CONSTRAINT [CK_Achievements_UpdatedAt] CHECK (([UpdatedAt]>=[CreatedAt]))
    );

    CREATE TABLE [catalog].[ArtifactCategories] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(32) NOT NULL,
        [Name] nvarchar(80) NOT NULL,
        CONSTRAINT [PK_ArtifactCategories] PRIMARY KEY ([Id])
    );

    CREATE TABLE [game].[ArtifactQuestionEntries] (
        [Id] uniqueidentifier NOT NULL,
        [ArtifactId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL CONSTRAINT [DF_ArtifactQuestionEntries_Enabled] DEFAULT CAST(1 AS bit),
        [Difficulty] tinyint NOT NULL CONSTRAINT [DF_ArtifactQuestionEntries_Difficulty] DEFAULT CAST(1 AS tinyint),
        [QuestionTemplateCode] nvarchar(50) NOT NULL CONSTRAINT [DF_ArtifactQuestionEntries_Template] DEFAULT N'GENERAL',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_ArtifactQuestionEntries_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_ArtifactQuestionEntries_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_ArtifactQuestionEntries] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ArtifactQuestionEntries_Difficulty] CHECK (([Difficulty]>=(1) AND [Difficulty]<=(5)))
    );

    CREATE TABLE [user].[AspNetRoles] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );

    CREATE TABLE [user].[AspNetUsers] (
        [Id] uniqueidentifier NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_AspNetUsers_Status] DEFAULT N'ACTIVE',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_AspNetUsers_CreatedAt] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_AspNetUsers_UpdatedAt] DEFAULT ((sysutcdatetime())),
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AspNetUsers_AccessFailedCount] CHECK (([AccessFailedCount]>=(0))),
        CONSTRAINT [CK_AspNetUsers_Status] CHECK (([Status]=N'DISABLED' OR [Status]=N'BANNED' OR [Status]=N'ACTIVE')),
        CONSTRAINT [CK_AspNetUsers_UpdatedAt] CHECK (([UpdatedAt]>=[CreatedAt]))
    );

    CREATE TABLE [common].[DailyMemberActivities] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ActivityType] nvarchar(20) NOT NULL,
        [ActivityDate] date NOT NULL,
        [OccurrenceCount] int NOT NULL CONSTRAINT [DF_DailyMemberActivities_OccurrenceCount] DEFAULT ((1)),
        [FirstOccurredAt] datetime2(3) NOT NULL,
        [LastOccurredAt] datetime2(3) NOT NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_DailyMemberActivities_CreatedAt] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_DailyMemberActivities_UpdatedAt] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_DailyMemberActivities] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_DailyMemberActivities_Type] CHECK (([ActivityType]=N'CHECK_IN' OR [ActivityType]=N'LOGIN')),
        CONSTRAINT [CK_DailyMemberActivities_OccurrenceCount] CHECK (([OccurrenceCount]>(0))),
        CONSTRAINT [CK_DailyMemberActivities_Times] CHECK (([LastOccurredAt]>=[FirstOccurredAt] AND [UpdatedAt]>=[CreatedAt])),
        CONSTRAINT [FK_DailyMemberActivities_User] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    CREATE UNIQUE INDEX [UX_DailyMemberActivities_User_Type_Date]
        ON [common].[DailyMemberActivities] ([UserId], [ActivityType], [ActivityDate]);
    CREATE INDEX [IX_DailyMemberActivities_Type_Date_User]
        ON [common].[DailyMemberActivities] ([ActivityType], [ActivityDate], [UserId]);

    CREATE TABLE [admin].[AuditLogs] (
        [Id] bigint NOT NULL IDENTITY(1,1),
        [ActorUserId] uniqueidentifier NULL,
        [Area] nvarchar(40) NOT NULL,
        [Controller] nvarchar(100) NOT NULL,
        [Action] nvarchar(100) NOT NULL,
        [HttpMethod] nvarchar(10) NOT NULL,
        [RequestPath] nvarchar(400) NOT NULL,
        [ResultStatusCode] int NOT NULL,
        [Detail] nvarchar(500) NULL,
        [OccurredAt] datetime2(3) NOT NULL CONSTRAINT [DF_AuditLogs_OccurredAt] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AuditLogs_ResultStatusCode] CHECK (([ResultStatusCode]>=(100) AND [ResultStatusCode]<=(599))),
        CONSTRAINT [FK_AuditLogs_ActorUser] FOREIGN KEY ([ActorUserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    CREATE INDEX [IX_AuditLogs_OccurredAt] ON [admin].[AuditLogs] ([OccurredAt] DESC);
    CREATE INDEX [IX_AuditLogs_ActorUserId] ON [admin].[AuditLogs] ([ActorUserId], [OccurredAt] DESC);

    CREATE TABLE [social].[ContentReports] (
        [Id] uniqueidentifier NOT NULL,
        [ReporterUserId] uniqueidentifier NOT NULL,
        [TargetType] nvarchar(20) NOT NULL,
        [TargetId] uniqueidentifier NOT NULL,
        [Reason] nvarchar(100) NOT NULL,
        [Detail] nvarchar(1000) NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_ContentReports_Status] DEFAULT N'PENDING',
        [Resolution] nvarchar(1000) NULL,
        [ReviewedByUserId] uniqueidentifier NULL,
        [ReviewedAt] datetime2(3) NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_ContentReports_Created] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_ContentReports] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ContentReports_Status] CHECK (([Status]=N'REJECTED' OR [Status]=N'RESOLVED' OR [Status]=N'PENDING')),
        CONSTRAINT [CK_ContentReports_Target] CHECK (([TargetType]=N'COMMENT' OR [TargetType]=N'POST'))
    );

    CREATE TABLE [store].[CouponDefinitions] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(50) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [DiscountType] nvarchar(20) NOT NULL,
        [AcquisitionType] nvarchar(30) NOT NULL CONSTRAINT [DF_CouponDefinitions_AcquisitionType] DEFAULT N'ADMIN_GRANT',
        [PointCost] int NULL,
        [ValidityDays] int NOT NULL CONSTRAINT [DF_CouponDefinitions_ValidityDays] DEFAULT ((365)),
        [DiscountValue] decimal(12,2) NOT NULL,
        [MinimumAmount] decimal(12,2) NOT NULL,
        [StartAt] datetime2(3) NOT NULL,
        [EndAt] datetime2(3) NOT NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_Coupons_Active] DEFAULT CAST(1 AS bit),
        CONSTRAINT [PK_CouponDefinitions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Coupons_Dates] CHECK (([EndAt]>[StartAt])),
        CONSTRAINT [CK_CouponDefinitions_Acquisition] CHECK (([AcquisitionType]=N'POINT_EXCHANGE' OR [AcquisitionType]=N'ADMIN_GRANT')),
        CONSTRAINT [CK_CouponDefinitions_PointCost] CHECK ((([AcquisitionType]=N'ADMIN_GRANT' AND [PointCost] IS NULL) OR ([AcquisitionType]=N'POINT_EXCHANGE' AND [PointCost]>(0)))),
        CONSTRAINT [CK_Coupons_Type] CHECK (([DiscountType]=N'PERCENT' OR [DiscountType]=N'FIXED')),
        CONSTRAINT [CK_Coupons_Value] CHECK (([DiscountValue]>(0) AND ([DiscountType]<>N'PERCENT' OR [DiscountValue]<=(100)))),
        CONSTRAINT [CK_CouponDefinitions_ValidityDays] CHECK (([ValidityDays]>(0)))
    );

    CREATE TABLE [catalog].[EraBuckets] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(40) NOT NULL,
        [Name] nvarchar(80) NOT NULL,
        [StartYear] smallint NULL,
        [EndYear] smallint NULL,
        CONSTRAINT [PK_EraBuckets] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_EraBuckets_Years] CHECK (([StartYear] IS NULL OR [EndYear] IS NULL OR [StartYear]<=[EndYear]))
    );

    CREATE TABLE [social].[Events] (
        [Id] uniqueidentifier NOT NULL,
        [EventType] nvarchar(20) NOT NULL,
        [OrganizerUserId] uniqueidentifier NULL,
        [Title] nvarchar(150) NOT NULL,
        [Content] nvarchar(max) NOT NULL,
        [Location] nvarchar(200) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [StartAt] datetime2(3) NOT NULL,
        [EndAt] datetime2(3) NOT NULL,
        [RegistrationEndAt] datetime2(3) NULL,
        [Capacity] int NULL,
        [ReviewStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_Events_Review] DEFAULT N'PENDING',
        [PublishStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_Events_Publish] DEFAULT N'DRAFT',
        [ReviewNote] nvarchar(500) NULL,
        [ReviewedByUserId] uniqueidentifier NULL,
        [ReviewedAt] datetime2(3) NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Events_Created] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_Events] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Events_Capacity] CHECK (([Capacity] IS NULL OR [Capacity]>(0))),
        CONSTRAINT [CK_Events_Coordinates] CHECK ((([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] IS NOT NULL AND [Longitude] IS NOT NULL))),
        CONSTRAINT [CK_Events_Dates] CHECK (([EndAt]>[StartAt] AND ([RegistrationEndAt] IS NULL OR [RegistrationEndAt]<=[StartAt]))),
        CONSTRAINT [CK_Events_Latitude] CHECK (([Latitude] IS NULL OR ([Latitude]>=(-90) AND [Latitude]<=(90)))),
        CONSTRAINT [CK_Events_Longitude] CHECK (([Longitude] IS NULL OR ([Longitude]>=(-180) AND [Longitude]<=(180)))),
        CONSTRAINT [CK_Events_Publish] CHECK (([PublishStatus]=N'CANCELLED' OR [PublishStatus]=N'PUBLISHED' OR [PublishStatus]=N'DRAFT')),
        CONSTRAINT [CK_Events_Review] CHECK (([ReviewStatus]=N'REJECTED' OR [ReviewStatus]=N'APPROVED' OR [ReviewStatus]=N'PENDING')),
        CONSTRAINT [CK_Events_Type] CHECK (([EventType]=N'PLAYER' OR [EventType]=N'OFFICIAL'))
    );

    CREATE TABLE [game].[GameRooms] (
        [Id] uniqueidentifier NOT NULL,
        [RoomCode] nvarchar(12) NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_GameRooms_Status] DEFAULT N'WAITING',
        [Visibility] nvarchar(10) NOT NULL CONSTRAINT [DF_GameRooms_Visibility] DEFAULT N'PUBLIC',
        [PasswordHash] nvarchar(255) NULL,
        [MaxPlayers] tinyint NOT NULL CONSTRAINT [DF_GameRooms_MaxPlayers] DEFAULT CAST(10 AS tinyint),
        [TotalRounds] tinyint NOT NULL CONSTRAINT [DF_GameRooms_TotalRounds] DEFAULT CAST(1 AS tinyint),
        [AnswerSeconds] smallint NOT NULL CONSTRAINT [DF_GameRooms_AnswerSeconds] DEFAULT CAST(120 AS smallint),
        [VotingSeconds] smallint NOT NULL CONSTRAINT [DF_GameRooms_VotingSeconds] DEFAULT CAST(60 AS smallint),
        [CategoryFilterCode] nvarchar(50) NULL,
        [EraBucketFilterCode] nvarchar(50) NULL,
        [CurrentRoundNo] tinyint NOT NULL,
        [StateVersion] int NOT NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_GameRooms_CreatedAt] DEFAULT ((sysutcdatetime())),
        [StartedAt] datetime2(3) NULL,
        [EndedAt] datetime2(3) NULL,
        [CompletedAt] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_GameRooms] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GameRooms_AnswerSeconds] CHECK (([AnswerSeconds]>=(30) AND [AnswerSeconds]<=(300))),
        CONSTRAINT [CK_GameRooms_CategoryFilterCode] CHECK (([CategoryFilterCode] IS NULL OR len(ltrim(rtrim([CategoryFilterCode])))>(0))),
        CONSTRAINT [CK_GameRooms_Completion] CHECK (([Status]=N'WAITING' AND [StartedAt] IS NULL AND [EndedAt] IS NULL AND [CompletedAt] IS NULL OR [Status]=N'PLAYING' AND [StartedAt] IS NOT NULL AND [EndedAt] IS NULL AND [CompletedAt] IS NULL OR [Status]=N'COMPLETED' AND [StartedAt] IS NOT NULL AND [EndedAt] IS NOT NULL AND [CompletedAt] IS NOT NULL OR [Status]=N'CANCELLED' AND [EndedAt] IS NOT NULL AND [CompletedAt] IS NOT NULL)),
        CONSTRAINT [CK_GameRooms_CurrentRoundNo] CHECK (([CurrentRoundNo]<=[TotalRounds])),
        CONSTRAINT [CK_GameRooms_EraBucketFilterCode] CHECK (([EraBucketFilterCode] IS NULL OR len(ltrim(rtrim([EraBucketFilterCode])))>(0))),
        CONSTRAINT [CK_GameRooms_MaxPlayers] CHECK (([MaxPlayers]>=(3) AND [MaxPlayers]<=(10))),
        CONSTRAINT [CK_GameRooms_Password] CHECK (([Visibility]=N'PUBLIC' AND [PasswordHash] IS NULL OR [Visibility]=N'PRIVATE' AND [PasswordHash] IS NOT NULL AND len(ltrim(rtrim([PasswordHash])))>(0))),
        CONSTRAINT [CK_GameRooms_RoomCode_NotBlank] CHECK ((len(ltrim(rtrim([RoomCode])))>=(4) AND len(ltrim(rtrim([RoomCode])))<=(12))),
        CONSTRAINT [CK_GameRooms_StateVersion] CHECK (([StateVersion]>=(0))),
        CONSTRAINT [CK_GameRooms_Status] CHECK (([Status]=N'CANCELLED' OR [Status]=N'COMPLETED' OR [Status]=N'PLAYING' OR [Status]=N'WAITING')),
        CONSTRAINT [CK_GameRooms_TimeOrder] CHECK ((([StartedAt] IS NULL OR [StartedAt]>=[CreatedAt]) AND ([EndedAt] IS NULL OR [EndedAt]>=[CreatedAt]) AND ([CompletedAt] IS NULL OR [CompletedAt]>=[CreatedAt]) AND ([StartedAt] IS NULL OR [EndedAt] IS NULL OR [EndedAt]>=[StartedAt]) AND ([StartedAt] IS NULL OR [CompletedAt] IS NULL OR [CompletedAt]>=[StartedAt]))),
        CONSTRAINT [CK_GameRooms_TotalRounds] CHECK (([TotalRounds]>=(1) AND [TotalRounds]<=(5))),
        CONSTRAINT [CK_GameRooms_Visibility] CHECK (([Visibility]=N'PRIVATE' OR [Visibility]=N'PUBLIC')),
        CONSTRAINT [CK_GameRooms_VotingSeconds] CHECK (([VotingSeconds]>=(20) AND [VotingSeconds]<=(180)))
    );

    CREATE TABLE [social].[OfficialAnnouncements] (
        [Id] uniqueidentifier NOT NULL,
        [Title] nvarchar(150) NOT NULL,
        [Summary] nvarchar(300) NULL,
        [Content] nvarchar(max) NOT NULL,
        [Category] nvarchar(30) NOT NULL CONSTRAINT [DF_Announcements_Category] DEFAULT N'UPDATE',
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_Announcements_Status] DEFAULT N'DRAFT',
        [PublishAt] datetime2(3) NULL,
        [EndAt] datetime2(3) NULL,
        [CreatedByUserId] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Announcements_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Announcements_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_OfficialAnnouncements] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Announcements_Dates] CHECK (([EndAt] IS NULL OR [PublishAt] IS NULL OR [EndAt]>[PublishAt])),
        CONSTRAINT [CK_Announcements_Status] CHECK (([Status]=N'ARCHIVED' OR [Status]=N'PUBLISHED' OR [Status]=N'DRAFT'))
    );

    CREATE TABLE [store].[PointBalances] (
        [UserId] uniqueidentifier NOT NULL,
        [Balance] int NOT NULL,
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_PointBalances_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_PointBalances] PRIMARY KEY ([UserId]),
        CONSTRAINT [CK_PointBalances_NonNegative] CHECK (([Balance]>=(0)))
    );

    CREATE TABLE [store].[PointTransactions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Amount] int NOT NULL,
        [Reason] nvarchar(40) NOT NULL,
        [ReferenceType] nvarchar(40) NULL,
        [ReferenceId] uniqueidentifier NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_PointTransactions_Created] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_PointTransactions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_PointTransactions_Amount] CHECK (([Amount]<>(0)))
    );

    CREATE TABLE [store].[Products] (
        [Id] uniqueidentifier NOT NULL,
        [ArtifactId] uniqueidentifier NULL,
        [CategoryCode] nvarchar(40) NOT NULL,
        [ExternalRef] nvarchar(100) NULL,
        [Name] nvarchar(200) NOT NULL,
        [Description] nvarchar(max) NULL,
        [SizeText] nvarchar(500) NULL,
        [Price] decimal(12,2) NOT NULL,
        [Stock] int NOT NULL,
        [PrimaryImagePath] nvarchar(500) NULL,
        [SourceUrl] nvarchar(1000) NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_Products_Active] DEFAULT CAST(1 AS bit),
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Products_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Products_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_Products] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Products_Price] CHECK (([Price]>=(0))),
        CONSTRAINT [CK_Products_Stock] CHECK (([Stock]>=(0)))
    );

    CREATE TABLE [store].[ProductReviews] (
        [Id] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Rating] tinyint NOT NULL,
        [Content] nvarchar(1000) NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_ProductReviews_Status] DEFAULT N'PUBLISHED',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_ProductReviews_CreatedAt] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_ProductReviews_UpdatedAt] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_ProductReviews] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ProductReviews_Rating] CHECK (([Rating]>=(1) AND [Rating]<=(5))),
        CONSTRAINT [CK_ProductReviews_Content_NotBlank] CHECK ((len(ltrim(rtrim([Content])))>(0))),
        CONSTRAINT [CK_ProductReviews_Status] CHECK (([Status]=N'DELETED' OR [Status]=N'HIDDEN' OR [Status]=N'PUBLISHED')),
        CONSTRAINT [CK_ProductReviews_UpdatedAt] CHECK (([UpdatedAt]>=[CreatedAt])),
        CONSTRAINT [FK_ProductReviews_Product] FOREIGN KEY ([ProductId]) REFERENCES [store].[Products] ([Id]),
        CONSTRAINT [FK_ProductReviews_User] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    CREATE TABLE [social].[SocialPosts] (
        [Id] uniqueidentifier NOT NULL,
        [BoardCode] nvarchar(30) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ArtifactId] uniqueidentifier NULL,
        [EventId] uniqueidentifier NULL,
        [PostType] nvarchar(20) NOT NULL CONSTRAINT [DF_SocialPosts_PostType] DEFAULT N'POST',
        [PublisherType] nvarchar(20) NOT NULL CONSTRAINT [DF_SocialPosts_PublisherType] DEFAULT N'COMMUNITY',
        [ContentMode] nvarchar(20) NOT NULL CONSTRAINT [DF_SocialPosts_ContentMode] DEFAULT N'CUSTOM',
        [Title] nvarchar(150) NOT NULL,
        [Content] nvarchar(max) NOT NULL,
        [LocationName] nvarchar(200) NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_SocialPosts_Status] DEFAULT N'PUBLISHED',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_SocialPosts_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_SocialPosts_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_SocialPosts] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_SocialPosts_BoardCode] CHECK ((len(ltrim(rtrim([BoardCode])))>(0))),
        CONSTRAINT [CK_SocialPosts_Coordinates] CHECK ((([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] IS NOT NULL AND [Longitude] IS NOT NULL))),
        CONSTRAINT [CK_SocialPosts_Latitude] CHECK (([Latitude] IS NULL OR ([Latitude]>=(-90) AND [Latitude]<=(90)))),
        CONSTRAINT [CK_SocialPosts_Longitude] CHECK (([Longitude] IS NULL OR ([Longitude]>=(-180) AND [Longitude]<=(180)))),
        CONSTRAINT [CK_SocialPosts_PostType] CHECK (([PostType]=N'POST' OR [PostType]=N'ANNOUNCEMENT' OR [PostType]=N'EVENT')),
        CONSTRAINT [CK_SocialPosts_PublisherType] CHECK (([PublisherType]=N'COMMUNITY' OR [PublisherType]=N'OFFICIAL')),
        CONSTRAINT [CK_SocialPosts_ContentMode] CHECK (([ContentMode]=N'TEMPLATE' OR [ContentMode]=N'CUSTOM')),
        CONSTRAINT [CK_SocialPosts_Status] CHECK (([Status]=N'DELETED' OR [Status]=N'HIDDEN' OR [Status]=N'PUBLISHED')),
        CONSTRAINT [FK_SocialPosts_Event] FOREIGN KEY ([EventId]) REFERENCES [social].[Events] ([Id])
    );

    CREATE SEQUENCE [social].[MediaAssetSequence]
        AS bigint
        START WITH 1
        INCREMENT BY 1;

    CREATE TABLE [social].[MediaAssets] (
        [Id] uniqueidentifier NOT NULL,
        [SequenceNo] bigint NOT NULL CONSTRAINT [DF_MediaAssets_SequenceNo] DEFAULT (NEXT VALUE FOR [social].[MediaAssetSequence]),
        [OwnerUserId] uniqueidentifier NOT NULL,
        [PostId] uniqueidentifier NULL,
        [OriginalFileName] nvarchar(260) NOT NULL,
        [StoredPath] nvarchar(500) NOT NULL,
        [ContentType] nvarchar(100) NOT NULL,
        [FileSize] bigint NOT NULL,
        [AltText] nvarchar(200) NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_MediaAssets_Status] DEFAULT N'ACTIVE',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_MediaAssets_CreatedAt] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_MediaAssets_UpdatedAt] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_MediaAssets] PRIMARY KEY ([Id]),
        CONSTRAINT [UQ_MediaAssets_SequenceNo] UNIQUE ([SequenceNo]),
        CONSTRAINT [CK_MediaAssets_FileSize] CHECK (([FileSize]>(0) AND [FileSize]<=(8388608))),
        CONSTRAINT [CK_MediaAssets_Status] CHECK (([Status]=N'ACTIVE' OR [Status]=N'HIDDEN' OR [Status]=N'DELETED')),
        CONSTRAINT [FK_MediaAssets_OwnerUser] FOREIGN KEY ([OwnerUserId]) REFERENCES [user].[AspNetUsers] ([Id]),
        CONSTRAINT [FK_MediaAssets_Post] FOREIGN KEY ([PostId]) REFERENCES [social].[SocialPosts] ([Id])
    );

    CREATE INDEX [IX_MediaAssets_Post_Status] ON [social].[MediaAssets] ([PostId], [Status], [CreatedAt]);
    CREATE INDEX [IX_MediaAssets_Owner_Status] ON [social].[MediaAssets] ([OwnerUserId], [Status], [CreatedAt] DESC);

    CREATE TABLE [social].[UserNotifications] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Title] nvarchar(150) NOT NULL,
        [Content] nvarchar(500) NOT NULL,
        [TargetUrl] nvarchar(500) NULL,
        [IsRead] bit NOT NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserNotifications_Created] DEFAULT ((sysutcdatetime())),
        [ReadAt] datetime2(3) NULL,
        CONSTRAINT [PK_UserNotifications] PRIMARY KEY ([Id])
    );

    CREATE TABLE [user].[AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [user].[AspNetRoles] ([Id]) ON DELETE CASCADE
    );

    CREATE TABLE [user].[AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]) ON DELETE CASCADE
    );

    CREATE TABLE [user].[AspNetUserLogins] (
        [LoginProvider] nvarchar(128) NOT NULL,
        [ProviderKey] nvarchar(128) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]) ON DELETE CASCADE
    );

    CREATE TABLE [user].[AspNetUserRoles] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [user].[AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]) ON DELETE CASCADE
    );

    CREATE TABLE [user].[AspNetUserTokens] (
        [UserId] uniqueidentifier NOT NULL,
        [LoginProvider] nvarchar(128) NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]) ON DELETE CASCADE
    );

    CREATE TABLE [user].[UserAchievements] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [AchievementId] uniqueidentifier NOT NULL,
        [AchievedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserAchievements_AchievedAt] DEFAULT ((sysutcdatetime())),
        [IsDisplayed] bit NOT NULL,
        [DisplayedAt] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_UserAchievements] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_UserAchievements_DisplayState] CHECK (([IsDisplayed]=(0) AND [DisplayedAt] IS NULL OR [IsDisplayed]=(1) AND [DisplayedAt] IS NOT NULL AND [DisplayedAt]>=[AchievedAt])),
        CONSTRAINT [FK_UserAchievements_Achievements_AchievementId] FOREIGN KEY ([AchievementId]) REFERENCES [user].[Achievements] ([Id]),
        CONSTRAINT [FK_UserAchievements_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    CREATE TABLE [user].[UserAddresses] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [AddressLabel] nvarchar(50) NOT NULL,
        [RecipientName] nvarchar(80) NOT NULL,
        [RecipientPhone] nvarchar(30) NOT NULL,
        [PostalCode] nvarchar(20) NULL,
        [City] nvarchar(80) NULL,
        [District] nvarchar(80) NULL,
        [AddressLine] nvarchar(300) NOT NULL,
        [Latitude] decimal(9,6) NULL,
        [Longitude] decimal(9,6) NULL,
        [IsDefault] bit NOT NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserAddresses_CreatedAt] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserAddresses_UpdatedAt] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_UserAddresses] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_UserAddresses_Address_NotBlank] CHECK ((len(ltrim(rtrim([AddressLine])))>(0))),
        CONSTRAINT [CK_UserAddresses_Coordinates] CHECK ((([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] IS NOT NULL AND [Longitude] IS NOT NULL))),
        CONSTRAINT [CK_UserAddresses_Latitude] CHECK (([Latitude] IS NULL OR ([Latitude]>=(-90) AND [Latitude]<=(90)))),
        CONSTRAINT [CK_UserAddresses_Longitude] CHECK (([Longitude] IS NULL OR ([Longitude]>=(-180) AND [Longitude]<=(180)))),
        CONSTRAINT [CK_UserAddresses_Label_NotBlank] CHECK ((len(ltrim(rtrim([AddressLabel])))>(0))),
        CONSTRAINT [CK_UserAddresses_Name_NotBlank] CHECK ((len(ltrim(rtrim([RecipientName])))>(0))),
        CONSTRAINT [CK_UserAddresses_Phone_NotBlank] CHECK ((len(ltrim(rtrim([RecipientPhone])))>(0))),
        CONSTRAINT [CK_UserAddresses_UpdatedAt] CHECK (([UpdatedAt]>=[CreatedAt])),
        CONSTRAINT [FK_UserAddresses_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    CREATE TABLE [user].[UserProfiles] (
        [UserId] uniqueidentifier NOT NULL,
        [Nickname] nvarchar(80) NOT NULL,
        [AvatarPath] nvarchar(1024) NULL,
        [Bio] nvarchar(1000) NULL,
        [Visibility] nvarchar(20) NOT NULL CONSTRAINT [DF_UserProfiles_Visibility] DEFAULT N'PUBLIC',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserProfiles_CreatedAt] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserProfiles_UpdatedAt] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_UserProfiles] PRIMARY KEY ([UserId]),
        CONSTRAINT [CK_UserProfiles_Nickname_NotBlank] CHECK ((len(ltrim(rtrim([Nickname])))>(0))),
        CONSTRAINT [CK_UserProfiles_UpdatedAt] CHECK (([UpdatedAt]>=[CreatedAt])),
        CONSTRAINT [CK_UserProfiles_Visibility] CHECK (([Visibility]=N'PRIVATE' OR [Visibility]=N'FRIENDS' OR [Visibility]=N'PUBLIC')),
        CONSTRAINT [FK_UserProfiles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    CREATE TABLE [store].[UserCoupons] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [CouponDefinitionId] uniqueidentifier NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_UserCoupons_Status] DEFAULT N'AVAILABLE',
        [IssuedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserCoupons_Issued] DEFAULT ((sysutcdatetime())),
        [ExpiresAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserCoupons_ExpiresAt] DEFAULT (dateadd(day,(365),sysutcdatetime())),
        [UsedAt] datetime2(3) NULL,
        [IssuedByAdminUserId] uniqueidentifier NULL,
        [IssueReason] nvarchar(200) NULL,
        [RevokedAt] datetime2(3) NULL,
        [RevokedByAdminUserId] uniqueidentifier NULL,
        [RevokeReason] nvarchar(200) NULL,
        [GrantBatchId] uniqueidentifier NULL,
        [RevokeBatchId] uniqueidentifier NULL,
        CONSTRAINT [PK_UserCoupons] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_UserCoupons_Status] CHECK (([Status]=N'REVOKED' OR [Status]=N'EXPIRED' OR [Status]=N'USED' OR [Status]=N'AVAILABLE')),
        CONSTRAINT [FK_UserCoupons_Definition] FOREIGN KEY ([CouponDefinitionId]) REFERENCES [store].[CouponDefinitions] ([Id])
    );

    CREATE TABLE [catalog].[Artifacts] (
        [Id] uniqueidentifier NOT NULL,
        [ArtifactRef] nvarchar(80) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [CategoryId] uniqueidentifier NOT NULL,
        [EraBucketId] uniqueidentifier NOT NULL,
        [EraTextOriginal] nvarchar(200) NULL,
        [CreatorDisplay] nvarchar(300) NULL,
        [Description] nvarchar(max) NULL,
        [SizeText] nvarchar(500) NULL,
        [PrimaryImagePath] nvarchar(500) NOT NULL,
        [ThumbnailPath] nvarchar(500) NULL,
        [SourceUrl] nvarchar(1000) NOT NULL,
        [LicenseCode] nvarchar(80) NULL,
        [AttributionText] nvarchar(500) NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_Artifacts_Active] DEFAULT CAST(1 AS bit),
        CONSTRAINT [PK_Artifacts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Artifacts_Category] FOREIGN KEY ([CategoryId]) REFERENCES [catalog].[ArtifactCategories] ([Id]),
        CONSTRAINT [FK_Artifacts_Era] FOREIGN KEY ([EraBucketId]) REFERENCES [catalog].[EraBuckets] ([Id])
    );

    CREATE TABLE [catalog].[KeyDefinitions] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(50) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [ScopeType] nvarchar(20) NOT NULL,
        [CategoryId] uniqueidentifier NULL,
        [EraBucketId] uniqueidentifier NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_KeyDefinitions_Active] DEFAULT CAST(1 AS bit),
        [RecyclePointValue] int NOT NULL CONSTRAINT [DF_KeyDefinitions_RecyclePointValue] DEFAULT ((0)),
        CONSTRAINT [PK_KeyDefinitions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_KeyDefinitions_Scope] CHECK (([ScopeType]=N'NORMAL' AND [CategoryId] IS NULL AND [EraBucketId] IS NULL OR [ScopeType]=N'CATEGORY' AND [CategoryId] IS NOT NULL AND [EraBucketId] IS NULL OR [ScopeType]=N'ERA' AND [CategoryId] IS NULL AND [EraBucketId] IS NOT NULL OR [ScopeType]=N'UNIVERSAL' AND [CategoryId] IS NULL AND [EraBucketId] IS NULL)),
        CONSTRAINT [CK_KeyDefinitions_RecyclePointValue] CHECK (([RecyclePointValue]>=(0))),
        CONSTRAINT [FK_KeyDefinitions_Category] FOREIGN KEY ([CategoryId]) REFERENCES [catalog].[ArtifactCategories] ([Id]),
        CONSTRAINT [FK_KeyDefinitions_Era] FOREIGN KEY ([EraBucketId]) REFERENCES [catalog].[EraBuckets] ([Id])
    );

    CREATE TABLE [social].[EventRegistrations] (
        [Id] uniqueidentifier NOT NULL,
        [EventId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_EventRegistrations_Status] DEFAULT N'REGISTERED',
        [RegisteredAt] datetime2(3) NOT NULL CONSTRAINT [DF_EventRegistrations_At] DEFAULT ((sysutcdatetime())),
        [RewardPointAmount] int NOT NULL CONSTRAINT [DF_EventRegistrations_RewardPointAmount] DEFAULT ((0)),
        [RewardCampaignId] uniqueidentifier NULL,
        [RewardKeyDefinitionId] uniqueidentifier NULL,
        [RewardKeyAmount] int NOT NULL CONSTRAINT [DF_EventRegistrations_RewardKeyAmount] DEFAULT ((0)),
        [RewardGrantedAt] datetime2(3) NULL,
        CONSTRAINT [PK_EventRegistrations] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_EventRegistrations_Status] CHECK (([Status]=N'ATTENDED' OR [Status]=N'CANCELLED' OR [Status]=N'REGISTERED')),
        CONSTRAINT [CK_EventRegistrations_RewardAmounts] CHECK (([RewardPointAmount]>=(0) AND [RewardKeyAmount]>=(0) AND (([RewardKeyAmount]=(0) AND [RewardKeyDefinitionId] IS NULL) OR ([RewardKeyAmount]>(0) AND [RewardKeyDefinitionId] IS NOT NULL)))),
        CONSTRAINT [FK_EventRegistrations_Event] FOREIGN KEY ([EventId]) REFERENCES [social].[Events] ([Id])
    );

    CREATE TABLE [game].[GamePlayers] (
        [Id] uniqueidentifier NOT NULL,
        [RoomId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [PlayerKey] nvarchar(80) NOT NULL,
        [DisplayName] nvarchar(80) NOT NULL,
        [Role] nvarchar(20) NOT NULL CONSTRAINT [DF_GamePlayers_Role] DEFAULT N'PLAYER',
        [IsReady] bit NOT NULL,
        [SeatNo] tinyint NULL,
        [JoinedAt] datetime2(3) NOT NULL CONSTRAINT [DF_GamePlayers_JoinedAt] DEFAULT ((sysutcdatetime())),
        [ConnectionStatus] nvarchar(12) NOT NULL CONSTRAINT [DF_GamePlayers_ConnectionStatus] DEFAULT N'ONLINE',
        [LastSeenAt] datetime2(3) NOT NULL CONSTRAINT [DF_GamePlayers_LastSeenAt] DEFAULT ((sysutcdatetime())),
        [DisconnectedAt] datetime2(3) NULL,
        [ReconnectDeadlineAt] datetime2(3) NULL,
        [LeftAt] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_GamePlayers] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GamePlayers_ConnectionStatus] CHECK (([ConnectionStatus]=N'LEFT' OR [ConnectionStatus]=N'OFFLINE' OR [ConnectionStatus]=N'ONLINE')),
        CONSTRAINT [CK_GamePlayers_DisplayName_NotBlank] CHECK ((len(ltrim(rtrim([DisplayName])))>(0))),
        CONSTRAINT [CK_GamePlayers_PlayerKey_NotBlank] CHECK ((len(ltrim(rtrim([PlayerKey])))>(0))),
        CONSTRAINT [CK_GamePlayers_PresenceTimes] CHECK (([LastSeenAt]>=[JoinedAt] AND ([DisconnectedAt] IS NULL OR [DisconnectedAt]>=[LastSeenAt]) AND ([ReconnectDeadlineAt] IS NULL OR [DisconnectedAt] IS NOT NULL AND [ReconnectDeadlineAt]>[DisconnectedAt]) AND ([LeftAt] IS NULL OR [LeftAt]>=[JoinedAt]) AND ([LeftAt] IS NULL OR [DisconnectedAt] IS NULL OR [LeftAt]>=[DisconnectedAt]) AND ([ConnectionStatus]=N'ONLINE' AND [LeftAt] IS NULL AND [DisconnectedAt] IS NULL AND [ReconnectDeadlineAt] IS NULL OR [ConnectionStatus]=N'OFFLINE' AND [LeftAt] IS NULL AND [DisconnectedAt] IS NOT NULL AND [ReconnectDeadlineAt] IS NOT NULL OR [ConnectionStatus]=N'LEFT' AND [LeftAt] IS NOT NULL))),
        CONSTRAINT [CK_GamePlayers_Role] CHECK (([Role]=N'PLAYER' OR [Role]=N'HOST')),
        CONSTRAINT [CK_GamePlayers_SeatNo] CHECK (([SeatNo] IS NULL OR [SeatNo]>=(1) AND [SeatNo]<=(10))),
        CONSTRAINT [FK_GamePlayers_GameRooms_RoomId] FOREIGN KEY ([RoomId]) REFERENCES [game].[GameRooms] ([Id])
    );

    CREATE TABLE [game].[GameRounds] (
        [Id] uniqueidentifier NOT NULL,
        [RoomId] uniqueidentifier NOT NULL,
        [ArtifactId] uniqueidentifier NOT NULL,
        [RoundNumber] int NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_GameRounds_Status] DEFAULT N'ANSWERING',
        [StateVersion] int NOT NULL,
        [IsSettled] bit NOT NULL,
        [StartedAt] datetime2(3) NOT NULL CONSTRAINT [DF_GameRounds_StartedAt] DEFAULT ((sysutcdatetime())),
        [AnswerDeadlineAt] datetime2(3) NOT NULL,
        [VotingDeadlineAt] datetime2(3) NOT NULL,
        [SettledAt] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_GameRounds] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GameRounds_Deadlines] CHECK (([AnswerDeadlineAt]>[StartedAt] AND [VotingDeadlineAt]>[AnswerDeadlineAt])),
        CONSTRAINT [CK_GameRounds_RoundNumber] CHECK (([RoundNumber]>=(1))),
        CONSTRAINT [CK_GameRounds_Settlement] CHECK ((([Status]=N'VOTING' OR [Status]=N'ANSWERING') AND [IsSettled]=(0) AND [SettledAt] IS NULL OR [Status]=N'REVEALED' AND [IsSettled]=(1) AND [SettledAt] IS NOT NULL AND [SettledAt]>=[StartedAt])),
        CONSTRAINT [CK_GameRounds_StateVersion] CHECK (([StateVersion]>=(0))),
        CONSTRAINT [CK_GameRounds_Status] CHECK (([Status]=N'REVEALED' OR [Status]=N'VOTING' OR [Status]=N'ANSWERING')),
        CONSTRAINT [FK_GameRounds_GameRooms_RoomId] FOREIGN KEY ([RoomId]) REFERENCES [game].[GameRooms] ([Id])
    );

    CREATE TABLE [store].[CartItems] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [Quantity] int NOT NULL,
        [AddedAt] datetime2(3) NOT NULL CONSTRAINT [DF_CartItems_Added] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_CartItems] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_CartItems_Quantity] CHECK (([Quantity]>=(1) AND [Quantity]<=(99))),
        CONSTRAINT [FK_CartItems_Product] FOREIGN KEY ([ProductId]) REFERENCES [store].[Products] ([Id])
    );

    CREATE TABLE [social].[SocialComments] (
        [Id] uniqueidentifier NOT NULL,
        [PostId] uniqueidentifier NOT NULL,
        [ParentCommentId] uniqueidentifier NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Content] nvarchar(2000) NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_SocialComments_Status] DEFAULT N'PUBLISHED',
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_SocialComments_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_SocialComments_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_SocialComments] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_SocialComments_Status] CHECK (([Status]=N'DELETED' OR [Status]=N'HIDDEN' OR [Status]=N'PUBLISHED')),
        CONSTRAINT [FK_SocialComments_Parent] FOREIGN KEY ([ParentCommentId]) REFERENCES [social].[SocialComments] ([Id]),
        CONSTRAINT [FK_SocialComments_Post] FOREIGN KEY ([PostId]) REFERENCES [social].[SocialPosts] ([Id])
    );

    CREATE TABLE [store].[StoreOrders] (
        [Id] uniqueidentifier NOT NULL,
        [OrderNo] nvarchar(30) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [UserCouponId] uniqueidentifier NULL,
        [Status] nvarchar(30) NOT NULL CONSTRAINT [DF_StoreOrders_Status] DEFAULT N'PENDING_PAYMENT',
        [Subtotal] decimal(12,2) NOT NULL,
        [DiscountAmount] decimal(12,2) NOT NULL,
        [PointsUsed] int NOT NULL,
        [TotalAmount] decimal(12,2) NOT NULL,
        [RecipientName] nvarchar(80) NOT NULL,
        [RecipientPhone] nvarchar(30) NOT NULL,
        [ShippingPostalCode] nvarchar(10) NOT NULL,
        [ShippingCity] nvarchar(30) NOT NULL,
        [ShippingDistrict] nvarchar(30) NOT NULL,
        [ShippingAddressLine] nvarchar(200) NOT NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_StoreOrders_Created] DEFAULT ((sysutcdatetime())),
        [PaidAt] datetime2(3) NULL,
        [CancelledAt] datetime2(3) NULL,
        CONSTRAINT [PK_StoreOrders] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_StoreOrders_Amounts] CHECK (([Subtotal]>=(0) AND [DiscountAmount]>=(0) AND [PointsUsed]>=(0) AND [TotalAmount]>=(0) AND [TotalAmount]=(([Subtotal]-[DiscountAmount])-[PointsUsed]))),
        CONSTRAINT [CK_StoreOrders_Status] CHECK (([Status]=N'COMPLETED' OR [Status]=N'SHIPPED' OR [Status]=N'FULFILLING' OR [Status]=N'CANCELLED' OR [Status]=N'PAID' OR [Status]=N'PENDING_PAYMENT')),
        CONSTRAINT [FK_StoreOrders_Coupon] FOREIGN KEY ([UserCouponId]) REFERENCES [store].[UserCoupons] ([Id])
    );

    CREATE TABLE [catalog].[KeyTransactions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [KeyDefinitionId] uniqueidentifier NOT NULL,
        [Amount] int NOT NULL,
        [Reason] nvarchar(40) NOT NULL,
        [ReferenceType] nvarchar(40) NULL,
        [ReferenceId] uniqueidentifier NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_KeyTransactions_Created] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_KeyTransactions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_KeyTransactions_Amount] CHECK (([Amount]<>(0))),
        CONSTRAINT [FK_KeyTransactions_Key] FOREIGN KEY ([KeyDefinitionId]) REFERENCES [catalog].[KeyDefinitions] ([Id])
    );

    CREATE TABLE [catalog].[UserKeyBalances] (
        [UserId] uniqueidentifier NOT NULL,
        [KeyDefinitionId] uniqueidentifier NOT NULL,
        [Balance] int NOT NULL,
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_UserKeyBalances_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_UserKeyBalances] PRIMARY KEY ([UserId], [KeyDefinitionId]),
        CONSTRAINT [CK_UserKeyBalances_NonNegative] CHECK (([Balance]>=(0))),
        CONSTRAINT [FK_UserKeyBalances_Key] FOREIGN KEY ([KeyDefinitionId]) REFERENCES [catalog].[KeyDefinitions] ([Id])
    );

    CREATE TABLE [game].[RoundAnswers] (
        [Id] uniqueidentifier NOT NULL,
        [RoundId] uniqueidentifier NOT NULL,
        [GamePlayerId] uniqueidentifier NOT NULL,
        [AnswerType] nvarchar(30) NOT NULL,
        [Text] nvarchar(500) NOT NULL,
        [SubmittedAt] datetime2(3) NOT NULL CONSTRAINT [DF_RoundAnswers_SubmittedAt] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_RoundAnswers] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_RoundAnswers_AnswerType] CHECK (([AnswerType]=N'CREATIVE_TALE' OR [AnswerType]=N'PLAUSIBLE_FICTION' OR [AnswerType]=N'FACTUAL_REASONING')),
        CONSTRAINT [CK_RoundAnswers_Text_NotBlank] CHECK ((len(ltrim(rtrim([Text])))>(0))),
        CONSTRAINT [FK_RoundAnswers_GamePlayers_GamePlayerId] FOREIGN KEY ([GamePlayerId]) REFERENCES [game].[GamePlayers] ([Id]),
        CONSTRAINT [FK_RoundAnswers_GameRounds_RoundId] FOREIGN KEY ([RoundId]) REFERENCES [game].[GameRounds] ([Id])
    );

    CREATE TABLE [store].[OrderDetails] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [ProductNameSnapshot] nvarchar(200) NOT NULL,
        [UnitPrice] decimal(12,2) NOT NULL,
        [Quantity] int NOT NULL,
        [LineTotal] decimal(12,2) NOT NULL,
        CONSTRAINT [PK_OrderDetails] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_OrderDetails_Values] CHECK (([UnitPrice]>=(0) AND [Quantity]>(0) AND [LineTotal]=[UnitPrice]*[Quantity])),
        CONSTRAINT [FK_OrderDetails_Order] FOREIGN KEY ([OrderId]) REFERENCES [store].[StoreOrders] ([Id]),
        CONSTRAINT [FK_OrderDetails_Product] FOREIGN KEY ([ProductId]) REFERENCES [store].[Products] ([Id])
    );

    CREATE TABLE [store].[Payments] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [MerchantTradeNo] nvarchar(30) NOT NULL,
        [EcpayTradeNo] nvarchar(30) NULL,
        [Amount] decimal(12,2) NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_Payments_Status] DEFAULT N'PENDING',
        [RtnCode] int NULL,
        [RtnMsg] nvarchar(200) NULL,
        [PaymentType] nvarchar(50) NULL,
        [CallbackReceivedAt] datetime2(3) NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Payments_Created] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_Payments] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Payments_Amount] CHECK (([Amount]>=(0))),
        CONSTRAINT [CK_Payments_Status] CHECK (([Status]=N'CANCELLED' OR [Status]=N'FAILED' OR [Status]=N'PAID' OR [Status]=N'PENDING')),
        CONSTRAINT [FK_Payments_Order] FOREIGN KEY ([OrderId]) REFERENCES [store].[StoreOrders] ([Id])
    );

    CREATE TABLE [catalog].[ArtifactUnlocks] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ArtifactId] uniqueidentifier NOT NULL,
        [UnlockMethod] nvarchar(20) NOT NULL,
        [GameRoundId] uniqueidentifier NULL,
        [KeyTransactionId] uniqueidentifier NULL,
        [UnlockedAt] datetime2(3) NOT NULL CONSTRAINT [DF_ArtifactUnlocks_At] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_ArtifactUnlocks] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ArtifactUnlocks_Method] CHECK (([UnlockMethod]=N'ADMIN' OR [UnlockMethod]=N'KEY' OR [UnlockMethod]=N'GAME')),
        CONSTRAINT [FK_ArtifactUnlocks_Artifact] FOREIGN KEY ([ArtifactId]) REFERENCES [catalog].[Artifacts] ([Id]),
        CONSTRAINT [FK_ArtifactUnlocks_KeyTx] FOREIGN KEY ([KeyTransactionId]) REFERENCES [catalog].[KeyTransactions] ([Id])
    );

    CREATE TABLE [game].[Votes] (
        [Id] uniqueidentifier NOT NULL,
        [RoundId] uniqueidentifier NOT NULL,
        [VoterGamePlayerId] uniqueidentifier NOT NULL,
        [AnswerId] uniqueidentifier NOT NULL,
        [Count] int NOT NULL,
        [SubmittedAt] datetime2(3) NOT NULL CONSTRAINT [DF_Votes_SubmittedAt] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_Votes] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Votes_Count] CHECK (([Count]>=(1) AND [Count]<=(3))),
        CONSTRAINT [FK_Votes_GamePlayers_VoterGamePlayerId] FOREIGN KEY ([VoterGamePlayerId]) REFERENCES [game].[GamePlayers] ([Id]),
        CONSTRAINT [FK_Votes_GameRounds_RoundId] FOREIGN KEY ([RoundId]) REFERENCES [game].[GameRounds] ([Id]),
        CONSTRAINT [FK_Votes_RoundAnswers_AnswerId] FOREIGN KEY ([AnswerId]) REFERENCES [game].[RoundAnswers] ([Id])
    );

    /* 跨 Area 的活動調整只保存批次主檔；實際點數與優惠券明細仍寫入原有流水表。 */
    CREATE TABLE [admin].[EconomyAdjustmentBatches] (
        [Id] uniqueidentifier NOT NULL,
        [AssetType] nvarchar(20) NOT NULL,
        [Operation] nvarchar(20) NOT NULL,
        [UnitAmount] int NOT NULL,
        [CouponDefinitionId] uniqueidentifier NULL,
        [FilterJson] nvarchar(max) NOT NULL,
        [Reason] nvarchar(200) NOT NULL,
        [CreatedByAdminUserId] uniqueidentifier NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [TargetCount] int NOT NULL,
        [SucceededCount] int NOT NULL,
        [FailedCount] int NOT NULL,
        [AffectedAssetCount] bigint NOT NULL,
        [FailureReason] nvarchar(500) NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_EconomyAdjustmentBatches_Created] DEFAULT ((sysutcdatetime())),
        [CompletedAt] datetime2(3) NULL,
        CONSTRAINT [PK_EconomyAdjustmentBatches] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_EconomyAdjustmentBatches_AssetType] CHECK (([AssetType]=N'COUPON' OR [AssetType]=N'POINT')),
        CONSTRAINT [CK_EconomyAdjustmentBatches_Operation] CHECK (([Operation]=N'ADD' OR [Operation]=N'DEDUCT')),
        CONSTRAINT [CK_EconomyAdjustmentBatches_Status] CHECK (([Status]=N'EMPTY' OR [Status]=N'FAILED' OR [Status]=N'COMPLETED' OR [Status]=N'PROCESSING')),
        CONSTRAINT [CK_EconomyAdjustmentBatches_Amounts] CHECK (([UnitAmount]>(0) AND [TargetCount]>=(0) AND [SucceededCount]>=(0) AND [FailedCount]>=(0) AND [AffectedAssetCount]>=(0))),
        CONSTRAINT [FK_EconomyAdjustmentBatches_CouponDefinition] FOREIGN KEY ([CouponDefinitionId]) REFERENCES [store].[CouponDefinitions] ([Id]),
        CONSTRAINT [FK_EconomyAdjustmentBatches_AdminUser] FOREIGN KEY ([CreatedByAdminUserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    /* 官方活動與會員私人房間共用的加碼規則。 */
    CREATE TABLE [admin].[CommunityRewardCampaigns] (
        [Id] uniqueidentifier NOT NULL,
        [TargetType] nvarchar(20) NOT NULL,
        [EventId] uniqueidentifier NULL,
        [GameRoomId] uniqueidentifier NULL,
        [OwnerUserId] uniqueidentifier NOT NULL,
        [SponsorType] nvarchar(20) NOT NULL,
        [BudgetMode] nvarchar(20) NOT NULL,
        [PointPerRecipient] int NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_PointPerRecipient] DEFAULT ((0)),
        [KeyDefinitionId] uniqueidentifier NULL,
        [KeyPerRecipient] int NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_KeyPerRecipient] DEFAULT ((0)),
        [PointBudget] int NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_PointBudget] DEFAULT ((0)),
        [PointIssued] int NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_PointIssued] DEFAULT ((0)),
        [KeyBudget] int NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_KeyBudget] DEFAULT ((0)),
        [KeyIssued] int NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_KeyIssued] DEFAULT ((0)),
        [ValidFrom] datetime2(3) NOT NULL,
        [ValidUntil] datetime2(3) NOT NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_Active] DEFAULT CAST(1 AS bit),
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_CommunityRewardCampaigns_Updated] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_CommunityRewardCampaigns] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_CommunityRewardCampaigns_Target] CHECK ((([TargetType]=N'EVENT' AND [EventId] IS NOT NULL AND [GameRoomId] IS NULL) OR ([TargetType]=N'GAME_ROOM' AND [EventId] IS NULL AND [GameRoomId] IS NOT NULL))),
        CONSTRAINT [CK_CommunityRewardCampaigns_Sponsor] CHECK ((([SponsorType]=N'MEMBER' AND [BudgetMode]=N'LIMITED') OR ([SponsorType]=N'OFFICIAL' AND [BudgetMode]=N'UNLIMITED'))),
        CONSTRAINT [CK_CommunityRewardCampaigns_Amounts] CHECK (([PointPerRecipient]>=(0) AND [KeyPerRecipient]>=(0) AND [PointBudget]>=(0) AND [PointIssued]>=(0) AND [KeyBudget]>=(0) AND [KeyIssued]>=(0) AND ([BudgetMode]=N'UNLIMITED' OR ([PointIssued]<=[PointBudget] AND [KeyIssued]<=[KeyBudget])) AND (([KeyPerRecipient]=(0) AND [KeyDefinitionId] IS NULL) OR ([KeyPerRecipient]>(0) AND [KeyDefinitionId] IS NOT NULL)))),
        CONSTRAINT [CK_CommunityRewardCampaigns_Time] CHECK (([ValidUntil]>[ValidFrom] AND [UpdatedAt]>=[CreatedAt])),
        CONSTRAINT [FK_CommunityRewardCampaigns_Event] FOREIGN KEY ([EventId]) REFERENCES [social].[Events] ([Id]),
        CONSTRAINT [FK_CommunityRewardCampaigns_GameRoom] FOREIGN KEY ([GameRoomId]) REFERENCES [game].[GameRooms] ([Id]),
        CONSTRAINT [FK_CommunityRewardCampaigns_OwnerUser] FOREIGN KEY ([OwnerUserId]) REFERENCES [user].[AspNetUsers] ([Id]),
        CONSTRAINT [FK_CommunityRewardCampaigns_KeyDefinition] FOREIGN KEY ([KeyDefinitionId]) REFERENCES [catalog].[KeyDefinitions] ([Id])
    );

    CREATE UNIQUE INDEX [UX_CommunityRewardCampaigns_Event]
        ON [admin].[CommunityRewardCampaigns] ([EventId]) WHERE ([EventId] IS NOT NULL);
    CREATE UNIQUE INDEX [UX_CommunityRewardCampaigns_GameRoom]
        ON [admin].[CommunityRewardCampaigns] ([GameRoomId]) WHERE ([GameRoomId] IS NOT NULL);
    CREATE INDEX [IX_CommunityRewardCampaigns_ActiveWindow]
        ON [admin].[CommunityRewardCampaigns] ([IsActive], [ValidFrom], [ValidUntil]);

    CREATE TABLE [game].[GameRoomInvitations] (
        [Id] uniqueidentifier NOT NULL,
        [RoomId] uniqueidentifier NOT NULL,
        [InviterUserId] uniqueidentifier NOT NULL,
        [InviteeUserId] uniqueidentifier NOT NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_GameRoomInvitations_Status] DEFAULT N'PENDING',
        [Message] nvarchar(300) NULL,
        [RewardPointAmount] int NOT NULL CONSTRAINT [DF_GameRoomInvitations_RewardPointAmount] DEFAULT ((0)),
        [RewardCampaignId] uniqueidentifier NULL,
        [RewardKeyDefinitionId] uniqueidentifier NULL,
        [RewardKeyAmount] int NOT NULL CONSTRAINT [DF_GameRoomInvitations_RewardKeyAmount] DEFAULT ((0)),
        [RewardGrantedAt] datetime2(3) NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_GameRoomInvitations_Created] DEFAULT ((sysutcdatetime())),
        [RespondedAt] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_GameRoomInvitations] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GameRoomInvitations_Status] CHECK (([Status]=N'CANCELLED' OR [Status]=N'DECLINED' OR [Status]=N'EXPIRED' OR [Status]=N'ACCEPTED' OR [Status]=N'PENDING')),
        CONSTRAINT [CK_GameRoomInvitations_NotSelf] CHECK (([InviterUserId]<>[InviteeUserId])),
        CONSTRAINT [CK_GameRoomInvitations_RewardAmounts] CHECK (([RewardPointAmount]>=(0) AND [RewardKeyAmount]>=(0) AND (([RewardKeyAmount]=(0) AND [RewardKeyDefinitionId] IS NULL) OR ([RewardKeyAmount]>(0) AND [RewardKeyDefinitionId] IS NOT NULL)))),
        CONSTRAINT [FK_GameRoomInvitations_Room] FOREIGN KEY ([RoomId]) REFERENCES [game].[GameRooms] ([Id]),
        CONSTRAINT [FK_GameRoomInvitations_InviterUser] FOREIGN KEY ([InviterUserId]) REFERENCES [user].[AspNetUsers] ([Id]),
        CONSTRAINT [FK_GameRoomInvitations_InviteeUser] FOREIGN KEY ([InviteeUserId]) REFERENCES [user].[AspNetUsers] ([Id]),
        CONSTRAINT [FK_GameRoomInvitations_RewardCampaign] FOREIGN KEY ([RewardCampaignId]) REFERENCES [admin].[CommunityRewardCampaigns] ([Id]),
        CONSTRAINT [FK_GameRoomInvitations_RewardKeyDefinition] FOREIGN KEY ([RewardKeyDefinitionId]) REFERENCES [catalog].[KeyDefinitions] ([Id])
    );

    CREATE INDEX [IX_GameRoomInvitations_Invitee_Status_CreatedAt]
        ON [game].[GameRoomInvitations] ([InviteeUserId], [Status], [CreatedAt] DESC);
    CREATE INDEX [IX_GameRoomInvitations_Room_CreatedAt]
        ON [game].[GameRoomInvitations] ([RoomId], [CreatedAt] DESC);
    CREATE UNIQUE INDEX [UX_GameRoomInvitations_Pending]
        ON [game].[GameRoomInvitations] ([RoomId], [InviteeUserId]) WHERE ([Status]=N'PENDING');

    CREATE TABLE [game].[GameEconomySettings] (
        [Id] tinyint NOT NULL,
        [MinimumPointReward] int NOT NULL,
        [MaximumPointReward] int NOT NULL,
        [BasePointReward] int NOT NULL,
        [MaximumVoteBonus] int NOT NULL,
        [MaximumWinBonus] int NOT NULL,
        [CompletedNormalKey] int NOT NULL,
        [ExcellentExtraNormalKey] int NOT NULL,
        [ExcellentThreshold] int NOT NULL,
        [DailyMiniGameRewardLimit] int NOT NULL,
        [KeyProgressToNormalKey] int NOT NULL,
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_GameEconomySettings_Updated] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_GameEconomySettings] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GameEconomySettings_Values] CHECK (([MinimumPointReward]>=(0) AND [MaximumPointReward]>=[MinimumPointReward] AND [BasePointReward]>=(0) AND [MaximumVoteBonus]>=(0) AND [MaximumWinBonus]>=(0) AND [CompletedNormalKey]>=(0) AND [ExcellentExtraNormalKey]>=(0) AND [ExcellentThreshold] BETWEEN 0 AND 100 AND [DailyMiniGameRewardLimit]>=(0) AND [KeyProgressToNormalKey]>(0)))
    );

    CREATE TABLE [game].[GameModeDefinitions] (
        [Id] uniqueidentifier NOT NULL,
        [Code] nvarchar(40) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [ConfigJson] nvarchar(max) NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_GameModeDefinitions_Active] DEFAULT CAST(1 AS bit),
        [GradeBThreshold] int NOT NULL,
        [GradeAThreshold] int NOT NULL,
        [GradeSThreshold] int NOT NULL,
        [FailPointReward] int NOT NULL,
        [FailKeyProgressReward] int NOT NULL,
        [BPointReward] int NOT NULL,
        [BKeyProgressReward] int NOT NULL,
        [APointReward] int NOT NULL,
        [AKeyProgressReward] int NOT NULL,
        [SPointReward] int NOT NULL,
        [SKeyProgressReward] int NOT NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_GameModeDefinitions_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_GameModeDefinitions_Updated] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_GameModeDefinitions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GameModeDefinitions_Thresholds] CHECK (([GradeBThreshold] BETWEEN 0 AND 100 AND [GradeAThreshold] BETWEEN [GradeBThreshold] AND 100 AND [GradeSThreshold] BETWEEN [GradeAThreshold] AND 100)),
        CONSTRAINT [CK_GameModeDefinitions_Rewards] CHECK (([FailPointReward]>=(0) AND [FailKeyProgressReward]>=(0) AND [BPointReward]>=(0) AND [BKeyProgressReward]>=(0) AND [APointReward]>=(0) AND [AKeyProgressReward]>=(0) AND [SPointReward]>=(0) AND [SKeyProgressReward]>=(0)))
    );
    CREATE UNIQUE INDEX [UX_GameModeDefinitions_Code] ON [game].[GameModeDefinitions] ([Code]);
    CREATE INDEX [IX_GameModeDefinitions_Active_Code] ON [game].[GameModeDefinitions] ([IsActive], [Code]);

    CREATE TABLE [game].[MiniGameAttempts] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [GameModeDefinitionId] uniqueidentifier NOT NULL,
        [ArtifactId] uniqueidentifier NULL,
        [ArtifactPoolJson] nvarchar(max) NULL,
        [Difficulty] nvarchar(30) NOT NULL,
        [Seed] nvarchar(128) NOT NULL,
        [ConfigJson] nvarchar(max) NULL,
        [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_MiniGameAttempts_Status] DEFAULT N'STARTED',
        [RawScore] int NULL,
        [RawResultJson] nvarchar(max) NULL,
        [NormalizedScore] int NULL,
        [Grade] nvarchar(2) NULL,
        [PointReward] int NOT NULL,
        [KeyProgressReward] int NOT NULL,
        [RewardAttemptNo] int NULL,
        [RewardGranted] bit NOT NULL,
        [StartedAt] datetime2(3) NOT NULL CONSTRAINT [DF_MiniGameAttempts_Started] DEFAULT ((sysutcdatetime())),
        [CompletedAt] datetime2(3) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_MiniGameAttempts] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_MiniGameAttempts_Status] CHECK (([Status]=N'EXPIRED' OR [Status]=N'COMPLETED' OR [Status]=N'STARTED')),
        CONSTRAINT [CK_MiniGameAttempts_Score] CHECK (([RawScore] IS NULL OR [RawScore] BETWEEN 0 AND 100) AND ([NormalizedScore] IS NULL OR [NormalizedScore] BETWEEN 0 AND 100)),
        CONSTRAINT [CK_MiniGameAttempts_Reward] CHECK (([PointReward]>=(0) AND [KeyProgressReward]>=(0))),
        CONSTRAINT [FK_MiniGameAttempts_Mode] FOREIGN KEY ([GameModeDefinitionId]) REFERENCES [game].[GameModeDefinitions] ([Id]),
        CONSTRAINT [FK_MiniGameAttempts_Artifact] FOREIGN KEY ([ArtifactId]) REFERENCES [catalog].[Artifacts] ([Id]),
        CONSTRAINT [FK_MiniGameAttempts_User] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );
    CREATE INDEX [IX_MiniGameAttempts_User_StartedAt] ON [game].[MiniGameAttempts] ([UserId], [StartedAt] DESC);
    CREATE INDEX [IX_MiniGameAttempts_User_Mode_Status] ON [game].[MiniGameAttempts] ([UserId], [GameModeDefinitionId], [Status]);

    CREATE TABLE [catalog].[KeyExchangeRules] (
        [Id] uniqueidentifier NOT NULL,
        [SourceKeyDefinitionId] uniqueidentifier NOT NULL,
        [SourceAmount] int NOT NULL,
        [TargetKeyDefinitionId] uniqueidentifier NOT NULL,
        [TargetAmount] int NOT NULL,
        [SortOrder] int NOT NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_KeyExchangeRules_Active] DEFAULT CAST(1 AS bit),
        [Description] nvarchar(300) NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_KeyExchangeRules_Created] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_KeyExchangeRules_Updated] DEFAULT ((sysutcdatetime())),
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_KeyExchangeRules] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_KeyExchangeRules_Amounts] CHECK (([SourceAmount]>(0) AND [TargetAmount]>(0))),
        CONSTRAINT [FK_KeyExchangeRules_SourceKey] FOREIGN KEY ([SourceKeyDefinitionId]) REFERENCES [catalog].[KeyDefinitions] ([Id]),
        CONSTRAINT [FK_KeyExchangeRules_TargetKey] FOREIGN KEY ([TargetKeyDefinitionId]) REFERENCES [catalog].[KeyDefinitions] ([Id])
    );
    CREATE UNIQUE INDEX [UX_KeyExchangeRules_Source_Target]
        ON [catalog].[KeyExchangeRules] ([SourceKeyDefinitionId], [TargetKeyDefinitionId]);
    CREATE INDEX [IX_KeyExchangeRules_Active_SortOrder]
        ON [catalog].[KeyExchangeRules] ([IsActive], [SortOrder]);

    CREATE TABLE [catalog].[KeyProgressBalances] (
        [UserId] uniqueidentifier NOT NULL,
        [Balance] int NOT NULL,
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_KeyProgressBalances_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_KeyProgressBalances] PRIMARY KEY ([UserId]),
        CONSTRAINT [CK_KeyProgressBalances_NonNegative] CHECK (([Balance]>=(0))),
        CONSTRAINT [FK_KeyProgressBalances_User] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );

    CREATE TABLE [catalog].[KeyProgressTransactions] (
        [Id] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Amount] int NOT NULL,
        [Reason] nvarchar(40) NOT NULL,
        [ReferenceType] nvarchar(40) NULL,
        [ReferenceId] uniqueidentifier NULL,
        [CreatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_KeyProgressTransactions_Created] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_KeyProgressTransactions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_KeyProgressTransactions_Amount] CHECK (([Amount]<>(0))),
        CONSTRAINT [FK_KeyProgressTransactions_User] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id])
    );
    CREATE INDEX [IX_KeyProgressTransactions_User]
        ON [catalog].[KeyProgressTransactions] ([UserId], [CreatedAt] DESC);

    CREATE TABLE [user].[EquippedTitles] (
        [UserId] uniqueidentifier NOT NULL,
        [UserAchievementId] uniqueidentifier NOT NULL,
        [EquippedAt] datetime2(3) NOT NULL CONSTRAINT [DF_EquippedTitles_Equipped] DEFAULT ((sysutcdatetime())),
        [UpdatedAt] datetime2(3) NOT NULL CONSTRAINT [DF_EquippedTitles_Updated] DEFAULT ((sysutcdatetime())),
        CONSTRAINT [PK_EquippedTitles] PRIMARY KEY ([UserId]),
        CONSTRAINT [FK_EquippedTitles_User] FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]),
        CONSTRAINT [FK_EquippedTitles_UserAchievement] FOREIGN KEY ([UserAchievementId]) REFERENCES [user].[UserAchievements] ([Id])
    );
    CREATE UNIQUE INDEX [UX_EquippedTitles_UserAchievement]
        ON [user].[EquippedTitles] ([UserAchievementId]);

    ALTER TABLE [store].[Products] ADD CONSTRAINT [FK_Products_Artifact]
        FOREIGN KEY ([ArtifactId]) REFERENCES [catalog].[Artifacts] ([Id]);

    ALTER TABLE [game].[ArtifactQuestionEntries] ADD CONSTRAINT [FK_ArtifactQuestionEntries_Artifact]
        FOREIGN KEY ([ArtifactId]) REFERENCES [catalog].[Artifacts] ([Id]);

    ALTER TABLE [catalog].[ArtifactUnlocks] ADD CONSTRAINT [FK_ArtifactUnlocks_GameRound]
        FOREIGN KEY ([GameRoundId]) REFERENCES [game].[GameRounds] ([Id]);

    ALTER TABLE [catalog].[ArtifactUnlocks] ADD CONSTRAINT [FK_ArtifactUnlocks_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [catalog].[KeyTransactions] ADD CONSTRAINT [FK_KeyTransactions_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [catalog].[UserKeyBalances] ADD CONSTRAINT [FK_UserKeyBalances_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [game].[GamePlayers] ADD CONSTRAINT [FK_GamePlayers_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [game].[GameRounds] ADD CONSTRAINT [FK_GameRounds_Artifact]
        FOREIGN KEY ([ArtifactId]) REFERENCES [catalog].[Artifacts] ([Id]);

    ALTER TABLE [social].[ContentReports] ADD CONSTRAINT [FK_ContentReports_ReporterUser]
        FOREIGN KEY ([ReporterUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[ContentReports] ADD CONSTRAINT [FK_ContentReports_ReviewedByUser]
        FOREIGN KEY ([ReviewedByUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[EventRegistrations] ADD CONSTRAINT [FK_EventRegistrations_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[Events] ADD CONSTRAINT [FK_Events_OrganizerUser]
        FOREIGN KEY ([OrganizerUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[Events] ADD CONSTRAINT [FK_Events_ReviewedByUser]
        FOREIGN KEY ([ReviewedByUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[OfficialAnnouncements] ADD CONSTRAINT [FK_OfficialAnnouncements_CreatedByUser]
        FOREIGN KEY ([CreatedByUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[SocialComments] ADD CONSTRAINT [FK_SocialComments_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[SocialPosts] ADD CONSTRAINT [FK_SocialPosts_Artifact]
        FOREIGN KEY ([ArtifactId]) REFERENCES [catalog].[Artifacts] ([Id]);

    ALTER TABLE [social].[SocialPosts] ADD CONSTRAINT [FK_SocialPosts_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [social].[UserNotifications] ADD CONSTRAINT [FK_UserNotifications_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[CartItems] ADD CONSTRAINT [FK_CartItems_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[PointBalances] ADD CONSTRAINT [FK_PointBalances_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[PointTransactions] ADD CONSTRAINT [FK_PointTransactions_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[StoreOrders] ADD CONSTRAINT [FK_StoreOrders_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[UserCoupons] ADD CONSTRAINT [FK_UserCoupons_User]
        FOREIGN KEY ([UserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[UserCoupons] ADD CONSTRAINT [FK_UserCoupons_IssuedByAdminUser]
        FOREIGN KEY ([IssuedByAdminUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[UserCoupons] ADD CONSTRAINT [FK_UserCoupons_RevokedByAdminUser]
        FOREIGN KEY ([RevokedByAdminUserId]) REFERENCES [user].[AspNetUsers] ([Id]);

    ALTER TABLE [store].[UserCoupons] ADD CONSTRAINT [FK_UserCoupons_GrantBatch]
        FOREIGN KEY ([GrantBatchId]) REFERENCES [admin].[EconomyAdjustmentBatches] ([Id]);

    ALTER TABLE [store].[UserCoupons] ADD CONSTRAINT [FK_UserCoupons_RevokeBatch]
        FOREIGN KEY ([RevokeBatchId]) REFERENCES [admin].[EconomyAdjustmentBatches] ([Id]);

    ALTER TABLE [social].[EventRegistrations] ADD CONSTRAINT [FK_EventRegistrations_RewardCampaign]
        FOREIGN KEY ([RewardCampaignId]) REFERENCES [admin].[CommunityRewardCampaigns] ([Id]);

    ALTER TABLE [social].[EventRegistrations] ADD CONSTRAINT [FK_EventRegistrations_RewardKeyDefinition]
        FOREIGN KEY ([RewardKeyDefinitionId]) REFERENCES [catalog].[KeyDefinitions] ([Id]);

    CREATE INDEX [IX_Achievements_Condition_Threshold] ON [user].[Achievements] ([Status], [ConditionType], [ThresholdValue]);

    CREATE INDEX [IX_Achievements_Status] ON [user].[Achievements] ([Status], [Code]);

    CREATE UNIQUE INDEX [UX_Achievements_Code] ON [user].[Achievements] ([Code]);

    CREATE UNIQUE INDEX [UQ_ArtifactCategories_Code] ON [catalog].[ArtifactCategories] ([Code]);

    CREATE UNIQUE INDEX [UQ_ArtifactQuestionEntries_Artifact] ON [game].[ArtifactQuestionEntries] ([ArtifactId]);

    CREATE INDEX [IX_Artifacts_EraBucketId] ON [catalog].[Artifacts] ([EraBucketId]);

    CREATE INDEX [IX_Artifacts_Filter] ON [catalog].[Artifacts] ([CategoryId], [EraBucketId]);

    CREATE UNIQUE INDEX [UQ_Artifacts_Ref] ON [catalog].[Artifacts] ([ArtifactRef]);

    CREATE INDEX [IX_ArtifactUnlocks_ArtifactId] ON [catalog].[ArtifactUnlocks] ([ArtifactId]);

    CREATE INDEX [IX_ArtifactUnlocks_KeyTransactionId] ON [catalog].[ArtifactUnlocks] ([KeyTransactionId]);

    CREATE INDEX [IX_ArtifactUnlocks_GameRoundId] ON [catalog].[ArtifactUnlocks] ([GameRoundId]);

    CREATE UNIQUE INDEX [UQ_ArtifactUnlocks_UserArtifact] ON [catalog].[ArtifactUnlocks] ([UserId], [ArtifactId]);

    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [user].[AspNetRoleClaims] ([RoleId]);

    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [user].[AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');

    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [user].[AspNetUserClaims] ([UserId]);

    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [user].[AspNetUserLogins] ([UserId]);

    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [user].[AspNetUserRoles] ([RoleId]);

    CREATE INDEX [EmailIndex] ON [user].[AspNetUsers] ([NormalizedEmail]);

    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [user].[AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');

    CREATE INDEX [IX_CartItems_ProductId] ON [store].[CartItems] ([ProductId]);

    CREATE UNIQUE INDEX [UQ_CartItems_MemberProduct] ON [store].[CartItems] ([UserId], [ProductId]);

    CREATE INDEX [IX_ContentReports_Status] ON [social].[ContentReports] ([Status], [CreatedAt]);

    CREATE INDEX [IX_ContentReports_ReporterUserId] ON [social].[ContentReports] ([ReporterUserId]);

    CREATE INDEX [IX_ContentReports_ReviewedByUserId] ON [social].[ContentReports] ([ReviewedByUserId]);

    CREATE UNIQUE INDEX [UQ_CouponDefinitions_Code] ON [store].[CouponDefinitions] ([Code]);

    CREATE UNIQUE INDEX [UQ_EraBuckets_Code] ON [catalog].[EraBuckets] ([Code]);

    CREATE UNIQUE INDEX [UQ_EventRegistrations_EventMember] ON [social].[EventRegistrations] ([EventId], [UserId]);

    CREATE INDEX [IX_EventRegistrations_UserId] ON [social].[EventRegistrations] ([UserId]);

    CREATE INDEX [IX_Events_OrganizerUserId] ON [social].[Events] ([OrganizerUserId]);

    CREATE INDEX [IX_Events_ReviewedByUserId] ON [social].[Events] ([ReviewedByUserId]);

    CREATE INDEX [IX_GamePlayers_Room_ConnectionStatus] ON [game].[GamePlayers] ([RoomId], [ConnectionStatus], [LeftAt]);

    EXEC(N'CREATE INDEX [IX_GamePlayers_UserId_RoomId] ON [game].[GamePlayers] ([UserId], [RoomId]) WHERE ([UserId] IS NOT NULL)');

    EXEC(N'CREATE UNIQUE INDEX [UX_GamePlayers_OneHostPerRoom] ON [game].[GamePlayers] ([RoomId]) WHERE ([Role]=N''HOST'')');

    CREATE UNIQUE INDEX [UX_GamePlayers_Room_PlayerKey] ON [game].[GamePlayers] ([RoomId], [PlayerKey]);

    EXEC(N'CREATE UNIQUE INDEX [UX_GamePlayers_Room_SeatNo] ON [game].[GamePlayers] ([RoomId], [SeatNo]) WHERE ([SeatNo] IS NOT NULL)');

    EXEC(N'CREATE UNIQUE INDEX [UX_GamePlayers_Room_UserId] ON [game].[GamePlayers] ([RoomId], [UserId]) WHERE ([UserId] IS NOT NULL)');

    CREATE INDEX [IX_GameRooms_PublicLobby] ON [game].[GameRooms] ([Status], [Visibility], [CreatedAt] DESC);

    CREATE INDEX [IX_GameRooms_Status_CreatedAt] ON [game].[GameRooms] ([Status], [CreatedAt] DESC);

    CREATE UNIQUE INDEX [UX_GameRooms_RoomCode] ON [game].[GameRooms] ([RoomCode]);

    CREATE INDEX [IX_GameRounds_ArtifactId] ON [game].[GameRounds] ([ArtifactId]);

    CREATE UNIQUE INDEX [UX_GameRounds_Room_RoundNumber] ON [game].[GameRounds] ([RoomId], [RoundNumber]);

    CREATE INDEX [IX_KeyDefinitions_CategoryId] ON [catalog].[KeyDefinitions] ([CategoryId]);

    CREATE INDEX [IX_KeyDefinitions_EraBucketId] ON [catalog].[KeyDefinitions] ([EraBucketId]);

    CREATE UNIQUE INDEX [UQ_KeyDefinitions_Code] ON [catalog].[KeyDefinitions] ([Code]);

    CREATE INDEX [IX_KeyTransactions_KeyDefinitionId] ON [catalog].[KeyTransactions] ([KeyDefinitionId]);

    CREATE INDEX [IX_KeyTransactions_User] ON [catalog].[KeyTransactions] ([UserId], [CreatedAt] DESC);

    CREATE INDEX [IX_OfficialAnnouncements_CreatedByUserId] ON [social].[OfficialAnnouncements] ([CreatedByUserId]);

    CREATE UNIQUE INDEX [UQ_SocialPosts_EventId] ON [social].[SocialPosts] ([EventId]) WHERE [EventId] IS NOT NULL;

    CREATE INDEX [IX_OrderDetails_ProductId] ON [store].[OrderDetails] ([ProductId]);

    CREATE UNIQUE INDEX [UQ_OrderDetails_OrderProduct] ON [store].[OrderDetails] ([OrderId], [ProductId]);

    CREATE UNIQUE INDEX [UQ_Payments_MerchantTradeNo] ON [store].[Payments] ([MerchantTradeNo]);

    CREATE UNIQUE INDEX [UQ_Payments_Order] ON [store].[Payments] ([OrderId]);

    CREATE INDEX [IX_PointTransactions_Member] ON [store].[PointTransactions] ([UserId], [CreatedAt] DESC);

    CREATE INDEX [IX_ProductReviews_Product_Status_Created] ON [store].[ProductReviews] ([ProductId], [Status], [CreatedAt] DESC);

    CREATE UNIQUE INDEX [UX_ProductReviews_Product_User] ON [store].[ProductReviews] ([ProductId], [UserId]);

    EXEC(N'CREATE UNIQUE INDEX [UX_Products_ExternalRef] ON [store].[Products] ([ExternalRef]) WHERE ([ExternalRef] IS NOT NULL)');

    EXEC(N'CREATE UNIQUE INDEX [UX_Products_ArtifactId] ON [store].[Products] ([ArtifactId]) WHERE ([ArtifactId] IS NOT NULL)');

    CREATE INDEX [IX_RoundAnswers_GamePlayerId] ON [game].[RoundAnswers] ([GamePlayerId]);

    CREATE UNIQUE INDEX [UX_RoundAnswers_Round_GamePlayer] ON [game].[RoundAnswers] ([RoundId], [GamePlayerId]);

    CREATE INDEX [IX_SocialComments_ParentCommentId] ON [social].[SocialComments] ([ParentCommentId]);

    CREATE INDEX [IX_SocialComments_PostId] ON [social].[SocialComments] ([PostId]);

    CREATE INDEX [IX_SocialComments_UserId] ON [social].[SocialComments] ([UserId]);

    CREATE INDEX [IX_SocialPosts_BoardCreated] ON [social].[SocialPosts] ([BoardCode], [Status], [CreatedAt] DESC);

    CREATE INDEX [IX_SocialPosts_ArtifactId] ON [social].[SocialPosts] ([ArtifactId]);

    CREATE INDEX [IX_SocialPosts_UserId] ON [social].[SocialPosts] ([UserId]);

    CREATE INDEX [IX_StoreOrders_UserCouponId] ON [store].[StoreOrders] ([UserCouponId]);

    CREATE INDEX [IX_StoreOrders_UserId] ON [store].[StoreOrders] ([UserId]);

    CREATE UNIQUE INDEX [UQ_StoreOrders_OrderNo] ON [store].[StoreOrders] ([OrderNo]);

    CREATE INDEX [IX_UserAchievements_Achievement_Member] ON [user].[UserAchievements] ([AchievementId], [UserId]);

    CREATE INDEX [IX_UserAchievements_Member_AchievedAt] ON [user].[UserAchievements] ([UserId], [AchievedAt] DESC);

    CREATE UNIQUE INDEX [UX_UserAchievements_Member_Achievement] ON [user].[UserAchievements] ([UserId], [AchievementId]);

    EXEC(N'CREATE UNIQUE INDEX [UX_UserAddresses_Member_Default] ON [user].[UserAddresses] ([UserId]) WHERE ([IsDefault]=(1))');

    CREATE UNIQUE INDEX [UX_UserAddresses_Member_Label] ON [user].[UserAddresses] ([UserId], [AddressLabel]);

    CREATE INDEX [IX_UserCoupons_CouponDefinitionId] ON [store].[UserCoupons] ([CouponDefinitionId]);

    CREATE INDEX [IX_UserCoupons_User_Status_ExpiresAt]
        ON [store].[UserCoupons] ([UserId], [Status], [ExpiresAt]);

    CREATE INDEX [IX_UserCoupons_Definition_IssuedAt]
        ON [store].[UserCoupons] ([CouponDefinitionId], [IssuedAt] DESC);

    CREATE INDEX [IX_UserKeyBalances_KeyDefinitionId] ON [catalog].[UserKeyBalances] ([KeyDefinitionId]);

    CREATE INDEX [IX_UserNotifications_Member] ON [social].[UserNotifications] ([UserId], [IsRead], [CreatedAt] DESC);

    CREATE INDEX [IX_Votes_AnswerId] ON [game].[Votes] ([AnswerId]);

    CREATE INDEX [IX_Votes_Round_Voter] ON [game].[Votes] ([RoundId], [VoterGamePlayerId]);

    CREATE INDEX [IX_Votes_VoterGamePlayerId] ON [game].[Votes] ([VoterGamePlayerId]);

    CREATE UNIQUE INDEX [UX_Votes_Round_Voter_Answer] ON [game].[Votes] ([RoundId], [VoterGamePlayerId], [AnswerId]);

COMMIT;
GO
