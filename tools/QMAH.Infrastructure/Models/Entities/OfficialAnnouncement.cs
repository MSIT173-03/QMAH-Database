using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class OfficialAnnouncement
{
    public Guid Id { get; set; }

    public string Title { get; set; } = null!;

    public string? Summary { get; set; }

    public string Content { get; set; } = null!;

    public string Category { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime? PublishAt { get; set; }

    public DateTime? EndAt { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

}
