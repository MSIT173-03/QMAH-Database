/* 呼叫端提供 @demoUsers(UserId,Ordinal,Nickname)，並負責交易的開始、回復與提交。 */
SET NOCOUNT ON;

IF (SELECT COUNT(*) FROM @demoUsers) <> 24
    THROW 51020, N'關聯修復需要恰好 24 個展示帳號。', 1;
IF EXISTS (SELECT 1 FROM @demoUsers WHERE Ordinal < 1 OR Ordinal > 24)
   OR (SELECT COUNT(DISTINCT Ordinal) FROM @demoUsers) <> 24
    THROW 51021, N'展示帳號序號必須是 1 至 24 的唯一值。', 1;
IF EXISTS (
    SELECT 1
    FROM @demoUsers d
    LEFT JOIN [user].AspNetUsers u ON u.Id = d.UserId
    LEFT JOIN [user].UserProfiles p ON p.UserId = d.UserId
    WHERE u.Id IS NULL OR u.Status <> N'ACTIVE' OR p.UserId IS NULL OR p.Nickname <> d.Nickname
)
    THROW 51022, N'展示帳號識別、啟用狀態或暱稱與來源資料不符。', 1;

DECLARE @csOrders TABLE
(
    OrderId uniqueidentifier NOT NULL PRIMARY KEY,
    OrderNo nvarchar(30) NOT NULL UNIQUE,
    NewUserId uniqueidentifier NOT NULL,
    Ordinal int NOT NULL
);
INSERT @csOrders (OrderId, OrderNo, NewUserId, Ordinal)
SELECT o.Id, o.OrderNo, d.UserId, d.Ordinal
FROM
(
    SELECT Id, OrderNo, ROW_NUMBER() OVER (ORDER BY OrderNo, Id) AS RowNo
    FROM store.StoreOrders
    WHERE OrderNo LIKE N'QMAH-GEN-%'
) o
JOIN @demoUsers d ON d.Ordinal = ((o.RowNo - 1) % 24) + 1;

IF (SELECT COUNT(*) FROM @csOrders) <> 170
    THROW 51023, N'預期恰好有 170 筆展示生成訂單。', 1;
IF EXISTS (
    SELECT 1 FROM @csOrders c
    JOIN store.StoreOrders o ON o.Id = c.OrderId
    WHERE o.OrderNo NOT LIKE N'QMAH-GEN-%'
)
    THROW 51024, N'關聯修復期間展示訂單範圍發生變化。', 1;

IF EXISTS (
    SELECT 1
    FROM @csOrders c
    JOIN store.StoreOrders o ON o.Id = c.OrderId
    LEFT JOIN store.OrderDetails d ON d.OrderId = o.Id
    GROUP BY c.OrderId, o.Subtotal, o.DiscountAmount, o.PointsUsed, o.TotalAmount, o.ShippingFee
    HAVING COUNT(d.Id) = 0
        OR SUM(d.LineTotal) <> o.Subtotal
        OR o.TotalAmount <> ((o.Subtotal - o.DiscountAmount) - o.PointsUsed + o.ShippingFee)
)
    THROW 51025, N'展示訂單明細或金額快照不一致。', 1;
IF EXISTS (
    SELECT 1 FROM @csOrders c
    JOIN store.OrderDetails d ON d.OrderId = c.OrderId
    WHERE d.LineTotal <> d.UnitPrice * d.Quantity
)
    THROW 51026, N'展示訂單品項快照不一致。', 1;
IF EXISTS (
    SELECT 1 FROM @csOrders c
    LEFT JOIN store.Payments p ON p.OrderId = c.OrderId
    JOIN store.StoreOrders o ON o.Id = c.OrderId
    GROUP BY c.OrderId, o.TotalAmount, o.Status
    HAVING COUNT(p.Id) <> 1
        OR MAX(CASE WHEN p.Amount <> o.TotalAmount THEN 1 ELSE 0 END) = 1
        OR MAX(CASE
            WHEN o.Status IN (N'PAID', N'FULFILLING', N'SHIPPED', N'COMPLETED') AND p.Status <> N'PAID' THEN 1
            WHEN o.Status = N'PENDING_PAYMENT' AND p.Status <> N'PENDING' THEN 1
            WHEN o.Status = N'CANCELLED' AND p.Status NOT IN (N'CANCELLED', N'FAILED') THEN 1
            ELSE 0 END) = 1
)
    THROW 51027, N'展示訂單付款快照不一致。', 1;
IF EXISTS (
    SELECT 1 FROM @csOrders c
    JOIN store.StoreOrders o ON o.Id = c.OrderId
    WHERE o.UserCouponId IS NOT NULL
    GROUP BY o.UserCouponId
    HAVING COUNT(*) > 1
)
    THROW 51028, N'同一張優惠券連結到多筆展示訂單。', 1;
IF EXISTS (
    SELECT 1 FROM @csOrders c
    JOIN store.StoreOrders o ON o.Id = c.OrderId
    JOIN store.UserCoupons uc ON uc.Id = o.UserCouponId
    WHERE EXISTS (SELECT 1 FROM store.StoreOrders other WHERE other.UserCouponId = uc.Id AND other.Id <> o.Id)
)
    THROW 51029, N'展示訂單優惠券另被其他訂單引用。', 1;

DECLARE @csReviewIds TABLE (ReviewId uniqueidentifier NOT NULL PRIMARY KEY, ReviewNo int NOT NULL UNIQUE);
DECLARE @csReviewInput TABLE (ReviewId uniqueidentifier NOT NULL PRIMARY KEY, ReviewNo int NOT NULL, ProductId uniqueidentifier NOT NULL);
DECLARE @csI int = 1;
DECLARE @csHash varbinary(32);
DECLARE @csGuidBytes varbinary(16);
WHILE @csI <= 102
BEGIN
    SET @csHash = HASHBYTES('SHA2_256', CONVERT(varchar(100), CONCAT('qmah-showcase-generated-review:', @csI)));
    SET @csGuidBytes = CONVERT(varbinary(16),
        SUBSTRING(@csHash, 1, 6)
        + CONVERT(binary(1), (CONVERT(tinyint, SUBSTRING(@csHash, 7, 1)) & 15) | 80)
        + SUBSTRING(@csHash, 8, 1)
        + CONVERT(binary(1), (CONVERT(tinyint, SUBSTRING(@csHash, 9, 1)) & 63) | 128)
        + SUBSTRING(@csHash, 10, 7));
    INSERT @csReviewIds (ReviewId, ReviewNo)
    VALUES (CONVERT(uniqueidentifier, @csGuidBytes), @csI);
    SET @csI += 1;
END;
INSERT @csReviewInput (ReviewId, ReviewNo, ProductId)
SELECT r.Id, ids.ReviewNo, r.ProductId
FROM @csReviewIds ids
JOIN store.ProductReviews r ON r.Id = ids.ReviewId;
IF (SELECT COUNT(*) FROM @csReviewInput) <> 102
    THROW 51030, N'預期恰好有 102 則展示生成評論。', 1;

/* 逐則將生成評論分給新訂單持有人中確實買過同商品的人，並盡量平衡
   評論總量；同時避開唯一的 ProductId/UserId 組合與所有非生成評論。 */
DECLARE @csReviewAssignments TABLE
(
    ReviewId uniqueidentifier NOT NULL PRIMARY KEY,
    ReviewNo int NOT NULL UNIQUE,
    ProductId uniqueidentifier NOT NULL,
    UserId uniqueidentifier NOT NULL,
    PaidAt datetime2(3) NOT NULL
);
DECLARE @csBaseLoad TABLE (UserId uniqueidentifier NOT NULL PRIMARY KEY, ReviewCount int NOT NULL);
INSERT @csBaseLoad (UserId, ReviewCount)
SELECT d.UserId, COUNT(r.Id)
FROM @demoUsers d
LEFT JOIN store.ProductReviews r ON r.UserId = d.UserId
    AND NOT EXISTS (SELECT 1 FROM @csReviewIds g WHERE g.ReviewId = r.Id)
GROUP BY d.UserId;
DECLARE @csReviewNo int = 1;
DECLARE @csReviewId uniqueidentifier;
DECLARE @csProductId uniqueidentifier;
DECLARE @csAssignedUser uniqueidentifier;
DECLARE @csPaidAt datetime2(3);
WHILE @csReviewNo <= 102
BEGIN
    SELECT @csReviewId = ReviewId, @csProductId = ProductId
    FROM @csReviewInput WHERE ReviewNo = @csReviewNo;
    SET @csAssignedUser = NULL;
    SET @csPaidAt = NULL;
    SELECT TOP (1) @csAssignedUser = d.UserId, @csPaidAt = purchases.PaidAt
    FROM @demoUsers d
    CROSS APPLY
    (
        SELECT MIN(o.PaidAt) AS PaidAt
        FROM @csOrders c
        JOIN store.StoreOrders o ON o.Id = c.OrderId
        JOIN store.OrderDetails od ON od.OrderId = o.Id
        WHERE c.NewUserId = d.UserId AND od.ProductId = @csProductId
          AND o.Status IN (N'PAID', N'FULFILLING', N'SHIPPED', N'COMPLETED')
          AND o.PaidAt IS NOT NULL
    ) purchases
    WHERE purchases.PaidAt IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1 FROM store.ProductReviews occupied
          WHERE occupied.ProductId = @csProductId AND occupied.UserId = d.UserId
            AND NOT EXISTS (SELECT 1 FROM @csReviewIds generated WHERE generated.ReviewId = occupied.Id)
      )
      AND NOT EXISTS
      (
          SELECT 1 FROM @csReviewAssignments assigned
          WHERE assigned.ProductId = @csProductId AND assigned.UserId = d.UserId
      )
    ORDER BY
      (SELECT b.ReviewCount FROM @csBaseLoad b WHERE b.UserId = d.UserId)
        + (SELECT COUNT(*) FROM @csReviewAssignments a WHERE a.UserId = d.UserId),
      d.Ordinal;
    IF @csAssignedUser IS NULL OR @csPaidAt IS NULL
        THROW 51031, N'某則展示評論找不到未衝突且買過該商品的展示帳號。', 1;
    INSERT @csReviewAssignments (ReviewId, ReviewNo, ProductId, UserId, PaidAt)
    VALUES (@csReviewId, @csReviewNo, @csProductId, @csAssignedUser, @csPaidAt);
    SET @csReviewNo += 1;
END;

/* 只處理唯讀稽核確認的五張未被訂單引用之展示優惠券。 */
DECLARE @csCouponTargets TABLE (CouponId uniqueidentifier NOT NULL PRIMARY KEY);
INSERT @csCouponTargets (CouponId) VALUES
('77081F8A-7765-45EE-B425-46FDA048B94E'),
('8F72416F-358B-4779-A197-80B10F5DC915'),
('34D53473-5EB9-4BE1-A3D4-B46BBC155530'),
('4AF962B6-2288-4D2F-8D0B-C5DC90AC6CEC'),
('DD48BAA8-0B87-4400-A457-F28D25D81F1F');
IF (SELECT COUNT(*) FROM @csCouponTargets) <> 5
    THROW 51032, N'預期恰好有五張已確認的展示管理員贈券。', 1;
IF (SELECT COUNT(*)
    FROM @csCouponTargets t
    JOIN store.UserCoupons uc ON uc.Id = t.CouponId
    JOIN store.CouponDefinitions cd ON cd.Id = uc.CouponDefinitionId
    JOIN @demoUsers d ON d.UserId = uc.UserId
    WHERE cd.AcquisitionType = N'ADMIN_GRANT' AND uc.GrantBatchId IS NULL) <> 5
    THROW 51035, N'已確認的展示優惠券不再符合管理員贈券來源範圍。', 1;
DECLARE @csUsedFixtureCount int;
SELECT @csUsedFixtureCount = COUNT(*)
FROM @csCouponTargets t JOIN store.UserCoupons uc ON uc.Id = t.CouponId
WHERE uc.Status = N'USED';
IF @csUsedFixtureCount NOT IN (0, 5)
    THROW 51034, N'已確認優惠券必須全為尚未修復的已使用狀態，或全為已修復狀態。', 1;
IF EXISTS
(
    SELECT 1 FROM @csCouponTargets t
    JOIN store.UserCoupons uc ON uc.Id = t.CouponId
    WHERE uc.Status NOT IN (N'USED', N'AVAILABLE', N'EXPIRED')
       OR (uc.Status <> N'USED' AND
           ((uc.ExpiresAt <= SYSUTCDATETIME() AND uc.Status <> N'EXPIRED')
             OR (uc.ExpiresAt > SYSUTCDATETIME() AND uc.Status <> N'AVAILABLE')
             OR uc.UsedAt IS NOT NULL))
)
    THROW 51036, N'已確認優惠券的狀態或使用時間戳記不符預期。', 1;
IF EXISTS
(
    SELECT 1 FROM @csCouponTargets t
    JOIN store.UserCoupons uc ON uc.Id = t.CouponId
    WHERE EXISTS (SELECT 1 FROM store.StoreOrders o WHERE o.UserCouponId = uc.Id)
)
    THROW 51033, N'已確認優惠券仍被訂單引用，停止變更其使用狀態。', 1;

UPDATE o SET UserId = c.NewUserId
FROM store.StoreOrders o JOIN @csOrders c ON c.OrderId = o.Id;
UPDATE uc SET UserId = c.NewUserId
FROM store.StoreOrders o
JOIN @csOrders c ON c.OrderId = o.Id
JOIN store.UserCoupons uc ON uc.Id = o.UserCouponId
WHERE uc.UserId <> c.NewUserId;

/* 已存在且連到訂單的流水隨根訂單移轉，不新增流水或餘額；餘額由呼叫端
   依交易前後的流水快照調整。 */
UPDATE t SET UserId = c.NewUserId
FROM store.PointTransactions t
JOIN @csOrders c ON c.OrderId = t.ReferenceId
WHERE t.ReferenceType = N'ORDER' AND t.UserId <> c.NewUserId;
UPDATE t SET UserId = c.NewUserId
FROM catalog.KeyTransactions t
JOIN @csOrders c ON c.OrderId = t.ReferenceId
WHERE t.ReferenceType = N'ORDER' AND t.UserId <> c.NewUserId;
UPDATE t SET UserId = c.NewUserId
FROM catalog.KeyProgressTransactions t
JOIN @csOrders c ON c.OrderId = t.ReferenceId
WHERE t.ReferenceType = N'ORDER' AND t.UserId <> c.NewUserId;

UPDATE r
SET UserId = a.UserId,
    CreatedAt = CASE WHEN r.CreatedAt < a.PaidAt THEN DATEADD(DAY, 1, CONVERT(datetime2(3), CONVERT(date, a.PaidAt))) ELSE r.CreatedAt END,
    UpdatedAt = CASE
        WHEN r.UpdatedAt < CASE WHEN r.CreatedAt < a.PaidAt THEN DATEADD(DAY, 1, CONVERT(datetime2(3), CONVERT(date, a.PaidAt))) ELSE r.CreatedAt END
            THEN DATEADD(MINUTE, 12, CASE WHEN r.CreatedAt < a.PaidAt THEN DATEADD(DAY, 1, CONVERT(datetime2(3), CONVERT(date, a.PaidAt))) ELSE r.CreatedAt END)
        ELSE r.UpdatedAt END
FROM store.ProductReviews r
JOIN @csReviewAssignments a ON a.ReviewId = r.Id;

DECLARE @csNow datetime2(3) = SYSUTCDATETIME();
UPDATE uc
SET Status = CASE WHEN uc.ExpiresAt <= @csNow THEN N'EXPIRED' ELSE N'AVAILABLE' END,
    UsedAt = NULL
FROM store.UserCoupons uc
JOIN @csCouponTargets t ON t.CouponId = uc.Id
WHERE uc.Status = N'USED' OR uc.UsedAt IS NOT NULL;

SELECT N'商務關聯修復完成' AS Result,
       (SELECT COUNT(*) FROM @csOrders) AS GeneratedOrders,
       (SELECT COUNT(*) FROM @csReviewAssignments) AS GeneratedReviews,
       (SELECT COUNT(*) FROM @demoUsers) AS DemoUsers,
       (SELECT COUNT(*) FROM @csCouponTargets) AS FixtureCoupons,
       @csUsedFixtureCount AS CouponsRepaired;
