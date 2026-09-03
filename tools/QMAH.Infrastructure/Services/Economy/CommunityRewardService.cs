using System.Data;

using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Services.Economy;

/// <summary>
/// 管理活動與私人房間的參與加碼規則，並在實際參與時結算點數與鑰匙。
/// </summary>
/// <remarks>
/// 會員規則採用總量上限，獎勵發出時才扣發起人的背包；若預算或背包不足，
/// 該次不發加碼，也不會把不足的數量硬扣成負數。官方規則則由管理員建立，
/// 使用有效期間內的 UNLIMITED 模式，不會扣除管理員個人資產。
/// </remarks>
public sealed class CommunityRewardService(QmahDbContext db)
{
    private const int MaxPointPerRecipient = 10_000;
    private const int MaxKeyPerRecipient = 100;
    private const int MaxPointBudget = 1_000_000;
    private const int MaxKeyBudget = 10_000;
    private const int MaxCampaignDays = 366;

    /// <summary>建立或更新私人遊戲房間的會員加碼規則。</summary>
    public async Task<EconomyResult<CommunityRewardPolicyView?>> ConfigureRoomAsync(
        Guid ownerUserId,
        Guid roomId,
        CommunityRewardConfiguration request,
        CancellationToken cancellationToken = default)
    {
        var room = await db.GameRooms
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken);
        if (room is null)
            return EconomyResult<CommunityRewardPolicyView?>.NotFound("找不到遊戲房間。");
        if (room.Visibility != "PRIVATE")
            return EconomyResult<CommunityRewardPolicyView?>.Invalid("只有 PRIVATE 房間可以設定會員加碼。");
        if (!await db.GamePlayers.AnyAsync(
                player => player.RoomId == roomId
                    && player.UserId == ownerUserId
                    && player.Role == "HOST",
                cancellationToken))
        {
            return EconomyResult<CommunityRewardPolicyView?>.Forbidden("只有房間發起人可以設定加碼規則。");
        }

        return await ConfigureAsync(
            targetType: "GAME_ROOM",
            eventId: null,
            gameRoomId: roomId,
            ownerUserId,
            sponsorType: "MEMBER",
            budgetMode: "LIMITED",
            request,
            cancellationToken);
    }

    /// <summary>建立或更新活動的參與加碼規則；官方活動由管理員提供，不扣管理員背包。</summary>
    public async Task<EconomyResult<CommunityRewardPolicyView?>> ConfigureEventAsync(
        Guid actorUserId,
        Guid eventId,
        bool isAdministrator,
        CommunityRewardConfiguration request,
        CancellationToken cancellationToken = default)
    {
        var eventData = await db.Events
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (eventData is null)
            return EconomyResult<CommunityRewardPolicyView?>.NotFound("找不到活動。");

        var isOfficial = eventData.EventType == "OFFICIAL";
        if (isOfficial && !isAdministrator)
            return EconomyResult<CommunityRewardPolicyView?>.Forbidden("只有管理員可以設定官方活動加碼。");
        if (!isOfficial)
        {
            if (!eventData.OrganizerUserId.HasValue)
                return EconomyResult<CommunityRewardPolicyView?>.Conflict("此活動尚未設定發起人，暫時不能設定會員加碼。");
            if (eventData.OrganizerUserId.Value != actorUserId)
                return EconomyResult<CommunityRewardPolicyView?>.Forbidden("只有活動發起人可以設定活動加碼。");
        }

        var sponsorType = isOfficial ? "OFFICIAL" : "MEMBER";
        var budgetMode = isOfficial ? "UNLIMITED" : "LIMITED";
        return await ConfigureAsync(
            targetType: "EVENT",
            eventId,
            gameRoomId: null,
            ownerUserId: isOfficial ? actorUserId : eventData.OrganizerUserId!.Value,
            sponsorType,
            budgetMode,
            request,
            cancellationToken);
    }

    /// <summary>讀取私人房間目前的加碼設定。</summary>
    public async Task<EconomyResult<CommunityRewardPolicyView?>> GetRoomPolicyAsync(
        Guid requesterUserId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        var room = await db.GameRooms
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken);
        if (room is null)
            return EconomyResult<CommunityRewardPolicyView?>.NotFound("找不到遊戲房間。");
        if (room.Visibility != "PRIVATE")
            return EconomyResult<CommunityRewardPolicyView?>.Invalid("只有 PRIVATE 房間可以查詢會員加碼規則。");
        if (!await db.GamePlayers.AnyAsync(
                player => player.RoomId == roomId
                    && player.UserId == requesterUserId
                    && player.ConnectionStatus != "LEFT"
                    && player.LeftAt == null,
                cancellationToken))
        {
            // 私人房間的預算與發起人資產狀態不應對尚未入場的會員公開。
            return EconomyResult<CommunityRewardPolicyView?>.Forbidden("只有私人房間的有效參與者可以查看加碼規則。");
        }

        return EconomyResult<CommunityRewardPolicyView?>.Success(
            await GetPolicyAsync("GAME_ROOM", roomId, cancellationToken));
    }

    /// <summary>讀取活動目前的加碼設定。</summary>
    public async Task<CommunityRewardPolicyView?> GetEventPolicyAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        await GetPolicyAsync("EVENT", eventId, cancellationToken);

    /// <summary>
    /// 為活動報名結算一次加碼；同一筆報名重複呼叫時不會重複發放。
    /// </summary>
    public async Task<CommunityRewardGrantView?> GrantEventRegistrationAsync(
        EventRegistration registration,
        CancellationToken cancellationToken = default)
    {
        if (registration.RewardGrantedAt.HasValue)
            return null;

        return await GrantAsync(
            targetType: "EVENT",
            targetId: registration.EventId,
            recipientUserId: registration.UserId,
            targetReferenceId: registration.Id,
            alreadyGranted: () => registration.RewardGrantedAt.HasValue,
            applyResult: (campaignId, pointAmount, keyDefinitionId, keyAmount, now) =>
            {
                registration.RewardCampaignId = campaignId;
                registration.RewardPointAmount = pointAmount;
                registration.RewardKeyDefinitionId = keyDefinitionId;
                registration.RewardKeyAmount = keyAmount;
                registration.RewardGrantedAt = now;
            },
            cancellationToken);
    }

    /// <summary>
    /// 為私人房間邀請結算一次加碼；邀請接受後才會從房主背包扣除。
    /// </summary>
    public async Task<CommunityRewardGrantView?> GrantRoomInvitationAsync(
        GameRoomInvitation invitation,
        CancellationToken cancellationToken = default)
    {
        if (invitation.RewardGrantedAt.HasValue)
            return null;

        return await GrantAsync(
            targetType: "GAME_ROOM",
            targetId: invitation.RoomId,
            recipientUserId: invitation.InviteeUserId,
            targetReferenceId: invitation.Id,
            alreadyGranted: () => invitation.RewardGrantedAt.HasValue,
            applyResult: (campaignId, pointAmount, keyDefinitionId, keyAmount, now) =>
            {
                invitation.RewardCampaignId = campaignId;
                invitation.RewardPointAmount = pointAmount;
                invitation.RewardKeyDefinitionId = keyDefinitionId;
                invitation.RewardKeyAmount = keyAmount;
                invitation.RewardGrantedAt = now;
            },
            cancellationToken);
    }

    private async Task<EconomyResult<CommunityRewardPolicyView?>> ConfigureAsync(
        string targetType,
        Guid? eventId,
        Guid? gameRoomId,
        Guid ownerUserId,
        string sponsorType,
        string budgetMode,
        CommunityRewardConfiguration request,
        CancellationToken cancellationToken)
    {
        var validationError = await ValidateConfigurationAsync(
            request,
            sponsorType,
            budgetMode,
            cancellationToken);
        if (validationError is not null)
            return EconomyResult<CommunityRewardPolicyView?>.Invalid(validationError);

        var now = DateTime.UtcNow;
        var validFrom = request.ValidFrom.HasValue
            ? NormalizeUtc(request.ValidFrom.Value)
            : now;
        var validUntil = request.ValidUntil.HasValue
            ? NormalizeUtc(request.ValidUntil.Value)
            : now.AddDays(7);
        if (validUntil <= validFrom)
            return EconomyResult<CommunityRewardPolicyView?>.Invalid("加碼有效結束時間必須晚於開始時間。");
        if ((validUntil - validFrom).TotalDays > MaxCampaignDays)
            return EconomyResult<CommunityRewardPolicyView?>.Invalid("加碼有效期間不可超過 366 天。");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var policy = await db.CommunityRewardCampaigns
            .SingleOrDefaultAsync(item =>
                item.TargetType == targetType
                && (targetType == "EVENT" ? item.EventId == eventId : item.GameRoomId == gameRoomId),
                cancellationToken);

        if (request.PointPerRecipient == 0 && request.KeyPerRecipient == 0)
        {
            if (policy is not null)
            {
                policy.IsActive = false;
                policy.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return EconomyResult<CommunityRewardPolicyView?>.Success(
                policy is null ? null : ToView(policy));
        }

        if (policy is null)
        {
            policy = new CommunityRewardCampaign
            {
                Id = Guid.NewGuid(),
                TargetType = targetType,
                EventId = eventId,
                GameRoomId = gameRoomId,
                OwnerUserId = ownerUserId,
                SponsorType = sponsorType,
                BudgetMode = budgetMode,
                CreatedAt = now
            };
            db.CommunityRewardCampaigns.Add(policy);
        }

        if (policy.SponsorType == "MEMBER"
            && (request.PointBudget < policy.PointIssued || request.KeyBudget < policy.KeyIssued))
        {
            return EconomyResult<CommunityRewardPolicyView?>.Conflict("新預算不能低於已經發出的加碼數量。");
        }

        policy.OwnerUserId = ownerUserId;
        policy.SponsorType = sponsorType;
        policy.BudgetMode = budgetMode;
        policy.PointPerRecipient = request.PointPerRecipient;
        policy.KeyDefinitionId = request.KeyDefinitionId;
        policy.KeyPerRecipient = request.KeyPerRecipient;
        policy.PointBudget = budgetMode == "UNLIMITED" ? 0 : request.PointBudget;
        policy.KeyBudget = budgetMode == "UNLIMITED" ? 0 : request.KeyBudget;
        policy.ValidFrom = validFrom;
        policy.ValidUntil = validUntil;
        policy.IsActive = true;
        policy.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return EconomyResult<CommunityRewardPolicyView?>.Success(ToView(policy));
    }

    private async Task<string?> ValidateConfigurationAsync(
        CommunityRewardConfiguration request,
        string sponsorType,
        string budgetMode,
        CancellationToken cancellationToken)
    {
        if (request.PointPerRecipient is < 0 or > MaxPointPerRecipient)
            return $"每位參與者的點數必須介於 0 至 {MaxPointPerRecipient}。";
        if (request.KeyPerRecipient is < 0 or > MaxKeyPerRecipient)
            return $"每位參與者的鑰匙數量必須介於 0 至 {MaxKeyPerRecipient}。";
        if (request.PointBudget is < 0 or > MaxPointBudget)
            return $"點數總預算必須介於 0 至 {MaxPointBudget}。";
        if (request.KeyBudget is < 0 or > MaxKeyBudget)
            return $"鑰匙總預算必須介於 0 至 {MaxKeyBudget}。";
        if (request.KeyPerRecipient == 0 && request.KeyDefinitionId.HasValue)
            return "有指定鑰匙種類時，鑰匙加碼數量必須大於 0。";
        if (request.KeyPerRecipient > 0 && !request.KeyDefinitionId.HasValue)
            return "設定鑰匙加碼時必須指定啟用中的鑰匙種類。";
        if (request.PointPerRecipient == 0 && request.PointBudget > 0)
            return "沒有點數加碼時，點數總預算必須為 0。";
        if (request.KeyPerRecipient == 0 && request.KeyBudget > 0)
            return "沒有鑰匙加碼時，鑰匙總預算必須為 0。";
        if (sponsorType == "OFFICIAL" && budgetMode != "UNLIMITED")
            return "官方活動必須使用官方無限量加碼模式。";
        if (sponsorType == "MEMBER" && budgetMode != "LIMITED")
            return "會員活動只能使用有限總量加碼模式。";
        if (budgetMode == "LIMITED"
            && request.PointPerRecipient > 0
            && request.PointBudget < request.PointPerRecipient)
        {
            return "點數總預算不可小於單次點數加碼。";
        }
        if (budgetMode == "LIMITED"
            && request.KeyPerRecipient > 0
            && request.KeyBudget < request.KeyPerRecipient)
        {
            return "鑰匙總預算不可小於單次鑰匙加碼。";
        }

        if (request.KeyDefinitionId.HasValue
            && !await db.KeyDefinitions.AnyAsync(
                key => key.Id == request.KeyDefinitionId.Value && key.IsActive,
                cancellationToken))
        {
            return "找不到啟用中的加碼鑰匙定義。";
        }

        return null;
    }

    private async Task<CommunityRewardPolicyView?> GetPolicyAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var policy = await db.CommunityRewardCampaigns
            .AsNoTracking()
            .Include(item => item.KeyDefinition)
            .SingleOrDefaultAsync(item =>
                item.TargetType == targetType
                && (targetType == "EVENT" ? item.EventId == targetId : item.GameRoomId == targetId),
                cancellationToken);
        return policy is null ? null : ToView(policy);
    }

    private async Task<CommunityRewardGrantView?> GrantAsync(
        string targetType,
        Guid targetId,
        Guid recipientUserId,
        Guid targetReferenceId,
        Func<bool> alreadyGranted,
        Action<Guid?, int, Guid?, int, DateTime> applyResult,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var policy = await db.CommunityRewardCampaigns
            .Include(item => item.KeyDefinition)
            .SingleOrDefaultAsync(item =>
                item.TargetType == targetType
                && (targetType == "EVENT" ? item.EventId == targetId : item.GameRoomId == targetId),
                cancellationToken);
        if (policy is null || !policy.IsActive)
        {
            // 設定不存在時也要標記本次報名已結算，避免未來補上規則後，舊參與紀錄被追溯發放獎勵。
            applyResult(null, 0, null, 0, DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CommunityRewardGrantView(false, 0, null, 0);
        }
        if (alreadyGranted())
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var now = DateTime.UtcNow;
        if (now < policy.ValidFrom || now >= policy.ValidUntil)
        {
            applyResult(policy.Id, 0, null, 0, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CommunityRewardGrantView(false, 0, null, 0);
        }

        var pointAmount = 0;
        Guid? keyDefinitionId = null;
        var keyAmount = 0;
        var sponsorIsMember = policy.SponsorType == "MEMBER";
        if (policy.PointPerRecipient > 0
            && CanIssue(policy, policy.PointPerRecipient, policy.PointBudget, policy.PointIssued))
        {
            if (!sponsorIsMember
                || await CanTakePointsAsync(policy.OwnerUserId, policy.PointPerRecipient, cancellationToken))
            {
                pointAmount = policy.PointPerRecipient;
            }
        }

        if (policy.KeyPerRecipient > 0
            && policy.KeyDefinitionId.HasValue
            && policy.KeyDefinition is { IsActive: true }
            && CanIssue(policy, policy.KeyPerRecipient, policy.KeyBudget, policy.KeyIssued))
        {
            if (!sponsorIsMember
                || await CanTakeKeyAsync(policy.OwnerUserId, policy.KeyDefinitionId.Value, policy.KeyPerRecipient, cancellationToken))
            {
                keyDefinitionId = policy.KeyDefinitionId;
                keyAmount = policy.KeyPerRecipient;
            }
        }

        if (sponsorIsMember && policy.OwnerUserId == recipientUserId)
        {
            // 發起人自己參與時不把資產從自己扣掉再發回自己，這次只記錄沒有加碼。
            pointAmount = 0;
            keyDefinitionId = null;
            keyAmount = 0;
        }

        if (pointAmount > 0)
        {
            if (sponsorIsMember)
            {
                var ownerBalance = await GetOrCreatePointBalanceAsync(policy.OwnerUserId, cancellationToken);
                ownerBalance.Balance -= pointAmount;
                ownerBalance.UpdatedAt = now;
                db.PointTransactions.Add(new PointTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = policy.OwnerUserId,
                    Amount = -pointAmount,
                    Reason = "活動加碼支出",
                    ReferenceType = "COMMUNITY_REWARD",
                    ReferenceId = targetReferenceId,
                    CreatedAt = now
                });
            }

            var recipientBalance = await GetOrCreatePointBalanceAsync(recipientUserId, cancellationToken);
            recipientBalance.Balance = checked(recipientBalance.Balance + pointAmount);
            recipientBalance.UpdatedAt = now;
            db.PointTransactions.Add(new PointTransaction
            {
                Id = Guid.NewGuid(),
                UserId = recipientUserId,
                Amount = pointAmount,
                Reason = "活動參與加碼",
                ReferenceType = "COMMUNITY_REWARD",
                ReferenceId = targetReferenceId,
                CreatedAt = now
            });
            policy.PointIssued = checked(policy.PointIssued + pointAmount);
        }

        if (keyAmount > 0 && keyDefinitionId.HasValue)
        {
            if (sponsorIsMember)
            {
                var ownerBalance = await GetOrCreateKeyBalanceAsync(
                    policy.OwnerUserId,
                    keyDefinitionId.Value,
                    cancellationToken);
                ownerBalance.Balance -= keyAmount;
                ownerBalance.UpdatedAt = now;
                db.KeyTransactions.Add(new KeyTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = policy.OwnerUserId,
                    KeyDefinitionId = keyDefinitionId.Value,
                    Amount = -keyAmount,
                    Reason = "活動加碼支出",
                    ReferenceType = "COMMUNITY_REWARD",
                    ReferenceId = targetReferenceId,
                    CreatedAt = now
                });
            }

            var recipientBalance = await GetOrCreateKeyBalanceAsync(
                recipientUserId,
                keyDefinitionId.Value,
                cancellationToken);
            recipientBalance.Balance = checked(recipientBalance.Balance + keyAmount);
            recipientBalance.UpdatedAt = now;
            db.KeyTransactions.Add(new KeyTransaction
            {
                Id = Guid.NewGuid(),
                UserId = recipientUserId,
                KeyDefinitionId = keyDefinitionId.Value,
                Amount = keyAmount,
                Reason = "活動參與加碼",
                ReferenceType = "COMMUNITY_REWARD",
                ReferenceId = targetReferenceId,
                CreatedAt = now
            });
            policy.KeyIssued = checked(policy.KeyIssued + keyAmount);
        }

        policy.UpdatedAt = now;
        applyResult(policy.Id, pointAmount, keyDefinitionId, keyAmount, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CommunityRewardGrantView(
            pointAmount > 0 || keyAmount > 0,
            pointAmount,
            keyDefinitionId,
            keyAmount);
    }

    private static bool CanIssue(
        CommunityRewardCampaign policy,
        int amount,
        int budget,
        int issued) =>
        policy.BudgetMode == "UNLIMITED"
            || (amount > 0 && issued <= budget - amount);

    private async Task<bool> CanTakePointsAsync(
        Guid userId,
        int amount,
        CancellationToken cancellationToken) =>
        await db.PointBalances
            .AsNoTracking()
            .AnyAsync(item => item.UserId == userId && item.Balance >= amount, cancellationToken);

    private async Task<bool> CanTakeKeyAsync(
        Guid userId,
        Guid keyDefinitionId,
        int amount,
        CancellationToken cancellationToken) =>
        await db.UserKeyBalances
            .AsNoTracking()
            .AnyAsync(item => item.UserId == userId
                && item.KeyDefinitionId == keyDefinitionId
                && item.Balance >= amount, cancellationToken);

    private async Task<PointBalance> GetOrCreatePointBalanceAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var balance = await db.PointBalances
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (balance is not null)
            return balance;

        balance = new PointBalance { UserId = userId, Balance = 0, UpdatedAt = DateTime.UtcNow };
        db.PointBalances.Add(balance);
        return balance;
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

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static CommunityRewardPolicyView ToView(CommunityRewardCampaign policy) =>
        new(
            policy.Id,
            policy.TargetType,
            policy.EventId,
            policy.GameRoomId,
            policy.SponsorType,
            policy.BudgetMode,
            policy.PointPerRecipient,
            policy.KeyDefinitionId,
            policy.KeyDefinition?.Code,
            policy.KeyDefinition?.Name,
            policy.KeyPerRecipient,
            policy.PointBudget,
            policy.BudgetMode == "UNLIMITED" ? null : Math.Max(0, policy.PointBudget - policy.PointIssued),
            policy.PointIssued,
            policy.KeyBudget,
            policy.BudgetMode == "UNLIMITED" ? null : Math.Max(0, policy.KeyBudget - policy.KeyIssued),
            policy.KeyIssued,
            policy.ValidFrom,
            policy.ValidUntil,
            policy.IsActive,
            policy.UpdatedAt);
}

/// <summary>活動或私人房間的每位參與者加碼與總量設定。</summary>
public sealed record CommunityRewardConfiguration(
    int PointPerRecipient,
    Guid? KeyDefinitionId,
    int KeyPerRecipient,
    int PointBudget,
    int KeyBudget,
    DateTime? ValidFrom = null,
    DateTime? ValidUntil = null);

/// <summary>後台或前台讀取的活動加碼規則與剩餘量。</summary>
public sealed record CommunityRewardPolicyView(
    Guid Id,
    string TargetType,
    Guid? EventId,
    Guid? GameRoomId,
    string SponsorType,
    string BudgetMode,
    int PointPerRecipient,
    Guid? KeyDefinitionId,
    string? KeyCode,
    string? KeyName,
    int KeyPerRecipient,
    int PointBudget,
    int? RemainingPointBudget,
    int PointIssued,
    int KeyBudget,
    int? RemainingKeyBudget,
    int KeyIssued,
    DateTime ValidFrom,
    DateTime ValidUntil,
    bool IsActive,
    DateTime UpdatedAt);

/// <summary>一次活動加碼的實際結算結果；未發放時仍可能留下 0 獎勵紀錄。</summary>
public sealed record CommunityRewardGrantView(
    bool Granted,
    int PointAmount,
    Guid? KeyDefinitionId,
    int KeyAmount);
