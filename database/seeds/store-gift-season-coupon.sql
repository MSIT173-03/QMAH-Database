/* 歲末送禮季折扣券：與商城首頁輪播「12 月結帳享 9 折，最低消費 NT$2,000」一致，可重複執行。 */
IF NOT EXISTS (SELECT 1 FROM [store].[CouponDefinitions] WHERE Code = N'GIFT_SEASON_10')
INSERT INTO [store].[CouponDefinitions]
    (Id, Code, Name, DiscountType, DiscountValue, MinimumAmount, StartAt, EndAt, IsActive, AcquisitionType, PointCost, ValidityDays)
VALUES
    (NEWID(), N'GIFT_SEASON_10', N'歲末送禮季 9 折券', N'PERCENT', 10.00, 2000.00,
     '2026-12-01T00:00:00', '2026-12-31T23:59:59', 1, N'ADMIN_GRANT', NULL, 365);
GO
