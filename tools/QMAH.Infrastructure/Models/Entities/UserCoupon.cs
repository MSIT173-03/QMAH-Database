using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class UserCoupon
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid CouponDefinitionId { get; set; }

    public string Status { get; set; } = null!;

    public DateTime IssuedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public Guid? IssuedByAdminUserId { get; set; }

    /// <summary>批次發放時回指活動主檔，單張發放則維持 null。</summary>
    public Guid? GrantBatchId { get; set; }

    public string? IssueReason { get; set; }

    public DateTime? RevokedAt { get; set; }

    public Guid? RevokedByAdminUserId { get; set; }

    /// <summary>批次撤銷時回指活動主檔，讓營運統計能從券回查來源。</summary>
    public Guid? RevokeBatchId { get; set; }

    public string? RevokeReason { get; set; }

    public virtual CouponDefinition CouponDefinition { get; set; } = null!;

    public virtual ICollection<StoreOrder> StoreOrders { get; set; } = new List<StoreOrder>();
}
