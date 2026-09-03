using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class Product
{
    public Guid Id { get; set; }

    public Guid? ArtifactId { get; set; }

    public string CategoryCode { get; set; } = null!;

    public string? ExternalRef { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public string? SizeText { get; set; }

    public decimal Price { get; set; }

    public int Stock { get; set; }

    public string? PrimaryImagePath { get; set; }

    public string? SourceUrl { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual ICollection<CartItem> CartItems { get; set; } = new List<CartItem>();

    public virtual Artifact? Artifact { get; set; }

    public virtual ICollection<OrderDetail> OrderDetails { get; set; } = new List<OrderDetail>();

    public virtual ICollection<ProductReview> ProductReviews { get; set; } = new List<ProductReview>();
}
