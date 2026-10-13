/* db-v1.0.7：整理貼文牆時間序，並替會員隨機加收藏。本機與遠端套用同一份，可重複執行。

   貼文牆預設新到舊，展示時往下滑要看到各種不同的貼文，所以重排前面約 60 篇的發文時間：
   - 各看板輪流穿插（文物討論、鑑賞心得、探索、研究筆記、問答、遊戲、商城、綜合），有圖的貼文優先，畫面比較豐富。
   - 活動貼文平均分散，不再連續十幾篇都是活動。
   - 「67」「676767」與幾篇廣告、洗版的犯規貼文排進前 15 篇內，展示審查功能時不用翻很久。
   - 館長今天發的貼文固定在最上面。
   留言有 2 位以上不同會員參與的貼文不動，所以熱門貼文的排名不受影響。移動的貼文，其留言時間同步平移，不會出現留言早於貼文。

   收藏：館長帳號收藏 14 篇（各看板都有），其他展示會員各隨機收藏約 5～9 篇。

   已套用過會直接跳過（第一名固定在 2026-10-13 13:30:00 這個時間）。 */
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF EXISTS (SELECT 1 FROM social.SocialPosts WHERE CreatedAt = '2026-10-13 13:30:00')
BEGIN
    PRINT N'貼文牆時間序已整理過，略過重排。';
END
ELSE
BEGIN
    BEGIN TRAN;

    SELECT p.Id, p.CreatedAt AS OrigAt, p.BoardCode, ABS(CHECKSUM(p.Id, N'arrange')) AS H,
           CASE
               WHEN p.Title = N'分享今天的一點心得' THEN 'PIN'
               WHEN p.PostType = 'EVENT' THEN 'EVENT'
               WHEN p.Title LIKE N'%低價出清%' OR p.Title LIKE N'%限時搶購%' OR p.Title LIKE N'%幫我衝人氣%' OR p.Title LIKE N'%一直發%' OR p.Title LIKE N'%67%' THEN 'SPAM'
               ELSE 'NORM'
           END AS Cat,
           CASE WHEN EXISTS (SELECT 1 FROM social.MediaAssets m WHERE m.PostId = p.Id AND m.Status = 'ACTIVE') THEN 0 ELSE 1 END AS NoImg,
           (SELECT COUNT(DISTINCT c.UserId) FROM social.SocialComments c
             WHERE c.PostId = p.Id AND c.Status = 'PUBLISHED' AND c.UserId <> p.UserId) AS Part
    INTO #c
    FROM social.SocialPosts p
    WHERE p.Status = 'PUBLISHED';

    -- 挑出要重排的貼文
    SELECT Id, OrigAt, Cat, H,
           CAST(CASE Cat
                    WHEN 'EVENT' THEN ROW_NUMBER() OVER (PARTITION BY Cat ORDER BY H) * 3.4
                    WHEN 'SPAM'  THEN 3 + ROW_NUMBER() OVER (PARTITION BY Cat ORDER BY H) * 2.6
                    ELSE ROW_NUMBER() OVER (PARTITION BY Cat ORDER BY RnB, H)
                END AS float) AS SlotKey
    INTO #pick
    FROM (
        SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.BoardCode ORDER BY c.NoImg, c.H) AS RnB
        FROM #c c
        WHERE c.Part < 2
          AND (c.Cat IN ('PIN', 'SPAM')
               OR (c.Cat = 'EVENT' AND EXISTS (SELECT 1 FROM social.SocialPosts p JOIN social.Events e ON e.Id = p.EventId
                                               WHERE p.Id = c.Id AND e.StartAt >= '2026-10-14'))
               OR (c.Cat = 'NORM' AND c.OrigAt < '2026-10-12' AND c.BoardCode IN ('CATALOG','REVIEW','DISCOVERY','GUIDE','QUESTION','GAME','STORE','GENERAL')))
    ) x
    WHERE (x.Cat = 'NORM' AND x.RnB <= 6)
       OR x.Cat IN ('PIN', 'SPAM', 'EVENT');

    -- 活動貼文只取 12 篇、一般貼文只取 40 篇，其餘維持原時間
    DELETE FROM #pick WHERE Cat = 'EVENT' AND SlotKey > 12 * 3.4 + 0.01;
    DELETE FROM #pick WHERE Cat = 'NORM' AND SlotKey > 40;

    SELECT Id, OrigAt,
           CASE WHEN Cat = 'PIN' THEN CAST('2026-10-13 14:53:00' AS datetime2(3))
                ELSE DATEADD(MINUTE, -(ROW_NUMBER() OVER (ORDER BY SlotKey, H) - 1) * 37, CAST('2026-10-13 13:30:00' AS datetime2(3)))
           END AS NewAt, Cat
    INTO #order
    FROM #pick
    WHERE Cat <> 'PIN';

    -- 留言先平移（要在貼文時間更新之前，才讀得到原貼文時間）；上限卡在 2026-10-13 14:00，且不早於貼文。
    UPDATE cm
    SET CreatedAt = (SELECT GREATEST(LEAST(DATEADD(MINUTE, DATEDIFF(MINUTE, o.OrigAt, cm.CreatedAt), o.NewAt), CAST('2026-10-13 14:00:00' AS datetime2(3))), o.NewAt)),
        UpdatedAt = (SELECT GREATEST(LEAST(DATEADD(MINUTE, DATEDIFF(MINUTE, o.OrigAt, cm.CreatedAt), o.NewAt), CAST('2026-10-13 14:00:00' AS datetime2(3))), o.NewAt))
    FROM social.SocialComments cm
    JOIN #order o ON o.Id = cm.PostId;

    UPDATE p SET CreatedAt = o.NewAt, UpdatedAt = o.NewAt
    FROM social.SocialPosts p JOIN #order o ON o.Id = p.Id;

    DROP TABLE #order; DROP TABLE #pick; DROP TABLE #c;
    COMMIT TRAN;
END;

-- 收藏：館長 14 篇（各看板），其他展示會員各約 5～9 篇。
IF OBJECT_ID(N'social.PostBookmarks', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM social.PostBookmarks b JOIN [user].AspNetUsers u ON u.Id = b.UserId WHERE u.Email = 'admin@qmah.local')
BEGIN
    INSERT INTO social.PostBookmarks (UserId, PostId, CreatedAt)
    SELECT a.Id, x.Id, DATEADD(MINUTE, -(x.Rn * 53), '2026-10-13 12:00:00')
    FROM [user].AspNetUsers a
    CROSS APPLY (
        SELECT TOP (14) y.Id, ROW_NUMBER() OVER (ORDER BY y.Ord, y.H) AS Rn
        FROM (
            SELECT p.Id, ABS(CHECKSUM(p.Id, N'admin-mark')) AS H,
                   CASE p.BoardCode WHEN 'CATALOG' THEN 0 WHEN 'REVIEW' THEN 1 WHEN 'DISCOVERY' THEN 2 WHEN 'GUIDE' THEN 3
                                    WHEN 'QUESTION' THEN 4 WHEN 'GAME' THEN 5 WHEN 'EVENTS' THEN 6 WHEN 'EVENT' THEN 6 ELSE 7 END AS Ord,
                   ROW_NUMBER() OVER (PARTITION BY p.BoardCode ORDER BY ABS(CHECKSUM(p.Id, N'admin-mark'))) AS RnB
            FROM social.SocialPosts p
            WHERE p.Status = 'PUBLISHED' AND p.UserId <> a.Id AND p.Title NOT LIKE N'%67%' AND p.Title NOT LIKE N'%出清%' AND p.Title NOT LIKE N'%搶購%'
        ) y
        WHERE y.RnB <= 3
        ORDER BY y.Ord, y.H
    ) x
    WHERE a.Email = 'admin@qmah.local';

    INSERT INTO social.PostBookmarks (UserId, PostId, CreatedAt)
    SELECT u.Id, p.Id, DATEADD(MINUTE, -(ABS(CHECKSUM(u.Id, p.Id, N't')) % 2400), '2026-10-13 12:00:00')
    FROM [user].AspNetUsers u
    JOIN social.SocialPosts p ON p.Status = 'PUBLISHED' AND p.UserId <> u.Id AND p.Title NOT LIKE N'%67%' AND p.Title NOT LIKE N'%出清%' AND p.Title NOT LIKE N'%搶購%'
    WHERE u.Email <> 'admin@qmah.local' AND (u.Email LIKE '%@qmah.test' OR u.Email LIKE '%@qmah.local')
      AND ABS(CHECKSUM(u.Id, p.Id, N'mark')) % 50 < 2
      AND NOT EXISTS (SELECT 1 FROM social.PostBookmarks b WHERE b.UserId = u.Id AND b.PostId = p.Id);
END;

-- 展示用：「676767」固定排在最新一篇（比館長的貼文還新），方便一打開就看到審查範例。
UPDATE social.SocialPosts SET CreatedAt = '2026-10-13 15:30:00', UpdatedAt = '2026-10-13 15:30:00'
WHERE Status = 'PUBLISHED' AND Title = N'676767' AND CreatedAt <> '2026-10-13 15:30:00';
UPDATE c SET CreatedAt = '2026-10-13 15:35:00', UpdatedAt = '2026-10-13 15:35:00'
FROM social.SocialComments c JOIN social.SocialPosts p ON p.Id = c.PostId
WHERE p.Title = N'676767' AND p.Status = 'PUBLISHED' AND c.CreatedAt < p.CreatedAt;
