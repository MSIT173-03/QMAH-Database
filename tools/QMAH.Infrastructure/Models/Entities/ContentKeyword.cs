using System;

namespace QMAH.Infrastructure.Models.Entities;

public partial class ContentKeyword
{
    public Guid Id { get; set; }

    public string Keyword { get; set; } = null!;

    public string Action { get; set; } = null!;

    public string? Category { get; set; }

    public bool IsActive { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
