using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

public partial class EventRegistration
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    public Guid UserId { get; set; }

    public string Status { get; set; } = null!;

    public DateTime RegisteredAt { get; set; }

    /// <summary>這次報名實際取得的鑑定點數；沒有活動加碼時為 0。</summary>
    public int RewardPointAmount { get; set; }

    /// <summary>實際結算這筆報名時所使用的加碼規則。</summary>
    public Guid? RewardCampaignId { get; set; }

    public Guid? RewardKeyDefinitionId { get; set; }

    /// <summary>這次報名實際取得的鑰匙數量；沒有活動加碼時為 0。</summary>
    public int RewardKeyAmount { get; set; }

    public DateTime? RewardGrantedAt { get; set; }

    public virtual Event Event { get; set; } = null!;

    public virtual CommunityRewardCampaign? RewardCampaign { get; set; }

    public virtual KeyDefinition? RewardKeyDefinition { get; set; }
}
