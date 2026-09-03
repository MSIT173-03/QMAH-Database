using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Models.Identity;

/// <summary>
/// 對應各功能資料表指向 ASP.NET Core Identity 使用者的導覽屬性。
/// </summary>
public partial class ApplicationUser
{
    public ICollection<ArtifactUnlock> ArtifactUnlocks { get; } = [];
    public ICollection<CartItem> CartItems { get; } = [];
    public ICollection<ContentReport> SubmittedContentReports { get; } = [];
    public ICollection<ContentReport> ReviewedContentReports { get; } = [];
    public ICollection<Event> OrganizedEvents { get; } = [];
    public ICollection<Event> ReviewedEvents { get; } = [];
    public ICollection<EventRegistration> EventRegistrations { get; } = [];
    public ICollection<GameRoomInvitation> SentGameRoomInvitations { get; } = [];
    public ICollection<GameRoomInvitation> ReceivedGameRoomInvitations { get; } = [];
    public ICollection<CommunityRewardCampaign> OwnedRewardCampaigns { get; } = [];
    public ICollection<GamePlayer> GamePlayers { get; } = [];
    public ICollection<KeyTransaction> KeyTransactions { get; } = [];
    public ICollection<OfficialAnnouncement> OfficialAnnouncements { get; } = [];
    public PointBalance? PointBalance { get; set; }
    public ICollection<PointTransaction> PointTransactions { get; } = [];
    public ICollection<ProductReview> ProductReviews { get; } = [];
    public ICollection<SocialComment> SocialComments { get; } = [];
    public ICollection<SocialPost> SocialPosts { get; } = [];
    public ICollection<StoreOrder> StoreOrders { get; } = [];
    public ICollection<UserCoupon> Coupons { get; } = [];
    public ICollection<UserKeyBalance> KeyBalances { get; } = [];
    public ICollection<KeyProgressTransaction> KeyProgressTransactions { get; } = [];
    public KeyProgressBalance? KeyProgressBalance { get; set; }
    public ICollection<MiniGameAttempt> MiniGameAttempts { get; } = [];
    public ICollection<EconomyAdjustmentBatch> EconomyAdjustmentBatches { get; } = [];
    public ICollection<DailyMemberActivity> DailyMemberActivities { get; } = [];
    public ICollection<UserNotification> Notifications { get; } = [];
}
