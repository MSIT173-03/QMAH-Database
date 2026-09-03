# QMAH-Database 工具

[QMAH 專案](https://github.com/MSIT173-03/QMAH) ｜ [QMAH-Docs 專案](https://github.com/MSIT173-03/QMAH-Docs) ｜ [QMAH-Database 專案](https://github.com/MSIT173-03/QMAH-Database) ｜ [QMAH-Docs 文件站](https://msit173-03.github.io/QMAH-Docs/)

本目錄包含建立、修改、驗證與交付 QMAH 測試資料所需的工具。工具可在只 clone `QMAH-Database` 的情況下建置；需要驗證網站啟動或把 NPM 文物資料匯入產品 Repository 時，再以參數指定 QMAH Repository 的位置。

## 工具分區

| 工具 | 用途 | 需要 QMAH Repository |
| --- | --- | --- |
| `NpmArtifactPipeline` | 從遠端 NPM Open Data 取得、整理與檢查文物資料 | 否 |
| `NpmShopSampleCollector` | 從遠端來源收集商城測試資料 | 否 |
| `NpmDataImporter` | 預檢並匯入文物資料包 | 匯入網站資料時需要 |
| `NpmDataWorkbench` | 以圖形介面串接共用的資料收集與匯入流程 | 依所選工作流程而定 |
| `ArtifactProductGenerator` | 由資料庫內的合格文物產生商品測試資料 | 否 |
| `QmahDatabaseRelease` | 展示資料、備份還原、Snapshot 匯出與資料驗證 | 網站啟動驗證時需要 |
| `QmahTestDataWorkbench` | 文物／商品受控編輯與一鍵建立關聯展示資料 | 網站啟動驗證時需要 |
| `Export-ReferenceDatabase.ps1` | 產出同源的 `.bak`、`.sql`、checksum 與報告 | 可選 |
| `Verify-SchemaParity.ps1` | 比對兩個 Repository 的 `Schema.sql` 並在不一致時警告 | 可選 |
| `Sync-Schema.ps1` | 依明確指定的來源，將 `Schema.sql` 複製到另一個 Repository | 可選 |

`QMAH.Infrastructure` 是資料庫專用工具的建置相依來源副本。產品程式仍以 QMAH Repository 的同名專案為準；這份副本讓資料庫工具可以獨立建置。

## 只使用 QMAH-Database

從本 Repository 根目錄執行：

```powershell
dotnet restore .\QMAH.DatabaseTools.sln
dotnet build .\QMAH.DatabaseTools.sln -c Release

dotnet run --project .\tools\QmahDataTools\ArtifactProductGenerator\ArtifactProductGenerator.csproj -- --help
dotnet run --project .\tools\QmahDataTools\QmahDatabaseRelease\QmahDatabaseRelease.csproj -- --help
```

`QmahDatabaseRelease` 的資料庫命令透過 `--connection` 指定目標 SQL Server。例如：

```powershell
dotnet run --project .\tools\QmahDataTools\QmahDatabaseRelease\QmahDatabaseRelease.csproj -- `
  generate-showcase-data `
  --connection "Server=(localdb)\MSSQLLocalDB;Database=QMAH;Trusted_Connection=True;TrustServerCertificate=True"
```

文物收集先用 `NpmArtifactPipeline --estimate-only` 取得八類各自的 `available` 原始筆數，再選擇 `diverse`、固定 seed 的 `random` 或 `sequential` 來源編號順序。資料庫展示流水可在同一命令產生，也可以只執行：

```powershell
dotnet run --project .\tools\QmahDataTools\QmahDatabaseRelease\QmahDatabaseRelease.csproj -- `
  generate-showcase-ledger `
  --connection "Server=(localdb)\MSSQLLocalDB;Database=QMAH;Trusted_Connection=True;TrustServerCertificate=True" `
  --activity-days 30 `
  --point-transaction-count 80 `
  --key-transaction-count 80 `
  --key-progress-transaction-count 80 `
  --seed 173
```

流水工具讀取資料庫內的啟用鑰匙定義與登入成就定義；固定 seed 只控制展示資料的可重現分配，不取代產品服務中的條件判定。完整參數與重跑規則見各工具目錄的 README。

命令只處理指定的本機資料庫，不會在網站啟動時自動建立資料庫、套用 Migration、執行 Seed 或覆寫資料。

若需要圖形介面，啟動資料庫專用工作台：

```powershell
dotnet run --project .\tools\QmahDataTools\QmahTestDataWorkbench\QmahTestDataWorkbench.csproj
```

工作台的編輯範圍、資料庫自動尋找規則與一鍵關聯資料流程，見 [QmahTestDataWorkbench 說明](QmahDataTools/QmahTestDataWorkbench/README.md)。

## 需要 QMAH Repository 的情況

若兩個 Repository 位於同一個工作區，預設位置是 `..\QMAH`。位置不同時明確指定：

```powershell
.\tools\QmahDataTools\Export-ReferenceDatabase.ps1 `
  -Version 0.7.0 `
  -QmahRepositoryPath 'D:\src\QMAH' `
  -RepositorySqlPath 'D:\snapshots\QMAH.sql' `
  -OutputDirectory 'D:\snapshots\work\0.7.0'
```

`NpmDataImporter` 的 `--project` 參數仍應指向 QMAH Repository 根目錄或 `QMAH.Web` 專案。遠端資料收集工具可以直接以輸出目錄與檔名參數產生資料包，不要求固定工作區位置。

## Schema 比對與同步

兩邊都保留 `database/Schema.sql`，方便各自 clone 後查看與使用。`QMAH-Database/database/Schema.sql` 是資料庫工具與 Snapshot 的預設基準；修改來源可依當次工作選擇，不能讓同步方向靠目前目錄猜測。

先檢查：

```powershell
.\tools\Verify-SchemaParity.ps1 -QmahRepositoryPath '..\QMAH'
```

檢查結果只有兩種：列出逐位元組一致，或以警告列出檔案大小與 SHA-256 差異。兩種情況都會以成功結束，不會阻止建置、測試或提交。若只 clone `QMAH-Database`，找不到另一個 Repository 時也只會警告。

需要同步時明確指定來源：

```powershell
.\tools\Sync-Schema.ps1 `
  -Source QMAH-Database `
  -QmahRepositoryPath '..\QMAH'

.\tools\Verify-SchemaParity.ps1 -QmahRepositoryPath '..\QMAH'
```

`Sync-Schema.ps1` 只會覆寫另一個 Repository 的 `database/Schema.sql`；可先加 `-WhatIf` 查看目標。沒有指定來源時不執行同步。

## Snapshot 交付

`Export-ReferenceDatabase.ps1` 會在隔離資料庫完成還原、資料掃描、`.bak` checksum、SQL 匯出、SQL 重建、資料比對與 Entity Framework 驗證後，將 SQL 寫到指定位置。`.bak` 不提交到 Git，正式交付時附加到 QMAH-Database 的 GitHub Release；Repository 內保留可審查的 `QMAH.sql`、`manifest.json` 與版本標籤。

工具輸出預設放在工作區的 `_工具輸出\reference-database\<version>`。密碼、Token、連線字串中的秘密值、原始回應與本機資料庫檔案不得提交。
