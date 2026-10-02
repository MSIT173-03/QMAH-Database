-- 餘額只反映本次整組移轉造成的流水差額，原始期初餘額保持不變。
DECLARE @pointAfter TABLE(UserId uniqueidentifier PRIMARY KEY,Amount bigint);
INSERT @pointAfter SELECT u.UserId,COALESCE(SUM(CONVERT(bigint,t.Amount)),0) FROM @demoUsers u LEFT JOIN store.PointTransactions t ON t.UserId=u.UserId GROUP BY u.UserId;
UPDATE b SET Balance=b.Balance+a.Amount-p.Amount FROM store.PointBalances b JOIN @pointBefore p ON p.UserId=b.UserId JOIN @pointAfter a ON a.UserId=b.UserId;
DECLARE @keyAfter TABLE(UserId uniqueidentifier,KeyDefinitionId uniqueidentifier,Amount bigint,PRIMARY KEY(UserId,KeyDefinitionId));
INSERT @keyAfter SELECT u.UserId,k.Id,COALESCE(SUM(CONVERT(bigint,t.Amount)),0) FROM @demoUsers u CROSS JOIN catalog.KeyDefinitions k LEFT JOIN catalog.KeyTransactions t ON t.UserId=u.UserId AND t.KeyDefinitionId=k.Id GROUP BY u.UserId,k.Id;
UPDATE b SET Balance=b.Balance+a.Amount-p.Amount FROM catalog.UserKeyBalances b JOIN @keyBefore p ON p.UserId=b.UserId AND p.KeyDefinitionId=b.KeyDefinitionId JOIN @keyAfter a ON a.UserId=b.UserId AND a.KeyDefinitionId=b.KeyDefinitionId;
DECLARE @progressAfter TABLE(UserId uniqueidentifier PRIMARY KEY,Amount decimal(18,2));
INSERT @progressAfter SELECT u.UserId,COALESCE(SUM(t.Amount),0) FROM @demoUsers u LEFT JOIN catalog.KeyProgressTransactions t ON t.UserId=u.UserId GROUP BY u.UserId;
UPDATE b SET Balance=b.Balance+a.Amount-p.Amount FROM catalog.KeyProgressBalances b JOIN @progressBefore p ON p.UserId=b.UserId JOIN @progressAfter a ON a.UserId=b.UserId;
IF EXISTS(SELECT 1 FROM @identityBefore b FULL JOIN [user].AspNetUsers u ON u.Id=b.Id WHERE b.Id IS NULL OR u.Id IS NULL OR b.Signature<>HASHBYTES('SHA2_256',CONCAT(u.Email,N'|',u.NormalizedEmail,N'|',u.UserName,N'|',u.NormalizedUserName,N'|',u.PasswordHash,N'|',u.SecurityStamp,N'|',u.ConcurrencyStamp))) THROW 51000,N'登入資料發生變更，已停止更新。',1;
IF EXISTS(SELECT UserId,RoleId FROM @rolesBefore EXCEPT SELECT UserId,RoleId FROM [user].AspNetUserRoles) OR EXISTS(SELECT UserId,RoleId FROM [user].AspNetUserRoles EXCEPT SELECT UserId,RoleId FROM @rolesBefore) THROW 51000,N'帳號角色發生變更，已停止更新。',1;
COMMIT TRANSACTION;
SELECT N'關聯種子資料完成' Result,(SELECT COUNT(*) FROM @demoUsers) Accounts,(SELECT COUNT(*) FROM @rooms) Rooms,(SELECT COUNT(*) FROM @answers) Answers;
