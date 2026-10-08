using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class PaymentAttempt
{
    public Guid Id { get; set; }

    public Guid PaymentId { get; set; }

    public string MerchantTradeNo { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? EcpayTradeNo { get; set; }

    public int? RtnCode { get; set; }

    public string? RtnMsg { get; set; }

    public DateTime? CallbackReceivedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Payment Payment { get; set; } = null!;
}
