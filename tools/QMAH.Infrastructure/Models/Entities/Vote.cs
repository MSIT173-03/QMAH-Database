using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class Vote
{
    public Guid Id { get; set; }

    public Guid RoundId { get; set; }

    public Guid VoterGamePlayerId { get; set; }

    public Guid AnswerId { get; set; }

    public int Count { get; set; }

    public DateTime SubmittedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual RoundAnswer Answer { get; set; } = null!;

    public virtual GameRound Round { get; set; } = null!;

    public virtual GamePlayer VoterGamePlayer { get; set; } = null!;
}