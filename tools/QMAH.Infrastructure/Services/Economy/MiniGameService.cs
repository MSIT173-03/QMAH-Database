using System.Data;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Services.Economy;

/// <summary>
/// 提供四種 Mini Game 共用的開始、驗證、評分與獎勵流程。
/// </summary>
public sealed class MiniGameService(QmahDbContext db, EconomyService economyService)
{
    /// <summary>取得所有啟用中的 Mini Game 模式及其評分門檻。</summary>
    public async Task<IReadOnlyList<MiniGameModeView>> GetModesAsync(
        CancellationToken cancellationToken = default)
    {
        var modes = await db.GameModeDefinitions
            .AsNoTracking()
            .Where(mode => mode.IsActive)
            .OrderBy(mode => mode.Code)
            .ToListAsync(cancellationToken);
        return modes.Select(ToModeView).ToList();
    }

    /// <summary>由伺服器選擇文物、素材池、難度與種子，建立一筆尚未完成的 Attempt。</summary>
    public async Task<EconomyResult<MiniGameStartView>> StartAttemptAsync(
        Guid userId,
        string modeCode,
        CancellationToken cancellationToken = default)
    {
        modeCode = modeCode.Trim().ToUpperInvariant();
        var mode = await db.GameModeDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Code == modeCode && item.IsActive, cancellationToken);
        if (mode is null)
            return EconomyResult<MiniGameStartView>.NotFound("找不到啟用中的 Mini Game 模式。");

        var artifacts = await db.Artifacts
            .AsNoTracking()
            .Where(artifact => artifact.IsActive)
            .OrderBy(artifact => artifact.Id)
            .Select(artifact => new ArtifactMaterialView(
                artifact.Id,
                artifact.Name,
                artifact.PrimaryImagePath,
                artifact.ThumbnailPath))
            .ToListAsync(cancellationToken);
        if (artifacts.Count == 0)
            return EconomyResult<MiniGameStartView>.Conflict("目前沒有可供 Mini Game 使用的啟用文物。");

        var poolSize = Math.Clamp(ReadConfigInt(mode.ConfigJson, "poolSize") ?? 1, 1, artifacts.Count);
        var pool = artifacts
            .OrderBy(_ => Random.Shared.Next())
            .Take(poolSize)
            .ToList();
        var selected = pool[Random.Shared.Next(pool.Count)];
        var now = DateTime.UtcNow;
        var attempt = new MiniGameAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GameModeDefinitionId = mode.Id,
            ArtifactId = selected.Id,
            ArtifactPoolJson = JsonSerializer.Serialize(pool.Select(item => item.Id)),
            Difficulty = ReadConfigString(mode.ConfigJson, "difficulty") ?? "NORMAL",
            Seed = Guid.NewGuid().ToString("N"),
            ConfigJson = mode.ConfigJson,
            Status = "STARTED",
            StartedAt = now
        };
        db.MiniGameAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        return EconomyResult<MiniGameStartView>.Success(new MiniGameStartView(
            attempt.Id,
            mode.Code,
            mode.Name,
            selected.Id,
            selected.Name,
            selected.PrimaryImagePath,
            selected.ThumbnailPath,
            pool.Select(item => new MiniGameArtifactView(
                item.Id,
                item.Name,
                item.PrimaryImagePath,
                item.ThumbnailPath)).ToList(),
            attempt.Difficulty,
            attempt.Seed,
            attempt.ConfigJson,
            attempt.StartedAt));
    }

    /// <summary>驗證玩家原始分數並由伺服器計算等級、點數、鑰匙進度與每日獎勵資格。</summary>
    public async Task<EconomyResult<MiniGameCompleteView>> CompleteAttemptAsync(
        Guid userId,
        Guid attemptId,
        int rawScore,
        string? rawResultJson,
        CancellationToken cancellationToken = default)
    {
        if (rawScore is < 0 or > 100)
            return EconomyResult<MiniGameCompleteView>.Invalid("rawScore 必須介於 0 至 100；分數由伺服器重新驗證。");
        if (!string.IsNullOrWhiteSpace(rawResultJson))
        {
            if (rawResultJson.Length > 4000)
                return EconomyResult<MiniGameCompleteView>.Invalid("rawResultJson 不可超過 4000 個字元。");
            try
            {
                using var parsed = JsonDocument.Parse(rawResultJson);
                if (parsed.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                    return EconomyResult<MiniGameCompleteView>.Invalid("rawResultJson 必須是 JSON 物件或陣列。");
            }
            catch (JsonException)
            {
                return EconomyResult<MiniGameCompleteView>.Invalid("rawResultJson 不是有效的 JSON。");
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var attempt = await db.MiniGameAttempts
            .Include(item => item.GameModeDefinition)
            .SingleOrDefaultAsync(
                item => item.Id == attemptId && item.UserId == userId,
                cancellationToken);
        if (attempt is null)
            return EconomyResult<MiniGameCompleteView>.NotFound("找不到目前會員的 Mini Game Attempt。");
        if (attempt.Status == "COMPLETED")
        {
            var currentProgress = await db.KeyProgressBalances
                .AsNoTracking()
                .Where(item => item.UserId == userId)
                .Select(item => (int?)item.Balance)
                .SingleOrDefaultAsync(cancellationToken) ?? 0;
            await transaction.CommitAsync(cancellationToken);
            return EconomyResult<MiniGameCompleteView>.Success(ToCompleteView(attempt, 0, currentProgress, true));
        }
        if (attempt.Status != "STARTED")
            return EconomyResult<MiniGameCompleteView>.Conflict("這個 Attempt 目前不可完成。");

        var mode = attempt.GameModeDefinition;
        if (mode.GradeBThreshold < 0
            || mode.GradeAThreshold < mode.GradeBThreshold
            || mode.GradeSThreshold < mode.GradeAThreshold
            || mode.GradeSThreshold > 100)
        {
            return EconomyResult<MiniGameCompleteView>.Conflict("Mini Game 模式的評分設定無效，請先由管理員修正。");
        }

        var grade = rawScore >= mode.GradeSThreshold
            ? "S"
            : rawScore >= mode.GradeAThreshold
                ? "A"
                : rawScore >= mode.GradeBThreshold
                    ? "B"
                    : rawScore > 0 ? "C" : "FAIL";
        var (pointReward, keyProgressReward) = grade switch
        {
            "S" => (mode.SPointReward, mode.SKeyProgressReward),
            "A" => (mode.APointReward, mode.AKeyProgressReward),
            "B" => (mode.BPointReward, mode.BKeyProgressReward),
            _ => (mode.FailPointReward, mode.FailKeyProgressReward)
        };
        if (pointReward < 0 || keyProgressReward < 0)
            return EconomyResult<MiniGameCompleteView>.Conflict("Mini Game 獎勵設定不可為負數。");

        var setting = await economyService.GetGameEconomySettingAsync(cancellationToken);
        if (setting.DailyMiniGameRewardLimit < 0 || setting.KeyProgressToNormalKey <= 0)
            return EconomyResult<MiniGameCompleteView>.Conflict("Mini Game 每日獎勵或進度門檻設定無效。");
        var utcDate = DateTime.UtcNow.Date;
        var nextUtcDate = utcDate.AddDays(1);
        var rewardedToday = await db.MiniGameAttempts
            .CountAsync(item => item.UserId == userId
                && item.Status == "COMPLETED"
                && item.RewardGranted
                && item.CompletedAt >= utcDate
                && item.CompletedAt < nextUtcDate,
                cancellationToken);
        var hasEconomicReward = rewardedToday < setting.DailyMiniGameRewardLimit;
        if (!hasEconomicReward)
        {
            pointReward = 0;
            keyProgressReward = 0;
        }

        var convertedNormalKeys = 0;
        var remainingProgress = await db.KeyProgressBalances
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => (int?)item.Balance)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var now = DateTime.UtcNow;
        if (hasEconomicReward && pointReward > 0)
        {
            var pointBalance = await GetOrCreatePointBalanceAsync(userId, cancellationToken);
            pointBalance.Balance = checked(pointBalance.Balance + pointReward);
            pointBalance.UpdatedAt = now;
            db.PointTransactions.Add(new PointTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = pointReward,
                Reason = $"Mini Game {mode.Name} {grade} 評分獎勵",
                ReferenceType = "MINIGAME_REWARD",
                ReferenceId = attempt.Id,
                CreatedAt = now
            });
        }
        if (hasEconomicReward && keyProgressReward > 0)
        {
            var progressBalance = await GetOrCreateProgressBalanceAsync(userId, cancellationToken);
            var totalProgress = checked(progressBalance.Balance + keyProgressReward);
            convertedNormalKeys = totalProgress / setting.KeyProgressToNormalKey;
            remainingProgress = totalProgress % setting.KeyProgressToNormalKey;
            progressBalance.Balance = remainingProgress;
            progressBalance.UpdatedAt = now;
            db.KeyProgressTransactions.Add(new KeyProgressTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = keyProgressReward,
                Reason = $"Mini Game {mode.Name} {grade} 鑰匙進度獎勵",
                ReferenceType = "MINIGAME_REWARD",
                ReferenceId = attempt.Id,
                CreatedAt = now
            });

            if (convertedNormalKeys > 0)
            {
                // 舊資料使用 KEY-NORMAL，新資料可使用 NORMAL；兩者都代表一般鑰匙，先採新代碼以維持相容性。
                var normalKey = await db.KeyDefinitions
                    .Where(item => item.IsActive && (item.Code == "NORMAL" || item.Code == "KEY-NORMAL"))
                    .OrderBy(item => item.Code == "NORMAL" ? 0 : 1)
                    .FirstOrDefaultAsync(cancellationToken);
                if (normalKey is null)
                    return EconomyResult<MiniGameCompleteView>.Conflict("找不到啟用中的 NORMAL 鑰匙定義，無法轉換進度。");
                var normalBalance = await GetOrCreateKeyBalanceAsync(userId, normalKey.Id, cancellationToken);
                normalBalance.Balance = checked(normalBalance.Balance + convertedNormalKeys);
                normalBalance.UpdatedAt = now;
                var convertedAmount = checked(convertedNormalKeys * setting.KeyProgressToNormalKey);
                db.KeyProgressTransactions.Add(new KeyProgressTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Amount = -convertedAmount,
                    Reason = "鑰匙進度達到門檻轉換 NORMAL 鑰匙",
                    ReferenceType = "MINIGAME_PROGRESS_CONVERSION",
                    ReferenceId = attempt.Id,
                    CreatedAt = now
                });
                db.KeyTransactions.Add(new KeyTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    KeyDefinitionId = normalKey.Id,
                    Amount = convertedNormalKeys,
                    Reason = "Mini Game 鑰匙進度轉換 NORMAL 鑰匙",
                    ReferenceType = "MINIGAME_PROGRESS_CONVERSION",
                    ReferenceId = attempt.Id,
                    CreatedAt = now
                });
            }
        }

        attempt.Status = "COMPLETED";
        attempt.RawScore = rawScore;
        attempt.RawResultJson = rawResultJson;
        attempt.NormalizedScore = rawScore;
        attempt.Grade = grade;
        attempt.PointReward = pointReward;
        attempt.KeyProgressReward = keyProgressReward;
        attempt.RewardAttemptNo = hasEconomicReward ? rewardedToday + 1 : null;
        attempt.RewardGranted = hasEconomicReward;
        attempt.CompletedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EconomyResult<MiniGameCompleteView>.Success(new MiniGameCompleteView(
            attempt.Id,
            mode.Code,
            rawScore,
            rawScore,
            grade,
            pointReward,
            keyProgressReward,
            convertedNormalKeys,
            remainingProgress,
            hasEconomicReward,
            false,
            attempt.CompletedAt.Value));
    }

    private static MiniGameModeView ToModeView(GameModeDefinition mode) => new(
        mode.Id,
        mode.Code,
        mode.Name,
        mode.Description,
        mode.ConfigJson,
        mode.GradeBThreshold,
        mode.GradeAThreshold,
        mode.GradeSThreshold);

    private static MiniGameCompleteView ToCompleteView(
        MiniGameAttempt attempt,
        int convertedNormalKeys,
        int remainingKeyProgress,
        bool alreadyCompleted) => new(
        attempt.Id,
        attempt.GameModeDefinition.Code,
        attempt.RawScore ?? 0,
        attempt.NormalizedScore ?? 0,
        attempt.Grade ?? "FAIL",
        attempt.PointReward,
        attempt.KeyProgressReward,
        convertedNormalKeys,
        remainingKeyProgress,
        attempt.RewardGranted,
        alreadyCompleted,
        attempt.CompletedAt ?? attempt.StartedAt);

    private async Task<UserKeyBalance> GetOrCreateKeyBalanceAsync(
        Guid userId,
        Guid keyDefinitionId,
        CancellationToken cancellationToken)
    {
        var balance = await db.UserKeyBalances
            .SingleOrDefaultAsync(item => item.UserId == userId && item.KeyDefinitionId == keyDefinitionId, cancellationToken);
        if (balance is not null)
            return balance;
        balance = new UserKeyBalance
        {
            UserId = userId,
            KeyDefinitionId = keyDefinitionId,
            Balance = 0,
            UpdatedAt = DateTime.UtcNow
        };
        db.UserKeyBalances.Add(balance);
        return balance;
    }

    private async Task<PointBalance> GetOrCreatePointBalanceAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var balance = await db.PointBalances
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (balance is not null)
            return balance;
        balance = new PointBalance
        {
            UserId = userId,
            Balance = 0,
            UpdatedAt = DateTime.UtcNow
        };
        db.PointBalances.Add(balance);
        return balance;
    }

    private async Task<KeyProgressBalance> GetOrCreateProgressBalanceAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var balance = await db.KeyProgressBalances
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (balance is not null)
            return balance;
        balance = new KeyProgressBalance
        {
            UserId = userId,
            Balance = 0,
            UpdatedAt = DateTime.UtcNow
        };
        db.KeyProgressBalances.Add(balance);
        return balance;
    }

    private static int? ReadConfigInt(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value)
                && value.TryGetInt32(out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadConfigString(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ArtifactMaterialView(
        Guid Id,
        string Name,
        string PrimaryImagePath,
        string? ThumbnailPath);
}

/// <summary>前端建立玩法所需的模式識別與評分門檻。</summary>
public sealed record MiniGameModeView(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string? ConfigJson,
    int GradeBThreshold,
    int GradeAThreshold,
    int GradeSThreshold);

/// <summary>開始 Mini Game 後回傳的伺服器素材與不可由客戶端自行決定的執行資訊。</summary>
public sealed record MiniGameStartView(
    Guid AttemptId,
    string ModeCode,
    string ModeName,
    Guid ArtifactId,
    string ArtifactName,
    string PrimaryImagePath,
    string? ThumbnailPath,
    IReadOnlyList<MiniGameArtifactView> ArtifactPool,
    string Difficulty,
    string Seed,
    string? ConfigJson,
    DateTime StartedAt);

/// <summary>Mini Game 素材池中的單件文物影像資料。</summary>
public sealed record MiniGameArtifactView(
    Guid ArtifactId,
    string Name,
    string PrimaryImagePath,
    string? ThumbnailPath);

/// <summary>完成 Attempt 後的伺服器評分、獎勵與累積進度結果。</summary>
public sealed record MiniGameCompleteView(
    Guid AttemptId,
    string ModeCode,
    int RawScore,
    int NormalizedScore,
    string Grade,
    int PointReward,
    int KeyProgressReward,
    int ConvertedNormalKeys,
    int RemainingKeyProgress,
    bool EconomicRewardGranted,
    bool AlreadyCompleted,
    DateTime CompletedAt);
