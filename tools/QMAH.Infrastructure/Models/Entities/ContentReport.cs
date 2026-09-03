using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class ContentReport
{
    public Guid Id { get; set; }

    public Guid ReporterUserId { get; set; }

    public string TargetType { get; set; } = null!;

    public Guid TargetId { get; set; }

    public string Reason { get; set; } = null!;

    public string? Detail { get; set; }

    public string Status { get; set; } = null!;

    public string? Resolution { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}