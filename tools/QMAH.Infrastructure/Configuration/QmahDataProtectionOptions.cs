namespace QMAH.Infrastructure.Configuration;

/// <summary>
/// 管理 Cookie 與 Token 使用的資料保護金鑰設定
/// </summary>
public sealed class QmahDataProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// 需要共用 Cookie 的執行個體必須使用相同名稱
    /// </summary>
    public string ApplicationName { get; set; } = "QMAH";

    /// <summary>
    /// 可選的持久化金鑰目錄
    /// Azure 擴展執行個體或重新部署時應設定為掛載的持久化路徑
    /// </summary>
    public string? KeysPath { get; set; }
}
