/* QMAH Store demonstration seed — 管理員帳號的特別活動優惠券，可重複執行。
   建立四種活動券（以 Code 判斷，已存在就不重建），並發放給所有管理員角色（Admin／SuperAdmin）的帳號；
   同一帳號已持有同一張定義的可用券就不再重複發放。
   歲末送禮季 9 折券（GIFT_SEASON_10）由 store-gift-season-coupon.sql 建立，這裡一併發放。 */
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @Defs TABLE (Code nvarchar(60), Name nvarchar(120), DiscountType nvarchar(20), DiscountValue decimal(18,2), MinimumAmount decimal(18,2));
INSERT @Defs VALUES
    (N'ADMIN_EVENT_COIN200',    N'錢幣特展加碼券',   N'FIXED',   200.00, 1000.00),
    (N'ADMIN_EVENT_CERAMIC15',  N'陶瓷職人鑑賞券',   N'PERCENT',  15.00, 1500.00),
    (N'ADMIN_EVENT_CURATOR300', N'館長特別回饋券',   N'FIXED',   300.00, 2000.00),
    (N'ADMIN_EVENT_STAFF50',    N'營運夥伴感謝券',   N'FIXED',    50.00,  300.00);

INSERT [store].[CouponDefinitions]
    (Id, Code, Name, DiscountType, DiscountValue, MinimumAmount, StartAt, EndAt, IsActive, AcquisitionType, PointCost, ValidityDays)
SELECT NEWID(), d.Code, d.Name, d.DiscountType, d.DiscountValue, d.MinimumAmount,
       '2026-10-01T00:00:00', '2026-12-31T23:59:59', 1, N'ADMIN_GRANT', NULL, 365
FROM @Defs d
WHERE NOT EXISTS (SELECT 1 FROM [store].[CouponDefinitions] c WHERE c.Code = d.Code);

-- 發放對象：所有管理員角色的帳號
SELECT DISTINCT ur.UserId
INTO #Admins
FROM [user].[AspNetUserRoles] ur
JOIN [user].[AspNetRoles] r ON r.Id = ur.RoleId
WHERE r.Name IN (N'Admin', N'SuperAdmin');

INSERT [store].[UserCoupons] (Id, UserId, CouponDefinitionId, Status, IssuedAt, UsedAt, ExpiresAt, IssuedByAdminUserId, IssueReason)
SELECT NEWID(), a.UserId, c.Id, N'AVAILABLE', SYSUTCDATETIME(), NULL, '2026-12-31T23:59:59', NULL, N'管理員特別活動優惠'
FROM #Admins a
JOIN [store].[CouponDefinitions] c
  ON c.Code IN (N'ADMIN_EVENT_COIN200', N'ADMIN_EVENT_CERAMIC15', N'ADMIN_EVENT_CURATOR300', N'ADMIN_EVENT_STAFF50', N'GIFT_SEASON_10')
WHERE NOT EXISTS (
    SELECT 1 FROM [store].[UserCoupons] u
    WHERE u.UserId = a.UserId AND u.CouponDefinitionId = c.Id AND u.Status = N'AVAILABLE');

COMMIT;

SELECT COUNT(*) AS admins FROM #Admins;
SELECT COUNT(*) AS admin_event_coupons
FROM [store].[UserCoupons] u JOIN [store].[CouponDefinitions] c ON c.Id = u.CouponDefinitionId
WHERE c.Code LIKE N'ADMIN_EVENT_%' OR c.Code = N'GIFT_SEASON_10';
GO
