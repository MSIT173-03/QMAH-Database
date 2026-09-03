using System.Data;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;
using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Services.Economy;

/// <summary>
/// 處理營運人員針對一批會員進行的點數與優惠券異動。
/// </summary>
/// <remarks>
/// 批次不是用一筆總帳取代會員明細：每位會員仍會有自己的 PointTransaction，
/// 每張優惠券也會保留發放或撤銷者與批次主檔。批次主檔只負責保存原因、篩選條件快照與總量，
/// 讓營運中心可以統計活動事件，並在需要時回查到逐筆紀錄。
/// </remarks>
public sealed class BulkEconomyService(QmahDbContext db)
{
    private const int PreviewMemberLimit = 12;
    private const int MaxTargetCount = 10_000;
    private const int MaxPointAmountPerMember = 1_000_000;
    private const int MaxCouponAmountPerMember = 100;

    private static readonly HashSet<string> AssetTypes = ["POINT", "COUPON"];
    private static readonly HashSet<string> Operations = ["ADD", "DEDUCT"];

    /// <summary>
    /// 只依條件查詢對象，不建立任何資產或流水，供送出前確認影響範圍。
    /// </summary>
    public async Task<BulkEconomyPreview> PreviewAsync(
        BulkEconomyRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        var validationError = await ValidateRequestAsync(normalized, cancellationToken);
        if (validationError is not null)
        {
            return new BulkEconomyPreview(
                false,
                validationError,
                0,
                [],
                null);
        }

        var query = ApplyMemberFilter(db.Users.AsNoTracking(), normalized.Filter);
        var targetCount = await query.CountAsync(cancellationToken);
        var sample = await query
            .OrderBy(user => user.Email)
            .ThenBy(user => user.Id)
            .Take(PreviewMemberLimit)
            .Select(user => new BulkMemberPreview(
                user.Id,
                db.UserProfiles
                    .Where(profile => profile.UserId == user.Id)
                    .Select(profile => profile.Nickname)
                    .FirstOrDefault() ?? user.Email ?? user.UserName ?? "未設定會員",
                user.Email ?? user.UserName ?? "未設定 Email",
                user.Status))
            .ToListAsync(cancellationToken);

        return new BulkEconomyPreview(
            true,
            null,
            targetCount,
            sample,
            normalized.CouponDefinitionId.HasValue
                ? await db.CouponDefinitions
                    .AsNoTracking()
                    .Where(definition => definition.Id == normalized.CouponDefinitionId.Value)
                    .Select(definition => definition.Name)
                    .SingleOrDefaultAsync(cancellationToken)
                : null);
    }

    /// <summary>
    /// 以可重現的會員條件重新選取對象並執行全有或全無的批次異動。
    /// </summary>
    public async Task<BulkEconomyResult> ExecuteAsync(
        Guid adminUserId,
        BulkEconomyRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        var validationError = await ValidateRequestAsync(normalized, cancellationToken);
        if (validationError is not null)
            return BulkEconomyResult.Invalid(validationError);

        // 交易採 Serializable，避免預覽後到正式執行間同一批會員的資產被其他請求同時改寫。
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var targetIds = await ApplyMemberFilter(db.Users.AsNoTracking(), normalized.Filter)
            .OrderBy(user => user.Id)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        var batch = new EconomyAdjustmentBatch
        {
            Id = Guid.NewGuid(),
            AssetType = normalized.AssetType,
            Operation = normalized.Operation,
            UnitAmount = normalized.UnitAmount,
            CouponDefinitionId = normalized.CouponDefinitionId,
            FilterJson = JsonSerializer.Serialize(normalized.Filter),
            Reason = normalized.Reason,
            CreatedByAdminUserId = adminUserId,
            Status = targetIds.Count == 0 ? "EMPTY" : "PROCESSING",
            TargetCount = targetIds.Count,
            CreatedAt = DateTime.UtcNow
        };
        db.EconomyAdjustmentBatches.Add(batch);

        if (targetIds.Count > MaxTargetCount)
        {
            return await CompleteFailureAsync(
                transaction,
                batch,
                "符合條件的會員超過 10,000 人，請縮小篩選範圍後再執行。",
                cancellationToken);
        }

        if (targetIds.Count == 0)
        {
            batch.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return BulkEconomyResult.From(batch);
        }

        if (normalized.AssetType == "POINT")
        {
            await ApplyPointBatchAsync(batch, targetIds, normalized, cancellationToken);
        }
        else
        {
            await ApplyCouponBatchAsync(batch, targetIds, normalized, cancellationToken);
        }

        if (batch.FailureReason is not null)
        {
            return await CompleteFailureAsync(
                transaction,
                batch,
                batch.FailureReason,
                cancellationToken);
        }

        batch.Status = "COMPLETED";
        batch.SucceededCount = targetIds.Count;
        batch.AffectedAssetCount = checked((long)targetIds.Count * normalized.UnitAmount);
        batch.CompletedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BulkEconomyResult.From(batch);
    }

    /// <summary>取得營運中心用的最近批次，包含活動原因、對象人數與實際異動量。</summary>
    public async Task<IReadOnlyList<BulkEconomyBatchView>> GetRecentBatchesAsync(
        int take = 40,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 200);
        return await (
            from batch in db.EconomyAdjustmentBatches.AsNoTracking()
            join definition in db.CouponDefinitions.AsNoTracking()
                on batch.CouponDefinitionId equals definition.Id into definitions
            from definition in definitions.DefaultIfEmpty()
            join admin in db.Users.AsNoTracking()
                on batch.CreatedByAdminUserId equals admin.Id
            join profile in db.UserProfiles.AsNoTracking()
                on admin.Id equals profile.UserId into profiles
            from profile in profiles.DefaultIfEmpty()
            orderby batch.CreatedAt descending
            select new BulkEconomyBatchView(
                batch.Id,
                batch.AssetType,
                batch.Operation,
                batch.UnitAmount,
                definition == null ? null : definition.Name,
                batch.Reason,
                batch.FilterJson,
                batch.Status,
                batch.TargetCount,
                batch.SucceededCount,
                batch.FailedCount,
                batch.AffectedAssetCount,
                batch.FailureReason,
                profile == null ? admin.Email ?? "未設定管理員" : profile.Nickname,
                batch.CreatedAt,
                batch.CompletedAt))
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private async Task ApplyPointBatchAsync(
        EconomyAdjustmentBatch batch,
        IReadOnlyList<Guid> targetIds,
        BulkEconomyRequest request,
        CancellationToken cancellationToken)
    {
        var balances = await db.PointBalances
            .Where(balance => targetIds.Contains(balance.UserId))
            .ToDictionaryAsync(balance => balance.UserId, cancellationToken);

        if (request.Operation == "DEDUCT"
            && targetIds.Any(userId => !balances.TryGetValue(userId, out var balance)
                || balance.Balance < request.UnitAmount))
        {
            batch.FailedCount = targetIds.Count;
            batch.FailureReason = "至少一位符合條件的會員點數不足；為避免批次只完成一部分，本次未執行任何異動。";
            return;
        }

        if (request.Operation == "ADD"
            && balances.Values.Any(balance => balance.Balance > int.MaxValue - request.UnitAmount))
        {
            // 先檢查整批上限，避免 checked 在迴圈中拋例外，讓管理員只收到 500 而不知道批次原因。
            batch.FailedCount = targetIds.Count;
            batch.FailureReason = "至少一位符合條件的會員點數會超過系統上限；為避免批次只完成一部分，本次未執行任何異動。";
            return;
        }

        var signedAmount = request.Operation == "ADD"
            ? request.UnitAmount
            : -request.UnitAmount;
        var now = DateTime.UtcNow;
        foreach (var userId in targetIds)
        {
            if (!balances.TryGetValue(userId, out var balance))
            {
                balance = new PointBalance
                {
                    UserId = userId,
                    Balance = 0,
                    UpdatedAt = now
                };
                balances.Add(userId, balance);
                db.PointBalances.Add(balance);
            }

            balance.Balance = checked(balance.Balance + signedAmount);
            balance.UpdatedAt = now;
            db.PointTransactions.Add(new PointTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Amount = signedAmount,
                Reason = request.Reason,
                ReferenceType = "ADMIN_BATCH",
                ReferenceId = batch.Id,
                CreatedAt = now
            });
        }
    }

    private async Task ApplyCouponBatchAsync(
        EconomyAdjustmentBatch batch,
        IReadOnlyList<Guid> targetIds,
        BulkEconomyRequest request,
        CancellationToken cancellationToken)
    {
        var definition = await db.CouponDefinitions
            .SingleOrDefaultAsync(item => item.Id == request.CouponDefinitionId!.Value, cancellationToken);
        if (definition is null)
        {
            batch.FailedCount = targetIds.Count;
            batch.FailureReason = "找不到指定的優惠券定義；本次未執行任何異動。";
            return;
        }

        var now = DateTime.UtcNow;
        if (request.Operation == "ADD")
        {
            if (!definition.IsActive || definition.AcquisitionType != "ADMIN_GRANT")
            {
                batch.FailedCount = targetIds.Count;
                batch.FailureReason = "批次發放只允許使用啟用中的管理員發放型優惠券。";
                return;
            }

            foreach (var userId in targetIds)
            {
                for (var index = 0; index < request.UnitAmount; index++)
                {
                    db.UserCoupons.Add(new UserCoupon
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        CouponDefinitionId = definition.Id,
                        Status = "AVAILABLE",
                        IssuedAt = now,
                        ExpiresAt = now.AddDays(definition.ValidityDays),
                        IssuedByAdminUserId = batch.CreatedByAdminUserId,
                        GrantBatchId = batch.Id,
                        IssueReason = request.Reason
                    });
                }
            }

            return;
        }

        // 撤銷只挑未使用且尚未過期的券，並先檢查所有會員數量，確保不會只撤銷一半。
        var availableCoupons = await db.UserCoupons
            .Where(coupon => targetIds.Contains(coupon.UserId)
                && coupon.CouponDefinitionId == definition.Id
                && coupon.Status == "AVAILABLE"
                && coupon.ExpiresAt > now)
            .OrderBy(coupon => coupon.IssuedAt)
            .ThenBy(coupon => coupon.Id)
            .ToListAsync(cancellationToken);
        var availableByUser = availableCoupons
            .GroupBy(coupon => coupon.UserId)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (targetIds.Any(userId => !availableByUser.TryGetValue(userId, out var coupons)
            || coupons.Count < request.UnitAmount))
        {
            batch.FailedCount = targetIds.Count;
            batch.FailureReason = "至少一位符合條件的會員沒有足夠的可撤銷優惠券；為避免批次只完成一部分，本次未執行任何異動。";
            return;
        }

        foreach (var userId in targetIds)
        {
            foreach (var coupon in availableByUser[userId].Take(request.UnitAmount))
            {
                coupon.Status = "REVOKED";
                coupon.RevokedAt = now;
                coupon.RevokedByAdminUserId = batch.CreatedByAdminUserId;
                coupon.RevokeBatchId = batch.Id;
                coupon.RevokeReason = request.Reason;
            }
        }
    }

    private async Task<BulkEconomyResult> CompleteFailureAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        EconomyAdjustmentBatch batch,
        string failureReason,
        CancellationToken cancellationToken)
    {
        batch.Status = "FAILED";
        batch.FailureReason = failureReason;
        batch.FailedCount = batch.TargetCount;
        batch.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BulkEconomyResult.From(batch);
    }

    private async Task<string?> ValidateRequestAsync(
        BulkEconomyRequest request,
        CancellationToken cancellationToken)
    {
        if (!AssetTypes.Contains(request.AssetType))
            return "不支援的批次資產種類。";
        if (!Operations.Contains(request.Operation))
            return "不支援的批次異動方向。";
        if (request.UnitAmount < 1)
            return "每位會員的異動數量必須大於 0。";
        if (request.AssetType == "POINT" && request.UnitAmount > MaxPointAmountPerMember)
            return "每位會員的點數異動上限為 1,000,000。";
        if (request.AssetType == "COUPON" && request.UnitAmount > MaxCouponAmountPerMember)
            return "每位會員的優惠券數量上限為 100 張。";
        if (string.IsNullOrWhiteSpace(request.Reason))
            return "批次原因必須填寫。";
        if (request.Reason.Trim().Length > 200)
            return "批次原因不可超過 200 個字元。";
        if (request.Filter.Keyword?.Length > 100)
            return "會員搜尋文字不可超過 100 個字元。";
        if (request.Filter.Role?.Length > 50 || request.Filter.Status?.Length > 20)
            return "會員角色或狀態篩選值過長。";
        if (request.Filter.MinPointBalance < 0 || request.Filter.MaxPointBalance < 0)
            return "點數範圍不可小於 0。";
        if (request.Filter.MinPointBalance.HasValue
            && request.Filter.MaxPointBalance.HasValue
            && request.Filter.MinPointBalance > request.Filter.MaxPointBalance)
        {
            return "點數最低值不可大於最高值。";
        }
        if (request.Filter.CreatedFrom.HasValue
            && request.Filter.CreatedTo.HasValue
            && request.Filter.CreatedFrom.Value.Date > request.Filter.CreatedTo.Value.Date)
        {
            return "會員建立日期起點不可晚於終點。";
        }

        if (request.AssetType != "COUPON")
            return null;
        if (!request.CouponDefinitionId.HasValue)
            return "批次優惠券異動必須指定優惠券定義。";

        var definition = await db.CouponDefinitions
            .AsNoTracking()
            .Where(item => item.Id == request.CouponDefinitionId.Value)
            .Select(item => new { item.ValidityDays, item.IsActive, item.AcquisitionType })
            .SingleOrDefaultAsync(cancellationToken);
        if (definition is null)
            return "找不到指定的優惠券定義。";
        if (request.Operation == "ADD"
            && (!definition.IsActive
                || definition.AcquisitionType != "ADMIN_GRANT"
                || definition.ValidityDays <= 0))
        {
            return "批次發放只允許使用啟用中的管理員發放型優惠券，且有效天數必須大於 0。";
        }

        return null;
    }

    private static BulkEconomyRequest NormalizeRequest(BulkEconomyRequest request)
    {
        // JSON 表單可能省略 filter；以空條件補齊，避免後台或前台送出不完整資料時直接 NullReferenceException。
        var filter = request.Filter ?? new BulkMemberFilter();
        return request with
        {
            AssetType = request.AssetType?.Trim().ToUpperInvariant() ?? "",
            Operation = request.Operation?.Trim().ToUpperInvariant() ?? "",
            Reason = request.Reason?.Trim() ?? "",
            Filter = filter with
            {
                Keyword = string.IsNullOrWhiteSpace(filter.Keyword) ? null : filter.Keyword.Trim(),
                Role = string.IsNullOrWhiteSpace(filter.Role) ? null : filter.Role.Trim(),
                Status = string.IsNullOrWhiteSpace(filter.Status) ? null : filter.Status.Trim().ToUpperInvariant(),
                CreatedFrom = filter.CreatedFrom?.Date,
                CreatedTo = filter.CreatedTo?.Date
            }
        };
    }

    private IQueryable<ApplicationUser> ApplyMemberFilter(
        IQueryable<ApplicationUser> query,
        BulkMemberFilter filter)
    {
        if (filter.Keyword is not null)
        {
            query = query.Where(user =>
                (user.Email != null && user.Email.Contains(filter.Keyword))
                || (user.UserName != null && user.UserName.Contains(filter.Keyword))
                || db.UserProfiles.Any(profile => profile.UserId == user.Id && profile.Nickname.Contains(filter.Keyword)));
        }

        if (filter.Role is not null)
        {
            query = query.Where(user => db.UserRoles.Any(userRole =>
                userRole.UserId == user.Id
                && db.Roles.Any(role => role.Id == userRole.RoleId && role.Name == filter.Role)));
        }

        if (filter.Status is not null)
            query = query.Where(user => user.Status == filter.Status);
        if (filter.CreatedFrom.HasValue)
            query = query.Where(user => user.CreatedAt >= filter.CreatedFrom.Value);
        if (filter.CreatedTo.HasValue)
            query = query.Where(user => user.CreatedAt < filter.CreatedTo.Value.AddDays(1));

        if (filter.MinPointBalance.HasValue)
        {
            var min = filter.MinPointBalance.Value;
            query = min == 0
                ? query
                : query.Where(user => db.PointBalances.Any(balance =>
                    balance.UserId == user.Id && balance.Balance >= min));
        }

        if (filter.MaxPointBalance.HasValue)
        {
            var max = filter.MaxPointBalance.Value;
            query = query.Where(user => !db.PointBalances.Any(balance =>
                balance.UserId == user.Id && balance.Balance > max));
        }

        return query;
    }
}

/// <summary>批次會員條件會完整寫入主檔，供日後稽核時重現當時的選取範圍。</summary>
public sealed record BulkMemberFilter(
    string? Keyword = null,
    string? Role = null,
    string? Status = "ACTIVE",
    DateTime? CreatedFrom = null,
    DateTime? CreatedTo = null,
    int? MinPointBalance = null,
    int? MaxPointBalance = null);

/// <summary>批次資產作業的輸入；UnitAmount 是每位符合條件會員的異動數量。</summary>
public sealed record BulkEconomyRequest(
    string AssetType,
    string Operation,
    int UnitAmount,
    Guid? CouponDefinitionId,
    string Reason,
    BulkMemberFilter Filter);

/// <summary>預覽時顯示的少量會員樣本，不代表完整批次對象清單。</summary>
public sealed record BulkMemberPreview(
    Guid UserId,
    string DisplayName,
    string Email,
    string Status);

/// <summary>批次送出前的驗證結果、預估對象數與會員樣本。</summary>
public sealed record BulkEconomyPreview(
    bool IsValid,
    string? Error,
    int TargetCount,
    IReadOnlyList<BulkMemberPreview> Sample,
    string? CouponName);

/// <summary>批次執行結果，批次主檔的明細數量與失敗原因以此結果回傳。</summary>
public sealed record BulkEconomyResult(
    Guid BatchId,
    string Status,
    int TargetCount,
    int SucceededCount,
    int FailedCount,
    long AffectedAssetCount,
    string? Error)
{
    /// <summary>建立尚未寫入批次主檔的輸入錯誤結果。</summary>
    public static BulkEconomyResult Invalid(string error) =>
        new(Guid.Empty, "INVALID", 0, 0, 0, 0, error);

    /// <summary>將已寫入資料庫的批次主檔轉為服務回傳模型。</summary>
    public static BulkEconomyResult From(EconomyAdjustmentBatch batch) =>
        new(
            batch.Id,
            batch.Status,
            batch.TargetCount,
            batch.SucceededCount,
            batch.FailedCount,
            batch.AffectedAssetCount,
            batch.FailureReason);
}

/// <summary>營運中心批次歷史清單項目，保留活動原因與篩選條件快照。</summary>
public sealed record BulkEconomyBatchView(
    Guid Id,
    string AssetType,
    string Operation,
    int UnitAmount,
    string? CouponName,
    string Reason,
    string FilterJson,
    string Status,
    int TargetCount,
    int SucceededCount,
    int FailedCount,
    long AffectedAssetCount,
    string? FailureReason,
    string AdminName,
    DateTime CreatedAt,
    DateTime? CompletedAt);
