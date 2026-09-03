# QMAH 資料處理工具入口

[QMAH 專案](https://github.com/MSIT173-03/QMAH) ｜ [QMAH-Docs 專案](https://github.com/MSIT173-03/QMAH-Docs) ｜ [QMAH-Database 專案](https://github.com/MSIT173-03/QMAH-Database) ｜ [QMAH-Docs 文件站](https://msit173-03.github.io/QMAH-Docs/)

資料工具的責任、命令、展示資料邊界與 Snapshot Release 流程，見 [QMAH-Docs 的資料工具參考](https://msit173-03.github.io/QMAH-Docs/reference/data-tools.html)。

本檔只列出程式 Repository 內的工具入口，不在這裡重複維護操作手冊。

常用工具位於本目錄：

- `NpmArtifactPipeline`：收集與檢查故宮文物資料。
- `NpmDataImporter`：文物資料包預檢與安全匯入。
- `ArtifactProductGenerator`：由授權文物產生對應商品。
- `QmahDatabaseRelease`：展示資料、Snapshot 匯出與資料庫驗證。
- `QmahTestDataWorkbench`：連線本機 QMAH、編輯受控文物／商品資料、填寫展示帳密與產生關聯展示資料。
- `Export-ReferenceDatabase.ps1`：單一 Snapshot pipeline。

流程與參數說明見 [QmahDatabaseRelease 工具說明](QmahDatabaseRelease/README.md) 與 [QmahTestDataWorkbench 工作台說明](QmahTestDataWorkbench/README.md)。完整 `QMAH.sql` 由 [QMAH-Database](https://github.com/MSIT173-03/QMAH-Database) 提供。

## 兩個 WPF 工作台

### QmahTestDataWorkbench

資料庫專用工作台位於 `QmahTestDataWorkbench`。從 QMAH-Database 根目錄執行：

```powershell
dotnet run --project .\tools\QmahDataTools\QmahTestDataWorkbench\QmahTestDataWorkbench.csproj
```

工作台啟動後會先保留畫面上的連線字串，再搜尋本機 SQL Server 中可連線且包含 `QMAH` 的資料庫。連線字串只是預設提示，不代表固定的 `.mdf` 路徑；自動尋找不掃描網路、不附加資料庫檔案，也不會自動還原 `.bak`。

「範本／填寫帳密」可在沒有現成 credentials 檔案時直接載入 `QMAH.DemoCredentials.csv`，逐筆輸入 20 個展示帳號的測試密碼。預設本機檔與備份檔位於偵測到的 Repository 資料夾上一層，兩個 Repository 放在同一父資料夾時可以共用：

```text
<Repository 的上一層>/QMAH.DemoCredentials.local.csv
<Repository 的上一層>/QMAH.DemoCredentials.local.backup.csv
```

密碼只寫入本機檔，範本保持空白且不覆寫；未填完時可以先儲存，`seed-showcase-users` 仍會在執行前指出未填帳號。

「快速產生」使用兩個既有命令：

```text
seed-showcase-users
generate-showcase-data
```

前者建立／更新展示會員，後者以固定 `seed` 與穩定識別碼建立貼文、留言、訂單、訂單明細、付款和商品評價。文物與商品頁只開放受控欄位，不提供任意資料表 CRUD。

### NpmDataWorkbench

資料來源工作台位於 `NpmDataWorkbench`，負責遠端 NPM Open Data、商城來源收集、預檢及匯入。文物頁的 8 個正式分類可以各自輸入非負 Int32；先執行來源估算，再可把八類各自的原始來源筆數套用為目標。「套用 256 件基準」只代表目前 Snapshot 的參考批次。

可自訂的文物收集項目包括每類數量、輸出資料夾、圖片根目錄、是否下載圖片、CSV／HTML 預覽、離線重整輸入，以及 Pipeline 執行檔位置。匯入區另有每類文物上限與商品上限，預設值仍對應目前 256 件參考資料包，但可依資料包調整，不會改變資料庫 Schema。

工作台啟動時會載入 `NpmDataWorkbench/presets/default-1-256.json` 的「預設 1」：八類各 32 件、`diverse`、seed `173`、每類文物匯入上限 32、商品上限 256。按「載入預設 1」即可恢復。預設檔不保存路徑、連線字串、credentials 或其他機密；兩個 Repository 的共用預設檔應維持一致，詳細欄位見 [預設檔說明](NpmDataWorkbench/presets/README.md)。

商城頁則可調整來源分類、目標商品數、每次請求延遲、週期性冷卻、最大頁數、圖片下載與預覽格式。遠端來源大量收集前應先使用估算功能，並檢查輸出目錄中的 `quality-report.json` 與 `manifest.json`。

網站啟動不建立資料庫，工具輸出、credentials、快取與資料庫檔案也不提交到 Git。
