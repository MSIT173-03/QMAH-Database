-- 先結清舊種子累積的進度，再依單局順序兌換，收據與流水使用相同來源。
DECLARE @threshold int=(SELECT TOP(1) KeyProgressToNormalKey FROM game.GameEconomySettings ORDER BY Id);
DECLARE @normalKey uniqueidentifier=(SELECT TOP(1) Id FROM catalog.KeyDefinitions WHERE ScopeType=N'NORMAL' AND IsActive=1 ORDER BY Id);
IF @threshold IS NULL OR @threshold<=0 OR @normalKey IS NULL THROW 51000,N'鑰匙兌換設定不存在。',1;
DECLARE @baselineProgress TABLE(UserId uniqueidentifier PRIMARY KEY,ConversionId uniqueidentifier,Keys int,Remainder decimal(12,2),CreatedAt datetime2(3));
INSERT @baselineProgress
SELECT u.UserId,CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-PROGRESS:'+CONVERT(nvarchar(36),u.UserId))),
    CONVERT(int,FLOOR(original.Balance/@threshold)),original.Balance%@threshold,
    DATEADD(millisecond,-1,(SELECT MIN(a.StartedAt) FROM game.MiniGameAttempts a JOIN @mini s ON s.Id=a.Id WHERE s.UserId=u.UserId))
FROM @demoUsers u JOIN catalog.KeyProgressBalances b ON b.UserId=u.UserId JOIN @progressBefore before ON before.UserId=u.UserId
CROSS APPLY(SELECT b.Balance+COALESCE((SELECT SUM(t.Amount) FROM catalog.KeyProgressTransactions t WHERE t.UserId=u.UserId),0)-before.Amount
    -(SELECT SUM(s.Progress) FROM @mini s WHERE s.UserId=u.UserId)
    -COALESCE((SELECT SUM(t.Amount) FROM catalog.KeyProgressTransactions t WHERE t.UserId=u.UserId AND
        (t.Id=CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-PROGRESS:'+CONVERT(nvarchar(36),u.UserId)))
        OR (t.ReferenceType=N'MINIGAME_PROGRESS_CONVERSION' AND EXISTS(SELECT 1 FROM @mini s WHERE s.Id=t.ReferenceId)))),0) Balance) original;
IF EXISTS(SELECT 1 FROM @baselineProgress WHERE Keys<0 OR Remainder<0) THROW 51000,N'無法重建單人遊戲前的進度。',1;

-- 舊進度只保留門檻內餘數，其兌換時間早於本批單人遊戲。
UPDATE t SET Amount=-CONVERT(decimal(12,2),b.Keys)*@threshold,CreatedAt=b.CreatedAt
FROM catalog.KeyProgressTransactions t JOIN @baselineProgress b ON b.ConversionId=t.Id WHERE b.Keys>0;
DELETE t FROM catalog.KeyProgressTransactions t JOIN @baselineProgress b ON b.ConversionId=t.Id WHERE b.Keys=0;
INSERT catalog.KeyProgressTransactions(Id,UserId,Amount,Reason,ReferenceType,ReferenceId,CreatedAt)
SELECT ConversionId,UserId,-CONVERT(decimal(12,2),Keys)*@threshold,N'既有鑰匙進度兌換',N'KEY_PROGRESS_CONVERT',ConversionId,CreatedAt
FROM @baselineProgress b WHERE Keys>0 AND NOT EXISTS(SELECT 1 FROM catalog.KeyProgressTransactions t WHERE t.Id=b.ConversionId);

UPDATE t SET Amount=b.Keys,CreatedAt=b.CreatedAt FROM catalog.KeyTransactions t JOIN @baselineProgress b
ON t.Id=CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-KEY:'+CONVERT(nvarchar(36),b.ConversionId))) WHERE b.Keys>0;
DELETE t FROM catalog.KeyTransactions t JOIN @baselineProgress b
ON t.Id=CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-KEY:'+CONVERT(nvarchar(36),b.ConversionId))) WHERE b.Keys=0;
INSERT catalog.KeyTransactions(Id,UserId,KeyDefinitionId,Amount,Reason,ReferenceType,ReferenceId,CreatedAt)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-KEY:'+CONVERT(nvarchar(36),ConversionId))),UserId,@normalKey,Keys,N'既有鑰匙進度兌換',N'KEY_PROGRESS_CONVERT',ConversionId,CreatedAt
FROM @baselineProgress b WHERE Keys>0 AND NOT EXISTS(SELECT 1 FROM catalog.KeyTransactions t WHERE t.Id=CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-KEY:'+CONVERT(nvarchar(36),b.ConversionId))));

DECLARE @miniConversions TABLE(AttemptId uniqueidentifier PRIMARY KEY,UserId uniqueidentifier,Keys int,CompletedAt datetime2(3));
;WITH runningProgress AS
(
    SELECT s.*,SUM(s.Progress) OVER(PARTITION BY s.UserId ORDER BY s.CompletedAt,s.Id ROWS UNBOUNDED PRECEDING) Running
    FROM @mini s
)
INSERT @miniConversions
SELECT s.Id,s.UserId,CONVERT(int,FLOOR((b.Remainder+s.Running)/@threshold)-FLOOR((b.Remainder+s.Running-s.Progress)/@threshold)),s.CompletedAt
FROM runningProgress s JOIN @baselineProgress b ON b.UserId=s.UserId;
UPDATE a SET ConvertedNormalKeys=c.Keys,KeyRewardDivisor=1 FROM game.MiniGameAttempts a JOIN @miniConversions c ON c.AttemptId=a.Id;
INSERT catalog.KeyProgressTransactions(Id,UserId,Amount,Reason,ReferenceType,ReferenceId,CreatedAt)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-MINI-CONVERSION:'+CONVERT(nvarchar(36),AttemptId))),UserId,-CONVERT(decimal(12,2),Keys)*@threshold,
    N'遊戲進度達標轉換探索鑰匙',N'MINIGAME_PROGRESS_CONVERSION',AttemptId,CompletedAt
FROM @miniConversions c WHERE Keys>0 AND NOT EXISTS(SELECT 1 FROM catalog.KeyProgressTransactions t WHERE t.ReferenceType=N'MINIGAME_PROGRESS_CONVERSION' AND t.ReferenceId=c.AttemptId);
INSERT catalog.KeyTransactions(Id,UserId,KeyDefinitionId,Amount,Reason,ReferenceType,ReferenceId,CreatedAt)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-CONNECTED-MINI-KEY:'+CONVERT(nvarchar(36),AttemptId))),UserId,@normalKey,Keys,
    N'遊戲進度達標轉換探索鑰匙',N'MINIGAME_REWARD',AttemptId,CompletedAt
FROM @miniConversions c WHERE Keys>0 AND NOT EXISTS(SELECT 1 FROM catalog.KeyTransactions t WHERE t.ReferenceType=N'MINIGAME_REWARD' AND t.ReferenceId=c.AttemptId);
INSERT catalog.UserKeyBalances(UserId,KeyDefinitionId,Balance)
SELECT UserId,@normalKey,0 FROM @baselineProgress b WHERE NOT EXISTS(SELECT 1 FROM catalog.UserKeyBalances owned WHERE owned.UserId=b.UserId AND owned.KeyDefinitionId=@normalKey);

IF EXISTS(SELECT 1 FROM @miniConversions c WHERE c.Keys<>COALESCE((SELECT SUM(t.Amount) FROM catalog.KeyTransactions t WHERE t.ReferenceType=N'MINIGAME_REWARD' AND t.ReferenceId=c.AttemptId AND t.UserId=c.UserId),0)
    OR -CONVERT(decimal(12,2),c.Keys)*@threshold<>COALESCE((SELECT SUM(t.Amount) FROM catalog.KeyProgressTransactions t WHERE t.ReferenceType=N'MINIGAME_PROGRESS_CONVERSION' AND t.ReferenceId=c.AttemptId AND t.UserId=c.UserId),0))
    THROW 51000,N'單局鑰匙收據與兌換流水不一致。',1;
IF EXISTS(SELECT 1 FROM catalog.ArtifactUnlocks a JOIN @demoUsers u ON u.UserId=a.UserId JOIN catalog.KeyTransactions t ON t.Id=a.KeyTransactionId WHERE t.UserId<>a.UserId OR t.Amount>=0)
    THROW 51000,N'文物解鎖與鑰匙扣除的會員不一致。',1;
