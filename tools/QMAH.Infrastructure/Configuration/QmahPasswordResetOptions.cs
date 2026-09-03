namespace QMAH.Infrastructure.Configuration;

/// <summary>
/// 管理前台密碼重設連結設定
/// </summary>
public sealed class QmahPasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    /// <summary>
    /// 以 reset-password 結尾的絕對網址或同源前台網址
    /// </summary>
    public string ClientUrl { get; set; } = "";
}
