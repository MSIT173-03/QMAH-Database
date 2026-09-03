using System;
using System.Collections.Generic;

using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Models.Entities;

public partial class UserAddress
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string AddressLabel { get; set; } = null!;

    public string RecipientName { get; set; } = null!;

    public string RecipientPhone { get; set; } = null!;

    public string? PostalCode { get; set; }

    public string? City { get; set; }

    public string? District { get; set; }

    public string AddressLine { get; set; } = null!;

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ApplicationUser User { get; set; } = null!;
}
