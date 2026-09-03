using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class ArtifactUnlock
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ArtifactId { get; set; }

    public string UnlockMethod { get; set; } = null!;

    public Guid? GameRoundId { get; set; }

    public Guid? KeyTransactionId { get; set; }

    public DateTime UnlockedAt { get; set; }

    public virtual Artifact Artifact { get; set; } = null!;

    public virtual KeyTransaction? KeyTransaction { get; set; }
}