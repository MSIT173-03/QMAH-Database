namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// 一次批次資產活動的主檔；明細仍分別寫入點數流水或會員優惠券，主檔則供營運統計與稽核使用。
/// </summary>
public sealed class EconomyAdjustmentBatch
{
    public Guid Id { get; set; }

    /// <summary>資產種類，目前支援 POINT 與 COUPON。</summary>
    public string AssetType { get; set; } = null!;

    /// <summary>異動方向：ADD 代表增加，DEDUCT 代表扣除。</summary>
    public string Operation { get; set; } = null!;

    /// <summary>每位符合條件會員的異動數量。</summary>
    public int UnitAmount { get; set; }

    public Guid? CouponDefinitionId { get; set; }

    /// <summary>送出時使用的篩選條件快照，避免日後無法還原批次對象。</summary>
    public string FilterJson { get; set; } = null!;

    public string Reason { get; set; } = null!;

    public Guid CreatedByAdminUserId { get; set; }

    public string Status { get; set; } = null!;

    public int TargetCount { get; set; }

    public int SucceededCount { get; set; }

    public int FailedCount { get; set; }

    /// <summary>實際新增或扣除的點數／優惠券總數；失敗批次為 0。</summary>
    public long AffectedAssetCount { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public CouponDefinition? CouponDefinition { get; set; }
}
