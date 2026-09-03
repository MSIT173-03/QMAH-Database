using System.Data;

using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Services.Common;

/// <summary>集中處理會員每日活動歷史與登入成就判定。</summary>
/// <remarks>
/// 這個服務只保存每位會員每天的活動事實；累積天數、連續天數、最高連續天數與登入率
/// 都在讀取時根據歷史資料計算。這樣不需要另外維護逐月或逐日統計快照，也能讓成就與
/// 營運中心使用同一個資料來源。RecordLoginAsync 由會員前台明確呼叫，管理後台登入不會自動觸發。
/// </remarks>
public sealed class DailyActivityService(QmahDbContext db)
{
    public const string LoginActivityType = "LOGIN";

    /// <summary>記錄一次會員前台登入活動，並依目前啟用的登入成就補發尚未取得的成就。</summary>
    public async Task<DailyActivitySummary> RecordLoginAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var activity = await db.DailyMemberActivities
            .SingleOrDefaultAsync(
                item => item.UserId == userId
                    && item.ActivityType == LoginActivityType
                    && item.ActivityDate == today,
                cancellationToken);

        if (activity is null)
        {
            db.DailyMemberActivities.Add(new DailyMemberActivity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ActivityType = LoginActivityType,
                ActivityDate = today,
                OccurrenceCount = 1,
                FirstOccurredAt = now,
                LastOccurredAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            // 同一天只更新歷史事實的次數與最後時間，不增加累積登入天數。
            activity.OccurrenceCount = checked(activity.OccurrenceCount + 1);
            activity.LastOccurredAt = now;
            activity.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await EnsureLoginAchievementsAsync(userId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetLoginSummaryAsync(userId, cancellationToken);
    }

    /// <summary>依會員的登入歷史即時計算每日登入進度。</summary>
    public async Task<DailyActivitySummary> GetLoginSummaryAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dates = await GetLoginDatesAsync(userId, today, cancellationToken);
        var memberCreatedAt = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => (DateTime?)user.CreatedAt)
            .SingleOrDefaultAsync(cancellationToken);
        var metrics = CalculateMetrics(dates, today, memberCreatedAt);

        return new DailyActivitySummary(
            dates.Count == 0 ? null : dates[^1],
            dates.Count > 0 && dates[^1] == today,
            metrics.TotalLoginDays,
            metrics.CurrentLoginStreak,
            metrics.LongestLoginStreak,
            metrics.LifetimeLoginRate);
    }

    private async Task<IReadOnlyList<DateOnly>> GetLoginDatesAsync(
        Guid userId,
        DateOnly throughDate,
        CancellationToken cancellationToken)
    {
        return await db.DailyMemberActivities
            .AsNoTracking()
            .Where(item => item.UserId == userId
                && item.ActivityType == LoginActivityType
                && item.ActivityDate <= throughDate)
            .OrderBy(item => item.ActivityDate)
            .Select(item => item.ActivityDate)
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureLoginAchievementsAsync(
        Guid userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(now);
        var dates = await GetLoginDatesAsync(userId, today, cancellationToken);
        if (dates.Count == 0)
            return;

        var metrics = CalculateMetrics(dates, today, memberCreatedAt: null);
        var definitions = await db.Achievements
            .Where(item => item.Status == "ACTIVE"
                && (item.ConditionType == "DAILY_LOGIN_COUNT"
                    || item.ConditionType == "DAILY_LOGIN_STREAK"))
            .ToListAsync(cancellationToken);
        if (definitions.Count == 0)
            return;

        var achievementIds = definitions.Select(item => item.Id).ToList();
        var earned = await db.UserAchievements
            .Where(item => item.UserId == userId && achievementIds.Contains(item.AchievementId))
            .Select(item => item.AchievementId)
            .ToHashSetAsync(cancellationToken);

        foreach (var definition in definitions)
        {
            var progress = definition.ConditionType == "DAILY_LOGIN_STREAK"
                ? metrics.CurrentLoginStreak
                : metrics.TotalLoginDays;
            if (progress < definition.ThresholdValue || earned.Contains(definition.Id))
                continue;

            // 登入成就只留下取得紀錄，不發鑑定點數、鑰匙或優惠券，避免 Prestige 反向形成經濟循環。
            db.UserAchievements.Add(new UserAchievement
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AchievementId = definition.Id,
                AchievedAt = now,
                IsDisplayed = false
            });
        }
    }

    private static LoginMetrics CalculateMetrics(
        IReadOnlyList<DateOnly> dates,
        DateOnly today,
        DateTime? memberCreatedAt)
    {
        if (dates.Count == 0)
            return new LoginMetrics(0, 0, 0, 0m);

        var longestStreak = 0;
        var trailingStreak = 0;
        DateOnly? previousDate = null;
        foreach (var date in dates)
        {
            trailingStreak = previousDate.HasValue && previousDate.Value.AddDays(1) == date
                ? trailingStreak + 1
                : 1;
            longestStreak = Math.Max(longestStreak, trailingStreak);
            previousDate = date;
        }

        var currentStreak = dates[^1] >= today.AddDays(-1)
            ? trailingStreak
            : 0;
        var startDate = memberCreatedAt.HasValue
            ? DateOnly.FromDateTime(memberCreatedAt.Value)
            : dates[0];
        var eligibleDays = Math.Max(1, today.DayNumber - startDate.DayNumber + 1);
        var loginRate = Math.Clamp(
            Math.Round(dates.Count / (decimal)eligibleDays, 4, MidpointRounding.AwayFromZero),
            0m,
            1m);

        return new LoginMetrics(dates.Count, currentStreak, longestStreak, loginRate);
    }

    private sealed record LoginMetrics(
        int TotalLoginDays,
        int CurrentLoginStreak,
        int LongestLoginStreak,
        decimal LifetimeLoginRate);
}

/// <summary>根據登入歷史即時計算出的會員進度，不是資料庫快照。</summary>
public sealed record DailyActivitySummary(
    DateOnly? LastLoginDate,
    bool HasLoggedInToday,
    int TotalLoginDays,
    int CurrentLoginStreak,
    int LongestLoginStreak,
    decimal LifetimeLoginRate);
