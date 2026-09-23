using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

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

    /// <summary>商品折扣百分比；0 代表沒有單品折扣，100 代表全額折抵。</summary>
    public decimal DiscountRate { get; set; }

    /// <summary>管理員指定的單品折扣後售價；有效值優先於 DiscountRate。</summary>
    public decimal? SalePrice { get; set; }

    /// <summary>依定價與折扣率計算的目前有效售價。</summary>
    [NotMapped]
    public decimal EffectivePrice => IsValidSalePrice(SalePrice, Price)
        ? SalePrice!.Value
        : CalculateDiscountPrice(Price, DiscountRate);

    private static decimal CalculateDiscountPrice(decimal price, decimal discountRate)
    {
        var normalizedRate = Math.Clamp(discountRate, 0m, 100m);
        return decimal.Round(price * (100m - normalizedRate) / 100m, 2, MidpointRounding.AwayFromZero);
    }

    private static bool IsValidSalePrice(decimal? salePrice, decimal price) =>
        salePrice is > 0m && salePrice < price;

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
