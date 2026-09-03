namespace QMAH.Infrastructure.Configuration;

/// <summary>
/// 管理登入端點的限流設定
/// </summary>
public sealed class QmahRateLimitOptions
{
    public const string SectionName = "RateLimiting:Auth";

    public int PermitLimit { get; set; } = 12;

    public int WindowSeconds { get; set; } = 60;
}
