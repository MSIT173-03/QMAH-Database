namespace QMAH.Infrastructure.Media;

/// <summary>
/// 控制公開圖片網址要使用本機應用程式或外部 CDN（內容傳遞網路）。
/// </summary>
public sealed class MediaDeliveryOptions
{
    public const string SectionName = "Media";

    /// <summary>
    /// Local（本機路徑）或 Cdn（外部圖片來源）。無法辨識時由解析器回到 Local。
    /// </summary>
    public string DeliveryMode { get; set; } = "Local";

    /// <summary>
    /// CDN（內容傳遞網路）的公開來源，例如 https://cdn.example.com。
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// CDN 網域後的共用路徑前綴；留白時沿用資料庫中的邏輯路徑。
    /// </summary>
    public string PublicPathPrefix { get; set; } = "";
}
