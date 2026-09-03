using System;
using System.Collections.Generic;

namespace QMAH.Infrastructure.Models.Entities;

/// <summary>定義活動或私人房間的參與加碼規則與總量上限。</summary>
/// <remarks>
/// 會員建立的規則使用 LIMITED 預算，實際發放時才從活動發起人的背包扣除；
/// OFFICIAL 規則使用 UNLIMITED，由管理員設定有效期間，不會扣除管理員的個人資產。
/// </remarks>
public partial class CommunityRewardCampaign
{
    public Guid Id { get; set; }

    public string TargetType { get; set; } = null!;

    public Guid? EventId { get; set; }

    public Guid? GameRoomId { get; set; }

    public Guid OwnerUserId { get; set; }

    public string SponsorType { get; set; } = null!;

    public string BudgetMode { get; set; } = null!;

    /// <summary>每位符合條件的參與者可取得的鑑定點數。</summary>
    public int PointPerRecipient { get; set; }

    /// <summary>每位符合條件的參與者可取得的鑰匙種類。</summary>
    public Guid? KeyDefinitionId { get; set; }

    /// <summary>每位符合條件的參與者可取得的鑰匙數量。</summary>
    public int KeyPerRecipient { get; set; }

    /// <summary>會員活動的點數總預算；官方活動使用 UNLIMITED 時為 0。</summary>
    public int PointBudget { get; set; }

    /// <summary>會員活動已發放的點數總量。</summary>
    public int PointIssued { get; set; }

    /// <summary>會員活動的鑰匙總預算；官方活動使用 UNLIMITED 時為 0。</summary>
    public int KeyBudget { get; set; }

    /// <summary>會員活動已發放的鑰匙總量。</summary>
    public int KeyIssued { get; set; }

    public DateTime ValidFrom { get; set; }

    public DateTime ValidUntil { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual Event? Event { get; set; }

    public virtual GameRoom? GameRoom { get; set; }

    public virtual KeyDefinition? KeyDefinition { get; set; }

    public virtual ICollection<EventRegistration> EventRegistrations { get; set; } = new List<EventRegistration>();

    public virtual ICollection<GameRoomInvitation> GameRoomInvitations { get; set; } = new List<GameRoomInvitation>();
}
