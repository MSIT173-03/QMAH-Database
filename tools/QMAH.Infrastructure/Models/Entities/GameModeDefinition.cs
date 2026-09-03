namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// Mini Game 的通用玩法契約與可調獎勵設定。
/// </summary>
public partial class GameModeDefinition
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string? ConfigJson { get; set; }

    public bool IsActive { get; set; }

    public int GradeBThreshold { get; set; }

    public int GradeAThreshold { get; set; }

    public int GradeSThreshold { get; set; }

    public int FailPointReward { get; set; }

    public int FailKeyProgressReward { get; set; }

    public int BPointReward { get; set; }

    public int BKeyProgressReward { get; set; }

    public int APointReward { get; set; }

    public int AKeyProgressReward { get; set; }

    public int SPointReward { get; set; }

    public int SKeyProgressReward { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ICollection<MiniGameAttempt> Attempts { get; set; } = new List<MiniGameAttempt>();
}
