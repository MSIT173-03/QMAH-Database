SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
BEGIN TRANSACTION;
IF COL_LENGTH(N'game.GameRooms',N'IsShowcase') IS NULL THROW 51000,N'請先升級至 db-v0.12.0。',1;
DECLARE @identityBefore TABLE(Id uniqueidentifier PRIMARY KEY,Signature varbinary(32));
INSERT @identityBefore SELECT Id,HASHBYTES('SHA2_256',CONCAT(Email,N'|',NormalizedEmail,N'|',UserName,N'|',NormalizedUserName,N'|',PasswordHash,N'|',SecurityStamp,N'|',ConcurrencyStamp)) FROM [user].AspNetUsers;
DECLARE @rolesBefore TABLE(UserId uniqueidentifier,RoleId uniqueidentifier,PRIMARY KEY(UserId,RoleId));
INSERT @rolesBefore SELECT UserId,RoleId FROM [user].AspNetUserRoles;
DECLARE @names TABLE(Ordinal int PRIMARY KEY,Nickname nvarchar(80));
INSERT @names VALUES(1,N'鑑定屋館長'),(2,N'青瓷小盞'),(3,N'案頭銅鏡'),(4,N'朱漆小盒'),(5,N'竹影筆筒'),(6,N'山水長卷'),(7,N'古錢小匣'),(8,N'白玉雙魚'),(9,N'雲紋香爐'),(10,N'墨色山水'),(11,N'青花小瓶'),(12,N'篆印藏家'),(13,N'素瓷茶盞'),(14,N'鏤空小球'),(15,N'雙魚銅洗'),(16,N'花鳥方瓶'),(17,N'翡翠小墜'),(18,N'剔紅套盒'),(19,N'蘭亭香筒'),(20,N'松間卷軸'),(21,N'硯邊留白'),(22,N'流釉茶碗'),(23,N'鳳紋琺瑯'),(24,N'荷風畫卷');
DECLARE @demoUsers TABLE(UserId uniqueidentifier PRIMARY KEY,Ordinal int UNIQUE,Nickname nvarchar(80));
INSERT @demoUsers SELECT u.Id,u.Ordinal,n.Nickname FROM (
    SELECT Id,ROW_NUMBER() OVER(ORDER BY CASE WHEN Email=N'admin@qmah.local' THEN 0 ELSE 1 END,Email,Id) Ordinal
    FROM [user].AspNetUsers WHERE (Email LIKE N'%@qmah.local' OR Email LIKE N'%@qmah.test') AND Status=N'ACTIVE'
) u JOIN @names n ON n.Ordinal=u.Ordinal;
IF (SELECT COUNT(*) FROM @demoUsers)<>24 THROW 51000,N'預期 24 個展示帳號，請檢查來源資料。',1;
UPDATE p SET Nickname=u.Nickname FROM [user].UserProfiles p JOIN @demoUsers u ON u.UserId=p.UserId;
UPDATE a SET RecipientName=u.Nickname FROM [user].UserAddresses a JOIN @demoUsers u ON u.UserId=a.UserId;
-- 保存流水總額。轉移整組資料後以差額調整餘額，保留原有起始餘額，絕不截斷負值。
DECLARE @pointBefore TABLE(UserId uniqueidentifier PRIMARY KEY,Amount bigint);
INSERT @pointBefore SELECT u.UserId,COALESCE(SUM(CONVERT(bigint,t.Amount)),0) FROM @demoUsers u LEFT JOIN store.PointTransactions t ON t.UserId=u.UserId GROUP BY u.UserId;
DECLARE @keyBefore TABLE(UserId uniqueidentifier,KeyDefinitionId uniqueidentifier,Amount bigint,PRIMARY KEY(UserId,KeyDefinitionId));
INSERT @keyBefore SELECT u.UserId,k.Id,COALESCE(SUM(CONVERT(bigint,t.Amount)),0) FROM @demoUsers u CROSS JOIN catalog.KeyDefinitions k LEFT JOIN catalog.KeyTransactions t ON t.UserId=u.UserId AND t.KeyDefinitionId=k.Id GROUP BY u.UserId,k.Id;
DECLARE @progressBefore TABLE(UserId uniqueidentifier PRIMARY KEY,Amount decimal(18,2));
INSERT @progressBefore SELECT u.UserId,COALESCE(SUM(t.Amount),0) FROM @demoUsers u LEFT JOIN catalog.KeyProgressTransactions t ON t.UserId=u.UserId GROUP BY u.UserId;
