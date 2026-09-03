using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Models.Entities;

/// <summary>保存會員在某一個活動日期發生過活動的歷史事實。</summary>
/// <remarks>
/// 同一會員、活動類型與日期只保留一列；重複呼叫只增加 OccurrenceCount，不會無限建立登入事件。
/// TotalLoginDays、連續登入天數與登入率都由這些歷史資料即時計算，不另外保存一份統計快照。
/// </remarks>
public class DailyMemberActivity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string ActivityType { get; set; } = null!;

    public DateOnly ActivityDate { get; set; }

    /// <summary>同一天被記錄的次數；不參與累積登入天數計算。</summary>
    public int OccurrenceCount { get; set; }

    public DateTime FirstOccurredAt { get; set; }

    public DateTime LastOccurredAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = null!;

    public virtual ApplicationUser User { get; set; } = null!;
}
