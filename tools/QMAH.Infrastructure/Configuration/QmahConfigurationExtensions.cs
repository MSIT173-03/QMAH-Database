using Microsoft.Extensions.Configuration;

namespace QMAH.Infrastructure.Configuration;

/// <summary>
/// 載入本機設定檔並保留環境變數作為 Azure App Service 的最終覆寫來源
/// </summary>
public static class QmahConfigurationExtensions
{
    public static IConfigurationManager AddQmahLocalConfiguration(
        this IConfigurationManager configuration,
        string[] commandLineArgs)
    {
        configuration.AddJsonFile(
            "appsettings.Local.json",
            optional: true,
            reloadOnChange: true);

        // WebApplication.CreateBuilder 已載入這些來源
        // 在本機設定檔後再次加入可確保 Azure 應用程式設定優先
        // 命令列參數則維持最高優先順序
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(commandLineArgs);
        return configuration;
    }
}
