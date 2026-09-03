using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class StoreOrder
{
    public Guid Id { get; set; }

    public string OrderNo { get; set; } = null!;

    public Guid UserId { get; set; }

    public Guid? UserCouponId { get; set; }

    public string Status { get; set; } = null!;

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public int PointsUsed { get; set; }

    public decimal TotalAmount { get; set; }

    public string RecipientName { get; set; } = null!;

    public string RecipientPhone { get; set; } = null!;

    public string ShippingPostalCode { get; set; } = null!;

    public string ShippingCity { get; set; } = null!;

    public string ShippingDistrict { get; set; } = null!;

    public string ShippingAddressLine { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public virtual ICollection<OrderDetail> OrderDetails { get; set; } = new List<OrderDetail>();

    public virtual Payment? Payment { get; set; }

    public virtual UserCoupon? UserCoupon { get; set; }
}