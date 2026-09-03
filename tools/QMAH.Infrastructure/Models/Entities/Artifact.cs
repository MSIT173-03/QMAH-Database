using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class Artifact
{
    public Guid Id { get; set; }

    public string ArtifactRef { get; set; } = null!;

    public string Name { get; set; } = null!;

    public Guid CategoryId { get; set; }

    public Guid EraBucketId { get; set; }

    public string? EraTextOriginal { get; set; }

    public string? CreatorDisplay { get; set; }

    public string? Description { get; set; }

    public string? SizeText { get; set; }

    public string PrimaryImagePath { get; set; } = null!;

    public string? ThumbnailPath { get; set; }

    public string SourceUrl { get; set; } = null!;

    public string? LicenseCode { get; set; }

    public string? AttributionText { get; set; }

    public bool IsActive { get; set; }

    public virtual ArtifactQuestionEntry? ArtifactQuestionEntry { get; set; }

    public virtual ICollection<ArtifactUnlock> ArtifactUnlocks { get; set; } = new List<ArtifactUnlock>();

    public virtual Product? Product { get; set; }

    public virtual ArtifactCategory Category { get; set; } = null!;

    public virtual EraBucket EraBucket { get; set; } = null!;
}