/* db-v1.0.7：新增一批折價券，可重複執行（以 Code 判斷，已存在則更新內容）。
   點數兌換：現金折抵新增兩個高額檔位、折扣券四個檔位；站方發放：依檔期命名的活動券。 */
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;

CREATE TABLE #c (Code nvarchar(50) COLLATE DATABASE_DEFAULT PRIMARY KEY, Name nvarchar(100), DiscountType nvarchar(20), Acq nvarchar(30),
                 PointCost int NULL, Validity int, Val decimal(12,2), Min decimal(12,2), StartAt datetime2(3), EndAt datetime2(3), Active bit);
INSERT INTO #c VALUES
(N'POINT_EXCHANGE_400_250',  N'鑑定點數兌換 250 元券',  N'FIXED',   N'POINT_EXCHANGE', 400,  365, 250,  1600, '2026-01-01', '2027-12-31', 1),
(N'POINT_EXCHANGE_1200_1000',N'鑑定點數兌換 1,000 元券',N'FIXED',   N'POINT_EXCHANGE', 1200, 365, 1000, 5000, '2026-01-01', '2027-12-31', 1),
(N'POINT_EXCHANGE_PCT95',    N'鑑定點數兌換 95 折券',   N'PERCENT', N'POINT_EXCHANGE', 80,   180, 5,    300,  '2026-01-01', '2027-12-31', 1),
(N'POINT_EXCHANGE_PCT90',    N'鑑定點數兌換 9 折券',    N'PERCENT', N'POINT_EXCHANGE', 200,  180, 10,   800,  '2026-01-01', '2027-12-31', 1),
(N'POINT_EXCHANGE_PCT85',    N'鑑定點數兌換 85 折券',   N'PERCENT', N'POINT_EXCHANGE', 400,  180, 15,   1500, '2026-01-01', '2027-12-31', 1),
(N'POINT_EXCHANGE_PCT80',    N'鑑定點數兌換 8 折券',    N'PERCENT', N'POINT_EXCHANGE', 900,  180, 20,   3500, '2026-01-01', '2027-12-31', 1),
(N'CAMPAIGN_ANNUAL_THANKS',  N'年度會員感謝券',         N'FIXED',   N'ADMIN_GRANT',    NULL, 365, 200,  1500, '2026-10-20', '2026-12-20', 1),
(N'CAMPAIGN_ANNIVERSARY',    N'週年慶回饋券',           N'PERCENT', N'ADMIN_GRANT',    NULL, 365, 12,   1800, '2026-11-01', '2026-11-15', 1),
(N'CAMPAIGN_DOUBLE11',       N'雙十一收藏券',           N'FIXED',   N'ADMIN_GRANT',    NULL, 365, 111,  1111, '2026-11-08', '2026-11-12', 1),
(N'CAMPAIGN_YEAREND_GIFT',   N'歲末送禮季折扣券',       N'PERCENT', N'ADMIN_GRANT',    NULL, 365, 10,   2000, '2026-12-01', '2026-12-31', 1),
(N'CAMPAIGN_NEW_ARRIVAL',    N'新品上架禮',             N'FIXED',   N'ADMIN_GRANT',    NULL, 365, 120,  800,  '2026-10-20', '2026-11-20', 1),
(N'CAMPAIGN_EXHIBIT_COBRAND',N'展覽聯名回饋券',         N'PERCENT', N'ADMIN_GRANT',    NULL, 365, 8,    1000, '2026-10-01', '2026-11-15', 1);

UPDATE d SET Name=c.Name, DiscountType=c.DiscountType, AcquisitionType=c.Acq, PointCost=c.PointCost, ValidityDays=c.Validity,
             DiscountValue=c.Val, MinimumAmount=c.Min, StartAt=c.StartAt, EndAt=c.EndAt, IsActive=c.Active
FROM store.CouponDefinitions d JOIN #c c ON c.Code = d.Code;

INSERT INTO store.CouponDefinitions (Id, Code, Name, DiscountType, AcquisitionType, PointCost, ValidityDays, DiscountValue, MinimumAmount, StartAt, EndAt, IsActive)
SELECT NEWID(), c.Code, c.Name, c.DiscountType, c.Acq, c.PointCost, c.Validity, c.Val, c.Min, c.StartAt, c.EndAt, c.Active
FROM #c c WHERE NOT EXISTS (SELECT 1 FROM store.CouponDefinitions d WHERE d.Code = c.Code);

DROP TABLE #c;
COMMIT TRAN;
