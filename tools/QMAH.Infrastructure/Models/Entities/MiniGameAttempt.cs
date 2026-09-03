namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// 四種 Mini Game 共用的開始、結果與獎勵紀錄。
/// </summary>
public partial class MiniGameAttempt
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid GameModeDefinitionId { get; set; }

    public Guid? ArtifactId { get; set; }

    public string? ArtifactPoolJson { get; set; }

    public string Difficulty { get; set; } = null!;

    public string Seed { get; set; } = null!;

    public string? ConfigJson { get; set; }

    public string Status { get; set; } = null!;

    public int? RawScore { get; set; }

    public string? RawResultJson { get; set; }

    public int? NormalizedScore { get; set; }

    public string? Grade { get; set; }

    public int PointReward { get; set; }

    public int KeyProgressReward { get; set; }

    public int? RewardAttemptNo { get; set; }

    public bool RewardGranted { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual GameModeDefinition GameModeDefinition { get; set; } = null!;

    public virtual Artifact? Artifact { get; set; }
}
