-- 唯讀驗證，直接沿用呼叫端與 reconcile-community.sql 建立的表變數。
-- 只檢查穩定鍵確認的展示貼文、留言，以及遊戲回顧的場次作者關聯。
IF (SELECT COUNT(*) FROM @demoUsers) <> 24
    THROW 51000, N'社群展示帳號應為 24 位。', 1;

IF (SELECT COUNT(*) FROM @gameRecaps) <> 43 OR (SELECT COUNT(*) FROM @communityPostCopy) <> 43
    THROW 51000, N'遊戲回顧或文案清單筆數不是 43。', 1;

IF EXISTS
(
    SELECT 1 FROM @communityPostCopy copy
    LEFT JOIN @gameRecaps recap ON recap.Id = copy.Id
    LEFT JOIN social.SocialPosts post ON post.Id = copy.Id
    WHERE recap.Id IS NULL OR post.Id IS NULL OR post.Title <> copy.Title OR post.Content <> copy.Content
)
    THROW 51000, N'遊戲回顧文案未完整套用，或清單包含非目標貼文。', 1;

IF EXISTS
(
    SELECT 1 FROM @matchedGeneratedPosts g
    JOIN social.SocialPosts p ON p.Id = g.Id
    WHERE NOT EXISTS (SELECT 1 FROM @gameRecaps recap WHERE recap.Id = p.Id)
      AND NOT EXISTS (SELECT 1 FROM @demoUsers demo WHERE demo.UserId = p.UserId)
)
    THROW 51000, N'一般生成貼文未分配給展示帳號。', 1;

IF EXISTS
(
    SELECT 1 FROM @gameRecaps recap
    WHERE recap.SourceRoomCode NOT IN (N'SHOW303', N'SHOW305')
       OR NOT EXISTS
       (
           SELECT 1 FROM game.GameRooms room
           JOIN game.GameRounds round ON round.RoomId = room.Id AND round.RoundNumber = 3
           WHERE room.RoomCode = recap.SourceRoomCode
       )
)
    THROW 51000, N'遊戲回顧來源場次快照沒有對應的第 3 回合。', 1;

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
    THROW 51000, N'連結文物的遊戲回顧作者不屬於 AP512 對應場次。', 1;

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

IF EXISTS
(
    SELECT 1 FROM @generatedComments g
    JOIN social.SocialComments comment ON comment.Id = g.Id
    WHERE NOT EXISTS (SELECT 1 FROM @demoUsers demo WHERE demo.UserId = comment.UserId)
)
    THROW 51000, N'生成留言未分配給展示帳號。', 1;

IF EXISTS
(
    SELECT 1 FROM @communityCopy copy
    LEFT JOIN @generatedComments generated ON generated.Id = copy.Id
    LEFT JOIN social.SocialComments comment ON comment.Id = copy.Id
    WHERE generated.Id IS NULL OR comment.Id IS NULL OR comment.Content <> copy.Content
)
    THROW 51000, N'社群文案清單未完整套用，或包含非本批生成留言。', 1;

IF EXISTS
(
    SELECT 1 FROM social.SocialComments child
    LEFT JOIN social.SocialComments parent ON parent.Id = child.ParentCommentId
    WHERE child.ParentCommentId IS NOT NULL
      AND (parent.Id IS NULL OR parent.PostId <> child.PostId)
)
    THROW 51000, N'留言父項不存在或與子留言貼文不一致。', 1;

IF EXISTS
(
    SELECT 1 FROM @matchedGeneratedPosts g
    JOIN social.SocialPosts post ON post.Id = g.Id
    WHERE NOT EXISTS (SELECT 1 FROM @gameRecaps recap WHERE recap.Id = post.Id)
      AND post.PostType = N'POST' AND post.PublisherType = N'COMMUNITY'
      AND NOT EXISTS (SELECT 1 FROM @demoUsers demo WHERE demo.UserId = post.UserId)
)
    THROW 51000, N'普通生成貼文作者不是展示帳號。', 1;

IF EXISTS
(
    SELECT MIN(userCount) AS MinimumCount, MAX(userCount) AS MaximumCount
    FROM
    (
        SELECT demo.UserId, COUNT(post.Id) AS userCount
        FROM @demoUsers demo
        LEFT JOIN @matchedGeneratedPosts generated ON 1 = 1
        LEFT JOIN social.SocialPosts post ON post.Id = generated.Id
          AND NOT EXISTS (SELECT 1 FROM @gameRecaps recap WHERE recap.Id = post.Id)
          AND post.UserId = demo.UserId
        GROUP BY demo.UserId
    ) distribution
    HAVING MAX(userCount) - MIN(userCount) > 1
)
    THROW 51000, N'普通生成貼文未平均分配。', 1;

IF EXISTS
(
    SELECT MIN(userCount) AS MinimumCount, MAX(userCount) AS MaximumCount
    FROM
    (
        SELECT demo.UserId, COUNT(comment.Id) AS userCount
        FROM @demoUsers demo
        LEFT JOIN @generatedComments generated ON 1 = 1
        LEFT JOIN social.SocialComments comment ON comment.Id = generated.Id AND comment.UserId = demo.UserId
        GROUP BY demo.UserId
    ) distribution
    HAVING MAX(userCount) - MIN(userCount) > 1
)
    THROW 51000, N'生成留言未平均分配。', 1;

SELECT N'一般貼文作者分布' AS Dataset, demo.Ordinal, demo.Nickname, COUNT(post.Id) AS TotalRows
FROM @demoUsers demo
LEFT JOIN @matchedGeneratedPosts generated ON 1 = 1
LEFT JOIN social.SocialPosts post ON post.Id = generated.Id
  AND NOT EXISTS (SELECT 1 FROM @gameRecaps recap WHERE recap.Id = post.Id)
  AND post.UserId = demo.UserId
GROUP BY demo.Ordinal, demo.Nickname
UNION ALL
SELECT N'生成留言作者分布', demo.Ordinal, demo.Nickname, COUNT(comment.Id)
FROM @demoUsers demo
LEFT JOIN @generatedComments generated ON 1 = 1
LEFT JOIN social.SocialComments comment ON comment.Id = generated.Id AND comment.UserId = demo.UserId
GROUP BY demo.Ordinal, demo.Nickname
ORDER BY Dataset, Ordinal;
