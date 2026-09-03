using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace QMAH.Infrastructure.Security;

/// <summary>
/// 清理舊版 QMAH／ASP.NET Core 登入 Cookie，避免切換本機版本後持續堆積標頭
/// </summary>
/// <remarks>
/// 這段只處理通過 Kestrel 標頭檢查並進入 ASP.NET Core pipeline 的 request
/// 超過上限的 request 仍需由瀏覽器清除一次
/// </remarks>
public static class QmahCookieRecoveryExtensions
{
    private static readonly string[] LegacyCookiePrefixes =
    [
        ".AspNetCore.Identity.Application",
        ".AspNetCore.Antiforgery.",
        ".QMAH.Web.Auth",
        ".QMAH.Api.Auth",
        ".QMAH.Web.Antiforgery",
        ".QMAH.Api.Antiforgery"
    ];

    public static IApplicationBuilder UseQmahCookieRecovery(
        this IApplicationBuilder app,
        params string[] currentCookieNames)
    {
        // 目前版本的 Cookie 不列入清理，只有已知舊前綴會在回應時刪除
        var currentNames = currentCookieNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return app.Use(async (context, next) =>
        {
            var staleNames = context.Request.Cookies.Keys
                .Where(name => IsLegacyCookie(name) && !currentNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (staleNames.Length > 0)
            {
                context.Response.OnStarting(() =>
                {
                    // 用根路徑刪除，涵蓋不同頁面寫入的同名舊 Cookie
                    foreach (var staleName in staleNames)
                    {
                        context.Response.Cookies.Delete(staleName, new CookieOptions
                        {
                            Path = "/",
                            HttpOnly = true,
                            Secure = context.Request.IsHttps,
                            SameSite = SameSiteMode.Lax
                        });
                    }

                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }

    private static bool IsLegacyCookie(string name) =>
        LegacyCookiePrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
