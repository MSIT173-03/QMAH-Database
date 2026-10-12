-- db-v1.0.6：讓每個帳號的訂單涵蓋六種狀態，方便展示評論、取消、出貨等流程。
-- 只改訂單與付款的狀態欄位，金額、品項與收件資料不動。可重複執行：同一帳號的訂單依建立時間輪流指派。
SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
DECLARE @paidMsg nvarchar(200)=(SELECT TOP 1 RtnMsg FROM store.Payments WHERE Status=N'PAID' AND RtnMsg IS NOT NULL);
DECLARE @failRc int=(SELECT TOP 1 RtnCode FROM store.Payments WHERE Status=N'FAILED'), @failMsg nvarchar(200)=(SELECT TOP 1 RtnMsg FROM store.Payments WHERE Status=N'FAILED');
;WITH ranked AS (
  SELECT o.Id, o.CreatedAt, p.Id AS PaymentId, p.PaymentType,
    CHOOSE(((ROW_NUMBER() OVER (PARTITION BY o.UserId ORDER BY o.CreatedAt, o.Id) - 1) % 6) + 1,
      N'COMPLETED', N'SHIPPED', N'FULFILLING', N'PAID', N'PENDING_PAYMENT', N'CANCELLED') AS NewStatus
  FROM store.StoreOrders o JOIN store.Payments p ON p.OrderId = o.Id)
SELECT * INTO #R FROM ranked;
UPDATE o SET Status=r.NewStatus,
  PaidAt = CASE WHEN r.NewStatus IN (N'PENDING_PAYMENT',N'CANCELLED') THEN NULL ELSE COALESCE(o.PaidAt, DATEADD(minute,2,o.CreatedAt)) END,
  CancelledAt = CASE WHEN r.NewStatus=N'CANCELLED' THEN COALESCE(o.CancelledAt, DATEADD(hour,1,o.CreatedAt)) ELSE NULL END
FROM store.StoreOrders o JOIN #R r ON r.Id=o.Id;
UPDATE p SET
  Status = CASE r.NewStatus WHEN N'PENDING_PAYMENT' THEN N'PENDING' WHEN N'CANCELLED' THEN N'FAILED' ELSE N'PAID' END,
  PaymentType = CASE WHEN r.NewStatus=N'PENDING_PAYMENT' THEN N'Credit_CreditCard' ELSE p.PaymentType END,
  RtnCode = CASE r.NewStatus WHEN N'PENDING_PAYMENT' THEN NULL WHEN N'CANCELLED' THEN @failRc ELSE 1 END,
  RtnMsg = CASE r.NewStatus WHEN N'PENDING_PAYMENT' THEN NULL WHEN N'CANCELLED' THEN @failMsg ELSE @paidMsg END,
  CallbackReceivedAt = CASE WHEN r.NewStatus=N'PENDING_PAYMENT' THEN NULL ELSE COALESCE(p.CallbackReceivedAt, DATEADD(minute,2,r.CreatedAt)) END
FROM store.Payments p JOIN #R r ON r.PaymentId=p.Id;
COMMIT;
SELECT Status, COUNT(*) FROM store.StoreOrders GROUP BY Status;
