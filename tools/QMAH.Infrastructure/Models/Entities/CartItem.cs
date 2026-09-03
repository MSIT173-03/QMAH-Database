using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class CartItem
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ProductId { get; set; }

    public int Quantity { get; set; }

    public DateTime AddedAt { get; set; }

    public virtual Product Product { get; set; } = null!;
}