# QmahDatabaseRelease 工具說明

[QMAH 專案](https://github.com/MSIT173-03/QMAH) ｜ [QMAH-Docs 專案](https://github.com/MSIT173-03/QMAH-Docs) ｜ [QMAH-Database 專案](https://github.com/MSIT173-03/QMAH-Database) ｜ [QMAH-Docs 文件站](https://msit173-03.github.io/QMAH-Docs/)

本頁列出 `QmahDatabaseRelease` 與 `Export-ReferenceDatabase.ps1` 的執行方式。工具輸出完整資料庫 Snapshot。

網站啟動不會呼叫這條流程。

## 工具做什麼

- 建立隔離的 LocalDB 工作資源。
- 執行展示資料命令與資料庫檢查。
- 輸出同一版本的 `.bak`、`.sql`、SHA256 與驗證報告。
- 將指定的 SQL Snapshot 更新到 QMAH-Database Repository 的目標路徑。

工具不負責正式環境資料庫、不在網站啟動時建立 Schema、不執行 Migration，也不把 Patch／Seed SQL 當成還原步驟。

## 執行前條件

- 工作目錄位於 QMAH Repository；`.NET 10`、`sqlcmd`、`sqllocaldb` 與可編譯的 QMAH solution 已可使用。
- 輸入資料庫是隔離的本機資料庫，不是正式或共用環境。
- 根目錄的 credentials 檔案只存在本機，密碼欄位不留白，也不提交到 Git。
- Snapshot 目標資料夾已存在，且輸出檔案的覆寫範圍已確認。

## 最短可重跑流程

下列命令示範執行順序；帳號、密碼與資料量依本機設定和當次需求決定：

```powershell
$connection = "Server=(localdb)\MSSQLLocalDB;Database=QMAH;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False"

dotnet run --project .\tools\QmahDataTools\QmahDatabaseRelease\QmahDatabaseRelease.csproj -- `
  seed-showcase-users `
  --connection $connection

dotnet run --project .\tools\QmahDataTools\QmahDatabaseRelease\QmahDatabaseRelease.csproj -- `
  generate-showcase-data `
  --connection $connection `
  --post-count 288 `
  --order-count 160 `
  --activity-days 30 `
  --point-transaction-count 80 `
  --key-transaction-count 80 `
  --key-progress-transaction-count 80 `
  --seed 173

pwsh -File .\tools\QmahDataTools\Export-ReferenceDatabase.ps1 -Version 0.7.1
```

輸出預設位於 `_工具輸出/reference-database/<version>`。完整 SQL 目標預設是 sibling 目錄 `..\QMAH-Database\QMAH.sql`。

每次 Snapshot 只使用同一次 pipeline 產生的 SQL／BAK，版本以 `db-v<version>` tag 保存。

`QmahDatabaseRelease` 的命令列參數要求明確提供 `--connection`。上例只把 LocalDB 當作工具執行時的輸入範例，不代表網站只會連線到該 instance。

## 展示流水與成就

`generate-showcase-data` 會在同一個資料庫交易中產生社群／商城關聯資料，以及每日登入／簽到、點數、鑰匙、鑰匙進度與符合登入條件的成就。各項資料量可分別指定：

```text
--activity-days <0-3650>
--point-transaction-count <0-10000>
--key-transaction-count <0-10000>
--key-progress-transaction-count <0-10000>
--seed <0-2147483647>
```

只產生上述活動與資產流水時，使用獨立命令：

```powershell
dotnet run --project .\tools\QmahDataTools\QmahDatabaseRelease\QmahDatabaseRelease.csproj -- `
  generate-showcase-ledger `
  --connection $connection `
  --activity-days 30 `
  --point-transaction-count 80 `
  --key-transaction-count 80 `
  --key-progress-transaction-count 80 `
  --seed 173
```

活動與三種資產流水使用穩定識別碼與 `SHOWCASE_GENERATED` 標記，重跑時只更新本工具管理的資料。鑰匙種類從目前啟用的 `catalog.KeyDefinitions` 讀取；成就從啟用中的 `DAILY_LOGIN_COUNT`／`DAILY_LOGIN_STREAK` 定義與門檻讀取，登入歷史達標後才建立會員成就。`seed-showcase-users` 的少量固定成就分配只用於展示初始畫面，不能視為產品成就判定邏輯。

網站的本機自動尋找規則請看[開發環境與啟動](https://msit173-03.github.io/QMAH-Docs/getting-started/development-environment.html)。

## 指定 Snapshot 位置與檔名

不使用 sibling 結構時，可指定完整檔案路徑：

```powershell
pwsh -File .\tools\QmahDataTools\Export-ReferenceDatabase.ps1 `
  -Version 0.7.1 `
  -RepositorySqlPath 'D:\qmah-snapshots\QMAH.sql'
```

也可以分開指定資料夾與檔名：

```powershell
pwsh -File .\tools\QmahDataTools\Export-ReferenceDatabase.ps1 `
  -Version 0.7.1 `
  -DatabaseRepositoryPath 'D:\qmah-snapshots' `
  -RepositorySqlFileName 'QMAH.sql'
```

`-RepositorySqlPath` 與資料夾／檔名參數不可混用。目標資料夾必須先存在。

工具不自動從遠端下載 Snapshot。遠端檔案先由 GitHub Repository、Raw URL 或 Clone 取得，再將本機目標路徑交給流程。

## Snapshot 交付檢查

1. `QMAH/database/Schema.sql`、`QMAH/database/VERSION`、QMAH-Database 的 `manifest.json` 與 tag 版本一致。
2. SQL、BAK、驗證報告與 SHA256 來自同一次輸出。
3. 目標 SQL 沒有被手動拼接 Patch／Seed，也沒有把 credentials、LocalDB 檔案或工具輸出提交到 Repository。
4. 受影響的資料工具 smoke check、文件 link 與 Snapshot byte comparison 已完成。

## 問題排查順序

遇到路徑、版本或資料問題時，依下列順序記錄：

1. `QMAH-Database` 目標資料夾與檔名。
2. `QMAH/database/VERSION` 與 `manifest.json` 的版本標記及產生時間。
3. `Export-ReferenceDatabase.ps1 -Version` 及路徑參數。
4. LocalDB、`sqlcmd`、`sqllocaldb` 與 QMAH 編譯結果。
5. pipeline 產物的 SQL、BAK、報告與 checksum 是否同源。

完整資料工具責任與展示資料規則見 [QMAH-Docs 資料工具參考](https://msit173-03.github.io/QMAH-Docs/reference/data-tools.html)。
