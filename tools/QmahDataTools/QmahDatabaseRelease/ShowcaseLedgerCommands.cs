using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.DataTools;

/// <summary>
/// 為本機展示會員建立可重跑的活動、資產流水與登入成就資料。
/// </summary>
/// <remarks>
/// 成就定義從資料庫讀取；本類別只依目前啟用中的登入成就定義判定成就，
/// 不把成就代碼或門檻複製成另一份固定清單。點數、鑰匙與進度流水使用
/// SHOWCASE_GENERATED 標記，重跑時只更新本工具管理的列，不改動其他資料。
/// </remarks>
public static class ShowcaseLedgerCommands
{
    public const string GeneratedReferenceType = "SHOWCASE_GENERATED";

    private const string LoginActivityType = "LOGIN";
    private const string CheckInActivityType = "CHECK_IN";

    public static async Task<ShowcaseLedgerResult> GenerateAsync(
        QmahDbContext db,
        int activityDays,
        int pointTransactionCount,
        int keyTransactionCount,
        int keyProgressTransactionCount,
        int seed)
    {
        ValidateRange(activityDays, 0, 3650, nameof(activityDays));
        ValidateRange(pointTransactionCount, 0, 10000, nameof(pointTransactionCount));
        ValidateRange(keyTransactionCount, 0, 10000, nameof(keyTransactionCount));
        ValidateRange(keyProgressTransactionCount, 0, 10000, nameof(keyProgressTransactionCount));

        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.Status == "ACTIVE"
                && user.Email != null
                && (user.Email.EndsWith("@qmah.local") || user.Email.EndsWith("@qmah.test")))
            .OrderBy(user => user.Email)
            .Select(user => new ShowcaseUser(user.Id, user.Email!))
            .ToListAsync();
        if (users.Count == 0)
            throw new InvalidOperationException("找不到展示會員，請先執行 seed-showcase-users。");

        var now = DateTime.UtcNow;
        var activity = await UpsertDailyActivitiesAsync(db, users, activityDays, seed, now);
        var pointCount = await UpsertPointTransactionsAsync(db, users, pointTransactionCount, seed, now);
        var keyCount = await UpsertKeyTransactionsAsync(db, users, keyTransactionCount, seed, now);
        var keyProgressCount = await UpsertKeyProgressTransactionsAsync(
            db, users, keyProgressTransactionCount, seed, now);
        var achievementCount = await UpsertLoginAchievementsAsync(
            db, users, activity.LoginDatesByUser, now);

        return new ShowcaseLedgerResult(
            activity.TotalCount,
            activity.LoginCount,
            activity.CheckInCount,
            pointCount,
            keyCount,
            keyProgressCount,
            achievementCount);
    }

    private static async Task<ActivityGenerationResult> UpsertDailyActivitiesAsync(
        QmahDbContext db,
        IReadOnlyList<ShowcaseUser> users,
        int activityDays,
        int seed,
        DateTime now)
    {
        var throughDate = DateOnly.FromDateTime(now).AddDays(-1);
        var drafts = new List<DailyActivityDraft>();
        for (var userIndex = 0; userIndex < users.Count; userIndex++)
        {
            for (var offset = 0; offset < activityDays; offset++)
            {
                var activityDate = throughDate.AddDays(-offset);
                var hour = 8 + (int)(StableNumber($"activity-time:{seed}:{userIndex}:{offset}") % 10);
                var firstOccurredAt = DateTime.SpecifyKind(
                    activityDate.ToDateTime(new TimeOnly(hour, 10)),
                    DateTimeKind.Utc);
                var occurrenceCount = 1 + ((userIndex + offset) % 4 == 0 ? 1 : 0);
                drafts.Add(new DailyActivityDraft(
                    users[userIndex].Id,
                    LoginActivityType,
                    activityDate,
                    occurrenceCount,
                    firstOccurredAt,
                    firstOccurredAt.AddMinutes(occurrenceCount > 1 ? 35 : 0)));

                // CHECK_IN 與 LOGIN 是 Schema 中兩種不同的每日活動事實；
                // 只在部分日期建立 CHECK_IN，讓查詢畫面能呈現兩種活動類型。
                if ((userIndex + offset + seed) % 4 == 0)
                {
                    var checkInAt = firstOccurredAt.AddMinutes(12);
                    drafts.Add(new DailyActivityDraft(
                        users[userIndex].Id,
                        CheckInActivityType,
                        activityDate,
                        1,
                        checkInAt,
                        checkInAt));
                }
            }
        }

        var userIds = users.Select(user => user.Id).ToArray();
        var existing = activityDays == 0
            ? []
            : await db.DailyMemberActivities
                .Where(activity => userIds.Contains(activity.UserId)
                    && activity.ActivityDate >= throughDate.AddDays(-(activityDays - 1))
                    && activity.ActivityDate <= throughDate)
                .ToListAsync();
        var existingByKey = existing.ToDictionary(
            activity => new ActivityKey(activity.UserId, activity.ActivityType, activity.ActivityDate));

        foreach (var draft in drafts)
        {
            var key = new ActivityKey(draft.UserId, draft.ActivityType, draft.ActivityDate);
            if (!existingByKey.TryGetValue(key, out var activity))
            {
                activity = new DailyMemberActivity
                {
                    Id = StableGuid($"showcase-activity:{draft.UserId:N}:{draft.ActivityType}:{draft.ActivityDate:yyyy-MM-dd}"),
                    UserId = draft.UserId,
                    ActivityType = draft.ActivityType,
                    ActivityDate = draft.ActivityDate,
                    CreatedAt = draft.FirstOccurredAt
                };
                db.DailyMemberActivities.Add(activity);
                existingByKey[key] = activity;
            }

            activity.OccurrenceCount = draft.OccurrenceCount;
            activity.FirstOccurredAt = draft.FirstOccurredAt;
            activity.LastOccurredAt = draft.LastOccurredAt;
            activity.UpdatedAt = now;
        }

        var storedLoginDates = await db.DailyMemberActivities
            .AsNoTracking()
            .Where(activity => userIds.Contains(activity.UserId)
                && activity.ActivityType == LoginActivityType)
            .Select(activity => new { activity.UserId, activity.ActivityDate })
            .ToListAsync();
        var loginDatesByUser = users.ToDictionary(
            user => user.Id,
            _ => new HashSet<DateOnly>());
        foreach (var row in storedLoginDates)
            loginDatesByUser[row.UserId].Add(row.ActivityDate);
        foreach (var draft in drafts.Where(draft => draft.ActivityType == LoginActivityType))
            loginDatesByUser[draft.UserId].Add(draft.ActivityDate);

        return new ActivityGenerationResult(
            drafts.Count,
            drafts.Count(draft => draft.ActivityType == LoginActivityType),
            drafts.Count(draft => draft.ActivityType == CheckInActivityType),
            loginDatesByUser);
    }

    private static async Task<int> UpsertPointTransactionsAsync(
        QmahDbContext db,
        IReadOnlyList<ShowcaseUser> users,
        int transactionCount,
        int seed,
        DateTime now)
    {
        var userIds = users.Select(user => user.Id).ToArray();
        var existing = await db.PointTransactions
            .Where(transaction => userIds.Contains(transaction.UserId)
                && transaction.ReferenceType == GeneratedReferenceType)
            .ToListAsync();
        var balances = await db.PointBalances
            .Where(balance => userIds.Contains(balance.UserId))
            .ToDictionaryAsync(balance => balance.UserId);
        var oldSums = existing
            .GroupBy(transaction => transaction.UserId)
            .ToDictionary(group => group.Key, group => group.Sum(transaction => (long)transaction.Amount));
        var baseline = users.ToDictionary(
            user => user.Id,
            user => BaseBalance(balances, user.Id, oldSums.GetValueOrDefault(user.Id)));

        var drafts = BuildPointDrafts(users, transactionCount, seed, now);
        var desiredIds = drafts.Select(draft => draft.Id).ToHashSet();
        var existingById = existing.ToDictionary(transaction => transaction.Id);
        foreach (var stale in existing.Where(transaction => !desiredIds.Contains(transaction.Id)))
            db.PointTransactions.Remove(stale);

        foreach (var draft in drafts)
        {
            if (!existingById.TryGetValue(draft.Id, out var transaction))
            {
                transaction = new PointTransaction { Id = draft.Id };
                db.PointTransactions.Add(transaction);
            }

            transaction.UserId = draft.UserId;
            transaction.Amount = draft.Amount;
            transaction.Reason = draft.Reason;
            transaction.ReferenceType = GeneratedReferenceType;
            transaction.ReferenceId = null;
            transaction.CreatedAt = draft.CreatedAt;
        }

        var newSums = drafts
            .GroupBy(draft => draft.UserId)
            .ToDictionary(group => group.Key, group => group.Sum(draft => (long)draft.Amount));
        foreach (var user in users)
        {
            if (!newSums.ContainsKey(user.Id) && !baseline.ContainsKey(user.Id))
                continue;

            var finalBalance = CheckedBalance(baseline[user.Id] + newSums.GetValueOrDefault(user.Id));
            if (!balances.TryGetValue(user.Id, out var balance))
            {
                balance = new PointBalance { UserId = user.Id };
                db.PointBalances.Add(balance);
                balances[user.Id] = balance;
            }

            balance.Balance = finalBalance;
            balance.UpdatedAt = now;
        }

        return drafts.Count;
    }

    private static async Task<int> UpsertKeyTransactionsAsync(
        QmahDbContext db,
        IReadOnlyList<ShowcaseUser> users,
        int transactionCount,
        int seed,
        DateTime now)
    {
        var definitions = await db.KeyDefinitions
            .AsNoTracking()
            .Where(definition => definition.IsActive)
            .OrderBy(definition => definition.Code)
            .ToListAsync();
        if (definitions.Count == 0)
            Console.Error.WriteLine("SHOWCASE_LEDGER_WARNING|沒有啟用中的鑰匙定義，略過鑰匙流水。");

        var userIds = users.Select(user => user.Id).ToArray();
        var existing = await db.KeyTransactions
            .Where(transaction => userIds.Contains(transaction.UserId)
                && transaction.ReferenceType == GeneratedReferenceType)
            .ToListAsync();
        var balances = await db.UserKeyBalances
            .Where(balance => userIds.Contains(balance.UserId))
            .ToDictionaryAsync(balance => new UserKey(balance.UserId, balance.KeyDefinitionId));
        var oldSums = existing
            .GroupBy(transaction => new UserKey(transaction.UserId, transaction.KeyDefinitionId))
            .ToDictionary(group => group.Key, group => group.Sum(transaction => (long)transaction.Amount));
        var baseline = balances.ToDictionary(
            pair => pair.Key,
            pair => BaseBalance(pair.Value.Balance, oldSums.GetValueOrDefault(pair.Key)));

        var drafts = BuildKeyDrafts(users, definitions, transactionCount, seed, now);
        var desiredIds = drafts.Select(draft => draft.Id).ToHashSet();
        var existingById = existing.ToDictionary(transaction => transaction.Id);
        foreach (var stale in existing.Where(transaction => !desiredIds.Contains(transaction.Id)))
            db.KeyTransactions.Remove(stale);

        foreach (var draft in drafts)
        {
            if (!existingById.TryGetValue(draft.Id, out var transaction))
            {
                transaction = new KeyTransaction { Id = draft.Id };
                db.KeyTransactions.Add(transaction);
            }

            transaction.UserId = draft.UserId;
            transaction.KeyDefinitionId = draft.KeyDefinitionId;
            transaction.Amount = draft.Amount;
            transaction.Reason = draft.Reason;
            transaction.ReferenceType = GeneratedReferenceType;
            transaction.ReferenceId = null;
            transaction.CreatedAt = draft.CreatedAt;
        }

        var newSums = drafts
            .GroupBy(draft => new UserKey(draft.UserId, draft.KeyDefinitionId))
            .ToDictionary(group => group.Key, group => group.Sum(draft => (long)draft.Amount));
        foreach (var key in baseline.Keys.Union(newSums.Keys).ToArray())
        {
            var finalBalance = CheckedBalance(baseline.GetValueOrDefault(key) + newSums.GetValueOrDefault(key));
            if (!balances.TryGetValue(key, out var balance))
            {
                balance = new UserKeyBalance
                {
                    UserId = key.UserId,
                    KeyDefinitionId = key.KeyDefinitionId
                };
                db.UserKeyBalances.Add(balance);
                balances[key] = balance;
            }

            balance.Balance = finalBalance;
            balance.UpdatedAt = now;
        }

        return drafts.Count;
    }

    private static async Task<int> UpsertKeyProgressTransactionsAsync(
        QmahDbContext db,
        IReadOnlyList<ShowcaseUser> users,
        int transactionCount,
        int seed,
        DateTime now)
    {
        var userIds = users.Select(user => user.Id).ToArray();
        var existing = await db.KeyProgressTransactions
            .Where(transaction => userIds.Contains(transaction.UserId)
                && transaction.ReferenceType == GeneratedReferenceType)
            .ToListAsync();
        var balances = await db.KeyProgressBalances
            .Where(balance => userIds.Contains(balance.UserId))
            .ToDictionaryAsync(balance => balance.UserId);
        var oldSums = existing
            .GroupBy(transaction => transaction.UserId)
            .ToDictionary(group => group.Key, group => group.Sum(transaction => (long)transaction.Amount));
        var baseline = users.ToDictionary(
            user => user.Id,
            user => BaseBalance(balances, user.Id, oldSums.GetValueOrDefault(user.Id)));

        var drafts = BuildKeyProgressDrafts(users, transactionCount, seed, now);
        var desiredIds = drafts.Select(draft => draft.Id).ToHashSet();
        var existingById = existing.ToDictionary(transaction => transaction.Id);
        foreach (var stale in existing.Where(transaction => !desiredIds.Contains(transaction.Id)))
            db.KeyProgressTransactions.Remove(stale);

        foreach (var draft in drafts)
        {
            if (!existingById.TryGetValue(draft.Id, out var transaction))
            {
                transaction = new KeyProgressTransaction { Id = draft.Id };
                db.KeyProgressTransactions.Add(transaction);
            }

            transaction.UserId = draft.UserId;
            transaction.Amount = draft.Amount;
            transaction.Reason = draft.Reason;
            transaction.ReferenceType = GeneratedReferenceType;
            transaction.ReferenceId = null;
            transaction.CreatedAt = draft.CreatedAt;
        }

        var newSums = drafts
            .GroupBy(draft => draft.UserId)
            .ToDictionary(group => group.Key, group => group.Sum(draft => (long)draft.Amount));
        foreach (var user in users)
        {
            var finalBalance = CheckedBalance(baseline[user.Id] + newSums.GetValueOrDefault(user.Id));
            if (!balances.TryGetValue(user.Id, out var balance))
            {
                balance = new KeyProgressBalance { UserId = user.Id };
                db.KeyProgressBalances.Add(balance);
                balances[user.Id] = balance;
            }

            balance.Balance = finalBalance;
            balance.UpdatedAt = now;
        }

        return drafts.Count;
    }

    private static async Task<int> UpsertLoginAchievementsAsync(
        QmahDbContext db,
        IReadOnlyList<ShowcaseUser> users,
        IReadOnlyDictionary<Guid, HashSet<DateOnly>> loginDatesByUser,
        DateTime now)
    {
        var definitions = await db.Achievements
            .AsNoTracking()
            .Where(achievement => achievement.Status == "ACTIVE"
                && (achievement.ConditionType == "DAILY_LOGIN_COUNT"
                    || achievement.ConditionType == "DAILY_LOGIN_STREAK"))
            .OrderBy(achievement => achievement.Code)
            .ToListAsync();
        if (definitions.Count == 0)
            return 0;

        var userIds = users.Select(user => user.Id).ToArray();
        var existing = await db.UserAchievements
            .Where(achievement => userIds.Contains(achievement.UserId))
            .ToListAsync();
        var existingKeys = existing
            .Select(achievement => new UserAchievementKey(achievement.UserId, achievement.AchievementId))
            .ToHashSet();
        var added = 0;

        foreach (var user in users)
        {
            var dates = loginDatesByUser.TryGetValue(user.Id, out var userDates)
                ? userDates.OrderBy(date => date).ToArray()
                : [];
            if (dates.Length == 0)
                continue;

            var streak = CurrentStreak(dates);
            foreach (var definition in definitions)
            {
                var progress = definition.ConditionType == "DAILY_LOGIN_STREAK"
                    ? streak
                    : dates.Length;
                if (progress < definition.ThresholdValue)
                    continue;

                var key = new UserAchievementKey(user.Id, definition.Id);
                if (!existingKeys.Add(key))
                    continue;

                var achievedDate = definition.ConditionType == "DAILY_LOGIN_STREAK"
                    ? FindStreakEnd(dates, (int)Math.Min(definition.ThresholdValue, int.MaxValue))
                    : dates[AchievementDateIndex(dates.Length, definition.ThresholdValue)];
                var achievedAt = DateTime.SpecifyKind(
                    achievedDate.ToDateTime(new TimeOnly(10, 0)),
                    DateTimeKind.Utc);
                if (achievedAt >= now)
                    achievedAt = now.AddMinutes(-1);

                db.UserAchievements.Add(new UserAchievement
                {
                    Id = StableGuid($"showcase-login-achievement:{user.Id:N}:{definition.Code}"),
                    UserId = user.Id,
                    AchievementId = definition.Id,
                    AchievedAt = achievedAt,
                    IsDisplayed = false,
                    DisplayedAt = null
                });
                added++;
            }
        }

        return added;
    }

    private static IReadOnlyList<PointDraft> BuildPointDrafts(
        IReadOnlyList<ShowcaseUser> users,
        int count,
        int seed,
        DateTime now)
    {
        var running = new long[users.Count];
        var drafts = new List<PointDraft>(count);
        for (var index = 0; index < count; index++)
        {
            var userIndex = (int)(StableNumber($"point-user:{seed}:{index}") % (ulong)users.Count);
            var amount = NextAmount(running, userIndex, $"point-amount:{seed}:{index}", index);
            running[userIndex] += amount;
            drafts.Add(new PointDraft(
                StableGuid($"showcase-point:{seed}:{index}"),
                users[userIndex].Id,
                amount,
                amount > 0 ? "展示資料：點數取得" : "展示資料：點數使用",
                GeneratedAt(now, $"point-date:{seed}:{index}")));
        }

        return drafts;
    }

    private static IReadOnlyList<KeyDraft> BuildKeyDrafts(
        IReadOnlyList<ShowcaseUser> users,
        IReadOnlyList<KeyDefinition> definitions,
        int count,
        int seed,
        DateTime now)
    {
        if (definitions.Count == 0 || count == 0)
            return [];

        var running = new Dictionary<UserKey, long>();
        var drafts = new List<KeyDraft>(count);
        for (var index = 0; index < count; index++)
        {
            var userIndex = (int)(StableNumber($"key-user:{seed}:{index}") % (ulong)users.Count);
            var definitionIndex = (int)(StableNumber($"key-definition:{seed}:{index}") % (ulong)definitions.Count);
            var definition = definitions[definitionIndex];
            var key = new UserKey(users[userIndex].Id, definition.Id);
            running.TryGetValue(key, out var current);
            var amount = NextAmount(running, key, $"key-amount:{seed}:{index}", index);
            running[key] = current + amount;
            drafts.Add(new KeyDraft(
                StableGuid($"showcase-key:{seed}:{index}"),
                users[userIndex].Id,
                definition.Id,
                amount,
                amount > 0 ? "展示資料：鑰匙取得" : "展示資料：鑰匙使用",
                GeneratedAt(now, $"key-date:{seed}:{index}")));
        }

        return drafts;
    }

    private static IReadOnlyList<KeyProgressDraft> BuildKeyProgressDrafts(
        IReadOnlyList<ShowcaseUser> users,
        int count,
        int seed,
        DateTime now)
    {
        var running = new long[users.Count];
        var drafts = new List<KeyProgressDraft>(count);
        for (var index = 0; index < count; index++)
        {
            var userIndex = (int)(StableNumber($"progress-user:{seed}:{index}") % (ulong)users.Count);
            var amount = NextAmount(running, userIndex, $"progress-amount:{seed}:{index}", index);
            running[userIndex] += amount;
            drafts.Add(new KeyProgressDraft(
                StableGuid($"showcase-key-progress:{seed}:{index}"),
                users[userIndex].Id,
                amount,
                amount > 0 ? "展示資料：鑰匙進度取得" : "展示資料：鑰匙進度使用",
                GeneratedAt(now, $"progress-date:{seed}:{index}")));
        }

        return drafts;
    }

    private static int NextAmount(long[] running, int userIndex, string key, int index)
    {
        var amount = 40 + (int)(StableNumber(key) % 161);
        if (index % 4 == 3 && running[userIndex] > 0)
        {
            var debit = 10 + (int)(StableNumber(key + ":debit") % 61);
            return -(int)Math.Min(running[userIndex], debit);
        }

        return amount;
    }

    private static int NextAmount(
        Dictionary<UserKey, long> running,
        UserKey userKey,
        string key,
        int index)
    {
        var amount = 1 + (int)(StableNumber(key) % 4);
        if (index % 4 == 3
            && running.TryGetValue(userKey, out var current)
            && current > 0)
        {
            var debit = 1 + (int)(StableNumber(key + ":debit") % 2);
            return -(int)Math.Min(current, debit);
        }

        return amount;
    }

    private static DateTime GeneratedAt(DateTime now, string key)
    {
        var daysAgo = (int)(StableNumber(key) % 360);
        var hour = 8 + (int)(StableNumber(key + ":hour") % 11);
        return DateTime.SpecifyKind(now.Date.AddDays(-daysAgo).AddHours(hour), DateTimeKind.Utc);
    }

    private static long BaseBalance(
        IReadOnlyDictionary<Guid, PointBalance> balances,
        Guid userId,
        long oldGeneratedSum) =>
        BaseBalance(balances.TryGetValue(userId, out var balance) ? balance.Balance : 0, oldGeneratedSum);

    private static long BaseBalance(
        IReadOnlyDictionary<UserKey, UserKeyBalance> balances,
        UserKey key,
        long oldGeneratedSum) =>
        BaseBalance(balances.TryGetValue(key, out var balance) ? balance.Balance : 0, oldGeneratedSum);

    private static long BaseBalance(
        IReadOnlyDictionary<Guid, KeyProgressBalance> balances,
        Guid userId,
        long oldGeneratedSum) =>
        BaseBalance(balances.TryGetValue(userId, out var balance) ? balance.Balance : 0, oldGeneratedSum);

    private static long BaseBalance(int currentBalance, long oldGeneratedSum) =>
        Math.Max(0L, currentBalance - oldGeneratedSum);

    private static int CheckedBalance(long value)
    {
        if (value is < 0 or > int.MaxValue)
            throw new InvalidOperationException("展示資料產生後的餘額超出資料庫允許範圍。");
        return (int)value;
    }

    private static int CurrentStreak(IReadOnlyList<DateOnly> dates)
    {
        if (dates.Count == 0)
            return 0;

        var streak = 1;
        for (var index = dates.Count - 1; index > 0; index--)
        {
            if (dates[index - 1].AddDays(1) != dates[index])
                break;
            streak++;
        }

        return streak;
    }

    private static DateOnly FindStreakEnd(IReadOnlyList<DateOnly> dates, int threshold)
    {
        var streak = 1;
        for (var index = 1; index < dates.Count; index++)
        {
            streak = dates[index - 1].AddDays(1) == dates[index] ? streak + 1 : 1;
            if (streak >= threshold)
                return dates[index];
        }

        return dates[^1];
    }

    private static int AchievementDateIndex(int dateCount, long threshold) =>
        threshold <= 1
            ? 0
            : threshold >= dateCount
                ? dateCount - 1
                : (int)threshold - 1;

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"{name} 必須是 {minimum} 到 {maximum} 的整數。");
    }

    private static ulong StableNumber(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        ulong result = 0;
        for (var index = 0; index < 8; index++)
            result = (result << 8) | bytes[index];
        return result;
    }

    private static Guid StableGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private readonly record struct ShowcaseUser(Guid Id, string Email);
    private readonly record struct ActivityKey(Guid UserId, string ActivityType, DateOnly ActivityDate);
    private readonly record struct UserKey(Guid UserId, Guid KeyDefinitionId);
    private readonly record struct UserAchievementKey(Guid UserId, Guid AchievementId);

    private sealed record DailyActivityDraft(
        Guid UserId,
        string ActivityType,
        DateOnly ActivityDate,
        int OccurrenceCount,
        DateTime FirstOccurredAt,
        DateTime LastOccurredAt);

    private sealed record PointDraft(Guid Id, Guid UserId, int Amount, string Reason, DateTime CreatedAt);

    private sealed record KeyDraft(
        Guid Id,
        Guid UserId,
        Guid KeyDefinitionId,
        int Amount,
        string Reason,
        DateTime CreatedAt);

    private sealed record KeyProgressDraft(Guid Id, Guid UserId, int Amount, string Reason, DateTime CreatedAt);

    private sealed record ActivityGenerationResult(
        int TotalCount,
        int LoginCount,
        int CheckInCount,
        IReadOnlyDictionary<Guid, HashSet<DateOnly>> LoginDatesByUser);
}

public sealed record ShowcaseLedgerResult(
    int DailyActivityCount,
    int LoginActivityCount,
    int CheckInActivityCount,
    int PointTransactionCount,
    int KeyTransactionCount,
    int KeyProgressTransactionCount,
    int LoginAchievementCount);
