using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Models.Entities;

public partial class ArtifactUnlock
{
    public virtual ApplicationUser User { get; set; } = null!;
    public virtual GameRound? GameRound { get; set; }
}

public partial class CartItem
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class ContentReport
{
    public virtual ApplicationUser ReporterUser { get; set; } = null!;
    public virtual ApplicationUser? ReviewedByUser { get; set; }
}

public partial class Event
{
    public virtual ApplicationUser? OrganizerUser { get; set; }
    public virtual ApplicationUser? ReviewedByUser { get; set; }
}

public partial class EventRegistration
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class GamePlayer
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class KeyTransaction
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class OfficialAnnouncement
{
    public virtual ApplicationUser CreatedByUser { get; set; } = null!;
}

public partial class PointBalance
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class PointTransaction
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class SocialComment
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class SocialPost
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class StoreOrder
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class ProductReview
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class UserCoupon
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class UserKeyBalance
{
    public virtual ApplicationUser User { get; set; } = null!;
}

public partial class UserNotification
{
    public virtual ApplicationUser User { get; set; } = null!;
}
