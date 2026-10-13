/* QMAH Store demonstration seed — 已售數量補強，可重複執行。
   讓每件商品的已售數量（已完成訂單的數量總和）合理地大於評價數：
   目標 = 評價數 x 3 + 2 ~ 7（依商品 Id 固定，不隨機），已經達標的商品不動。
   不足的部分以 QMAH-TOPUP- 開頭的「已完成」歷史訂單補上（每筆訂單 1–3 個品項、每品項 1–4 件，
   日期分散在 2025-11-01 到 2026-10-02），因此商城上的「已售」仍然完全來自真實訂單資料。
   前置：先執行 store-showcase-orders-shipping.sql／歷史訂單種子，使 QMAH-GEN- 訂單與可用的收件資料存在。 */
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRAN;

-- 重跑保護：先移除先前補強的訂單
DELETE d FROM store.OrderDetails d JOIN store.StoreOrders o ON o.Id = d.OrderId WHERE o.OrderNo LIKE 'QMAH-TOPUP-%';
DELETE p FROM store.Payments p JOIN store.StoreOrders o ON o.Id = p.OrderId WHERE o.OrderNo LIKE 'QMAH-TOPUP-%';
DELETE FROM store.StoreOrders WHERE OrderNo LIKE 'QMAH-TOPUP-%';

-- 每件商品的缺口
SELECT p.Id AS ProductId, p.Name, COALESCE(p.SalePrice, p.Price) AS UnitPrice,
       (ISNULL(r.rc, 0) * 3 + 2 + (ABS(CHECKSUM(p.Id)) % 6)) - ISNULL(s.sold, 0) AS Deficit
INTO #Gap
FROM store.Products p
LEFT JOIN (SELECT d.ProductId, SUM(d.Quantity) AS sold
           FROM store.OrderDetails d JOIN store.StoreOrders o ON o.Id = d.OrderId
           WHERE o.Status = 'COMPLETED' GROUP BY d.ProductId) s ON s.ProductId = p.Id
LEFT JOIN (SELECT ProductId, COUNT(*) AS rc FROM store.ProductReviews GROUP BY ProductId) r ON r.ProductId = p.Id
WHERE p.IsActive = 1;
DELETE FROM #Gap WHERE Deficit <= 0;

-- 把缺口拆成每品項最多 4 件的明細（Deficit 最多約 40，編號表取 1..12 即可涵蓋）
SELECT g.ProductId, g.Name, g.UnitPrice,
       CASE WHEN g.Deficit - (n.k - 1) * 4 >= 4 THEN 4 ELSE g.Deficit - (n.k - 1) * 4 END AS Qty,
       ROW_NUMBER() OVER (ORDER BY g.ProductId, n.k) AS RowNo
INTO #Lines
FROM #Gap g
JOIN (SELECT TOP (12) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS k FROM sys.all_objects) n
  ON g.Deficit - (n.k - 1) * 4 > 0;

-- 每 3 筆明細合成一張訂單
SELECT (RowNo - 1) / 3 + 1 AS OrderIdx, ProductId, Name, UnitPrice, Qty INTO #L2 FROM #Lines;
SELECT OrderIdx, NEWID() AS Id,
       'QMAH-TOPUP-' + RIGHT('00000' + CAST(OrderIdx AS varchar(5)), 5) AS OrderNo,
       ABS(CHECKSUM(CAST(OrderIdx AS varchar(10)) + 'u')) AS UHash,
       DATEADD(MINUTE, ABS(CHECKSUM(CAST(OrderIdx AS varchar(10)) + 't')) % 1440,
               DATEADD(DAY, ABS(CHECKSUM(CAST(OrderIdx AS varchar(10)) + 'd')) % 336, CAST('2025-11-01' AS datetime2))) AS CreatedAt
INTO #O0 FROM (SELECT DISTINCT OrderIdx FROM #L2) x;

DECLARE @Users TABLE (UserId uniqueidentifier, N int);
INSERT @Users SELECT UserId, ROW_NUMBER() OVER (ORDER BY UserId)
FROM (SELECT DISTINCT UserId FROM store.StoreOrders WHERE OrderNo LIKE 'QMAH-GEN-%') u;
DECLARE @UserCount int = (SELECT COUNT(*) FROM @Users);
IF @UserCount = 0 THROW 50000, N'找不到 QMAH-GEN- 訂單的使用者，請先執行歷史訂單種子。', 1;

SELECT o.OrderIdx, o.Id, o.OrderNo, u.UserId, o.CreatedAt
INTO #O FROM #O0 o JOIN @Users u ON u.N = 1 + (o.UHash % @UserCount);

-- 同一張訂單內同一商品只會出現一次（缺口明細最多 12 筆且分散在不同訂單），合併以防萬一
SELECT o.Id AS OrderId, l.ProductId, MAX(l.Name) AS Name, MAX(l.UnitPrice) AS UnitPrice, SUM(l.Qty) AS Qty
INTO #D FROM #L2 l JOIN #O o ON o.OrderIdx = l.OrderIdx GROUP BY o.Id, l.ProductId;

INSERT store.StoreOrders (Id, OrderNo, UserId, UserCouponId, Status, Subtotal, DiscountAmount, PointsUsed, TotalAmount,
    RecipientName, RecipientPhone, ShippingPostalCode, ShippingCity, ShippingDistrict, ShippingAddressLine,
    CreatedAt, PaidAt, CancelledAt, ShippingMethod, ShippingFee)
SELECT o.Id, o.OrderNo, o.UserId, NULL, 'COMPLETED',
       s.Sub, 0, 0, s.Sub + CASE WHEN s.Sub >= 1500 THEN 0 ELSE 60 END,
       a.RecipientName, a.RecipientPhone, a.ShippingPostalCode, a.ShippingCity, a.ShippingDistrict, a.ShippingAddressLine,
       o.CreatedAt, DATEADD(MINUTE, 5, o.CreatedAt), NULL,
       CASE WHEN s.Sub >= 1500 THEN 'STANDARD' ELSE 'CVS' END,
       CASE WHEN s.Sub >= 1500 THEN 0 ELSE 60 END
FROM #O o
CROSS APPLY (SELECT SUM(UnitPrice * Qty) AS Sub FROM #D WHERE OrderId = o.Id) s
OUTER APPLY (SELECT TOP 1 RecipientName, RecipientPhone, ShippingPostalCode, ShippingCity, ShippingDistrict, ShippingAddressLine
             FROM store.StoreOrders WHERE UserId = o.UserId AND OrderNo LIKE 'QMAH-GEN-%') a;

INSERT store.OrderDetails (Id, OrderId, ProductId, ProductNameSnapshot, UnitPrice, Quantity, LineTotal)
SELECT NEWID(), OrderId, ProductId, Name, UnitPrice, Qty, UnitPrice * Qty FROM #D;

INSERT store.Payments (Id, OrderId, MerchantTradeNo, EcpayTradeNo, Amount, Status, RtnCode, RtnMsg, PaymentType, CallbackReceivedAt, CreatedAt)
SELECT NEWID(), o.Id, 'QMAH-GP-T' + RIGHT('00000' + CAST(h.OrderIdx AS varchar(5)), 5),
       'QMAH-TOPUP-' + RIGHT('00000' + CAST(h.OrderIdx AS varchar(5)), 5),
       o.TotalAmount, 'PAID', 1, N'付款成功', CASE WHEN h.OrderIdx % 3 = 0 THEN 'WebATM' ELSE 'Credit_CreditCard' END,
       DATEADD(MINUTE, 5, o.CreatedAt), o.CreatedAt
FROM store.StoreOrders o JOIN #O h ON h.Id = o.Id;

COMMIT;

SELECT COUNT(*) AS topup_orders FROM store.StoreOrders WHERE OrderNo LIKE 'QMAH-TOPUP-%';
GO
