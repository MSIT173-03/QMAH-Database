namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// 多人主遊戲獎勵的單一目前版本設定。
/// </summary>
public partial class GameEconomySetting
{
    public byte Id { get; set; }

    public int MinimumPointReward { get; set; }

    public int MaximumPointReward { get; set; }

    public int BasePointReward { get; set; }

    public int MaximumVoteBonus { get; set; }

    public int MaximumWinBonus { get; set; }

    public int CompletedNormalKey { get; set; }

    public int ExcellentExtraNormalKey { get; set; }

    public int ExcellentThreshold { get; set; }

    public int DailyMiniGameRewardLimit { get; set; }

    public int KeyProgressToNormalKey { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;
}
