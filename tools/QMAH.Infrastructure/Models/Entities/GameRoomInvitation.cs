using System;

using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Models.Entities;

/// <summary>私人遊戲房間對指定會員發出的邀請與回應狀態。</summary>
/// <remarks>
/// 邀請本身不會預扣資產；接受邀請時由 CommunityRewardCampaign 決定是否發放，
/// 會員預算用完或背包不足時仍可加入房間，但不會被強制扣除更多資產。
/// </remarks>
public partial class GameRoomInvitation
{
    public Guid Id { get; set; }

    public Guid RoomId { get; set; }

    public Guid InviterUserId { get; set; }

    public Guid InviteeUserId { get; set; }

    public string Status { get; set; } = null!;

    public string? Message { get; set; }

    public int RewardPointAmount { get; set; }

    /// <summary>實際結算這筆邀請時所使用的加碼規則。</summary>
    public Guid? RewardCampaignId { get; set; }

    public Guid? RewardKeyDefinitionId { get; set; }

    public int RewardKeyAmount { get; set; }

    public DateTime? RewardGrantedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RespondedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual GameRoom Room { get; set; } = null!;

    public virtual ApplicationUser InviterUser { get; set; } = null!;

    public virtual ApplicationUser InviteeUser { get; set; } = null!;

    public virtual CommunityRewardCampaign? RewardCampaign { get; set; }

    public virtual KeyDefinition? RewardKeyDefinition { get; set; }
}
