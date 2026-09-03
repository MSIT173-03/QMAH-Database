using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class GameRoom
{
    public Guid Id { get; set; }

    public string RoomCode { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string Visibility { get; set; } = null!;

    public string? PasswordHash { get; set; }

    public byte MaxPlayers { get; set; }

    public byte TotalRounds { get; set; }

    public short AnswerSeconds { get; set; }

    public short VotingSeconds { get; set; }

    public string? CategoryFilterCode { get; set; }

    public string? EraBucketFilterCode { get; set; }

    public byte CurrentRoundNo { get; set; }

    public int StateVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ICollection<GamePlayer> GamePlayers { get; set; } = new List<GamePlayer>();

    public virtual ICollection<GameRoomInvitation> Invitations { get; set; } = new List<GameRoomInvitation>();

    public virtual ICollection<CommunityRewardCampaign> RewardCampaigns { get; set; } = new List<CommunityRewardCampaign>();

    public virtual ICollection<GameRound> GameRounds { get; set; } = new List<GameRound>();
}
