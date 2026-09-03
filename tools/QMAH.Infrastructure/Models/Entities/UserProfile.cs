using System;
using System.Collections.Generic;

using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Models.Entities;

public partial class UserProfile
{
    public Guid UserId { get; set; }

    public string Nickname { get; set; } = null!;

    public string? AvatarPath { get; set; }

    public string? Bio { get; set; }

    public string Visibility { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ApplicationUser User { get; set; } = null!;
}