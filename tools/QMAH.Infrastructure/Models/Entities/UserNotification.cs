using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class UserNotification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Title { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string? TargetUrl { get; set; }

    public bool IsRead { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }
}