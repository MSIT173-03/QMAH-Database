using System;

namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// 會員對商城商品留下的星等與簡短心得。
/// </summary>
public partial class ProductReview
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public Guid UserId { get; set; }

    public byte Rating { get; set; }

    public string Content { get; set; } = null!;

    public string Status { get; set; } = "PUBLISHED";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
