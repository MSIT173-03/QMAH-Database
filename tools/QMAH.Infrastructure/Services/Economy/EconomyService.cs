using System.Data;

using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Services.Economy;

/// <summary>
/// 集中處理鑑定點數、鑰匙、鑰匙進度、優惠券與配戴稱號的交易邊界。
/// </summary>
public sealed class EconomyService(QmahDbContext db)
{
    /// <summary>讀取會員目前的鑑定點數、鑰匙、解鎖候選數與可用兌換規則。</summary>
    public async Task<MemberEconomyView> GetMemberEconomyAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // 查詢時順手標記過期券，讓會員、結帳與管理後台看到同一份生命週期狀態，且不需要額外常駐背景工作。
        await SyncExpiredCouponsAsync(userId, cancellationToken);

        var artifacts = await db.Artifacts
            .AsNoTracking()
            .Where(artifact => artifact.IsActive)
            .Select(artifact => new ArtifactScopeView(
                artifact.Id,
                artifact.CategoryId,
                artifact.EraBucketId))
            .ToListAsync(cancellationToken);
        var unlockedIds = await db.ArtifactUnlocks
            .AsNoTracking()
            .Where(unlock => unlock.UserId == userId)
            .Select(unlock => unlock.ArtifactId)
            .ToHashSetAsync(cancellationToken);
        var keyDefinitions = await db.KeyDefinitions
            .AsNoTracking()
            .Where(key => key.IsActive)
            .OrderBy(key => key.Code)
            .ToListAsync(cancellationToken);
        var balances = await db.UserKeyBalances
            .AsNoTracking()
            .Where(balance => balance.UserId == userId)
            .ToDictionaryAsync(balance => balance.KeyDefinitionId, cancellationToken);
        var pointBalance = await db.PointBalances
            .AsNoTracking()
            .Where(balance => balance.UserId == userId)
            .Select(balance => (int?)balance.Balance)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var keyProgress = await db.KeyProgressBalances
            .AsNoTracking()
            .Where(balance => balance.UserId == userId)
            .Select(balance => (int?)balance.Balance)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var gameSetting = await GetGameEconomySettingAsync(cancellationToken);

        var keys = keyDefinitions
            .Select(key => new KeyBalanceView(
                key.Id,
                key.Code,
                key.Name,
                key.ScopeType,
                key.CategoryId,
                key.EraBucketId,
                balances.TryGetValue(key.Id, out var balance) ? balance.Balance : 0,
                CountEligibleArtifacts(key, artifacts, unlockedIds),
                key.RecyclePointValue))
            .ToList();

        var exchangeRules = await GetExchangeRulesAsync(userId, artifacts, unlockedIds, cancellationToken);
        return new MemberEconomyView(
            pointBalance,
            keyProgress,
            gameSetting.KeyProgressToNormalKey,
            keys,
            exchangeRules);
    }

    /// <summary>取得目前仍有可解鎖文物的鑰匙兌換規則。</summary>
    public async Task<IReadOnlyList<KeyExchangeRuleView>> GetExchangeRulesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var artifacts = await db.Artifacts
            .AsNoTracking()
            .Where(artifact => artifact.IsActive)
            .Select(artifact => new ArtifactScopeView(
                artifact.Id,
                artifact.CategoryId,
                artifact.EraBucketId))
            .ToListAsync(cancellationToken);
        var unlockedIds = await db.ArtifactUnlocks
            .AsNoTracking()
            .Where(unlock => unlock.UserId == userId)
            .Select(unlock => unlock.ArtifactId)
            .ToHashSetAsync(cancellationToken);
        return await GetExchangeRulesAsync(userId, artifacts, unlockedIds, cancellationToken);
    }

    /// <summary>依鑰匙範圍解鎖一件文物；抽選與扣除都由伺服器在同一交易中完成。</summary>
    public async Task<EconomyResult<ArtifactUnlockView>> UnlockArtifactAsync(
        Guid userId,
        string keyCode,
        Guid? artifactId,
        CancellationToken cancellationToken = default)
    {
        keyCode = NormalizeCode(keyCode);
        if (string.IsNullOrWhiteSpace(keyCode))
            return EconomyResult<ArtifactUnlockView>.Invalid("鑰匙代碼不可為空。");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var key = await db.KeyDefinitions
            .SingleOrDefaultAsync(item => item.Code == keyCode && item.IsActive, cancellationToken);
        if (key is null)
            return EconomyResult<ArtifactUnlockView>.NotFound("找不到啟用中的鑰匙定義。");
        if (key.ScopeType == "UNIVERSAL" && !artifactId.HasValue)
            return EconomyResult<ArtifactUnlockView>.Invalid("UNIVERSAL 鑰匙必須指定要解鎖的文物。");
        if (key.ScopeType != "UNIVERSAL" && artifactId.HasValue)
            return EconomyResult<ArtifactUnlockView>.Invalid("只有 UNIVERSAL 鑰匙可以指定文物。");

        // 候選集完全由伺服器依啟用文物與會員既有解鎖紀錄建立；客戶端不能用指定 ID 影響 NORMAL、CATEGORY 或 ERA 的抽選。
        var candidates = await GetEligibleArtifactQuery(userId, key)
            .Select(artifact => new ArtifactCandidateView(artifact.Id, artifact.Name))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return EconomyResult<ArtifactUnlockView>.Success(new ArtifactUnlockView(
                false,
                null,
                null,
                0,
                "目前沒有符合這把鑰匙的未解鎖文物，因此沒有扣除鑰匙。"));
        }

        ArtifactCandidateView? selected;
        if (artifactId.HasValue)
        {
            selected = candidates.FirstOrDefault(candidate => candidate.Id == artifactId.Value);
            if (selected is null)
                return EconomyResult<ArtifactUnlockView>.Invalid("指定文物不存在、未啟用或已經解鎖。");
        }
        else
        {
            selected = candidates[Random.Shared.Next(candidates.Count)];
        }

        var balance = await GetOrCreateKeyBalanceAsync(userId, key.Id, cancellationToken);
        if (balance.Balance < 1)
            return EconomyResult<ArtifactUnlockView>.Conflict("鑰匙數量不足，無法解鎖文物。");

        var now = DateTime.UtcNow;
        balance.Balance--;
        balance.UpdatedAt = now;
        var keyTransaction = new KeyTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            KeyDefinitionId = key.Id,
            Amount = -1,
            Reason = $"使用{key.Name}解鎖文物",
            ReferenceType = "ARTIFACT_UNLOCK",
            ReferenceId = selected.Id,
            CreatedAt = now
        };
        var unlock = new ArtifactUnlock
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ArtifactId = selected.Id,
            UnlockMethod = key.Code,
            KeyTransactionId = keyTransaction.Id,
            UnlockedAt = now
        };
        db.KeyTransactions.Add(keyTransaction);
        db.ArtifactUnlocks.Add(unlock);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EconomyResult<ArtifactUnlockView>.Success(new ArtifactUnlockView(
            true,
            selected.Id,
            selected.Name,
            candidates.Count - 1,
            null));
    }

    /// <summary>依資料庫中的兌換規則交換鑰匙，並留下來源與目標兩筆鑰匙流水。</summary>
    public async Task<EconomyResult<KeyExchangeView>> ExchangeKeysAsync(
        Guid userId,
        Guid ruleId,
        int units,
        CancellationToken cancellationToken = default)
    {
        if (units < 1 || units > 100)
            return EconomyResult<KeyExchangeView>.Invalid("兌換次數必須介於 1 至 100。");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var rule = await db.KeyExchangeRules
            .Include(item => item.SourceKeyDefinition)
            .Include(item => item.TargetKeyDefinition)
            .SingleOrDefaultAsync(item => item.Id == ruleId && item.IsActive, cancellationToken);
        if (rule is null
            || !rule.SourceKeyDefinition.IsActive
            || !rule.TargetKeyDefinition.IsActive)
        {
            return EconomyResult<KeyExchangeView>.NotFound("找不到啟用中的鑰匙兌換規則。");
        }

        var targetEligibleCount = await GetEligibleArtifactQuery(userId, rule.TargetKeyDefinition)
            .CountAsync(cancellationToken);
        if (targetEligibleCount == 0)
            return EconomyResult<KeyExchangeView>.Conflict("目標鑰匙目前沒有可解鎖的文物，不能兌換。");

        var sourceBalance = await GetOrCreateKeyBalanceAsync(
            userId,
            rule.SourceKeyDefinitionId,
            cancellationToken);
        var targetBalance = await GetOrCreateKeyBalanceAsync(
            userId,
            rule.TargetKeyDefinitionId,
            cancellationToken);
        var sourceAmount = checked(rule.SourceAmount * units);
        var targetAmount = checked(rule.TargetAmount * units);
        if (sourceBalance.Balance < sourceAmount)
            return EconomyResult<KeyExchangeView>.Conflict("來源鑰匙數量不足，不能兌換。");

        var now = DateTime.UtcNow;
        var operationId = Guid.NewGuid();
        sourceBalance.Balance -= sourceAmount;
        sourceBalance.UpdatedAt = now;
        targetBalance.Balance = checked(targetBalance.Balance + targetAmount);
        targetBalance.UpdatedAt = now;
        db.KeyTransactions.Add(new KeyTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            KeyDefinitionId = rule.SourceKeyDefinitionId,
            Amount = -sourceAmount,
            Reason = "鑰匙兌換扣除來源鑰匙",
            ReferenceType = "KEY_EXCHANGE_SOURCE",
            ReferenceId = operationId,
            CreatedAt = now
        });
        db.KeyTransactions.Add(new KeyTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            KeyDefinitionId = rule.TargetKeyDefinitionId,
            Amount = targetAmount,
            Reason = "鑰匙兌換取得目標鑰匙",
            ReferenceType = "KEY_EXCHANGE_TARGET",
            ReferenceId = operationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EconomyResult<KeyExchangeView>.Success(new KeyExchangeView(
            rule.Id,
            rule.SourceKeyDefinition.Code,
            sourceAmount,
            rule.TargetKeyDefinition.Code,
            targetAmount,
            targetEligibleCount));
    }

    /// <summary>回收已沒有任何可解鎖文物的鑰匙，並同步產生鑰匙與點數流水。</summary>
    public async Task<EconomyResult<KeyRecycleView>> RecycleKeyAsync(
        Guid userId,
        string keyCode,
        int amount,
        CancellationToken cancellationToken = default)
    {
        keyCode = NormalizeCode(keyCode);
        if (amount < 1 || amount > 100)
            return EconomyResult<KeyRecycleView>.Invalid("回收數量必須介於 1 至 100。");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var key = await db.KeyDefinitions
            .SingleOrDefaultAsync(item => item.Code == keyCode && item.IsActive, cancellationToken);
        if (key is null)
            return EconomyResult<KeyRecycleView>.NotFound("找不到啟用中的鑰匙定義。");
        if (key.RecyclePointValue <= 0)
            return EconomyResult<KeyRecycleView>.Conflict("這把鑰匙目前未設定可回收的鑑定點數。");

        var eligibleCount = await GetEligibleArtifactQuery(userId, key).CountAsync(cancellationToken);
        if (eligibleCount > 0)
            return EconomyResult<KeyRecycleView>.Conflict("仍有可解鎖文物，不能回收這把鑰匙。");

        var balance = await GetOrCreateKeyBalanceAsync(userId, key.Id, cancellationToken);
        if (balance.Balance < amount)
            return EconomyResult<KeyRecycleView>.Conflict("鑰匙數量不足，不能回收。");
        var pointAmount = checked(key.RecyclePointValue * amount);
        var pointBalance = await GetOrCreatePointBalanceAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        balance.Balance -= amount;
        balance.UpdatedAt = now;
        pointBalance.Balance = checked(pointBalance.Balance + pointAmount);
        pointBalance.UpdatedAt = now;
        var operationId = Guid.NewGuid();
        db.KeyTransactions.Add(new KeyTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            KeyDefinitionId = key.Id,
            Amount = -amount,
            Reason = "回收已無可解鎖文物的鑰匙",
            ReferenceType = "KEY_RECYCLE",
            ReferenceId = operationId,
            CreatedAt = now
        });
        db.PointTransactions.Add(new PointTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Amount = pointAmount,
            Reason = "鑰匙回收取得鑑定點數",
            ReferenceType = "KEY_RECYCLE",
            ReferenceId = operationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EconomyResult<KeyRecycleView>.Success(new KeyRecycleView(
            key.Code,
            amount,
            pointAmount,
            0));
    }

    /// <summary>以增減量調整會員點數；不接受直接指定餘額，且每次都建立點數流水。</summary>
    public async Task<EconomyResult<BalanceAdjustmentView>> AdjustPointsAsync(
        Guid userId,
        int amount,
        string reason,
        string referenceType = "ADMIN_ADJUSTMENT",
        CancellationToken cancellationToken = default)
    {
        var reasonResult = ValidateReason(reason);
        if (reasonResult is not null)
            return EconomyResult<BalanceAdjustmentView>.Invalid(reasonResult);
        if (amount == 0)
            return EconomyResult<BalanceAdjustmentView>.Invalid("點數調整不可為 0。");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var balance = await GetOrCreatePointBalanceAsync(userId, cancellationToken);
        var nextBalance = balance.Balance + (long)amount;
        if (nextBalance < 0 || nextBalance > int.MaxValue)
            return EconomyResult<BalanceAdjustmentView>.Conflict("點數餘額不可小於 0 或超過系統上限。");
        var now = DateTime.UtcNow;
        balance.Balance = (int)nextBalance;
        balance.UpdatedAt = now;
        db.PointTransactions.Add(new PointTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Amount = amount,
            Reason = reason.Trim(),
            ReferenceType = referenceType,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EconomyResult<BalanceAdjustmentView>.Success(new BalanceAdjustmentView(amount, balance.Balance));
    }

    /// <summary>以增減量調整會員指定類型的鑰匙；每次都建立鑰匙流水。</summary>
    public async Task<EconomyResult<BalanceAdjustmentView>> AdjustKeysAsync(
        Guid userId,
        Guid keyDefinitionId,
        int amount,
        string reason,
        string referenceType = "ADMIN_ADJUSTMENT",
        CancellationToken cancellationToken = default)
    {
        var reasonResult = ValidateReason(reason);
        if (reasonResult is not null)
            return EconomyResult<BalanceAdjustmentView>.Invalid(reasonResult);
        if (amount == 0)
            return EconomyResult<BalanceAdjustmentView>.Invalid("鑰匙調整不可為 0。");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var key = await db.KeyDefinitions
            .SingleOrDefaultAsync(item => item.Id == keyDefinitionId && item.IsActive, cancellationToken);
        if (key is null)
            return EconomyResult<BalanceAdjustmentView>.NotFound("找不到啟用中的鑰匙定義。");
        var balance = await GetOrCreateKeyBalanceAsync(userId, keyDefinitionId, cancellationToken);
        var nextBalance = balance.Balance + (long)amount;
        if (nextBalance < 0 || nextBalance > int.MaxValue)
            return EconomyResult<BalanceAdjustmentView>.Conflict("鑰匙餘額不可小於 0 或超過系統上限。");
        var now = DateTime.UtcNow;
        balance.Balance = (int)nextBalance;
        balance.UpdatedAt = now;
        db.KeyTransactions.Add(new KeyTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            KeyDefinitionId = keyDefinitionId,
            Amount = amount,
            Reason = reason.Trim(),
            ReferenceType = referenceType,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EconomyResult<BalanceAdjustmentView>.Success(new BalanceAdjustmentView(amount, balance.Balance));
    }

    /// <summary>使用鑑定點數兌換一張 POINT_EXCHANGE 優惠券，期限從本次取得時間起算。</summary>
    public async Task<EconomyResult<CouponView>> RedeemPointCouponAsync(
        Guid userId,
        Guid couponDefinitionId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var definition = await db.CouponDefinitions
            .SingleOrDefaultAsync(item => item.Id == couponDefinitionId, cancellationToken);
        if (definition is null)
            return EconomyResult<CouponView>.NotFound("找不到優惠券定義。");
        var now = DateTime.UtcNow;
        if (!definition.IsActive
            || definition.AcquisitionType != "POINT_EXCHANGE"
            || !definition.PointCost.HasValue
            || definition.PointCost.Value <= 0
            || definition.StartAt > now
            || definition.EndAt <= now)
        {
            return EconomyResult<CouponView>.Conflict("這張優惠券目前不可用於點數兌換。");
        }
        if (definition.ValidityDays <= 0)
            return EconomyResult<CouponView>.Conflict("優惠券有效天數設定無效。");

        var pointBalance = await GetOrCreatePointBalanceAsync(userId, cancellationToken);
        if (pointBalance.Balance < definition.PointCost.Value)
            return EconomyResult<CouponView>.Conflict("鑑定點數不足，不能兌換這張優惠券。");

        pointBalance.Balance -= definition.PointCost.Value;
        pointBalance.UpdatedAt = now;
        var coupon = new UserCoupon
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CouponDefinitionId = definition.Id,
            Status = "AVAILABLE",
            IssuedAt = now,
            ExpiresAt = now.AddDays(definition.ValidityDays),
            IssueReason = "會員以鑑定點數兌換",
        };
        db.UserCoupons.Add(coupon);
        db.PointTransactions.Add(new PointTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Amount = -definition.PointCost.Value,
            Reason = "兌換點數優惠券",
            ReferenceType = "POINT_COUPON_REDEEM",
            ReferenceId = coupon.Id,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EconomyResult<CouponView>.Success(ToCouponView(coupon, definition));
    }

    /// <summary>列出目前可用且已設定點數成本的常駐兌換券。</summary>
    public async Task<IReadOnlyList<PointCouponOptionView>> GetPointCouponOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        return await db.CouponDefinitions
            .AsNoTracking()
            .Where(definition => definition.IsActive
                && definition.AcquisitionType == "POINT_EXCHANGE"
                && definition.PointCost.HasValue
                && definition.PointCost.Value > 0
                && definition.StartAt <= now
                && definition.EndAt > now)
            .OrderBy(definition => definition.PointCost)
            .ThenBy(definition => definition.Code)
            .Select(definition => new PointCouponOptionView(
                definition.Id,
                definition.Code,
                definition.Name,
                definition.PointCost!.Value,
                definition.DiscountType,
                definition.DiscountValue,
                definition.MinimumAmount,
                definition.ValidityDays,
                definition.StartAt,
                definition.EndAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>由管理員發放一張優惠券；原因、發放者與有效期限會一併保留。</summary>
    public async Task<EconomyResult<CouponView>> GrantCouponAsync(
        Guid adminUserId,
        Guid userId,
        Guid couponDefinitionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 200)
            return EconomyResult<CouponView>.Invalid("發放原因必須填寫且不可超過 200 個字元。");

        var definition = await db.CouponDefinitions
            .SingleOrDefaultAsync(item => item.Id == couponDefinitionId, cancellationToken);
        if (definition is null || !definition.IsActive)
            return EconomyResult<CouponView>.NotFound("找不到啟用中的優惠券定義。");
        if (definition.AcquisitionType != "ADMIN_GRANT")
            return EconomyResult<CouponView>.Conflict("POINT_EXCHANGE 優惠券必須透過點數兌換取得。");
        if (definition.ValidityDays <= 0)
            return EconomyResult<CouponView>.Conflict("優惠券有效天數設定無效。");
        if (!await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
            return EconomyResult<CouponView>.NotFound("找不到指定會員。");

        var now = DateTime.UtcNow;
        var coupon = new UserCoupon
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CouponDefinitionId = definition.Id,
            Status = "AVAILABLE",
            IssuedAt = now,
            ExpiresAt = now.AddDays(definition.ValidityDays),
            IssuedByAdminUserId = adminUserId,
            IssueReason = reason.Trim()
        };
        db.UserCoupons.Add(coupon);
        await db.SaveChangesAsync(cancellationToken);
        return EconomyResult<CouponView>.Success(ToCouponView(coupon, definition));
    }

    /// <summary>撤銷仍可使用的優惠券，保留資料列與撤銷稽核資訊，不進行物理刪除。</summary>
    public async Task<EconomyResult<CouponView>> RevokeCouponAsync(
        Guid adminUserId,
        Guid couponId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 200)
            return EconomyResult<CouponView>.Invalid("撤銷原因必須填寫且不可超過 200 個字元。");
        var coupon = await db.UserCoupons
            .Include(item => item.CouponDefinition)
            .SingleOrDefaultAsync(item => item.Id == couponId, cancellationToken);
        if (coupon is null)
            return EconomyResult<CouponView>.NotFound("找不到會員優惠券。");
        // 只同步目標會員的過期狀態，避免一次撤銷操作掃描整個優惠券資料表。
        await SyncExpiredCouponsAsync(coupon.UserId, cancellationToken);
        if (coupon.Status == "AVAILABLE" && coupon.ExpiresAt <= DateTime.UtcNow)
            coupon.Status = "EXPIRED";
        if (coupon.Status != "AVAILABLE")
            return EconomyResult<CouponView>.Conflict("只有 AVAILABLE 狀態的優惠券可以撤銷。");

        var now = DateTime.UtcNow;
        coupon.Status = "REVOKED";
        coupon.RevokedAt = now;
        coupon.RevokedByAdminUserId = adminUserId;
        coupon.RevokeReason = reason.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return EconomyResult<CouponView>.Success(ToCouponView(coupon, coupon.CouponDefinition));
    }

    /// <summary>將已超過 ExpiresAt 的 AVAILABLE 優惠券標記為 EXPIRED。</summary>
    public async Task<int> SyncExpiredCouponsAsync(
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = db.UserCoupons.Where(coupon => coupon.Status == "AVAILABLE" && coupon.ExpiresAt <= now);
        if (userId.HasValue)
            query = query.Where(coupon => coupon.UserId == userId.Value);
        var coupons = await query.ToListAsync(cancellationToken);
        foreach (var coupon in coupons)
            coupon.Status = "EXPIRED";
        if (coupons.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
        return coupons.Count;
    }

    /// <summary>讀取會員目前配戴的單一成就稱號。</summary>
    public async Task<EquippedTitleView?> GetEquippedTitleAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await db.UserEquippedTitles
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new EquippedTitleView(
                item.UserAchievementId,
                item.UserAchievement.AchievementId,
                item.UserAchievement.Achievement.Code,
                item.UserAchievement.Achievement.Name,
                item.UserAchievement.Achievement.Title,
                item.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>設定或清除會員目前配戴的稱號，並確認來源成就屬於該會員。</summary>
    public async Task<EconomyResult<EquippedTitleView?>> SetEquippedTitleAsync(
        Guid userId,
        Guid? userAchievementId,
        CancellationToken cancellationToken = default)
    {
        var current = await db.UserEquippedTitles
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (!userAchievementId.HasValue)
        {
            if (current is not null)
            {
                db.UserEquippedTitles.Remove(current);
                await db.SaveChangesAsync(cancellationToken);
            }
            return EconomyResult<EquippedTitleView?>.Success(null);
        }

        var achievement = await db.UserAchievements
            .Include(item => item.Achievement)
            .SingleOrDefaultAsync(
                item => item.Id == userAchievementId.Value && item.UserId == userId,
                cancellationToken);
        if (achievement is null)
            return EconomyResult<EquippedTitleView?>.Invalid("只能配戴目前會員已取得的成就稱號。");

        var now = DateTime.UtcNow;
        if (current is null)
        {
            current = new UserEquippedTitle
            {
                UserId = userId,
                UserAchievementId = achievement.Id,
                EquippedAt = now,
                UpdatedAt = now
            };
            db.UserEquippedTitles.Add(current);
        }
        else
        {
            current.UserAchievementId = achievement.Id;
            current.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return EconomyResult<EquippedTitleView?>.Success(new EquippedTitleView(
            achievement.Id,
            achievement.AchievementId,
            achievement.Achievement.Code,
            achievement.Achievement.Name,
            achievement.Achievement.Title,
            now));
    }

    /// <summary>依多人主遊戲的回合與投票結果計算一次性獎勵，並以玩家紀錄防止重複領取。</summary>
    public async Task<EconomyResult<GameRewardView>> RewardMainGameAsync(
        Guid userId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var room = await db.GameRooms
            .Include(item => item.GamePlayers)
            .Include(item => item.GameRounds)
                .ThenInclude(round => round.RoundAnswers)
                    .ThenInclude(answer => answer.Votes)
            .SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken);
        if (room is null)
            return EconomyResult<GameRewardView>.NotFound("找不到遊戲房間。");
        if (room.Status != "COMPLETED")
            return EconomyResult<GameRewardView>.Conflict("遊戲尚未完成，現在不能結算獎勵。");
        var player = room.GamePlayers.FirstOrDefault(item => item.UserId == userId && item.ConnectionStatus != "LEFT");
        if (player is null)
            return EconomyResult<GameRewardView>.Forbidden("目前會員不是這場遊戲的有效參與者。");

        var existingPointTransaction = await db.PointTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.UserId == userId
                    && item.ReferenceType == "MAIN_GAME_REWARD"
                    && item.ReferenceId == player.Id,
                cancellationToken);
        if (existingPointTransaction is not null)
        {
            var existingKeyAmount = await db.KeyTransactions
                .AsNoTracking()
                .Where(item => item.UserId == userId
                    && item.ReferenceType == "MAIN_GAME_REWARD"
                    && item.ReferenceId == player.Id)
                .SumAsync(item => (int?)item.Amount, cancellationToken) ?? 0;
            await transaction.CommitAsync(cancellationToken);
            return EconomyResult<GameRewardView>.Success(new GameRewardView(
                existingPointTransaction.Amount,
                existingKeyAmount,
                0,
                0,
                true));
        }

        var settings = await GetGameEconomySettingAsync(cancellationToken);
        if (settings.MinimumPointReward < 0
            || settings.MaximumPointReward < settings.MinimumPointReward
            || settings.BasePointReward < 0
            || settings.MaximumVoteBonus < 0
            || settings.MaximumWinBonus < 0
            || settings.CompletedNormalKey < 0
            || settings.ExcellentExtraNormalKey < 0
            || settings.ExcellentThreshold is < 0 or > 100)
        {
            return EconomyResult<GameRewardView>.Conflict("主遊戲經濟設定無效，請先由管理員修正。");
        }
        var settledRounds = room.GameRounds.Where(round => round.IsSettled).ToList();
        if (settledRounds.Count == 0)
            return EconomyResult<GameRewardView>.Conflict("這場遊戲沒有可結算的回合。");
        var totalVotes = settledRounds
            .SelectMany(round => round.RoundAnswers)
            .SelectMany(answer => answer.Votes)
            .Sum(vote => Math.Max(0, vote.Count));
        var playerVotes = settledRounds
            .SelectMany(round => round.RoundAnswers)
            .Where(answer => answer.GamePlayerId == player.Id)
            .SelectMany(answer => answer.Votes)
            .Sum(vote => Math.Max(0, vote.Count));
        var roundsWon = settledRounds.Count(round =>
        {
            var ranked = round.RoundAnswers
                .Select(answer => new
                {
                    Answer = answer,
                    Votes = answer.Votes.Sum(vote => Math.Max(0, vote.Count))
                })
                .OrderByDescending(item => item.Votes)
                .ThenBy(item => item.Answer.SubmittedAt)
                .ThenBy(item => item.Answer.Id)
                .FirstOrDefault();
            return ranked is not null
                && ranked.Votes > 0
                && ranked.Answer.GamePlayerId == player.Id;
        });
        var voteRatio = totalVotes == 0 ? 0d : Math.Clamp(playerVotes / (double)totalVotes, 0d, 1d);
        var winRatio = Math.Clamp(roundsWon / (double)Math.Max(1, settledRounds.Count), 0d, 1d);
        var performance = (int)Math.Round((voteRatio + winRatio) * 50d, MidpointRounding.ToZero);
        var points = settings.BasePointReward
            + (int)Math.Floor(settings.MaximumVoteBonus * voteRatio)
            + (int)Math.Floor(settings.MaximumWinBonus * winRatio);
        points = Math.Clamp(points, settings.MinimumPointReward, settings.MaximumPointReward);
        var keyReward = settings.CompletedNormalKey
            + (performance >= settings.ExcellentThreshold ? settings.ExcellentExtraNormalKey : 0);
        if (keyReward < 0)
            return EconomyResult<GameRewardView>.Conflict("主遊戲獎勵設定不可產生負數鑰匙。");

        var pointBalance = await GetOrCreatePointBalanceAsync(userId, cancellationToken);
        // 舊快照使用 KEY-NORMAL，新資料使用 NORMAL；兩者都代表一般鑰匙，避免還原舊資料後無法發獎勵。
        var keyDefinition = await db.KeyDefinitions
            .Where(item => item.IsActive && (item.Code == "NORMAL" || item.Code == "KEY-NORMAL"))
            .OrderBy(item => item.Code == "NORMAL" ? 0 : 1)
            .FirstOrDefaultAsync(cancellationToken);
        if (keyReward > 0 && keyDefinition is null)
            return EconomyResult<GameRewardView>.Conflict("找不到啟用中的 NORMAL 鑰匙定義。");
        var keyBalance = keyDefinition is null
            ? null
            : await GetOrCreateKeyBalanceAsync(userId, keyDefinition.Id, cancellationToken);
        var now = DateTime.UtcNow;
        pointBalance.Balance = checked(pointBalance.Balance + points);
        pointBalance.UpdatedAt = now;
        db.PointTransactions.Add(new PointTransaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Amount = points,
            Reason = "多人主遊戲完成獎勵",
            ReferenceType = "MAIN_GAME_REWARD",
            ReferenceId = player.Id,
            CreatedAt = now
        });
        if (keyDefinition is not null && keyBalance is not null && keyReward > 0)
        {
            keyBalance.Balance = checked(keyBalance.Balance + keyReward);
            keyBalance.UpdatedAt = now;
            db.KeyTransactions.Add(new KeyTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                KeyDefinitionId = keyDefinition.Id,
                Amount = keyReward,
                Reason = "多人主遊戲完成獎勵",
                ReferenceType = "MAIN_GAME_REWARD",
                ReferenceId = player.Id,
                CreatedAt = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EconomyResult<GameRewardView>.Success(new GameRewardView(
            points,
            keyReward,
            performance,
            roundsWon,
            false));
    }

    /// <summary>讀取單一主遊戲經濟設定；資料庫尚未建立設定時回傳可供本地開發使用的預設值。</summary>
    public async Task<GameEconomySetting> GetGameEconomySettingAsync(
        CancellationToken cancellationToken = default)
    {
        var setting = await db.GameEconomySettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        return setting ?? new GameEconomySetting
        {
            Id = 1,
            MinimumPointReward = 8,
            MaximumPointReward = 20,
            BasePointReward = 8,
            MaximumVoteBonus = 8,
            MaximumWinBonus = 4,
            CompletedNormalKey = 1,
            ExcellentExtraNormalKey = 1,
            ExcellentThreshold = 80,
            DailyMiniGameRewardLimit = 5,
            KeyProgressToNormalKey = 100
        };
    }

    private async Task<IReadOnlyList<KeyExchangeRuleView>> GetExchangeRulesAsync(
        Guid userId,
        IReadOnlyCollection<ArtifactScopeView> artifacts,
        IReadOnlySet<Guid> unlockedIds,
        CancellationToken cancellationToken)
    {
        var rules = await db.KeyExchangeRules
            .AsNoTracking()
            .Include(item => item.SourceKeyDefinition)
            .Include(item => item.TargetKeyDefinition)
            .Where(item => item.IsActive
                && item.SourceKeyDefinition.IsActive
                && item.TargetKeyDefinition.IsActive)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return rules
            .Where(rule => rule.SourceAmount > 0 && rule.TargetAmount > 0)
            .Select(rule => new KeyExchangeRuleView(
                rule.Id,
                rule.SourceKeyDefinition.Code,
                rule.SourceKeyDefinition.Name,
                rule.SourceAmount,
                rule.TargetKeyDefinition.Code,
                rule.TargetKeyDefinition.Name,
                rule.TargetAmount,
                CountEligibleArtifacts(rule.TargetKeyDefinition, artifacts, unlockedIds),
                rule.Description))
            .ToList();
    }

    private IQueryable<Artifact> GetEligibleArtifactQuery(Guid userId, KeyDefinition key)
    {
        var query = db.Artifacts
            .Where(artifact => artifact.IsActive
                && !db.ArtifactUnlocks.Any(unlock => unlock.UserId == userId && unlock.ArtifactId == artifact.Id));
        return key.ScopeType switch
        {
            "CATEGORY" when key.CategoryId.HasValue => query.Where(artifact => artifact.CategoryId == key.CategoryId.Value),
            "ERA" when key.EraBucketId.HasValue => query.Where(artifact => artifact.EraBucketId == key.EraBucketId.Value),
            "NORMAL" or "UNIVERSAL" => query,
            // 定義資料若不符合 scope 規則，不能意外退回「全部文物」，避免錯誤設定擴大解鎖範圍。
            _ => query.Where(_ => false)
        };
    }

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

    private static int CountEligibleArtifacts(
        KeyDefinition key,
        IEnumerable<ArtifactScopeView> artifacts,
        IReadOnlySet<Guid> unlockedIds) =>
        artifacts.Count(artifact => !unlockedIds.Contains(artifact.Id) && IsInScope(key, artifact));

    private static bool IsInScope(KeyDefinition key, ArtifactScopeView artifact) => key.ScopeType switch
    {
        "CATEGORY" => key.CategoryId.HasValue && key.CategoryId.Value == artifact.CategoryId,
        "ERA" => key.EraBucketId.HasValue && key.EraBucketId.Value == artifact.EraBucketId,
        _ => true
    };

    private static CouponView ToCouponView(UserCoupon coupon, CouponDefinition definition) => new(
        coupon.Id,
        definition.Id,
        definition.Code,
        definition.Name,
        definition.AcquisitionType,
        definition.PointCost,
        definition.DiscountType,
        definition.DiscountValue,
        definition.MinimumAmount,
        coupon.Status,
        coupon.IssuedAt,
        coupon.ExpiresAt,
        coupon.UsedAt,
        coupon.RevokedAt);

    private static string? ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "異動原因必須填寫。";
        return reason.Trim().Length > 40 ? "異動原因不可超過 40 個字元。" : null;
    }

    private static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();

    private sealed record ArtifactScopeView(Guid Id, Guid CategoryId, Guid EraBucketId);

    private sealed record ArtifactCandidateView(Guid Id, string Name);
}

/// <summary>封裝經濟服務的成功結果或可由 API 轉換成 Problem Details 的錯誤。</summary>
public sealed record EconomyResult<T>(T? Value, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>表示這次操作是否成功。</summary>
    public bool Succeeded => ErrorCode is null;

    /// <summary>建立成功結果。</summary>
    public static EconomyResult<T> Success(T value) => new(value, null, null);

    /// <summary>建立輸入資料無效的結果。</summary>
    public static EconomyResult<T> Invalid(string message) => new(default, "INVALID", message);

    /// <summary>建立資源不存在的結果。</summary>
    public static EconomyResult<T> NotFound(string message) => new(default, "NOT_FOUND", message);

    /// <summary>建立目前狀態不允許操作的結果。</summary>
    public static EconomyResult<T> Conflict(string message) => new(default, "CONFLICT", message);

    /// <summary>建立目前會員沒有操作權限的結果。</summary>
    public static EconomyResult<T> Forbidden(string message) => new(default, "FORBIDDEN", message);
}

/// <summary>會員經濟總覽，包含點數、鑰匙進度、鑰匙餘額與可用兌換規則。</summary>
public sealed record MemberEconomyView(
    int PointBalance,
    int KeyProgressBalance,
    int KeyProgressToNormalKey,
    IReadOnlyList<KeyBalanceView> Keys,
    IReadOnlyList<KeyExchangeRuleView> ExchangeRules);

/// <summary>單一鑰匙定義在會員身上的餘額與目前可解鎖數。</summary>
public sealed record KeyBalanceView(
    Guid Id,
    string Code,
    string Name,
    string ScopeType,
    Guid? CategoryId,
    Guid? EraBucketId,
    int Balance,
    int EligibleArtifactCount,
    int RecyclePointValue);

/// <summary>前端可直接呈現的鑰匙兌換規則與目標候選數。</summary>
public sealed record KeyExchangeRuleView(
    Guid Id,
    string SourceKeyCode,
    string SourceKeyName,
    int SourceAmount,
    string TargetKeyCode,
    string TargetKeyName,
    int TargetAmount,
    int TargetEligibleArtifactCount,
    string? Description);

/// <summary>使用鑰匙後的解鎖結果；沒有候選文物時 Unlocked 為 false 且不扣鑰匙。</summary>
public sealed record ArtifactUnlockView(
    bool Unlocked,
    Guid? ArtifactId,
    string? ArtifactName,
    int RemainingEligibleArtifactCount,
    string? Message);

/// <summary>鑰匙兌換完成後的來源、目標與剩餘候選資訊。</summary>
public sealed record KeyExchangeView(
    Guid RuleId,
    string SourceKeyCode,
    int SourceAmount,
    string TargetKeyCode,
    int TargetAmount,
    int TargetEligibleArtifactCount);

/// <summary>鑰匙回收完成後的扣除數量與取得點數。</summary>
public sealed record KeyRecycleView(
    string KeyCode,
    int KeyAmount,
    int PointAmount,
    int RemainingEligibleArtifactCount);

/// <summary>資產增減後回傳的異動量與新餘額。</summary>
public sealed record BalanceAdjustmentView(int Amount, int Balance);

/// <summary>會員優惠券的生命週期與稽核相關欄位。</summary>
public sealed record CouponView(
    Guid Id,
    Guid CouponDefinitionId,
    string Code,
    string Name,
    string AcquisitionType,
    int? PointCost,
    string DiscountType,
    decimal DiscountValue,
    decimal MinimumAmount,
    string Status,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime? UsedAt,
    DateTime? RevokedAt);

/// <summary>前端可顯示的點數兌換券選項；成本與折扣值來自後台設定。</summary>
public sealed record PointCouponOptionView(
    Guid Id,
    string Code,
    string Name,
    int PointCost,
    string DiscountType,
    decimal DiscountValue,
    decimal MinimumAmount,
    int ValidityDays,
    DateTime StartAt,
    DateTime EndAt);

/// <summary>會員目前配戴的成就稱號。</summary>
public sealed record EquippedTitleView(
    Guid UserAchievementId,
    Guid AchievementId,
    string AchievementCode,
    string AchievementName,
    string Title,
    DateTime UpdatedAt);

/// <summary>多人主遊戲一次獎勵結算的結果。</summary>
public sealed record GameRewardView(
    int PointReward,
    int NormalKeyReward,
    int PerformanceScore,
    int RoundsWon,
    bool AlreadyRewarded);
