using Microsoft.Extensions.Options;

namespace QMAH.Infrastructure.Media;

/// <summary>
/// 將資料庫保存的邏輯媒體路徑轉成目前部署環境可使用的網址。
/// </summary>
public sealed class QmahMediaUrlResolver(IOptions<MediaDeliveryOptions> options)
{
    private static readonly string[] PublicLogicalRoots = ["/media", "/uploads"];

    /// <summary>
    /// Local 模式保留本機路徑；Cdn 模式只轉換公開圖片根目錄。
    /// </summary>
    public string? Resolve(string? logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath))
            return logicalPath;

        var value = logicalPath.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return value;
        }

        // 保留協定相對網址與受保護 API 網址，不把它們當成本機公開圖片處理。
        if (value.StartsWith("//", StringComparison.Ordinal))
            return value;

        var normalizedPath = value.Replace('\\', '/');
        if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
            normalizedPath = $"/{normalizedPath}";

        if (!IsPublicLogicalPath(normalizedPath) || !TryGetCdnBaseUrl(out var baseUrl))
            return normalizedPath;

        return $"{baseUrl}{NormalizePrefix(options.Value.PublicPathPrefix)}{normalizedPath}";
    }

    private bool TryGetCdnBaseUrl(out string baseUrl)
    {
        baseUrl = "";
        if (!string.Equals(options.Value.DeliveryMode, "Cdn", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl)
            || !Uri.TryCreate(options.Value.PublicBaseUrl.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        baseUrl = uri.ToString().TrimEnd('/');
        return true;
    }

    private static bool IsPublicLogicalPath(string path) =>
        PublicLogicalRoots.Any(root =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return "";

        var normalized = prefix.Trim().Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "" : $"/{normalized}";
    }
}
