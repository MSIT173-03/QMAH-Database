using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class RoundAnswer
{
    public Guid Id { get; set; }

    public Guid RoundId { get; set; }

    public Guid GamePlayerId { get; set; }

    public string AnswerType { get; set; } = null!;

    public string Text { get; set; } = null!;

    public DateTime SubmittedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual GamePlayer GamePlayer { get; set; } = null!;

    public virtual GameRound Round { get; set; } = null!;

    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();
}