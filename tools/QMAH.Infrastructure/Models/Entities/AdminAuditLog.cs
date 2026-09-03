using QMAH.Infrastructure.Models.Identity;

namespace QMAH.Infrastructure.Models.Entities;

/// <summary>
/// 後台狀態異動的稽核紀錄；不保存密碼、Token 或原始 request body。
/// </summary>
public partial class AdminAuditLog
{
    public long Id { get; set; }

    public Guid? ActorUserId { get; set; }

    public string Area { get; set; } = null!;

    public string Controller { get; set; } = null!;

    public string Action { get; set; } = null!;

    public string HttpMethod { get; set; } = null!;

    public string RequestPath { get; set; } = null!;

    public int ResultStatusCode { get; set; }

    public string? Detail { get; set; }

    public DateTime OccurredAt { get; set; }

    public virtual ApplicationUser? ActorUser { get; set; }
}
