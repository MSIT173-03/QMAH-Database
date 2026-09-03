namespace QMAH.Infrastructure.Configuration;

/// <summary>
/// 集中管理 API 與 Razor 後台的驗證 Cookie 設定
/// </summary>
public sealed class QmahCookieOptions
{
    public const string SectionName = "Cookies";

    public string ApiAuthenticationName { get; set; } = ".QMAH.Api.Auth";

    public string WebAuthenticationName { get; set; } = ".QMAH.Web.Auth";

    public string ApiAntiforgeryName { get; set; } = ".QMAH.Api.Antiforgery";

    public string WebAntiforgeryName { get; set; } = ".QMAH.Web.Antiforgery";

    public int LifetimeDays { get; set; } = 14;
}
