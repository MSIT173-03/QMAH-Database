using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class ArtifactQuestionEntry
{
    public Guid Id { get; set; }

    public Guid ArtifactId { get; set; }

    public bool IsEnabled { get; set; }

    public byte Difficulty { get; set; }

    public string QuestionTemplateCode { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Artifact Artifact { get; set; } = null!;
}