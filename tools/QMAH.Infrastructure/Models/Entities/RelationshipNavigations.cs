namespace QMAH.Infrastructure.Models.Entities;

public partial class Artifact
{
    public virtual ICollection<GameRound> GameRounds { get; set; } = [];
    public virtual ICollection<SocialPost> SocialPosts { get; set; } = [];
}

public partial class GameRound
{
    public virtual Artifact Artifact { get; set; } = null!;
    public virtual ICollection<ArtifactUnlock> ArtifactUnlocks { get; set; } = [];
}

public partial class SocialPost
{
    public virtual Artifact? Artifact { get; set; }
}