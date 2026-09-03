# QMAH-Database

[QMAH 專案](https://github.com/MSIT173-03/QMAH) ｜ [QMAH-Docs 專案](https://github.com/MSIT173-03/QMAH-Docs) ｜ [QMAH-Database 專案](https://github.com/MSIT173-03/QMAH-Database) ｜ [QMAH-Docs 文件站](https://msit173-03.github.io/QMAH-Docs/)

本 Repository 管理 QMAH 的完整 SQL Server Snapshot、測試資料產生工具與資料庫交付檔案。QMAH-Database 可以獨立 clone、建置工具、連線本機資料庫、修改受控測試資料並產生新的展示資料。

## Repository 內容

| 路徑 | 內容 |
| --- | --- |
| QMAH.sql | 可在乾淨 SQL Server 建立 QMAH 資料庫的完整 Schema 與資料 |
| database/Schema.sql | 與 QMAH Repository 逐位元組一致的結構契約 |
| manifest.json | Snapshot 版本、來源與產出資訊 |
| QMAH.DatabaseTools.sln | 資料庫工具與建置相依的獨立方案 |
| tools/QmahDataTools | 遠端資料收集、資料匯入、商品產生、Snapshot 與測試資料工具 |
| tools/QMAH.Infrastructure | 供資料庫工具獨立建置使用的 Entity、DbContext 與共用服務副本 |
| QMAH.DemoCredentials.csv | 展示帳密範本；Password 欄位保持空白，不含秘密 |

NpmArtifactPipeline、NpmDataImporter、NpmShopSampleCollector 與 NpmDataWorkbench 是 QMAH 與 QMAH-Database 共用的資料來源工具，因此兩邊都保留。ArtifactProductGenerator、QmahDatabaseRelease、QmahTestDataWorkbench 與 Export-ReferenceDatabase.ps1 是資料庫測試資料與 Snapshot 工具，集中由本 Repository 維護。

## 建置環境

優先使用 Visual Studio 2026 或 Visual Studio Code 2026，命令列建置使用 .NET 10。版本由根目錄 global.json 控制。資料庫工具方案支援直接以 dotnet 執行，不要求開啟產品網站。

```powershell
dotnet restore .\QMAH.DatabaseTools.sln
dotnet build .\QMAH.DatabaseTools.sln -c Release
```

資料庫工具需要可連線的 SQL Server；Snapshot 交付腳本另外需要 sqlcmd 與 sqllocaldb。工具不會在網站啟動時建立資料庫、不會套用 Migration，也不會自動還原 .bak。

## 本機資料庫位置

工作台與網站使用的是「自動尋找本機 SQL Server 中含有 QMAH 資料庫的 instance」規則。Server=.;Database=QMAH;... 是找不到其他候選時的預設連線字串，不是固定資料庫檔案位置，也不代表資料庫一定位於某個資料夾。

自動尋找只查詢本機候選 instance 的 sys.databases，不掃描網路、不自動附加 .mdf，也不會從 Release 自動還原 .bak。若連線字串已明確指定，會先檢查該設定，再依本機候選清單尋找。

## 測試資料工作台

QmahTestDataWorkbench 是資料庫專用 WPF GUI：

```powershell
dotnet run --project .\tools\QmahDataTools\QmahTestDataWorkbench\QmahTestDataWorkbench.csproj
```

工作台提供：

- 文物清單的新增與編輯，分類與年代從資料庫清單選取。
- 商品清單的新增與編輯，可選擇關聯文物。
- 展示會員建立／更新，以及沒有現成帳密檔時的範本載入與密碼填寫視窗。
- 一鍵執行 seed-showcase-users 與 generate-showcase-data。
- 顯示執行記錄、目前連線目標與文物／商品／會員筆數。

generate-showcase-data 沿用既有產生器，以固定識別碼在單一交易中處理社群貼文、留言、商城訂單、訂單明細、付款與商品評價。這個 GUI 不提供任意資料表的無限制 CRUD；需要其他資料表情境時，應在 QmahDatabaseRelease 新增可驗證的情境命令，避免手動建立不完整外鍵鏈。

### 展示帳密設定

「範本／填寫帳密」會直接開啟內建編輯視窗。沒有本機檔時，視窗讀取 Repository 內的 `QMAH.DemoCredentials.csv`；已有本機檔時，則載入既有內容。每一列提供顯示名稱、Email、角色與遮罩密碼欄位，填寫後按「儲存帳密檔」即可。

預設本機帳密檔與備份檔都放在偵測到的 Repository 資料夾上一層：

```text
<Repository 的上一層>/QMAH.DemoCredentials.local.csv
<Repository 的上一層>/QMAH.DemoCredentials.local.backup.csv
```

此位置不依賴 `C:\專題初期整合` 或其他固定工作區名稱；兩個 Repository 放在同一個父資料夾時可以共用。工作台也可以用「選擇檔案」改用其他位置。版本庫範本只含帳號識別資料，密碼欄位維持空白，不能把密碼寫回範本。

命令列不使用工作台時，可以從 QMAH-Database 根目錄以目前資料夾的上一層建立本機檔：

```powershell
$credentialsDirectory = Split-Path -Parent (Get-Location).Path
Copy-Item .\QMAH.DemoCredentials.csv (Join-Path $credentialsDirectory 'QMAH.DemoCredentials.local.csv')
```

填妥所有 `Password` 後再執行 `seed-showcase-users`。該命令會把本次使用的內容寫回本機檔與備份檔；未填密碼時會停止，不會自行產生密碼。`QMAH.DemoCredentials.local.csv` 與備份檔不提交到任何 Repository。

## 文物收集數量與自訂項目

文物收集由共用的 `NpmArtifactPipeline` 和 `NpmDataWorkbench` 處理。畫面中的 8 個分類數量可以分開設定為非負 Int32，並即時計算總目標；256 件只是目前參考 Snapshot 的基準，不是 API 收集上限。來源 API 原始筆數、初步可出題候選、圖片下載結果、年代判讀與品質規則仍可能使最後輸出少於目標。

### 預設 1：256 件參考設定

兩份共用的 `NpmDataWorkbench` 都保存同一份 `tools/QmahDataTools/NpmDataWorkbench/presets/default-1-256.json`。工作台啟動時會自動載入「預設 1」，也可以按「載入預設 1」恢復。內容是八類各 32 件、`diverse`、seed `173`、不產生預覽、下載圖片，以及每類文物匯入上限 32、商品匯入上限 256。

預設檔不含輸出路徑、資料庫連線字串、帳密或本機檔案位置；路徑由工作台自動尋找結果或畫面欄位決定。兩個 Repository 的預設檔應維持逐位元組一致，修改時同步更新兩份，並在文件中說明變更。

GUI 可調整下列項目：

- 8 個正式分類的個別目標數量。
- 是否下載圖片，以及圖片實體根目錄。
- JSON 輸出資料夾、CSV／HTML 人類可讀預覽與離線重整輸入。
- 文物 Pipeline 與 Importer 的專案、執行檔或 DLL 路徑。
- 匯入預檢的每類文物上限與商品上限；這兩個值和線上抓取目標分開設定。

數量與取樣方式可以分開調整。先估算八類來源，再決定每類目標：

```powershell
dotnet run --project .\tools\QmahDataTools\NpmArtifactPipeline\NpmArtifactPipeline.csproj -- `
  --estimate-only
```

固定 seed 的多樣性取樣：

```powershell
dotnet run --project .\tools\QmahDataTools\NpmArtifactPipeline\NpmArtifactPipeline.csproj -- `
  --per-dataset 64 `
  --selection-mode random `
  --seed 173 `
  --readable both `
  --output 'D:\qmah-data\output\random' `
  --media-root 'D:\qmah-data\output\media'
```

來源編號順序取樣：

```powershell
dotnet run --project .\tools\QmahDataTools\NpmArtifactPipeline\NpmArtifactPipeline.csproj -- `
  --per-dataset 64 `
  --selection-mode sequential `
  --output 'D:\qmah-data\output\sequential' `
  --media-root 'D:\qmah-data\output\media'
```

需要不同分類數量時，個別參數會覆蓋 `--per-dataset`：

```powershell
dotnet run --project .\tools\QmahDataTools\NpmArtifactPipeline\NpmArtifactPipeline.csproj -- `
  --per-dataset 32 `
  --ceramic 80 `
  --jade 64 `
  --painting 48 `
  --output 'D:\qmah-data\output\custom' `
  --media-root 'D:\qmah-data\output\media'
```

`--estimate-only` 會逐類輸出 `available` 原始筆數與 `question-ready` 初步可出題候選；`--no-images` 只產生資料欄位與品質報告；`--offline --offline-input <檔案或資料夾>` 可不連線重新套用年代規則；`--all-categories` 才會把另外 8 個保留來源類別納入輸出。`--selection-mode diverse` 以欄位完整度與年代桶輪流取樣，`random` 以 seed 產生可重現的不同樣本，`sequential` 依來源編號順序取樣。來源筆數是原始上限，最後輸出仍會受到欄位、年代、授權、圖片與下載結果影響。

資料包匯入時可以另外設定篩選量：

```powershell
dotnet run --project .\tools\QmahDataTools\NpmDataImporter\NpmDataImporter.csproj -- `
  --project 'D:\src\QMAH' `
  --artifacts 'D:\qmah-data\output\current\import\artifacts.json' `
  --products 'D:\qmah-data\products\products.import.json' `
  --media-root 'D:\src\QMAH\QMAH.Web\wwwroot\media' `
  --artifact-per-category 32 `
  --max-products 256
```

`--artifact-per-category` 和 `--max-products` 的有效範圍是正 Int32；上例沿用目前參考 Snapshot 的篩選量，擴充資料時應改成資料包實際筆數。`--skip-products` 可只驗證文物與題庫。匯入器仍會執行 Schema、唯一鍵、圖片路徑、授權與題庫條件檢查，數量放寬不會跳過品質驗證；輸入資料不足時不會補造資料。

### 工具可達上限

| 工具或項目 | 可接受範圍 | 實際有效上限 |
| --- | ---: | --- |
| `NpmArtifactPipeline` 每類收集目標 | `0`～`2,147,483,647` | 最近一次 API `available`、`question-ready`、圖片、年代與品質規則 |
| `NpmDataImporter` 每類文物上限 | `1`～`2,147,483,647` | 輸入資料包筆數、Schema、重複與欄位檢查 |
| `NpmDataImporter` 商品上限 | `1`～`2,147,483,647` | 商品 JSON、文物關聯與既有交易歷史 |
| `ArtifactProductGenerator` | 正整數，或 `--count all` | 輸入資料包中符合條件的文物；已有購物車／訂單關聯時不能任意替換 |
| `QmahDatabaseRelease` 社群／訂單批次 | `1`～`512` | 工具管理的穩定識別碼批次與現有資料關聯 |
| `QmahDatabaseRelease` 每日活動天數 | `0`～`3,650` | 不含執行日；既有活動歷史不因縮短參數而刪除 |
| `QmahDatabaseRelease` 點數／鑰匙／鑰匙進度流水 | 各 `0`～`10,000` | 只清理同一個 `SHOWCASE_GENERATED` 工具批次，不刪除其他來源資料 |
| 各工具固定 seed | `0`～`2,147,483,647` | 只控制可重現的選擇順序，不增加來源資料量 |

2026-09-03 最後一次來源估算的觀測值為：BRONZE `available=6,238`／`question-ready=1,355`、CERAMIC `25,631`／`9,563`、JADE `13,501`／`1,153`、ENAMEL `2,523`／`1,120`、LACQUER `764`／`157`、COIN `6,953`／`5,081`、CARVING `670`／`159`、PAINTING `18,142`／`419`；原始筆數合計 74,422、初步候選合計 19,007。這是當次 API 回應，建立新資料包前仍須重新估算。

256 件是預設 1 和目前 Snapshot 的基準值，不是工具上限。來源估算、選取模式與預設檔的重用方式見 [資料工具參考](https://msit173-03.github.io/QMAH-Docs/reference/data-tools.html) 與 [預設檔說明](tools/QmahDataTools/NpmDataWorkbench/presets/README.md)。

## 資料來源與輸出位置

遠端 NPM Open Data 的收集與匯入仍由共用工具處理。工具可以直接指定輸出資料夾、輸入資料檔、圖片根目錄、QMAH 專案路徑與執行檔位置，不依賴固定工作區：

```powershell
dotnet run --project .\tools\QmahDataTools\NpmDataImporter\NpmDataImporter.csproj -- `
  --project 'D:\src\QMAH' `
  --artifacts 'D:\data\artifacts.import.json' `
  --products 'D:\data\products.import.json' `
  --media-root 'D:\src\QMAH\QMAH.Web\wwwroot\media'
```

收集結果、原始回應、快取、圖片與報告放在工作區外或 _工具輸出，不提交到 Git。NpmDataImporter 寫入的是指定的 QMAH SQL Server；QMAH-Database 只 clone 時仍可建置共用工具，但需要 QMAH Repository 才能完成產品資料匯入。

## Schema 比對與同步

兩個 Repository 都保留 database/Schema.sql，因此各自 clone 後仍可查看結構契約。跨 Repository 比對是警告式，不會阻止建置、測試、提交或 Snapshot 產出：

```powershell
.\tools\Verify-SchemaParity.ps1 -QmahRepositoryPath '..\QMAH'
```

檢查會列出兩份檔案的 byte 數與 SHA-256。相同時顯示 identical；不相同、缺少另一個 Repository 或路徑無效時只顯示警告並以成功結束。

需要同步時，必須明確指定來源；不依目前所在目錄猜測覆寫方向：

```powershell
.\tools\Sync-Schema.ps1 -Source QMAH-Database -QmahRepositoryPath '..\QMAH'
.\tools\Verify-SchemaParity.ps1 -QmahRepositoryPath '..\QMAH'
```

Sync-Schema.ps1 只會處理兩個已確認的 database/Schema.sql 路徑，可先加 -WhatIf 檢視目標。Schema 變更仍需在兩個 Repository 各自提交，方便各自的版本歷史追查。

## Snapshot 與 .bak Release

QMAH.sql 是 Repository 內可審查、可直接執行的完整 SQL；.bak 是從同一個 canonical database 產生的二進位備份，正式交付時只附加到 QMAH-Database 的 GitHub Release，不提交到 Git 歷史。

從 QMAH-Database 根目錄執行 Snapshot pipeline：

```powershell
.\tools\QmahDataTools\Export-ReferenceDatabase.ps1 `
  -Version 0.7.1 `
  -QmahRepositoryPath '..\QMAH'
```

不使用固定 sibling 結構時，可指定輸出檔案與工作資料夾：

```powershell
.\tools\QmahDataTools\Export-ReferenceDatabase.ps1 `
  -Version 0.7.1 `
  -RepositorySqlPath 'D:\snapshots\QMAH.sql' `
  -OutputDirectory 'D:\snapshots\work\0.7.1'
```

Pipeline 會使用隔離資料庫完成還原、資料掃描、.bak checksum、SQL 匯出、SQL 重建、資料比對與 EF 驗證。若指定 -QmahRepositoryPath，才會再進行 QMAH.Web 啟動驗證。正式 Release 應附加同一次輸出的 .bak，並讓 QMAH.sql、manifest.json 與 tag 使用同一個版本。

目前 Repository 版本入口為 db-v0.7.0。Snapshot 取得位置、開發資料內容、資料表說明與完整工具參數見 [QMAH-Docs 資料工具參考](https://msit173-03.github.io/QMAH-Docs/reference/data-tools.html)。
