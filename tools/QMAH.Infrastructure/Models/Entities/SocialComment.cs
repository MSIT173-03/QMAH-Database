using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class SocialComment
{
    public Guid Id { get; set; }

    public Guid PostId { get; set; }

    public Guid? ParentCommentId { get; set; }

    public Guid UserId { get; set; }

    public string Content { get; set; } = null!;

    public long? SimHash { get; set; }

    // 同 SocialPost.AiReviewedAt：null 代表還沒排到 AI 複審。
    public DateTime? AiReviewedAt { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<SocialComment> InverseParentComment { get; set; } = new List<SocialComment>();

    public virtual SocialComment? ParentComment { get; set; }

    public virtual SocialPost Post { get; set; } = null!;
}