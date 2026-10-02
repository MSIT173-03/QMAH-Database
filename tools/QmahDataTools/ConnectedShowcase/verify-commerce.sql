/* 唯讀驗證片段。呼叫端提供 @demoUsers 並負責交易。 */
SET NOCOUNT ON;

IF (SELECT COUNT(*) FROM @demoUsers) <> 24
    THROW 51040, N'關聯驗證需要恰好 24 個展示帳號。', 1;
IF EXISTS (SELECT 1 FROM @demoUsers WHERE Ordinal < 1 OR Ordinal > 24)
   OR (SELECT COUNT(DISTINCT Ordinal) FROM @demoUsers) <> 24
    THROW 51041, N'展示帳號序號必須是 1 至 24 的唯一值。', 1;
IF EXISTS (
    SELECT 1 FROM @demoUsers d
    LEFT JOIN [user].AspNetUsers u ON u.Id = d.UserId
    LEFT JOIN [user].UserProfiles p ON p.UserId = d.UserId
    WHERE u.Id IS NULL OR u.Status <> N'ACTIVE' OR p.UserId IS NULL OR p.Nickname <> d.Nickname
)
    THROW 51042, N'展示帳號識別、啟用狀態或暱稱與來源資料不符。', 1;

DECLARE @cvOrders TABLE (OrderId uniqueidentifier NOT NULL PRIMARY KEY, OrderNo nvarchar(30) NOT NULL, UserId uniqueidentifier NOT NULL);
INSERT @cvOrders (OrderId, OrderNo, UserId)
SELECT o.Id, o.OrderNo, o.UserId
FROM store.StoreOrders o
WHERE o.OrderNo LIKE N'QMAH-GEN-%';
IF (SELECT COUNT(*) FROM @cvOrders) <> 170
    THROW 51043, N'預期恰好有 170 筆展示生成訂單。', 1;
IF EXISTS (SELECT 1 FROM @cvOrders c LEFT JOIN @demoUsers d ON d.UserId = c.UserId WHERE d.UserId IS NULL)
    THROW 51044, N'展示訂單的持有人不在展示帳號範圍內。', 1;
IF EXISTS (
    SELECT 1 FROM @demoUsers d
    OUTER APPLY (SELECT COUNT(*) AS OrderCount FROM @cvOrders c WHERE c.UserId = d.UserId) x
    WHERE x.OrderCount NOT BETWEEN 7 AND 8
)
    THROW 51045, N'展示訂單未平均分配給所有展示帳號。', 1;
IF EXISTS (
    SELECT 1
    FROM @cvOrders c
    JOIN store.StoreOrders o ON o.Id = c.OrderId
    LEFT JOIN store.OrderDetails d ON d.OrderId = o.Id
    GROUP BY c.OrderId, o.Subtotal, o.DiscountAmount, o.PointsUsed, o.TotalAmount, o.ShippingFee
    HAVING COUNT(d.Id) = 0 OR SUM(d.LineTotal) <> o.Subtotal
        OR o.TotalAmount <> ((o.Subtotal - o.DiscountAmount) - o.PointsUsed + o.ShippingFee)
)
    THROW 51046, N'展示訂單明細或金額快照不一致。', 1;
IF EXISTS (
    SELECT 1 FROM @cvOrders c JOIN store.OrderDetails d ON d.OrderId = c.OrderId
    WHERE d.LineTotal <> d.UnitPrice * d.Quantity
)
    THROW 51047, N'展示訂單品項快照不一致。', 1;
IF EXISTS (
    SELECT 1 FROM @cvOrders c
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
    THROW 51048, N'展示訂單付款快照不一致。', 1;
IF EXISTS (
    SELECT 1 FROM @cvOrders c
    JOIN store.StoreOrders o ON o.Id = c.OrderId
    JOIN store.UserCoupons uc ON uc.Id = o.UserCouponId
    WHERE uc.UserId <> o.UserId
)
    THROW 51049, N'訂單所附優惠券持有人與訂單持有人不一致。', 1;
IF EXISTS (
    SELECT 1 FROM store.PointTransactions t JOIN @cvOrders c ON c.OrderId = t.ReferenceId
    WHERE t.ReferenceType = N'ORDER' AND t.UserId <> c.UserId
) OR EXISTS (
    SELECT 1 FROM catalog.KeyTransactions t JOIN @cvOrders c ON c.OrderId = t.ReferenceId
    WHERE t.ReferenceType = N'ORDER' AND t.UserId <> c.UserId
) OR EXISTS (
    SELECT 1 FROM catalog.KeyProgressTransactions t JOIN @cvOrders c ON c.OrderId = t.ReferenceId
    WHERE t.ReferenceType = N'ORDER' AND t.UserId <> c.UserId
)
    THROW 51050, N'訂單關聯流水持有人與訂單持有人不一致。', 1;

DECLARE @cvReviewIds TABLE (ReviewId uniqueidentifier NOT NULL PRIMARY KEY, ReviewNo int NOT NULL UNIQUE);
DECLARE @cvI int = 1;
DECLARE @cvHash varbinary(32);
DECLARE @cvGuidBytes varbinary(16);
WHILE @cvI <= 102
BEGIN
    SET @cvHash = HASHBYTES('SHA2_256', CONVERT(varchar(100), CONCAT('qmah-showcase-generated-review:', @cvI)));
    SET @cvGuidBytes = CONVERT(varbinary(16),
        SUBSTRING(@cvHash, 1, 6)
        + CONVERT(binary(1), (CONVERT(tinyint, SUBSTRING(@cvHash, 7, 1)) & 15) | 80)
        + SUBSTRING(@cvHash, 8, 1)
        + CONVERT(binary(1), (CONVERT(tinyint, SUBSTRING(@cvHash, 9, 1)) & 63) | 128)
        + SUBSTRING(@cvHash, 10, 7));
    INSERT @cvReviewIds (ReviewId, ReviewNo) VALUES (CONVERT(uniqueidentifier, @cvGuidBytes), @cvI);
    SET @cvI += 1;
END;
IF (SELECT COUNT(*) FROM store.ProductReviews r JOIN @cvReviewIds i ON i.ReviewId = r.Id) <> 102
    THROW 51051, N'預期恰好有 102 則展示生成評論。', 1;
IF EXISTS (
    SELECT 1 FROM store.ProductReviews r
    JOIN @cvReviewIds i ON i.ReviewId = r.Id
    LEFT JOIN @demoUsers d ON d.UserId = r.UserId
    WHERE d.UserId IS NULL OR r.UpdatedAt < r.CreatedAt
)
    THROW 51052, N'展示評論的持有人或更新時間戳記不正確。', 1;
IF EXISTS (
    SELECT 1 FROM store.ProductReviews r
    JOIN @cvReviewIds i ON i.ReviewId = r.Id
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM store.OrderDetails od
        JOIN store.StoreOrders o ON o.Id = od.OrderId
        WHERE od.ProductId = r.ProductId AND o.UserId = r.UserId
          AND o.Status IN (N'PAID', N'FULFILLING', N'SHIPPED', N'COMPLETED')
          AND o.PaidAt IS NOT NULL AND o.PaidAt <= r.CreatedAt
    )
)
    THROW 51053, N'展示評論沒有對應同商品且付款時間早於評論的有效購買。', 1;

DECLARE @cvCouponTargets TABLE (CouponId uniqueidentifier NOT NULL PRIMARY KEY);
INSERT @cvCouponTargets (CouponId) VALUES
('77081F8A-7765-45EE-B425-46FDA048B94E'),
('8F72416F-358B-4779-A197-80B10F5DC915'),
('34D53473-5EB9-4BE1-A3D4-B46BBC155530'),
('4AF962B6-2288-4D2F-8D0B-C5DC90AC6CEC'),
('DD48BAA8-0B87-4400-A457-F28D25D81F1F');
DECLARE @cvCouponCount int = (SELECT COUNT(*) FROM @cvCouponTargets);
IF @cvCouponCount <> 5 OR (SELECT COUNT(*)
    FROM @cvCouponTargets t
    JOIN store.UserCoupons uc ON uc.Id = t.CouponId
    JOIN store.CouponDefinitions cd ON cd.Id = uc.CouponDefinitionId
    JOIN @demoUsers d ON d.UserId = uc.UserId
    WHERE cd.AcquisitionType = N'ADMIN_GRANT' AND uc.GrantBatchId IS NULL) <> 5
    THROW 51054, N'預期找到五張已確認的展示管理員贈券。', 1;
IF EXISTS (
    SELECT 1
    FROM @cvCouponTargets t
    JOIN store.UserCoupons uc ON uc.Id = t.CouponId
    WHERE uc.UsedAt IS NOT NULL
        OR (uc.ExpiresAt <= SYSUTCDATETIME() AND uc.Status <> N'EXPIRED')
        OR (uc.ExpiresAt > SYSUTCDATETIME() AND uc.Status <> N'AVAILABLE')
        OR EXISTS (SELECT 1 FROM store.StoreOrders o WHERE o.UserCouponId = uc.Id)
)
    THROW 51055, N'展示優惠券狀態、到期時間或訂單引用不正確。', 1;

SELECT N'商務關聯驗證通過' AS Result,
       (SELECT COUNT(*) FROM @cvOrders) AS GeneratedOrders,
       (SELECT COUNT(*) FROM store.ProductReviews r JOIN @cvReviewIds i ON i.ReviewId = r.Id) AS GeneratedReviews,
       (SELECT COUNT(*) FROM @demoUsers) AS DemoUsers,
       @cvCouponCount AS FixtureCoupons;
