using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class GameRound
{
    public Guid Id { get; set; }

    public Guid RoomId { get; set; }

    public Guid ArtifactId { get; set; }

    public int RoundNumber { get; set; }

    public string Status { get; set; } = null!;

    public int StateVersion { get; set; }

    public bool IsSettled { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime AnswerDeadlineAt { get; set; }

    public DateTime VotingDeadlineAt { get; set; }

    public DateTime? SettledAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual GameRoom Room { get; set; } = null!;

    public virtual ICollection<RoundAnswer> RoundAnswers { get; set; } = new List<RoundAnswer>();

    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();
}