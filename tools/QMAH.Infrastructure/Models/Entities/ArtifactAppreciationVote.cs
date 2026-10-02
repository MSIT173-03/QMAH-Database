namespace QMAH.Infrastructure.Models.Entities;

/// <summary>鑑賞區的一人一票。沿用原回答，不複製文字，也不改動遊戲回合票數。</summary>
public sealed class ArtifactAppreciationVote
{
    public Guid AnswerId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public RoundAnswer Answer { get; set; } = null!;
}
