using Microsoft.AspNetCore.Identity;

using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Models.Identity;

public partial class ApplicationUser : IdentityUser<Guid>
{
    public string Status { get; set; } = "ACTIVE";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public UserProfile? Profile { get; set; }

    public ICollection<UserAddress> Addresses { get; } = [];

    public ICollection<UserAchievement> Achievements { get; } = [];
}