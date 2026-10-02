-- 每位會員各有四種玩法的完成紀錄，不佔用今天的點數額度或突破條件。
DECLARE @mini TABLE(Id uniqueidentifier PRIMARY KEY,UserId uniqueidentifier,ModeId uniqueidentifier,ArtifactId uniqueidentifier,Pool nvarchar(max),Config nvarchar(max),Result nvarchar(max),Score int,Grade nvarchar(2),Points int,Progress decimal(12,2),CompletedAt datetime2(3));
INSERT @mini
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-MINI:'+CONVERT(nvarchar(36),u.UserId)+N':'+m.Code)),u.UserId,m.Id,material.ArtifactId,pool.Json,m.ConfigJson,
    (SELECT m.Code modeCode,material.ArtifactId artifactId,3 scoringVersion,u.Ordinal%3 hintsUsed,0 autoPlaced,
    CASE WHEN m.Code=N'ARTIFACT_PUZZLE' THEN JSON_QUERY(N'[0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24]') END puzzleOrder,
    CASE WHEN m.Code=N'STRIP_RESTORE' THEN JSON_QUERY(N'[0,1,2,3,4,5,6,7,8,9,10,11,12,13,14]') END restoreOrder,
    CASE WHEN m.Code=N'DETAIL_LOCATOR' THEN material.ArtifactId END locatorChoice,
    CASE WHEN m.Code=N'MEMORY_MATCH' THEN 8 END memoryPairs,CASE WHEN m.Code=N'MEMORY_MATCH' THEN 8 END memoryMatched,
    CASE m.Code WHEN N'STRIP_RESTORE' THEN 90 WHEN N'ARTIFACT_PUZZLE' THEN 150 ELSE 96 END elapsedSeconds,CASE m.Code WHEN N'STRIP_RESTORE' THEN 15 WHEN N'ARTIFACT_PUZZLE' THEN 25 ELSE 16 END moves FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
    score.Value,grade.Value,
    CASE grade.Value WHEN N'S' THEN m.SPointReward WHEN N'A' THEN m.APointReward ELSE m.BPointReward END,
    CASE grade.Value WHEN N'S' THEN m.SKeyProgressReward WHEN N'A' THEN m.AKeyProgressReward ELSE m.BKeyProgressReward END,
    DATEADD(minute,-u.Ordinal,CONVERT(datetime2(3),'2026-09-29T10:00:00'))
FROM @demoUsers u CROSS JOIN game.GameModeDefinitions m
CROSS APPLY(SELECT TOP(1) a.Id ArtifactId FROM catalog.Artifacts a JOIN catalog.ArtifactCategories c ON c.Id=a.CategoryId WHERE a.IsActive=1 AND (m.Code<>N'STRIP_RESTORE' OR (c.Code=N'PAINTING' AND a.Name LIKE N'%千岩萬壑%')) ORDER BY CASE WHEN a.Id=(SELECT TOP(1) ArtifactId FROM @rooms r JOIN @players p ON p.RoomId=r.RoomId WHERE p.UserId=u.UserId ORDER BY r.Ordinal) THEN 0 ELSE 1 END,a.ArtifactRef) material
CROSS APPLY(SELECT N'['+STRING_AGG(N'"'+CONVERT(nvarchar(36),a.Id)+N'"',N',') WITHIN GROUP(ORDER BY a.Id)+N']' Json FROM(SELECT TOP(CASE m.Code WHEN N'MEMORY_MATCH' THEN 8 WHEN N'DETAIL_LOCATOR' THEN 4 ELSE 1 END) Id FROM catalog.Artifacts WHERE IsActive=1 ORDER BY CASE WHEN Id=material.ArtifactId THEN 0 ELSE 1 END,ArtifactRef) a) pool
CROSS APPLY(SELECT 100-u.Ordinal%3*CASE WHEN m.Code=N'DETAIL_LOCATOR' THEN 10 ELSE 3 END Value) score
CROSS APPLY(SELECT CASE WHEN score.Value>=m.GradeSThreshold THEN N'S' WHEN score.Value>=m.GradeAThreshold THEN N'A' ELSE N'B' END Value) grade
WHERE m.Code IN(N'ARTIFACT_PUZZLE',N'STRIP_RESTORE',N'MEMORY_MATCH',N'DETAIL_LOCATOR') AND m.IsActive=1;
IF (SELECT COUNT(*) FROM @mini)<>96 THROW 51000,N'單人玩法種子未完整產生。',1;
INSERT game.MiniGameAttempts(Id,UserId,GameModeDefinitionId,ArtifactId,ArtifactPoolJson,Difficulty,Seed,ConfigJson,Status,RawScore,RawResultJson,NormalizedScore,Grade,PointReward,KeyProgressReward,RewardAttemptNo,RewardGranted,StartedAt,CompletedAt)
SELECT Id,UserId,ModeId,ArtifactId,Pool,N'NORMAL',N'v3-'+REPLACE(CONVERT(nvarchar(36),Id),N'-',N''),Config,N'COMPLETED',100,Result,Score,Grade,Points,Progress,1,1,DATEADD(second,-CONVERT(int,JSON_VALUE(Result,'$.elapsedSeconds')),CompletedAt),CompletedAt FROM @mini s WHERE NOT EXISTS(SELECT 1 FROM game.MiniGameAttempts a WHERE a.Id=s.Id);
-- 僅更新固定識別碼的種子結果，讓重跑能修正舊片數，不重發獎勵。
UPDATE a SET RawResultJson=s.Result,StartedAt=DATEADD(second,-CONVERT(int,JSON_VALUE(s.Result,'$.elapsedSeconds')),s.CompletedAt)
FROM game.MiniGameAttempts a JOIN @mini s ON s.Id=a.Id WHERE a.Seed=N'v3-'+REPLACE(CONVERT(nvarchar(36),s.Id),N'-',N'');
-- 同日四種玩法的獎勵次序為 1 至 4，保留其他歷史紀錄參與排序。
;WITH rewardOrder AS
(
    SELECT a.Id,ROW_NUMBER() OVER(PARTITION BY a.UserId,CONVERT(date,DATEADD(hour,8,a.CompletedAt)) ORDER BY a.CompletedAt,a.Id) AttemptNo
    FROM game.MiniGameAttempts a JOIN @demoUsers u ON u.UserId=a.UserId
    WHERE a.Status=N'COMPLETED' AND a.RewardGranted=1
)
UPDATE a SET RewardAttemptNo=r.AttemptNo FROM game.MiniGameAttempts a JOIN @mini s ON s.Id=a.Id JOIN rewardOrder r ON r.Id=a.Id;
INSERT store.PointTransactions(Id,UserId,Amount,Reason,ReferenceType,ReferenceId,CreatedAt)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-MINI-POINT:'+CONVERT(nvarchar(36),s.Id))),UserId,Points,N'完成單人遊戲',N'MINIGAME_REWARD',s.Id,CompletedAt FROM @mini s WHERE Points>0 AND NOT EXISTS(SELECT 1 FROM store.PointTransactions t WHERE t.UserId=s.UserId AND t.ReferenceType=N'MINIGAME_REWARD' AND t.ReferenceId=s.Id);
INSERT catalog.KeyProgressTransactions(Id,UserId,Amount,Reason,ReferenceType,ReferenceId,CreatedAt)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-MINI-PROGRESS:'+CONVERT(nvarchar(36),s.Id))),UserId,Progress,N'完成單人遊戲',N'MINIGAME_REWARD',s.Id,CompletedAt FROM @mini s WHERE Progress>0 AND NOT EXISTS(SELECT 1 FROM catalog.KeyProgressTransactions t WHERE t.UserId=s.UserId AND t.ReferenceType=N'MINIGAME_REWARD' AND t.ReferenceId=s.Id);
-- 修復已確認的舊種子完成結果，保留零獎勵，不動玩家尚未完成的遊戲。
UPDATE a SET RawScore=100,NormalizedScore=82,Grade=N'A',StartedAt=DATEADD(second,-150,a.CompletedAt),ConfigJson=m.ConfigJson,ArtifactPoolJson=N'["'+CONVERT(nvarchar(36),a.ArtifactId)+N'"]',RawResultJson=(SELECT m.Code modeCode,a.ArtifactId artifactId,3 scoringVersion,6 hintsUsed,0 autoPlaced,150 elapsedSeconds,25 moves,JSON_QUERY(N'[0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24]') puzzleOrder FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
FROM game.MiniGameAttempts a JOIN game.GameModeDefinitions m ON m.Id=a.GameModeDefinitionId JOIN @demoUsers u ON u.UserId=a.UserId WHERE a.Id='6C1B4BE0-A7B5-5B5E-88C6-139B5B67F205' AND a.Seed=N'showcase-173-1' AND a.PointReward=0 AND a.KeyProgressReward=0 AND m.Code=N'ARTIFACT_PUZZLE';
IF EXISTS(SELECT 1 FROM @mini s JOIN game.MiniGameAttempts a ON a.Id=s.Id WHERE ISJSON(a.ConfigJson)<>1 OR ISJSON(a.RawResultJson)<>1 OR ISJSON(a.ArtifactPoolJson)<>1 OR NOT EXISTS(SELECT 1 FROM store.PointTransactions t WHERE t.UserId=a.UserId AND t.ReferenceId=a.Id AND t.ReferenceType=N'MINIGAME_REWARD' AND t.Amount=a.PointReward)) THROW 51000,N'單人紀錄或獎勵流水不一致。',1;
IF EXISTS(SELECT 1 FROM @mini s JOIN game.GameModeDefinitions m ON m.Id=s.ModeId
    WHERE m.Code IN(N'ARTIFACT_PUZZLE',N'STRIP_RESTORE') AND
    (SELECT COUNT(*) FROM OPENJSON(s.Result,CASE m.Code WHEN N'ARTIFACT_PUZZLE' THEN '$.puzzleOrder' ELSE '$.restoreOrder' END))<>CASE m.Code WHEN N'ARTIFACT_PUZZLE' THEN 25 ELSE 15 END)
    THROW 51000,N'拼圖片數不符合目前玩法。',1;
