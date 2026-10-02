-- 完成多人回合即解鎖該局文物，直接沿用已有場次，不增加鑰匙或捏造消費。
-- 原種子誤給一筆「分類收齊」，該帳號並未完成任何分類，且沒有裝備此稱號。
DELETE held FROM [user].UserAchievements held JOIN @demoUsers u ON u.UserId=held.UserId
JOIN [user].Achievements a ON a.Id=held.AchievementId
WHERE held.Id='798020B7-CDE1-495D-B12F-71B00BB2A7B9' AND held.IsDisplayed=0
AND a.Code=N'SHOWCASE_ACHIEVEMENT_CATALOG_CATEGORY_COMPLETE'
AND NOT EXISTS(SELECT 1 FROM catalog.ArtifactCategories c WHERE EXISTS(SELECT 1 FROM catalog.Artifacts active WHERE active.CategoryId=c.Id AND active.IsActive=1)
    AND NOT EXISTS(SELECT 1 FROM catalog.Artifacts active WHERE active.CategoryId=c.Id AND active.IsActive=1
        AND NOT EXISTS(SELECT 1 FROM catalog.ArtifactUnlocks owned WHERE owned.UserId=held.UserId AND owned.ArtifactId=active.Id)));
INSERT catalog.ArtifactUnlocks(Id,UserId,ArtifactId,UnlockMethod,GameRoundId,UnlockedAt)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-UNLOCK:'+CONVERT(nvarchar(36),p.UserId)+N':'+CONVERT(nvarchar(36),r.ArtifactId))),
    p.UserId,r.ArtifactId,N'GAME',r.RoundId,r.CompletedAt
FROM @players p JOIN @rooms r ON r.RoomId=p.RoomId
WHERE NOT EXISTS(SELECT 1 FROM catalog.ArtifactUnlocks a WHERE a.UserId=p.UserId AND a.ArtifactId=r.ArtifactId);

-- 補上本次收藏進度達成的啟用成就，取得時間使用真正跨過門檻的那次解鎖。
;WITH rankedUnlocks AS
(
    SELECT x.UserId,x.UnlockedAt,ROW_NUMBER() OVER(PARTITION BY x.UserId ORDER BY x.UnlockedAt,x.Id) Ordinal
    FROM catalog.ArtifactUnlocks x JOIN @demoUsers u ON u.UserId=x.UserId
    JOIN catalog.Artifacts a ON a.Id=x.ArtifactId AND a.IsActive=1
)
INSERT [user].UserAchievements(Id,UserId,AchievementId,AchievedAt,IsDisplayed)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-COLLECTION-ACHIEVEMENT:'+CONVERT(nvarchar(36),x.UserId)+N':'+CONVERT(nvarchar(36),a.Id))),
    x.UserId,a.Id,CASE WHEN x.UnlockedAt<a.CreatedAt THEN a.CreatedAt ELSE x.UnlockedAt END,0
FROM rankedUnlocks x JOIN [user].Achievements a ON a.ConditionType=N'ARTIFACT_UNLOCK_COUNT' AND a.Status=N'ACTIVE' AND a.ThresholdValue=x.Ordinal
WHERE NOT EXISTS(SELECT 1 FROM [user].UserAchievements held WHERE held.UserId=x.UserId AND held.AchievementId=a.Id);

-- 少量文物的年代範圍可能已收齊，成就依啟用文物完整度判定。
;WITH completedScopes AS
(
    SELECT x.UserId,N'CATEGORY_COMPLETE_COUNT' ConditionType,a.CategoryId ScopeId,MAX(x.UnlockedAt) CompletedAt
    FROM catalog.ArtifactUnlocks x JOIN @demoUsers u ON u.UserId=x.UserId JOIN catalog.Artifacts a ON a.Id=x.ArtifactId AND a.IsActive=1
    GROUP BY x.UserId,a.CategoryId HAVING COUNT(*)=(SELECT COUNT(*) FROM catalog.Artifacts allArtifacts WHERE allArtifacts.IsActive=1 AND allArtifacts.CategoryId=a.CategoryId)
    UNION ALL
    SELECT x.UserId,N'ERA_COMPLETE_COUNT',a.EraBucketId,MAX(x.UnlockedAt)
    FROM catalog.ArtifactUnlocks x JOIN @demoUsers u ON u.UserId=x.UserId JOIN catalog.Artifacts a ON a.Id=x.ArtifactId AND a.IsActive=1
    GROUP BY x.UserId,a.EraBucketId HAVING COUNT(*)=(SELECT COUNT(*) FROM catalog.Artifacts allArtifacts WHERE allArtifacts.IsActive=1 AND allArtifacts.EraBucketId=a.EraBucketId)
), rankedScopes AS
(
    SELECT *,ROW_NUMBER() OVER(PARTITION BY UserId,ConditionType ORDER BY CompletedAt,ScopeId) Ordinal FROM completedScopes
)
INSERT [user].UserAchievements(Id,UserId,AchievementId,AchievedAt,IsDisplayed)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-COLLECTION-ACHIEVEMENT:'+CONVERT(nvarchar(36),x.UserId)+N':'+CONVERT(nvarchar(36),a.Id))),
    x.UserId,a.Id,CASE WHEN x.CompletedAt<a.CreatedAt THEN a.CreatedAt ELSE x.CompletedAt END,0
FROM rankedScopes x JOIN [user].Achievements a ON a.ConditionType=x.ConditionType AND a.Status=N'ACTIVE' AND a.ThresholdValue=x.Ordinal
WHERE NOT EXISTS(SELECT 1 FROM [user].UserAchievements held WHERE held.UserId=x.UserId AND held.AchievementId=a.Id);

UPDATE held SET AchievedAt=a.CreatedAt FROM [user].UserAchievements held JOIN [user].Achievements a ON a.Id=held.AchievementId
WHERE held.AchievedAt<a.CreatedAt AND held.IsDisplayed=0
AND held.Id=CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-COLLECTION-ACHIEVEMENT:'+CONVERT(nvarchar(36),held.UserId)+N':'+CONVERT(nvarchar(36),a.Id)));

-- 舊假資料的收藏成就曾早於解鎖紀錄，校正日期但保留展示與稱號選擇。
DECLARE @collectionAwardDates TABLE(HeldId uniqueidentifier PRIMARY KEY,AchievedAt datetime2(3));
;WITH rankedUnlocks AS
(
    SELECT x.UserId,x.UnlockedAt,ROW_NUMBER() OVER(PARTITION BY x.UserId ORDER BY x.UnlockedAt,x.Id) Ordinal
    FROM catalog.ArtifactUnlocks x JOIN @demoUsers u ON u.UserId=x.UserId JOIN catalog.Artifacts a ON a.Id=x.ArtifactId AND a.IsActive=1
)
INSERT @collectionAwardDates
SELECT held.Id,CASE WHEN x.UnlockedAt<a.CreatedAt THEN a.CreatedAt ELSE x.UnlockedAt END
FROM rankedUnlocks x JOIN [user].Achievements a ON a.ConditionType=N'ARTIFACT_UNLOCK_COUNT' AND a.ThresholdValue=x.Ordinal AND a.Code LIKE N'SHOWCASE[_]%'
JOIN [user].UserAchievements held ON held.UserId=x.UserId AND held.AchievementId=a.Id
WHERE held.AchievedAt<x.UnlockedAt OR held.AchievedAt<a.CreatedAt;
UPDATE held SET AchievedAt=dates.AchievedAt,DisplayedAt=CASE WHEN held.IsDisplayed=1 AND held.DisplayedAt<dates.AchievedAt THEN dates.AchievedAt ELSE held.DisplayedAt END
FROM [user].UserAchievements held JOIN @collectionAwardDates dates ON dates.HeldId=held.Id;
UPDATE title SET EquippedAt=dates.AchievedAt FROM [user].EquippedTitles title JOIN @collectionAwardDates dates ON dates.HeldId=title.UserAchievementId WHERE title.EquippedAt<dates.AchievedAt;

IF EXISTS(SELECT 1 FROM @players p JOIN @rooms r ON r.RoomId=p.RoomId
    WHERE NOT EXISTS(SELECT 1 FROM catalog.ArtifactUnlocks a WHERE a.UserId=p.UserId AND a.ArtifactId=r.ArtifactId))
    THROW 51000,N'已完成回合的文物尚未解鎖。',1;
IF EXISTS(SELECT 1 FROM catalog.ArtifactUnlocks a JOIN @demoUsers u ON u.UserId=a.UserId
    JOIN @rooms r ON r.RoundId=a.GameRoundId
    WHERE a.ArtifactId<>r.ArtifactId OR a.UnlockedAt<r.CompletedAt
    OR NOT EXISTS(SELECT 1 FROM @players p WHERE p.RoomId=r.RoomId AND p.UserId=a.UserId))
    THROW 51000,N'圖鑑解鎖與遊戲回合不一致。',1;
