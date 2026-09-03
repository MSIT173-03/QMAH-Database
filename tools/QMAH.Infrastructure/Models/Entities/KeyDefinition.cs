using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

/// <summary>定義一種圖鑑解鎖鑰匙及其適用的文物範圍。</summary>
public partial class KeyDefinition
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string ScopeType { get; set; } = null!;

    public Guid? CategoryId { get; set; }

    public Guid? EraBucketId { get; set; }

    public bool IsActive { get; set; }

    /// <summary>當會員已沒有符合範圍的可解鎖文物時，每把鑰匙可回收的鑑定點數。</summary>
    public int RecyclePointValue { get; set; }

    public virtual ArtifactCategory? Category { get; set; }

    public virtual EraBucket? EraBucket { get; set; }

    public virtual ICollection<KeyTransaction> KeyTransactions { get; set; } = new List<KeyTransaction>();

    public virtual ICollection<KeyExchangeRule> SourceExchangeRules { get; set; } = new List<KeyExchangeRule>();

    public virtual ICollection<KeyExchangeRule> TargetExchangeRules { get; set; } = new List<KeyExchangeRule>();

    public virtual ICollection<CommunityRewardCampaign> RewardCampaigns { get; set; } = new List<CommunityRewardCampaign>();

    public virtual ICollection<GameRoomInvitation> RewardInvitations { get; set; } = new List<GameRoomInvitation>();

    public virtual ICollection<EventRegistration> RewardRegistrations { get; set; } = new List<EventRegistration>();

    public virtual ICollection<UserKeyBalance> UserKeyBalances { get; set; } = new List<UserKeyBalance>();
}
