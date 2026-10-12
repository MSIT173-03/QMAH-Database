-- db-v1.0.2：展示用管理員／功能帳號補發點數與鑰匙，並寫入對應流水。重跑不會重複補發。
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @admin uniqueidentifier = (SELECT Id FROM [user].AspNetUsers WHERE Email = N'admin@qmah.local');
DECLARE @universal uniqueidentifier = (SELECT Id FROM catalog.KeyDefinitions WHERE Code = N'KEY-UNIVERSAL');
DECLARE @normal uniqueidentifier = (SELECT Id FROM catalog.KeyDefinitions WHERE Code = N'KEY-NORMAL');
DECLARE @now datetime2 = SYSUTCDATETIME();
DECLARE @t TABLE (Email nvarchar(256), Points int, Universal int, Normal int);
INSERT @t VALUES
 (N'admin@qmah.local', 200000, 999, 999),
 (N'user@qmah.local', 80000, 300, 600),
 (N'store@qmah.local', 80000, 300, 600),
 (N'game@qmah.local', 80000, 300, 600),
 (N'social@qmah.local', 80000, 300, 600),
 (N'catalog@qmah.local', 80000, 300, 600),
 (N'player-a@qmah.local', 50000, 150, 300),
 (N'player-b@qmah.local', 50000, 150, 300);

DECLARE @g TABLE (UserId uniqueidentifier, Points int, Universal int, Normal int);
INSERT @g
SELECT u.Id, t.Points, t.Universal, t.Normal
FROM @t t JOIN [user].AspNetUsers u ON u.Email = t.Email
WHERE NOT EXISTS (SELECT 1 FROM store.PointTransactions p WHERE p.UserId = u.Id AND p.ReferenceType = N'DEMO_ECONOMY_V102');

-- 點數：兩筆流水（展示儲值加碼、活動補發），金額合計等於 Points。
INSERT store.PointTransactions (Id, UserId, Amount, Reason, ReferenceType, ReferenceId, CreatedAt, CreatedByAdminUserId)
SELECT NEWID(), UserId, Points * 7 / 10, N'展示資料：管理員補發展示點數', N'DEMO_ECONOMY_V102', NULL, @now, @admin FROM @g
UNION ALL
SELECT NEWID(), UserId, Points - Points * 7 / 10, N'展示資料：活動獎勵補發', N'DEMO_ECONOMY_V102', NULL, DATEADD(SECOND, 1, @now), @admin FROM @g;

MERGE store.PointBalances AS b
USING (SELECT UserId, Points FROM @g) s ON b.UserId = s.UserId
WHEN MATCHED THEN UPDATE SET Balance = b.Balance + s.Points, UpdatedAt = @now
WHEN NOT MATCHED THEN INSERT (UserId, Balance, UpdatedAt) VALUES (s.UserId, s.Points, @now);

-- 鑰匙：萬能鑰匙與一般鑰匙各一筆 ADMIN_GRANT 流水。
INSERT catalog.KeyTransactions (Id, UserId, KeyDefinitionId, Amount, Reason, ReferenceType, ReferenceId, CreatedAt, CreatedByAdminUserId)
SELECT NEWID(), UserId, @universal, Universal, N'ADMIN_GRANT', N'DEMO_ECONOMY_V102', NULL, @now, @admin FROM @g
UNION ALL
SELECT NEWID(), UserId, @normal, Normal, N'ADMIN_GRANT', N'DEMO_ECONOMY_V102', NULL, @now, @admin FROM @g;

MERGE catalog.UserKeyBalances AS b
USING (SELECT UserId, @universal AS KeyId, Universal AS Amt FROM @g UNION ALL SELECT UserId, @normal, Normal FROM @g) s
  ON b.UserId = s.UserId AND b.KeyDefinitionId = s.KeyId
WHEN MATCHED THEN UPDATE SET Balance = b.Balance + s.Amt, UpdatedAt = @now
WHEN NOT MATCHED THEN INSERT (UserId, KeyDefinitionId, Balance, UpdatedAt) VALUES (s.UserId, s.KeyId, s.Amt, @now);
COMMIT;
