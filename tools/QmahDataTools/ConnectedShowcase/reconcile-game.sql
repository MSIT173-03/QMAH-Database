DECLARE @answersJson nvarchar(max)=N'__ANSWERS__';
DECLARE @texts TABLE(ArtifactId uniqueidentifier PRIMARY KEY,Factual nvarchar(500),Fiction nvarchar(500),Creative nvarchar(500));
INSERT @texts SELECT * FROM OPENJSON(@answersJson) WITH(ArtifactId uniqueidentifier '$.Id',Factual nvarchar(500) '$.factual',Fiction nvarchar(500) '$.fiction',Creative nvarchar(500) '$.creative');
IF EXISTS(SELECT 1 FROM @texts t LEFT JOIN catalog.Artifacts a ON a.Id=t.ArtifactId WHERE a.Id IS NULL OR a.IsActive=0) THROW 51000,N'文案所對應文物不存在或已停用。',1;
DECLARE @rooms TABLE(RoomId uniqueidentifier PRIMARY KEY,RoundId uniqueidentifier,ArtifactId uniqueidentifier,RoomCode nvarchar(12),Ordinal int,CompletedAt datetime2(3));
INSERT @rooms SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-APPRECIATION-ROOM:'+a.ArtifactRef)),CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-APPRECIATION-ROUND:'+a.ArtifactRef)),a.Id,
    N'AP'+RIGHT(N'00000'+CONVERT(nvarchar(5),ROW_NUMBER() OVER(ORDER BY a.ArtifactRef,a.Id)),5),ROW_NUMBER() OVER(ORDER BY a.ArtifactRef,a.Id),
    DATEADD(minute,-ROW_NUMBER() OVER(ORDER BY a.ArtifactRef,a.Id),CONVERT(datetime2(3),'2026-10-01T10:00:00'))
FROM catalog.Artifacts a JOIN @texts t ON t.ArtifactId=a.Id;
INSERT game.GameRooms(Id,RoomCode,Status,IsShowcase,Visibility,MaxPlayers,TotalRounds,AnswerSeconds,VotingSeconds,CurrentRoundNo,StateVersion,CreatedAt,StartedAt,EndedAt,CompletedAt)
SELECT RoomId,RoomCode,N'COMPLETED',1,N'PUBLIC',3,1,120,90,1,3,DATEADD(minute,-10,CompletedAt),DATEADD(minute,-5,CompletedAt),CompletedAt,CompletedAt FROM @rooms d WHERE NOT EXISTS(SELECT 1 FROM game.GameRooms r WHERE r.Id=d.RoomId);
UPDATE r SET CreatedAt=DATEADD(minute,-10,d.CompletedAt),StartedAt=DATEADD(minute,-5,d.CompletedAt),EndedAt=d.CompletedAt,CompletedAt=d.CompletedAt FROM game.GameRooms r JOIN @rooms d ON d.RoomId=r.Id;
DECLARE @players TABLE(Id uniqueidentifier PRIMARY KEY,RoomId uniqueidentifier,UserId uniqueidentifier,Seat int,Nickname nvarchar(80));
INSERT @players SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-APPRECIATION-PLAYER:'+d.RoomCode+N':'+CONVERT(nvarchar(1),s.Seat))),d.RoomId,u.UserId,s.Seat,u.Nickname
FROM @rooms d CROSS JOIN(VALUES(1),(2),(3)) s(Seat) JOIN @demoUsers u ON u.Ordinal=((d.Ordinal-1)%8)*3+(s.Seat-1+(d.Ordinal-1)/8)%3+1;
INSERT game.GamePlayers(Id,RoomId,UserId,PlayerKey,DisplayName,Role,IsReady,SeatNo,JoinedAt,ConnectionStatus,LastSeenAt,LeftAt)
SELECT p.Id,p.RoomId,p.UserId,N'appreciation-showcase-'+d.RoomCode+N'-'+CONVERT(nvarchar(1),p.Seat),p.Nickname,CASE p.Seat WHEN 1 THEN N'HOST' ELSE N'PLAYER' END,1,p.Seat,DATEADD(minute,-10,d.CompletedAt),N'LEFT',d.CompletedAt,d.CompletedAt
FROM @players p JOIN @rooms d ON d.RoomId=p.RoomId WHERE NOT EXISTS(SELECT 1 FROM game.GamePlayers e WHERE e.Id=p.Id);
UPDATE p SET UserId=d.UserId,DisplayName=d.Nickname,ConnectionStatus=N'LEFT',JoinedAt=DATEADD(minute,-10,r.CompletedAt),LastSeenAt=r.CompletedAt,LeftAt=r.CompletedAt,DisconnectedAt=NULL,ReconnectDeadlineAt=NULL FROM game.GamePlayers p JOIN @players d ON d.Id=p.Id JOIN @rooms r ON r.RoomId=p.RoomId;
INSERT game.GameRounds(Id,RoomId,ArtifactId,RoundNumber,Status,StateVersion,IsSettled,StartedAt,AnswerDeadlineAt,VotingDeadlineAt,SettledAt)
SELECT RoundId,RoomId,ArtifactId,1,N'REVEALED',3,1,DATEADD(minute,-5,CompletedAt),DATEADD(minute,-3,CompletedAt),DATEADD(minute,-1,CompletedAt),CompletedAt FROM @rooms d WHERE NOT EXISTS(SELECT 1 FROM game.GameRounds r WHERE r.Id=d.RoundId);
UPDATE g SET StartedAt=DATEADD(minute,-5,d.CompletedAt),AnswerDeadlineAt=DATEADD(minute,-3,d.CompletedAt),VotingDeadlineAt=DATEADD(minute,-1,d.CompletedAt),SettledAt=d.CompletedAt FROM game.GameRounds g JOIN @rooms d ON d.RoundId=g.Id;
DECLARE @answers TABLE(Id uniqueidentifier PRIMARY KEY,RoundId uniqueidentifier,PlayerId uniqueidentifier,AnswerType nvarchar(30),Text nvarchar(500),SubmittedAt datetime2(3));
INSERT @answers SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-APPRECIATION-ANSWER:'+d.RoomCode+N':'+CONVERT(nvarchar(1),p.Seat))),d.RoundId,p.Id,
CASE p.Seat WHEN 1 THEN N'FACTUAL_REASONING' WHEN 2 THEN N'PLAUSIBLE_FICTION' ELSE N'CREATIVE_TALE' END,
CASE p.Seat WHEN 1 THEN t.Factual WHEN 2 THEN t.Fiction ELSE t.Creative END,DATEADD(second,p.Seat,DATEADD(minute,-4,d.CompletedAt)) FROM @rooms d JOIN @texts t ON t.ArtifactId=d.ArtifactId JOIN @players p ON p.RoomId=d.RoomId;
INSERT game.RoundAnswers(Id,RoundId,GamePlayerId,AnswerType,Text,SubmittedAt) SELECT Id,RoundId,PlayerId,AnswerType,Text,SubmittedAt FROM @answers d WHERE NOT EXISTS(SELECT 1 FROM game.RoundAnswers a WHERE a.Id=d.Id);
UPDATE a SET Text=d.Text,SubmittedAt=d.SubmittedAt FROM game.RoundAnswers a JOIN @answers d ON d.Id=a.Id;
INSERT game.Votes(Id,RoundId,VoterGamePlayerId,AnswerId,Count,SubmittedAt)
SELECT CONVERT(uniqueidentifier,HASHBYTES('MD5',N'QMAH-APPRECIATION-VOTE:'+CONVERT(nvarchar(36),a.Id))),a.RoundId,v.Id,a.Id,1+ABS(CHECKSUM(a.Id)%3),DATEADD(minute,2,a.SubmittedAt)
FROM @answers a JOIN @players p ON p.Id=a.PlayerId JOIN @players v ON v.RoomId=p.RoomId AND v.Seat=p.Seat%3+1 WHERE NOT EXISTS(SELECT 1 FROM game.Votes e WHERE e.AnswerId=a.Id AND e.VoterGamePlayerId=v.Id);
UPDATE v SET SubmittedAt=DATEADD(minute,2,a.SubmittedAt) FROM game.Votes v JOIN @answers a ON a.Id=v.AnswerId;
-- 鑑賞票與回合內投票分開。會員只投別人的回答，票數可形成排序，固定鍵確保重跑不重複。
INSERT catalog.ArtifactAppreciationVotes(AnswerId,UserId,CreatedAt)
SELECT a.Id,u.UserId,DATEADD(minute,10,r.CompletedAt) FROM @answers a JOIN @players p ON p.Id=a.PlayerId JOIN @rooms r ON r.RoomId=p.RoomId JOIN @demoUsers u ON (u.Ordinal-r.Ordinal%24+23)%24 < 2+ABS(CHECKSUM(a.Id)%7)
WHERE u.UserId<>p.UserId AND NOT EXISTS(SELECT 1 FROM catalog.ArtifactAppreciationVotes v WHERE v.AnswerId=a.Id AND v.UserId=u.UserId);
UPDATE p SET DisplayName=u.Nickname FROM game.GamePlayers p JOIN @demoUsers u ON u.UserId=p.UserId;
UPDATE r SET IsShowcase=1 FROM game.GameRooms r WHERE r.RoomCode IN(N'SHOW301',N'SHOW302',N'SHOW303',N'SHOW304',N'SHOW305',N'SHOW306',N'SHOW307',N'SHOW308',N'QMAH-RWD-01');
-- 舊種子沒有回合的場次不能宣稱已完成，改為已取消並保留紀錄。
UPDATE r SET Status=N'CANCELLED' FROM game.GameRooms r WHERE r.IsShowcase=1 AND r.Status=N'COMPLETED' AND NOT EXISTS(SELECT 1 FROM game.GameRounds g WHERE g.RoomId=r.Id);
IF EXISTS(SELECT 1 FROM @answers a JOIN game.RoundAnswers e ON e.Id=a.Id JOIN game.GamePlayers p ON p.Id=e.GamePlayerId JOIN game.GameRounds g ON g.Id=e.RoundId WHERE p.RoomId<>g.RoomId) THROW 51000,N'回答與玩家不屬於同一場次。',1;
IF EXISTS(SELECT 1 FROM game.Votes v JOIN @answers a ON a.Id=v.AnswerId JOIN game.GamePlayers p ON p.Id=v.VoterGamePlayerId JOIN game.GamePlayers author ON author.Id=a.PlayerId WHERE p.RoomId<>author.RoomId OR p.Id=author.Id OR v.RoundId<>a.RoundId) THROW 51000,N'回合投票關聯不一致。',1;
IF EXISTS(SELECT 1 FROM @demoUsers u OUTER APPLY(SELECT COUNT(*) n FROM @players p WHERE p.UserId=u.UserId) x WHERE x.n<>64) THROW 51000,N'遊戲紀錄未平均分配。',1;
