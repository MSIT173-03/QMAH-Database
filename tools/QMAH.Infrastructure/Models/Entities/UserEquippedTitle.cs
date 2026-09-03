namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// 每位會員最多一筆的目前配戴稱號。
/// </summary>
public partial class UserEquippedTitle
{
    public Guid UserId { get; set; }

    public Guid UserAchievementId { get; set; }

    public DateTime EquippedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual UserAchievement UserAchievement { get; set; } = null!;
}
