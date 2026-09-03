using System.Data;

using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.Services.Economy;

/// <summary>
/// 集中處理私人房間邀請、接受入場與邀請加碼的共用流程。
/// </summary>
/// <remarks>
/// Controller 只負責登入者、路由與 HTTP 回應；房間容量、邀請狀態、座位與加碼
/// 的先後順序都在這裡處理，避免遊戲與社群入口各自複製一套規則。
/// </remarks>
public sealed class GameRoomInvitationService(
    QmahDbContext db,
    CommunityRewardService communityRewardService)
{
    /// <summary>建立一筆私人房間邀請；同一房間對同一會員同時只能有一筆待處理邀請。</summary>
    public async Task<EconomyResult<GameRoomInvitationView>> CreateAsync(
        Guid inviterUserId,
        Guid roomId,
        CreateGameRoomInvitationInput input,
        CancellationToken cancellationToken = default)
    {
        var message = string.IsNullOrWhiteSpace(input.Message) ? null : input.Message.Trim();
        if (message is { Length: > 300 })
            return EconomyResult<GameRoomInvitationView>.Invalid("邀請訊息不可超過 300 個字元。");
        if (input.InviteeUserId == inviterUserId)
            return EconomyResult<GameRoomInvitationView>.Invalid("不能邀請自己加入私人房間。");

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var room = await db.GameRooms
            .Include(item => item.GamePlayers)
            .SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken);
        if (room is null || room.Status == "CANCELLED")
            return EconomyResult<GameRoomInvitationView>.NotFound("找不到可邀請的私人房間。");
        if (room.Visibility != "PRIVATE")
            return EconomyResult<GameRoomInvitationView>.Invalid("只有 PRIVATE 房間可以發送邀請。");
        if (room.Status != "WAITING")
            return EconomyResult<GameRoomInvitationView>.Conflict("只有等待中的房間可以發送邀請。");
        if (!room.GamePlayers.Any(player => player.UserId == inviterUserId && player.Role == "HOST"))
            return EconomyResult<GameRoomInvitationView>.Forbidden("只有房間發起人可以發送邀請。");
        if (!await db.Users.AnyAsync(
                user => user.Id == input.InviteeUserId && user.Status == "ACTIVE",
                cancellationToken))
        {
            return EconomyResult<GameRoomInvitationView>.NotFound("找不到可邀請的啟用會員。");
        }
        if (room.GamePlayers.Any(player => player.UserId == input.InviteeUserId
            && player.ConnectionStatus != "LEFT"
            && player.LeftAt == null))
        {
            return EconomyResult<GameRoomInvitationView>.Conflict("這位會員已經在房間中。");
        }
        if (await db.GameRoomInvitations.AnyAsync(
                invitation => invitation.RoomId == roomId
                    && invitation.InviteeUserId == input.InviteeUserId
                    && invitation.Status == "PENDING",
                cancellationToken))
        {
            return EconomyResult<GameRoomInvitationView>.Conflict("這位會員已有一筆待處理邀請。");
        }

        var invitation = new GameRoomInvitation
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            InviterUserId = inviterUserId,
            InviteeUserId = input.InviteeUserId,
            Status = "PENDING",
            Message = message,
            CreatedAt = DateTime.UtcNow
        };
        db.GameRoomInvitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EconomyResult<GameRoomInvitationView>.Success(
            await GetRequiredViewAsync(invitation.Id, cancellationToken));
    }

    /// <summary>讀取目前會員收到的待處理與歷史邀請。</summary>
    public async Task<IReadOnlyList<GameRoomInvitationView>> GetReceivedAsync(
        Guid inviteeUserId,
        CancellationToken cancellationToken = default)
    {
        var invitations = await db.GameRoomInvitations
            .AsNoTracking()
            .Where(item => item.InviteeUserId == inviteeUserId)
            .OrderBy(item => item.Status == "PENDING" ? 0 : 1)
            .ThenByDescending(item => item.CreatedAt)
            .Include(item => item.Room)
            .Include(item => item.InviterUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.InviteeUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.RewardKeyDefinition)
            .Take(100)
            .ToListAsync(cancellationToken);
        return invitations.Select(ToView).ToList();
    }

    /// <summary>讀取房主在指定私人房間送出的邀請。</summary>
    public async Task<EconomyResult<IReadOnlyList<GameRoomInvitationView>>> GetSentAsync(
        Guid inviterUserId,
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        var roomExists = await db.GameRooms.AnyAsync(item => item.Id == roomId, cancellationToken);
        if (!roomExists)
            return EconomyResult<IReadOnlyList<GameRoomInvitationView>>.NotFound("找不到遊戲房間。");
        if (!await IsHostAsync(inviterUserId, roomId, cancellationToken))
            return EconomyResult<IReadOnlyList<GameRoomInvitationView>>.Forbidden("只有房間發起人可以查看邀請。");

        var invitations = await db.GameRoomInvitations
            .AsNoTracking()
            .Where(item => item.RoomId == roomId && item.InviterUserId == inviterUserId)
            .OrderByDescending(item => item.CreatedAt)
            .Include(item => item.Room)
            .Include(item => item.InviterUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.InviteeUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.RewardKeyDefinition)
            .Take(200)
            .ToListAsync(cancellationToken);
        return EconomyResult<IReadOnlyList<GameRoomInvitationView>>.Success(
            invitations.Select(ToView).ToList());
    }

    /// <summary>
    /// 接受或拒絕邀請；接受時先加入房間，再由共用加碼服務結算一次獎勵。
    /// </summary>
    public async Task<EconomyResult<GameRoomInvitationView>> RespondAsync(
        Guid inviteeUserId,
        Guid invitationId,
        RespondGameRoomInvitationInput input,
        CancellationToken cancellationToken = default)
    {
        var decision = input.Decision.Trim().ToUpperInvariant();
        if (decision is not ("ACCEPT" or "DECLINE"))
            return EconomyResult<GameRoomInvitationView>.Invalid("Decision 只能是 ACCEPT 或 DECLINE。");

        var invitation = await db.GameRoomInvitations
            .Include(item => item.Room)
                .ThenInclude(room => room.GamePlayers)
            .Include(item => item.InviterUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.InviteeUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.RewardKeyDefinition)
            .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        if (invitation is null)
            return EconomyResult<GameRoomInvitationView>.NotFound("找不到房間邀請。");
        if (invitation.InviteeUserId != inviteeUserId)
            return EconomyResult<GameRoomInvitationView>.Forbidden("這筆邀請不屬於目前會員。");
        if (invitation.Status != "PENDING")
            return EconomyResult<GameRoomInvitationView>.Conflict("這筆邀請已經回應，不能重複處理。");

        var now = DateTime.UtcNow;
        if (decision == "DECLINE")
        {
            invitation.Status = "DECLINED";
            invitation.RespondedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return EconomyResult<GameRoomInvitationView>.Success(
                await GetRequiredViewAsync(invitation.Id, cancellationToken));
        }

        var room = invitation.Room;
        if (room.Status != "WAITING")
            return EconomyResult<GameRoomInvitationView>.Conflict("房間目前不在等待加入狀態。");
        if (room.GamePlayers.Any(player => player.UserId == inviteeUserId))
            return EconomyResult<GameRoomInvitationView>.Conflict("目前會員已經在房間中。");

        var activePlayers = room.GamePlayers.Count(player =>
            player.ConnectionStatus != "LEFT" && player.LeftAt == null);
        if (activePlayers >= room.MaxPlayers)
            return EconomyResult<GameRoomInvitationView>.Conflict("房間已額滿，無法接受邀請。");

        var usedSeats = room.GamePlayers
            .Where(player => player.SeatNo.HasValue
                && player.ConnectionStatus != "LEFT"
                && player.LeftAt == null)
            .Select(player => player.SeatNo!.Value)
            .ToHashSet();
        var seat = Enumerable.Range(1, room.MaxPlayers)
            .Select(value => (byte)value)
            .FirstOrDefault(value => !usedSeats.Contains(value));
        if (seat == 0)
            return EconomyResult<GameRoomInvitationView>.Conflict("房間目前沒有可用座位。");

        var displayName = string.IsNullOrWhiteSpace(input.DisplayName)
            ? invitation.InviteeUser.Profile?.Nickname
                ?? invitation.InviteeUser.Email
                ?? "玩家"
            : input.DisplayName.Trim();
        if (displayName.Length > 80)
            return EconomyResult<GameRoomInvitationView>.Invalid("遊戲顯示名稱不可超過 80 個字元。");

        room.GamePlayers.Add(new GamePlayer
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = inviteeUserId,
            PlayerKey = $"invite-{room.Id:N}-{inviteeUserId:N}",
            DisplayName = displayName,
            Role = "PLAYER",
            IsReady = false,
            SeatNo = seat,
            JoinedAt = now,
            ConnectionStatus = "ONLINE",
            LastSeenAt = now
        });
        room.StateVersion++;
        invitation.Status = "ACCEPTED";
        invitation.RespondedAt = now;

        // 若有會員或官方加碼，這次 SaveChanges 會和房間加入、帳本交易一起進入共用服務的交易。
        var grant = await communityRewardService.GrantRoomInvitationAsync(
            invitation,
            cancellationToken);
        if (grant is null)
            await db.SaveChangesAsync(cancellationToken);

        return EconomyResult<GameRoomInvitationView>.Success(
            await GetRequiredViewAsync(invitation.Id, cancellationToken));
    }

    /// <summary>取消尚未回應的房間邀請；已接受或已拒絕的邀請保留歷史。</summary>
    public async Task<EconomyResult<GameRoomInvitationView>> CancelAsync(
        Guid inviterUserId,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        var invitation = await db.GameRoomInvitations
            .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        if (invitation is null)
            return EconomyResult<GameRoomInvitationView>.NotFound("找不到房間邀請。");
        if (invitation.InviterUserId != inviterUserId)
            return EconomyResult<GameRoomInvitationView>.Forbidden("只有邀請發起人可以取消邀請。");
        if (invitation.Status != "PENDING")
            return EconomyResult<GameRoomInvitationView>.Conflict("只有待處理邀請可以取消。");

        invitation.Status = "CANCELLED";
        invitation.RespondedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return EconomyResult<GameRoomInvitationView>.Success(
            await GetRequiredViewAsync(invitation.Id, cancellationToken));
    }

    private async Task<bool> IsHostAsync(
        Guid userId,
        Guid roomId,
        CancellationToken cancellationToken) =>
        await db.GamePlayers.AnyAsync(
            player => player.RoomId == roomId
                && player.UserId == userId
                && player.Role == "HOST"
                && player.ConnectionStatus != "LEFT"
                && player.LeftAt == null,
            cancellationToken);

    private async Task<GameRoomInvitationView> GetRequiredViewAsync(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        var invitation = await db.GameRoomInvitations
            .AsNoTracking()
            .Include(item => item.Room)
            .Include(item => item.InviterUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.InviteeUser)
                .ThenInclude(user => user.Profile)
            .Include(item => item.RewardKeyDefinition)
            .Where(item => item.Id == invitationId)
            .SingleAsync(cancellationToken);
        return ToView(invitation);
    }

    private static GameRoomInvitationView ToView(GameRoomInvitation invitation) => new(
        invitation.Id,
        invitation.RoomId,
        invitation.Room.RoomCode,
        invitation.Status,
        invitation.InviterUserId,
        invitation.InviterUser.Profile?.Nickname ?? invitation.InviterUser.Email ?? "會員",
        invitation.InviteeUserId,
        invitation.InviteeUser.Profile?.Nickname ?? invitation.InviteeUser.Email ?? "會員",
        invitation.Message,
        invitation.RewardCampaignId,
        invitation.RewardPointAmount,
        invitation.RewardKeyDefinitionId,
        invitation.RewardKeyDefinition == null ? null : invitation.RewardKeyDefinition.Code,
        invitation.RewardKeyDefinition == null ? null : invitation.RewardKeyDefinition.Name,
        invitation.RewardKeyAmount,
        invitation.RewardGrantedAt,
        invitation.CreatedAt,
        invitation.RespondedAt);
}

/// <summary>建立房間邀請時使用的共用輸入。</summary>
public sealed record CreateGameRoomInvitationInput(Guid InviteeUserId, string? Message = null);

/// <summary>回應房間邀請時使用的共用輸入。</summary>
public sealed record RespondGameRoomInvitationInput(string Decision, string? DisplayName = null);

/// <summary>房間邀請、回應與實際加碼結果。</summary>
public sealed record GameRoomInvitationView(
    Guid Id,
    Guid RoomId,
    string RoomCode,
    string Status,
    Guid InviterUserId,
    string InviterDisplayName,
    Guid InviteeUserId,
    string InviteeDisplayName,
    string? Message,
    Guid? RewardCampaignId,
    int RewardPointAmount,
    Guid? RewardKeyDefinitionId,
    string? RewardKeyCode,
    string? RewardKeyName,
    int RewardKeyAmount,
    DateTime? RewardGrantedAt,
    DateTime CreatedAt,
    DateTime? RespondedAt);
