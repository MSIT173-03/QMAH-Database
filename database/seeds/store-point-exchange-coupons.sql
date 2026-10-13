/* QMAH Store demonstration seed — 兌換商店：補齊點數兌換的折價券（現金折抵加密、折扣券新增），可重複執行。
   以 Code 判斷，已存在就不重建。 */
SET NOCOUNT ON;
DECLARE @Defs TABLE (Code nvarchar(60), Name nvarchar(120), DiscountType nvarchar(20), DiscountValue decimal(18,2), MinimumAmount decimal(18,2), PointCost int);
INSERT @Defs VALUES
    (N'POINT_EXCHANGE_30_10',    N'鑑定點數兌換 10 元券',  N'FIXED',    10.00,  100.00,   30),
    (N'POINT_EXCHANGE_150_80',   N'鑑定點數兌換 80 元券',  N'FIXED',    80.00,  600.00,  150),
    (N'POINT_EXCHANGE_1000_800', N'鑑定點數兌換 800 元券', N'FIXED',   800.00, 4000.00, 1000),
    (N'POINT_EXCHANGE_80_P3',    N'點數兌換 97 折券',      N'PERCENT',   3.00,  200.00,   80),
    (N'POINT_EXCHANGE_120_P5',   N'點數兌換 95 折券',      N'PERCENT',   5.00,  300.00,  120),
    (N'POINT_EXCHANGE_300_P8',   N'點數兌換 92 折券',      N'PERCENT',   8.00,  800.00,  300),
    (N'POINT_EXCHANGE_450_P10',  N'點數兌換 9 折券',       N'PERCENT',  10.00, 1500.00,  450),
    (N'POINT_EXCHANGE_650_P12',  N'點數兌換 88 折券',      N'PERCENT',  12.00, 2000.00,  650),
    (N'POINT_EXCHANGE_900_P15',  N'點數兌換 85 折券',      N'PERCENT',  15.00, 3000.00,  900),
    (N'POINT_EXCHANGE_1200_P18', N'點數兌換 82 折券',      N'PERCENT',  18.00, 3500.00, 1200),
    (N'POINT_EXCHANGE_1500_P20', N'點數兌換 8 折券',       N'PERCENT',  20.00, 4000.00, 1500);

INSERT [store].[CouponDefinitions]
    (Id, Code, Name, DiscountType, DiscountValue, MinimumAmount, StartAt, EndAt, IsActive, AcquisitionType, PointCost, ValidityDays)
SELECT NEWID(), d.Code, d.Name, d.DiscountType, d.DiscountValue, d.MinimumAmount,
       '2026-10-01T00:00:00', '2026-12-31T23:59:59', 1, N'POINT_EXCHANGE', d.PointCost, 365
FROM @Defs d
WHERE NOT EXISTS (SELECT 1 FROM [store].[CouponDefinitions] c WHERE c.Code = d.Code);

SELECT COUNT(*) AS point_exchange_coupons FROM [store].[CouponDefinitions] WHERE AcquisitionType = N'POINT_EXCHANGE';
GO
