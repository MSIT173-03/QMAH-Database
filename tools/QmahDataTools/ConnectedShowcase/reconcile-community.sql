-- 此批只處理 ShowcaseDataCommands 以穩定鍵建立的社群貼文與留言。
-- @demoUsers、@rooms、@players 由呼叫端建立，本批不提交交易。
IF (SELECT COUNT(*) FROM @demoUsers) <> 24
    THROW 51000, N'社群展示帳號應為 24 位。', 1;

DECLARE @communityCopyJson nvarchar(max) = N'__COMMUNITYCOPY__';
DECLARE @communityCopy TABLE(Id uniqueidentifier PRIMARY KEY, Content nvarchar(2000) NOT NULL);
INSERT @communityCopy(Id, Content)
SELECT Id, Content
FROM OPENJSON(@communityCopyJson)
WITH (Id uniqueidentifier '$.Id', Content nvarchar(2000) '$.Content');

DECLARE @communityPostCopyJson nvarchar(max) = N'__COMMUNITYPOSTCOPY__';
DECLARE @communityPostCopy TABLE(Id uniqueidentifier PRIMARY KEY, SourceRoomCode nvarchar(7) NOT NULL, Title nvarchar(150) NOT NULL, Content nvarchar(max) NOT NULL);
INSERT @communityPostCopy(Id, SourceRoomCode, Title, Content)
SELECT Id, SourceRoomCode, Title, Content
FROM OPENJSON(@communityPostCopyJson)
WITH (Id uniqueidentifier '$.Id', SourceRoomCode nvarchar(7) '$.SourceRoomCode', Title nvarchar(150) '$.Title', Content nvarchar(max) '$.Content');

DECLARE @generatedPosts TABLE(GeneratorIndex int PRIMARY KEY, Id uniqueidentifier UNIQUE);
;WITH Indexes AS
(
    SELECT TOP (512) CONVERT(int, ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id)) AS GeneratorIndex
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), Hashes AS
(
    SELECT i.GeneratorIndex,
           LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256',
               CONVERT(varbinary(max), CONCAT('qmah-showcase-generated-post:', i.GeneratorIndex))), 2)) AS HashHex
    FROM Indexes i
), Versioned AS
(
    SELECT GeneratorIndex,
           STUFF(STUFF(HashHex, 13, 1, '5'), 17, 1,
               SUBSTRING('89ab', ((CHARINDEX(SUBSTRING(HashHex, 17, 1), '0123456789abcdef') - 1) % 4) + 1, 1)) AS HashHex
    FROM Hashes
)
INSERT @generatedPosts(GeneratorIndex, Id)
SELECT GeneratorIndex,
       CONVERT(uniqueidentifier,
           CONCAT(SUBSTRING(HashHex,7,2),SUBSTRING(HashHex,5,2),SUBSTRING(HashHex,3,2),SUBSTRING(HashHex,1,2),'-',
                  SUBSTRING(HashHex,11,2),SUBSTRING(HashHex,9,2),'-',
                  SUBSTRING(HashHex,15,2),SUBSTRING(HashHex,13,2),'-',
                  SUBSTRING(HashHex,17,4),'-',SUBSTRING(HashHex,21,12)))
FROM Versioned;

DECLARE @matchedGeneratedPosts TABLE(GeneratorIndex int PRIMARY KEY, Id uniqueidentifier UNIQUE);
INSERT @matchedGeneratedPosts(GeneratorIndex, Id)
SELECT g.GeneratorIndex, p.Id
FROM @generatedPosts g
JOIN social.SocialPosts p ON p.Id = g.Id
WHERE p.PostType = N'POST' AND p.PublisherType = N'COMMUNITY' AND p.EventId IS NULL;

-- 遊戲回顧有其場次關聯。已連結文物者只在原作者不屬於對應 AP512 場次時，
-- 才改派給同文物回合的參與者；沒有文物或場次來源不足以判定者保留原作者。
DECLARE @gameRecaps TABLE(Id uniqueidentifier PRIMARY KEY, ArtifactId uniqueidentifier NULL, UserId uniqueidentifier NOT NULL, SourceRoomCode nvarchar(7) NOT NULL);
INSERT @gameRecaps(Id, ArtifactId, UserId, SourceRoomCode)
SELECT p.Id, p.ArtifactId, p.UserId, copy.SourceRoomCode
FROM @communityPostCopy copy
JOIN @matchedGeneratedPosts g ON g.Id = copy.Id
JOIN social.SocialPosts p ON p.Id = g.Id;

IF EXISTS
(
    SELECT 1 FROM @communityPostCopy copy
    LEFT JOIN @matchedGeneratedPosts generated ON generated.Id = copy.Id
    LEFT JOIN social.SocialPosts post ON post.Id = copy.Id
    WHERE generated.Id IS NULL OR post.Id IS NULL OR post.BoardCode <> N'GAME'
      OR copy.SourceRoomCode NOT IN (N'SHOW303', N'SHOW305')
)
    THROW 51000, N'回顧清單包含非生成遊戲貼文或來源場次代碼無效。', 1;

UPDATE post
SET Title = copy.Title, Content = copy.Content, UpdatedAt = SYSUTCDATETIME()
FROM social.SocialPosts post
JOIN @communityPostCopy copy ON copy.Id = post.Id
JOIN @gameRecaps recap ON recap.Id = post.Id
WHERE post.Title <> copy.Title OR post.Content <> copy.Content;

IF EXISTS
(
    SELECT 1 FROM @communityPostCopy copy
    LEFT JOIN @gameRecaps recap ON recap.Id = copy.Id
    LEFT JOIN social.SocialPosts post ON post.Id = copy.Id
    WHERE recap.Id IS NULL OR post.Id IS NULL OR post.Title <> copy.Title OR post.Content <> copy.Content
)
    THROW 51000, N'遊戲回顧文案未完整套用，或清單包含非目標貼文。', 1;

DECLARE @gameOwnerUpdates TABLE(Id uniqueidentifier PRIMARY KEY, UserId uniqueidentifier NOT NULL);
;WITH ArtifactRecaps AS
(
    SELECT r.Id, r.ArtifactId, r.UserId,
           ROW_NUMBER() OVER(PARTITION BY r.ArtifactId ORDER BY r.Id) AS ArtifactPostOrdinal
    FROM @gameRecaps r
    WHERE r.ArtifactId IS NOT NULL
), CandidatePlayers AS
(
    SELECT p.Id AS PostId, p.UserId AS CurrentUserId,
           pl.UserId AS CandidateUserId, pl.Seat AS CandidateSeat,
           a.ArtifactPostOrdinal
    FROM ArtifactRecaps a
    JOIN @rooms room ON room.ArtifactId = a.ArtifactId
    JOIN @players pl ON pl.RoomId = room.RoomId
    JOIN social.SocialPosts p ON p.Id = a.Id
)
INSERT @gameOwnerUpdates(Id, UserId)
SELECT c.PostId, chosen.CandidateUserId
FROM CandidatePlayers c
CROSS APPLY
(
    SELECT TOP (1) c2.CandidateUserId
    FROM CandidatePlayers c2
    WHERE c2.PostId = c.PostId
    ORDER BY CASE WHEN c2.CandidateUserId = c.CurrentUserId THEN 0 ELSE 1 END,
             CASE WHEN c2.CandidateUserId = c.CurrentUserId THEN c2.CandidateSeat
                  ELSE (c2.ArtifactPostOrdinal - 1) % 3 + 1 END
) chosen
WHERE c.CandidateSeat = 1;

-- 未連結文物的來源場次由文案清單快照提供，沿用該場實際玩家名單，避免依賴已更新的標題。
;WITH RoomRecaps AS
(
    SELECT r.Id, r.UserId, r.SourceRoomCode,
           ROW_NUMBER() OVER(PARTITION BY r.SourceRoomCode ORDER BY r.Id) AS RecapOrdinal
    FROM @gameRecaps r
    WHERE r.ArtifactId IS NULL
), RoomPlayers AS
(
    SELECT room.Id AS RoomId, room.RoomCode, player.UserId,
           ROW_NUMBER() OVER(PARTITION BY room.Id ORDER BY COALESCE(player.SeatNo, 2147483647), player.Id) AS PlayerOrdinal,
           COUNT(*) OVER(PARTITION BY room.Id) AS PlayerCount
    FROM game.GameRooms room
    JOIN game.GamePlayers player ON player.RoomId = room.Id
    WHERE room.RoomCode IN (SELECT DISTINCT SourceRoomCode FROM RoomRecaps)
      AND player.UserId IS NOT NULL
), LegacyOwnerCandidates AS
(
    SELECT recap.Id, recap.UserId AS CurrentUserId, player.UserId AS CandidateUserId,
           recap.RecapOrdinal, player.PlayerOrdinal, player.PlayerCount
    FROM RoomRecaps recap
    JOIN RoomPlayers player ON player.RoomCode = recap.SourceRoomCode
)
INSERT @gameOwnerUpdates(Id, UserId)
SELECT c.Id,
       COALESCE(currentPlayer.CandidateUserId, selectedPlayer.CandidateUserId)
FROM LegacyOwnerCandidates c
OUTER APPLY
(
    SELECT TOP (1) candidate.CandidateUserId
    FROM LegacyOwnerCandidates candidate
    WHERE candidate.Id = c.Id AND candidate.CandidateUserId = c.CurrentUserId
) currentPlayer
OUTER APPLY
(
    SELECT TOP (1) candidate.CandidateUserId
    FROM LegacyOwnerCandidates candidate
    WHERE candidate.Id = c.Id
      AND candidate.PlayerOrdinal = ((c.RecapOrdinal - 1) % c.PlayerCount) + 1
) selectedPlayer
WHERE c.PlayerOrdinal = 1
  AND COALESCE(currentPlayer.CandidateUserId, selectedPlayer.CandidateUserId) IS NOT NULL;

UPDATE post
SET UserId = owner.UserId, UpdatedAt = SYSUTCDATETIME()
FROM social.SocialPosts post
JOIN @gameOwnerUpdates owner ON owner.Id = post.Id
WHERE post.UserId <> owner.UserId;

-- 其餘一般生成貼文按穩定 ID 輪替分配，24 位的數量差最多一篇。
;WITH OrdinaryPosts AS
(
    SELECT p.Id, ROW_NUMBER() OVER(ORDER BY p.Id) AS RowNo
    FROM @matchedGeneratedPosts g
    JOIN social.SocialPosts p ON p.Id = g.Id
    WHERE NOT EXISTS (SELECT 1 FROM @gameRecaps r WHERE r.Id = p.Id)
), PostOwners AS
(
    SELECT p.Id, u.UserId
    FROM OrdinaryPosts p
    JOIN @demoUsers u ON u.Ordinal = (CONVERT(int, p.RowNo - 1) % 24) + 1
)
UPDATE post
SET UserId = owner.UserId, UpdatedAt = SYSUTCDATETIME()
FROM social.SocialPosts post
JOIN PostOwners owner ON owner.Id = post.Id
WHERE post.UserId <> owner.UserId;

DECLARE @generatedComments TABLE(Id uniqueidentifier PRIMARY KEY, GeneratorIndex int, Slot int);
;WITH CommentKeys AS
(
    SELECT g.GeneratorIndex, slots.Slot,
           LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max),
               CONCAT('qmah-showcase-generated-comment:', g.GeneratorIndex, ':', slots.Slot))), 2)) AS HashHex
    FROM @matchedGeneratedPosts g
    CROSS APPLY (VALUES(1),(2),(3)) slots(Slot)
    WHERE slots.Slot <= CASE WHEN g.GeneratorIndex % 3 = 0 THEN 3 ELSE 2 END
), Versioned AS
(
    SELECT GeneratorIndex, Slot,
           STUFF(STUFF(HashHex, 13, 1, '5'), 17, 1,
               SUBSTRING('89ab', ((CHARINDEX(SUBSTRING(HashHex, 17, 1), '0123456789abcdef') - 1) % 4) + 1, 1)) AS HashHex
    FROM CommentKeys
)
INSERT @generatedComments(Id, GeneratorIndex, Slot)
SELECT CONVERT(uniqueidentifier,
           CONCAT(SUBSTRING(HashHex,7,2),SUBSTRING(HashHex,5,2),SUBSTRING(HashHex,3,2),SUBSTRING(HashHex,1,2),'-',
                  SUBSTRING(HashHex,11,2),SUBSTRING(HashHex,9,2),'-',
                  SUBSTRING(HashHex,15,2),SUBSTRING(HashHex,13,2),'-',
                  SUBSTRING(HashHex,17,4),'-',SUBSTRING(HashHex,21,12))), GeneratorIndex, Slot
FROM Versioned;

-- 只更新能用穩定鍵確認的展示留言；一般玩家與管理者的留言不在目標集合。
;WITH GeneratedComments AS
(
    SELECT c.Id, ROW_NUMBER() OVER(ORDER BY c.Id) AS RowNo
    FROM @generatedComments g
    JOIN social.SocialComments c ON c.Id = g.Id
    JOIN @matchedGeneratedPosts p ON p.Id = c.PostId
)
UPDATE comment
SET UserId = u.UserId, UpdatedAt = SYSUTCDATETIME()
FROM social.SocialComments comment
JOIN GeneratedComments g ON g.Id = comment.Id
JOIN @demoUsers u ON u.Ordinal = (CONVERT(int, g.RowNo - 1) % 24) + 1
WHERE comment.UserId <> u.UserId;

-- 社群文案以 JSON manifest 的固定留言 ID 更新，保留留言作者、父子關係與審核狀態。
UPDATE comment
SET Content = copy.Content, UpdatedAt = SYSUTCDATETIME()
FROM social.SocialComments comment
JOIN @communityCopy copy ON copy.Id = comment.Id
JOIN @generatedComments generated ON generated.Id = comment.Id
WHERE comment.Content <> copy.Content;

IF EXISTS
(
    SELECT 1 FROM @communityCopy copy
    LEFT JOIN @generatedComments generated ON generated.Id = copy.Id
    LEFT JOIN social.SocialComments comment ON comment.Id = copy.Id
    WHERE generated.Id IS NULL OR comment.Id IS NULL
)
    THROW 51000, N'文案清單包含非本批生成的留言。', 1;

IF EXISTS
(
    SELECT 1
    FROM social.SocialComments child
    LEFT JOIN social.SocialComments parent ON parent.Id = child.ParentCommentId
    WHERE child.ParentCommentId IS NOT NULL
      AND (parent.Id IS NULL OR parent.PostId <> child.PostId)
)
    THROW 51000, N'留言父項不存在或與子留言貼文不一致。', 1;

IF EXISTS
(
    SELECT 1 FROM @gameRecaps recap
    JOIN social.SocialPosts post ON post.Id = recap.Id
    WHERE recap.ArtifactId IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM @rooms room
          JOIN @players player ON player.RoomId = room.RoomId
          WHERE room.ArtifactId = post.ArtifactId AND player.UserId = post.UserId
      )
)
    THROW 51000, N'遊戲回顧作者與對應文物回合參與者不一致。', 1;

IF EXISTS
(
    SELECT 1 FROM @gameRecaps recap
    JOIN social.SocialPosts post ON post.Id = recap.Id
    WHERE recap.ArtifactId IS NULL
      AND EXISTS (SELECT 1 FROM game.GameRooms room WHERE room.RoomCode = recap.SourceRoomCode)
      AND NOT EXISTS
      (
          SELECT 1 FROM game.GameRooms room
          JOIN game.GamePlayers player ON player.RoomId = room.Id
          WHERE room.RoomCode = recap.SourceRoomCode AND player.UserId = post.UserId
      )
)
    THROW 51000, N'未連結文物的遊戲回顧作者不屬於標題指定場次。', 1;
