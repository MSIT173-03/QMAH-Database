using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class SocialPost
{
    public Guid Id { get; set; }

    public string BoardCode { get; set; } = null!;

    public Guid UserId { get; set; }

    public Guid? ArtifactId { get; set; }

    public Guid? EventId { get; set; }

    public string PostType { get; set; } = "POST";

    public string PublisherType { get; set; } = "COMMUNITY";

    public string ContentMode { get; set; } = "CUSTOM";

    public string Title { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string? LocationName { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public long? SimHash { get; set; }

    // null 代表還沒排到 AI 複審（見 AiContentReviewWorker）；有值代表已經審查過，不管結果是否命中
    // 都不會再被排程撿到，避免同一篇貼文重複呼叫 AI。
    public DateTime? AiReviewedAt { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<SocialComment> SocialComments { get; set; } = new List<SocialComment>();

    public virtual ICollection<MediaAsset> MediaAssets { get; set; } = new List<MediaAsset>();

    public virtual Event? Event { get; set; }
}
