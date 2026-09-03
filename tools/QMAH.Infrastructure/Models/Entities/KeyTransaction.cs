using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class KeyTransaction
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid KeyDefinitionId { get; set; }

    public int Amount { get; set; }

    public string Reason { get; set; } = null!;

    public string? ReferenceType { get; set; }

    public Guid? ReferenceId { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<ArtifactUnlock> ArtifactUnlocks { get; set; } = new List<ArtifactUnlock>();

    public virtual KeyDefinition KeyDefinition { get; set; } = null!;
}