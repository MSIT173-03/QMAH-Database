using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class Payment
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public string MerchantTradeNo { get; set; } = null!;

    public string? EcpayTradeNo { get; set; }

    public decimal Amount { get; set; }

    public string Status { get; set; } = null!;

    public int? RtnCode { get; set; }

    public string? RtnMsg { get; set; }

    public string? PaymentType { get; set; }

    public DateTime? CallbackReceivedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual StoreOrder Order { get; set; } = null!;
}