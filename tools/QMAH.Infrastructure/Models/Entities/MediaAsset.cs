using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Models.Entities;

public partial class MediaAsset
{
    public Guid Id { get; set; }

    public long SequenceNo { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid? PostId { get; set; }

    public string OriginalFileName { get; set; } = null!;

    public string StoredPath { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public long FileSize { get; set; }

    public string? AltText { get; set; }

    // 圖片一律排入 AI 審查（辨識暴力／色情內容），沒有規則式訊號可以先篩掉一部分；
    // null 代表還沒審查，同 SocialPost.AiReviewedAt。
    public DateTime? AiReviewedAt { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ApplicationUser OwnerUser { get; set; } = null!;

    public virtual SocialPost? Post { get; set; }
}
