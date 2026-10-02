SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ownsTransaction bit = 0;
IF @@TRANCOUNT = 0
BEGIN
    BEGIN TRANSACTION;
    SET @ownsTransaction = 1;
END;

BEGIN TRY
    DROP TABLE IF EXISTS #achievementDemoUsers;
    CREATE TABLE #achievementDemoUsers(UserId uniqueidentifier PRIMARY KEY);
    INSERT #achievementDemoUsers(UserId)
    SELECT Id
    FROM [user].AspNetUsers
    WHERE Status=N'ACTIVE' AND (Email LIKE N'%@qmah.local' OR Email LIKE N'%@qmah.test');
    IF (SELECT COUNT(*) FROM #achievementDemoUsers)<>24
        THROW 51000,N'成就整理僅允許 24 個展示帳號，帳號範圍不符，未修改資料。',1;

    -- 依服務排行榜規則重建每局及每回合勝者：票數、提交時間、回答 Id。
    SELECT a.Id AnswerId,a.RoundId,a.GamePlayerId,a.SubmittedAt,
        SUM(CONVERT(bigint,CASE WHEN v.Count>0 THEN v.Count ELSE 0 END)) VoteCount
    INTO #achievementAnswerVotes
    FROM game.RoundAnswers a
    JOIN game.GameRounds g ON g.Id=a.RoundId AND g.IsSettled=1
    LEFT JOIN game.Votes v ON v.AnswerId=a.Id
    GROUP BY a.Id,a.RoundId,a.GamePlayerId,a.SubmittedAt;

    ;WITH RankedAnswers AS
    (
        SELECT *,ROW_NUMBER() OVER(PARTITION BY RoundId ORDER BY VoteCount DESC,SubmittedAt,AnswerId) WinnerOrder
        FROM #achievementAnswerVotes
    )
    SELECT winner.RoundId,winner.GamePlayerId,winner.VoteCount
    INTO #achievementRoundWinners
    FROM RankedAnswers winner
    JOIN game.GameRounds g ON g.Id=winner.RoundId AND g.IsSettled=1 AND g.Status=N'REVEALED'
    WHERE winner.WinnerOrder=1 AND winner.VoteCount>0;

    SELECT p.UserId,p.RoomId,p.Id GamePlayerId,p.DisplayName,r.CompletedAt,
        SUM(COALESCE(v.VoteCount,CONVERT(bigint,0))) TotalScore,
        COUNT(DISTINCT a.Id) RoundsAnswered,
        COUNT(DISTINCT rw.RoundId) RoundsWon
    INTO #achievementRoomPlayerStats
    FROM game.GamePlayers p
    JOIN #achievementDemoUsers demo ON demo.UserId=p.UserId
    JOIN game.GameRooms r ON r.Id=p.RoomId AND r.Status=N'COMPLETED' AND r.CompletedAt IS NOT NULL
    LEFT JOIN game.GameRounds g ON g.RoomId=r.Id AND g.IsSettled=1
    LEFT JOIN game.RoundAnswers a ON a.RoundId=g.Id AND a.GamePlayerId=p.Id
    LEFT JOIN #achievementAnswerVotes v ON v.AnswerId=a.Id
    LEFT JOIN #achievementRoundWinners rw ON rw.RoundId=g.Id AND rw.GamePlayerId=p.Id
    WHERE EXISTS(SELECT 1 FROM game.GameRounds settled WHERE settled.RoomId=r.Id AND settled.IsSettled=1)
    GROUP BY p.UserId,p.RoomId,p.Id,p.DisplayName,r.CompletedAt;

    ;WITH RankedPlayers AS
    (
        SELECT *,ROW_NUMBER() OVER(PARTITION BY RoomId ORDER BY TotalScore DESC,RoundsWon DESC,RoundsAnswered DESC,
            DisplayName COLLATE Latin1_General_100_BIN2 ASC,GamePlayerId ASC) FinalRank
        FROM #achievementRoomPlayerStats
    )
    SELECT * INTO #achievementRoomWinners FROM RankedPlayers WHERE FinalRank=1;

    ;WITH Completed AS
    (
        SELECT p.UserId,r.Id RoomId,r.CompletedAt,
            ROW_NUMBER() OVER(PARTITION BY p.UserId ORDER BY r.CompletedAt,r.Id) Ordinal
        FROM game.GamePlayers p
        JOIN #achievementDemoUsers demo ON demo.UserId=p.UserId
        JOIN game.GameRooms r ON r.Id=p.RoomId AND r.Status=N'COMPLETED' AND r.CompletedAt IS NOT NULL
        WHERE EXISTS(SELECT 1 FROM game.GameRounds g WHERE g.RoomId=r.Id AND g.IsSettled=1)
    )
    SELECT * INTO #achievementCompletedOrdinals FROM Completed;

    ;WITH Wins AS
    (
        SELECT stats.UserId,stats.RoomId,stats.CompletedAt,
            ROW_NUMBER() OVER(PARTITION BY stats.UserId ORDER BY stats.CompletedAt,stats.RoomId) Ordinal
        FROM #achievementRoomWinners stats
    )
    SELECT * INTO #achievementGameWinOrdinals FROM Wins;

    ;WITH Wins AS
    (
        SELECT p.UserId,w.RoundId,g.SettledAt,
            ROW_NUMBER() OVER(PARTITION BY p.UserId ORDER BY g.SettledAt,w.RoundId) Ordinal
        FROM #achievementRoundWinners w
        JOIN game.GameRounds g ON g.Id=w.RoundId
        JOIN game.GamePlayers p ON p.Id=w.GamePlayerId
        JOIN #achievementDemoUsers demo ON demo.UserId=p.UserId
        WHERE g.SettledAt IS NOT NULL
    )
    SELECT * INTO #achievementRoundWinOrdinals FROM Wins;

    ;WITH Eligible AS
    (
        SELECT ua.UserId,ach.ConditionType,
            attempt.CompletedAt QualifiedAt,
            attempt.Id TieId
        FROM [user].UserAchievements ua
        JOIN #achievementDemoUsers demo ON demo.UserId=ua.UserId
        JOIN [user].Achievements ach ON ach.Id=ua.AchievementId
        JOIN game.MiniGameAttempts attempt ON attempt.UserId=ua.UserId AND attempt.Status=N'COMPLETED'
        LEFT JOIN game.GameModeDefinitions mode ON mode.Id=attempt.GameModeDefinitionId
        WHERE ach.ConditionType IN(N'MINIGAME_ARTIFACT_PUZZLE_COUNT',N'MINIGAME_DETAIL_LOCATOR_COUNT',N'MINIGAME_MEMORY_MATCH_COUNT',N'MINIGAME_STRIP_RESTORE_COUNT',N'MINIGAME_GRADE_S_COUNT')
            AND attempt.CompletedAt IS NOT NULL
            AND ((ach.ConditionType=N'MINIGAME_GRADE_S_COUNT' AND attempt.Grade=N'S') OR
                (ach.ConditionType<>N'MINIGAME_GRADE_S_COUNT' AND mode.Code=CASE ach.ConditionType
                    WHEN N'MINIGAME_ARTIFACT_PUZZLE_COUNT' THEN N'ARTIFACT_PUZZLE'
                    WHEN N'MINIGAME_DETAIL_LOCATOR_COUNT' THEN N'DETAIL_LOCATOR'
                    WHEN N'MINIGAME_MEMORY_MATCH_COUNT' THEN N'MEMORY_MATCH'
                    WHEN N'MINIGAME_STRIP_RESTORE_COUNT' THEN N'STRIP_RESTORE' END))
    ), Ranked AS
    (
        SELECT *,ROW_NUMBER() OVER(PARTITION BY UserId,ConditionType ORDER BY QualifiedAt,TieId) Ordinal
        FROM Eligible
    )
    SELECT UserId,ConditionType,Ordinal,QualifiedAt INTO #achievementMiniGameOrdinals FROM Ranked;

    CREATE TABLE #achievementRepairs
    (
        UserAchievementId uniqueidentifier PRIMARY KEY,
        ConditionType nvarchar(40) NOT NULL,
        ThresholdValue bigint NOT NULL,
        PreviousAchievedAt datetime2(3) NOT NULL,
        NewAchievedAt datetime2(3) NULL
    );

    INSERT #achievementRepairs(UserAchievementId,ConditionType,ThresholdValue,PreviousAchievedAt,NewAchievedAt)
    SELECT ua.Id,a.ConditionType,a.ThresholdValue,ua.AchievedAt,
        CASE WHEN crossing.QualifiedAt IS NULL THEN NULL
            WHEN crossing.QualifiedAt<a.CreatedAt THEN a.CreatedAt ELSE crossing.QualifiedAt END
    FROM [user].UserAchievements ua
    JOIN #achievementDemoUsers demo ON demo.UserId=ua.UserId
    JOIN [user].Achievements a ON a.Id=ua.AchievementId
    OUTER APPLY
    (
        SELECT CASE a.ConditionType
            WHEN N'EVENT_HOST_COUNT' THEN
                (SELECT item.QualifiedAt FROM
                    (SELECT COALESCE(e.ReviewedAt,e.CreatedAt) QualifiedAt,
                        ROW_NUMBER() OVER(ORDER BY COALESCE(e.ReviewedAt,e.CreatedAt),e.Id) Ordinal
                     FROM social.Events e
                     WHERE e.OrganizerUserId=ua.UserId AND e.ReviewStatus=N'APPROVED' AND e.PublishStatus=N'PUBLISHED') item
                 WHERE item.Ordinal=a.ThresholdValue)
            WHEN N'EVENT_JOIN_COUNT' THEN
                (SELECT item.QualifiedAt FROM
                    (SELECT er.RegisteredAt QualifiedAt,
                        ROW_NUMBER() OVER(ORDER BY er.RegisteredAt,er.Id) Ordinal
                     FROM social.EventRegistrations er
                     WHERE er.UserId=ua.UserId AND er.Status IN(N'REGISTERED',N'ATTENDED')) item
                 WHERE item.Ordinal=a.ThresholdValue)
            WHEN N'GAME_COMPLETE_COUNT' THEN
                (SELECT item.CompletedAt FROM #achievementCompletedOrdinals item
                 WHERE item.UserId=ua.UserId AND item.Ordinal=a.ThresholdValue)
            WHEN N'GAME_WIN_COUNT' THEN
                (SELECT item.CompletedAt FROM #achievementGameWinOrdinals item
                 WHERE item.UserId=ua.UserId AND item.Ordinal=a.ThresholdValue)
            WHEN N'GAME_ROUND_WIN_COUNT' THEN
                (SELECT item.SettledAt FROM #achievementRoundWinOrdinals item
                 WHERE item.UserId=ua.UserId AND item.Ordinal=a.ThresholdValue)
            WHEN N'MINIGAME_ARTIFACT_PUZZLE_COUNT' THEN
                (SELECT item.QualifiedAt FROM #achievementMiniGameOrdinals item
                 WHERE item.UserId=ua.UserId AND item.ConditionType=N'MINIGAME_ARTIFACT_PUZZLE_COUNT' AND item.Ordinal=a.ThresholdValue)
            WHEN N'MINIGAME_DETAIL_LOCATOR_COUNT' THEN
                (SELECT item.QualifiedAt FROM #achievementMiniGameOrdinals item
                 WHERE item.UserId=ua.UserId AND item.ConditionType=N'MINIGAME_DETAIL_LOCATOR_COUNT' AND item.Ordinal=a.ThresholdValue)
            WHEN N'MINIGAME_MEMORY_MATCH_COUNT' THEN
                (SELECT item.QualifiedAt FROM #achievementMiniGameOrdinals item
                 WHERE item.UserId=ua.UserId AND item.ConditionType=N'MINIGAME_MEMORY_MATCH_COUNT' AND item.Ordinal=a.ThresholdValue)
            WHEN N'MINIGAME_STRIP_RESTORE_COUNT' THEN
                (SELECT item.QualifiedAt FROM #achievementMiniGameOrdinals item
                 WHERE item.UserId=ua.UserId AND item.ConditionType=N'MINIGAME_STRIP_RESTORE_COUNT' AND item.Ordinal=a.ThresholdValue)
            WHEN N'MINIGAME_GRADE_S_COUNT' THEN
                (SELECT item.QualifiedAt FROM #achievementMiniGameOrdinals item
                 WHERE item.UserId=ua.UserId AND item.ConditionType=N'MINIGAME_GRADE_S_COUNT' AND item.Ordinal=a.ThresholdValue)
        END QualifiedAt
    ) crossing
    WHERE a.ConditionType IN
    (
        N'EVENT_HOST_COUNT',N'EVENT_JOIN_COUNT',N'GAME_COMPLETE_COUNT',N'GAME_WIN_COUNT',N'GAME_ROUND_WIN_COUNT',
        N'MINIGAME_ARTIFACT_PUZZLE_COUNT',N'MINIGAME_DETAIL_LOCATOR_COUNT',N'MINIGAME_MEMORY_MATCH_COUNT',
        N'MINIGAME_STRIP_RESTORE_COUNT',N'MINIGAME_GRADE_S_COUNT'
    );

    IF EXISTS
    (
        SELECT 1 FROM #achievementRepairs repair
        JOIN [user].EquippedTitles equipped ON equipped.UserAchievementId=repair.UserAchievementId
        WHERE repair.NewAchievedAt IS NULL
    )
        THROW 51000,N'有待移除的未達標成就仍裝備中，已停止且未提交本次整理。',1;

    UPDATE ua SET AchievedAt=repair.NewAchievedAt,
        DisplayedAt=CASE WHEN ua.IsDisplayed=1 AND ua.DisplayedAt<repair.NewAchievedAt
            THEN repair.NewAchievedAt ELSE ua.DisplayedAt END
    FROM [user].UserAchievements ua
    JOIN #achievementRepairs repair ON repair.UserAchievementId=ua.Id
    WHERE repair.NewAchievedAt IS NOT NULL AND ua.AchievedAt<>repair.NewAchievedAt;

    DELETE ua
    FROM [user].UserAchievements ua
    JOIN #achievementRepairs repair ON repair.UserAchievementId=ua.Id
    WHERE repair.NewAchievedAt IS NULL;

    IF EXISTS
    (
        SELECT 1 FROM #achievementRepairs repair
        JOIN [user].UserAchievements ua ON ua.Id=repair.UserAchievementId
        WHERE repair.NewAchievedAt IS NOT NULL AND ua.AchievedAt<>repair.NewAchievedAt
    )
        THROW 51000,N'成就日期整理後驗證失敗。',1;
    IF EXISTS
    (
        SELECT 1 FROM #achievementRepairs repair
        JOIN [user].UserAchievements ua ON ua.Id=repair.UserAchievementId
        WHERE repair.NewAchievedAt IS NULL
    )
        THROW 51000,N'仍有未達標成就沒有移除。',1;

    SELECT ConditionType,COUNT(*) CheckedRows,
        SUM(CASE WHEN NewAchievedAt IS NULL THEN 1 ELSE 0 END) RemovedRows,
        SUM(CASE WHEN NewAchievedAt IS NOT NULL THEN 1 ELSE 0 END) QualifiedRows,
        SUM(CASE WHEN NewAchievedAt IS NOT NULL AND PreviousAchievedAt<>NewAchievedAt THEN 1 ELSE 0 END) DatesCorrected
    FROM #achievementRepairs
    GROUP BY ConditionType
    ORDER BY ConditionType;

    IF @ownsTransaction=1 COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @ownsTransaction=1 AND @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
