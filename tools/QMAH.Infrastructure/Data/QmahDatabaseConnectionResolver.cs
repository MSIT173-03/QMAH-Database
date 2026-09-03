using System.Diagnostics;

using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace QMAH.Infrastructure.Data;

public sealed record QmahDatabaseResolution(
    string ConnectionString,
    string Target,
    bool UsedAutomaticDiscovery,
    IReadOnlyList<string> FoundTargets);

/// <summary>
/// 依設定檔優先順序辨識真正包含 QMAH 的本機 SQL Server instance。
/// </summary>
/// <remarks>
/// 自動搜尋只檢查本機候選 instance 的 sys.databases，不會掃描網路或自動附加 mdf／還原 bak。
/// 是否啟用由 QmahDatabaseDiscovery:Enabled 控制，讓部署環境可以明確關閉 fallback。
/// </remarks>
public static class QmahDatabaseConnectionResolver
{
    public const string DefaultConnectionString =
        "Server=.;Database=QMAH;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False";

    public static async Task<QmahDatabaseResolution> ResolveAsync(
        string? configuredConnectionString,
        bool enableAutomaticDiscovery,
        CancellationToken cancellationToken = default)
    {
        var configured = NormalizeConnectionString(configuredConnectionString);
        if (!enableAutomaticDiscovery)
        {
            var connectionString = configured ?? DefaultConnectionString;
            return new(
                connectionString,
                Describe(connectionString),
                UsedAutomaticDiscovery: false,
                FoundTargets: Array.Empty<string>());
        }

        var candidates = new List<ConnectionCandidate>();
        if (configured is not null)
        {
            AddCandidate(
                candidates,
                configured,
                isConfigured: true);
        }

        foreach (var server in await GetLocalServerCandidatesAsync(cancellationToken))
        {
            AddCandidate(
                candidates,
                CreateIntegratedSecurityConnectionString(server),
                isConfigured: false);
        }

        var foundTargets = new List<string>();
        ConnectionCandidate? selected = null;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await ContainsQmahDatabaseAsync(candidate.ConnectionString, cancellationToken))
            {
                continue;
            }

            var target = Describe(candidate.ConnectionString);
            foundTargets.Add(target);
            selected ??= candidate;
        }

        if (selected is not null)
        {
            return new(
                selected.ConnectionString,
                Describe(selected.ConnectionString),
                UsedAutomaticDiscovery: !selected.IsConfigured,
                FoundTargets: foundTargets);
        }

        var fallback = configured ?? DefaultConnectionString;
        return new(
            fallback,
            Describe(fallback),
            UsedAutomaticDiscovery: false,
            FoundTargets: foundTargets);
    }

    private static async Task<bool> ContainsQmahDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        try
        {
            await StartLocalDbInstanceAsync(connectionString, cancellationToken);

            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                ConnectTimeout = 2
            };

            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CASE WHEN EXISTS
                (
                    SELECT 1
                    FROM sys.databases
                    WHERE name = N'QMAH'
                      AND state_desc = N'ONLINE'
                ) THEN 1 ELSE 0 END;
                """;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is int value && value == 1;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 掃描候選 instance 時，無法連線的候選只是「不是可用的 QMAH」，
            // 不應讓網站啟動流程因此中斷；登入頁會再顯示目前目標的友善警告。
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> GetLocalServerCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var servers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddServer(string? server)
        {
            if (!string.IsNullOrWhiteSpace(server) && seen.Add(server.Trim()))
            {
                servers.Add(server.Trim());
            }
        }

        // 順序是刻意的：先找標準 LocalDB，再找 SSMS 最常用的本機預設 instance。
        AddServer("(localdb)\\MSSQLLocalDB");
        AddServer(".");

        foreach (var localDbInstance in await GetLocalDbInstancesAsync(cancellationToken))
        {
            AddServer($"(localdb)\\{localDbInstance}");
        }

        foreach (var sqlInstance in GetRegisteredSqlInstances())
        {
            AddServer(sqlInstance);
        }

        return servers;
    }

    private static async Task<IReadOnlyList<string>> GetLocalDbInstancesAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sqllocaldb.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("info");

            if (!process.Start())
            {
                return Array.Empty<string>();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;

            return output
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> GetRegisteredSqlInstances()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL",
            @"SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server\Instance Names\SQL"
        };

        foreach (var registryPath in registryPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(registryPath);
                if (key is null)
                {
                    continue;
                }

                foreach (var instanceName in key.GetValueNames())
                {
                    if (string.Equals(instanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
                    {
                        servers.Add(".");
                    }
                    else if (!string.IsNullOrWhiteSpace(instanceName))
                    {
                        servers.Add($"localhost\\{instanceName}");
                    }
                }
            }
            catch
            {
                // 登錄檔可能因權限或非 Windows 環境不可讀；仍保留其他候選。
            }
        }

        return servers.ToArray();
    }

    private static async Task StartLocalDbInstanceAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        const string prefix = "(localdb)\\";
        if (!builder.DataSource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var instanceName = builder.DataSource[prefix.Length..];
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sqllocaldb.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("start");
            process.StartInfo.ArgumentList.Add(instanceName);

            if (!process.Start())
            {
                return;
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 找不到 SqlLocalDB 或 instance 已在執行時，交由 SqlClient 繼續嘗試。
        }
    }

    private static string? NormalizeConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "QMAH"
            };
            return builder.ConnectionString;
        }
        catch
        {
            return null;
        }
    }

    private static string CreateIntegratedSecurityConnectionString(string server)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "QMAH",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = false,
            ConnectTimeout = 2
        };
        return builder.ConnectionString;
    }

    private static void AddCandidate(
        ICollection<ConnectionCandidate> candidates,
        string connectionString,
        bool isConfigured)
    {
        var target = Describe(connectionString);
        if (candidates.Any(candidate =>
                string.Equals(candidate.Target, target, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(new(connectionString, target, isConfigured));
    }

    private static string Describe(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return $"{builder.DataSource};Database={builder.InitialCatalog}";
        }
        catch
        {
            return "QMAH 資料庫連線設定無效";
        }
    }

    private sealed record ConnectionCandidate(
        string ConnectionString,
        string Target,
        bool IsConfigured);
}
