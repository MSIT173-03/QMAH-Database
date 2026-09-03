namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// Mini Game 累積的鑰匙進度；達到設定門檻後轉成一般鑰匙。
/// </summary>
public partial class KeyProgressBalance
{
    public Guid UserId { get; set; }

    public int Balance { get; set; }

    public DateTime UpdatedAt { get; set; }
}
