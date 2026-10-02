using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class GamePlayer
{
    public Guid Id { get; set; }

    public Guid RoomId { get; set; }

    public Guid UserId { get; set; }

    public string PlayerKey { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public string Role { get; set; } = null!;

    public bool IsReady { get; set; }

    public byte? SeatNo { get; set; }

    public DateTime JoinedAt { get; set; }

    public string ConnectionStatus { get; set; } = null!;

    public DateTime LastSeenAt { get; set; }

    public DateTime? DisconnectedAt { get; set; }

    public DateTime? ReconnectDeadlineAt { get; set; }

    public DateTime? LeftAt { get; set; }

    /// <summary>本場獎勵收據。即使點數已達上限，仍記錄鑰匙與結算結果，避免重領。</summary>
    public DateTime? RewardClaimedAt { get; set; }

    public int? RewardPoints { get; set; }

    public int? RewardNormalKeys { get; set; }

    public int? RewardPerformanceScore { get; set; }

    public int? RewardRoundsWon { get; set; }

    public decimal? RewardKeyProgress { get; set; }

    public byte? RewardKeyDivisor { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual GameRoom Room { get; set; } = null!;

    public virtual ICollection<RoundAnswer> RoundAnswers { get; set; } = new List<RoundAnswer>();

    public virtual ICollection<Vote> Votes { get; set; } = new List<Vote>();
}
