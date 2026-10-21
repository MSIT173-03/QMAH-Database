# QMAH-Database

[QMAH 清明鑑定屋](https://github.com/MSIT173-03/QMAH) 的 SQL Server 資料庫快照與資料工具。主程式倉庫不放完整資料庫，需要資料時從這裡的 Release 下載。

目前版本：[db-v1.0.10](https://github.com/MSIT173-03/QMAH-Database/releases/tag/db-v1.0.10)，對應 QMAH `v1.0.10` 以上。

## 還原資料庫

到 Release 下載 `QMAH-1.0.10.bak` 與 `SHA256SUMS.txt`，核對 SHA-256 後擇一方式建立資料庫：

- **備份檔**：在 SSMS 對 `QMAH-1.0.10.bak` 執行 Restore Database，資料庫名稱用 `QMAH`。
- **SQL 腳本**：在乾淨的 SQL Server 執行 `QMAH-1.0.10.sql`。這個腳本不能疊加到已有資料的資料庫。

用 Docker 的話，QMAH 主倉庫的 `docker compose up --build` 會自動下載並還原。

## 內容

| 路徑 | 說明 |
| --- | --- |
| `QMAH.sql` | 完整結構與資料的 SQL 腳本 |
| `database/Schema.sql` | 資料庫結構，與 QMAH 主倉庫一致 |
| `database/upgrades/`、`database/seeds/` | 版本升級與展示資料腳本 |
| `manifest.json` | 快照版本、來源與備份檔 SHA-256 |
| `tools/QmahDataTools/` | 資料匯入、快照匯出與驗證工具 |
| `tools/Sync-Schema.ps1`、`Verify-SchemaParity.ps1` | 與主倉庫的結構同步與比對 |
| `QMAH.DemoCredentials.csv` | 展示帳號範本，密碼欄空白 |

## 建置工具

需要 .NET SDK 10.0.301 以上與 SQL Server。

```powershell
dotnet build .\QMAH.DatabaseTools.sln -c Release
```

匯出新快照使用 `tools/QmahDataTools/Export-ReferenceDatabase.ps1`，流程會在隔離資料庫完成還原、資料比對與驗證。

## 版本

| 版本 | 日期 | 內容 |
| --- | --- | --- |
| db-v1.0.10 | 2026-10-13 | 與 Azure 展示環境同源，加入貼文收藏、私訊、AI 審查結構、22 種點數兌換券與 846 筆歷史展示訂單 |

先前的快照已從 Release 頁移除，修改記錄保留在 git 歷史。

## 資料來源與授權

文物資料取自國立故宮博物院 [Open Data](https://digitalarchive.npm.gov.tw/opendata/)，依各資料頁標示的 CC0 或 CC BY 4.0 使用，授權代碼與姓名標示保存在 `AttributionText` 欄位。展示用的會員、貼文與訂單為虛構資料，不含真實個資。
