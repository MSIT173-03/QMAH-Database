using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace QMAH.Infrastructure.Data;

/// <summary>
/// 提供主機在登入前辨識 QMAH 資料庫狀態所需的共用診斷工具。
/// </summary>
public static class QmahDatabaseDiagnostics
{
    public static bool IsDatabaseFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException or RetryLimitExceededException)
            {
                return true;
            }
        }

        return false;
    }

    public static string GetTarget(QmahDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        return $"{connection.DataSource};Database={connection.Database}";
    }
}
