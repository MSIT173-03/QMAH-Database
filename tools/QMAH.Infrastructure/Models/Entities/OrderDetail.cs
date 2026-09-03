using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class OrderDetail
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid ProductId { get; set; }

    public string ProductNameSnapshot { get; set; } = null!;

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public decimal LineTotal { get; set; }

    public virtual StoreOrder Order { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}