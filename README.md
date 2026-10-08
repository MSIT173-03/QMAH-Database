# QMAH-Database

[QMAH 專案](https://github.com/MSIT173-03/QMAH) ｜ [QMAH-Docs 專案](https://github.com/MSIT173-03/QMAH-Docs) ｜ [QMAH-Database 專案](https://github.com/MSIT173-03/QMAH-Database) ｜ [QMAH-Docs 文件站](https://msit173-03.github.io/QMAH-Docs/)

本 Repository 管理 QMAH 的完整 SQL Server Snapshot、測試資料產生工具與資料庫交付檔案。QMAH-Database 可以獨立 clone、建置工具、連線本機資料庫、修改受控測試資料並產生新的展示資料。

## 目前正式 Release

目前正式資料庫入口是 [db-v0.12.1 Release](https://github.com/MSIT173-03/QMAH-Database/releases/tag/db-v0.12.1)，與 QMAH 主程式 `v0.12.1` 對齊。Release 提供與 tag 同源的 `QMAH.sql`、已通過 `RESTORE VERIFYONLY` 的 `QMAH-0.12.1.bak` 與 `SHA256SUMS.txt`。`.bak` 不提交到 Git；要產生下一版請使用 `Export-ReferenceDatabase.ps1`。

> ⚠️ 舊版 Snapshot 僅供歷史追溯；新環境請使用 `db-v0.12.1`。

### db-v0.11.0 更新內容

- 訂單新增 `ShippingMethod` 與 `ShippingFee`，金額約束納入運費；180 筆純展示訂單依目前宅配／超商與滿額免運規則重整，保留原付款方式、狀態與交易欄位，付款金額同步納入運費。展示交易欄位不代表真實收款。
- 與主程式 `v0.11.0` 對齊版本入口；SQL 與 BAK 由同一次資料庫匯出產生。SQL 可重建乾淨資料庫，56 張表的 Schema 與逐表資料比對一致，EF model 驗證通過。

### db-v0.10.2 更新內容

- 社群貼文與留言新增 `SimHash`，檢舉支援系統自動產生；`ContentKeywords` 與 `ContentModerationSettings` 提供內容審核規則及相似內容比對設定。
- 貼文、留言及圖片新增 `AiReviewedAt`，供背景 AI 審核追蹤進度；保留既有商城折扣、文物、會員與展示資料。
- Snapshot 含 56 張表、512 件文物與商品、318 篇社群貼文和 728 則留言；SQL 重建後逐表資料及 EF model 驗證均通過。

### db-v0.10.0 更新內容

- 商城商品保留 `Price`、`DiscountRate` 與可為 NULL 的 `SalePrice`；有效售價優先採用有效 `SalePrice`，否則依 `DiscountRate` 計算。
- 管理員可用折扣率批次套用商品價格，也可指定單品或批次的固定折扣後售價；購物車與訂單沿用同一有效售價規則。
- 修正官方商城公告與點數兌換券的 2099 結束日期，改為 2026/12/31。

### db-v0.10.0 修正版更新內容

- 文物、題庫與相關年代資料重新建立為 512 件；舊的 256 件展示文物不保留，八類分布與 18 個年代桶已一併寫入同一份 Snapshot。
- 商城同步建立 512 件「複製品＆文物明信片套組」，每件商品保留對應原文物尺寸；明信片固定為 A6（148 × 105 mm），版型由主圖比例自動套用，文案明確分開明信片與縮小複製品。
- 商品描述模板已更新為 v3：依文物分類提供可核對的觀看重點，並分層輸出套組內容、原文物資料、用途、來源與原始說明；不再產生測試流程、付款流程或無意義的展示話術。
- 社群展示資料已由 `ShowcaseDataCommands` 全量重產；會排除面向評審、前台與資料表的內部檢討句，移除固定共鳴式留言與展示用框選引號，保留可讀的會員文章與分類關聯。此次 Snapshot 的 `social.SocialPosts=314`、`social.SocialComments=728`。
- 保留既有會員、社群、優惠券與鑰匙資料，並以目前有效優惠券建立官方活動展示資料；另產生跨會員的訂單、付款、評論、點數與鑰匙流水供整合驗證。
- 媒體與商品路徑和 `QMAH-develop/develop` 同步，列表與明信片詳情使用各自的媒體尺寸；正式部署可依文件將媒體拆出至 CDN。
- 修正展示資料產生器，依目前 `ProductId` 同步既有訂單明細的商品名稱快照，避免歷史展示訂單仍顯示舊的「縮小複製品」文字。
- 補上新增 `JAPAN_EDO` 年代的 `KEY-ERA-JAPAN_EDO` 解鎖鑰匙，並重新產生對應的會員鑰匙餘額與流水資料。
- `catalog.ArtifactUnlocks` 已補入 13 筆可追溯的展示解鎖紀錄，透過 `KeyTransactionId` 對應鑰匙扣除流水；同時為商城、遊戲、社群、公告、通知與稱號流程補入最小可驗證的業務資料。Identity claims、外部登入與 token 表仍保持空白，避免捏造不具部署意義的認證資料。

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

目前 Repository 版本入口為 [db-v0.12.1 Release](https://github.com/MSIT173-03/QMAH-Database/releases/tag/db-v0.12.1)。請以完整 `QMAH.sql`（或 Release 內壓縮的 `QMAH-0.12.1.sql.zip`）或 `QMAH-0.12.1.bak` 建立／還原 Snapshot。Snapshot 取得位置、開發資料內容、資料表說明與完整工具參數見 [QMAH-Docs 資料工具參考](https://msit173-03.github.io/QMAH-Docs/reference/data-tools.html)。

## db-v0.12.1 更新內容

本次完整 SQL 與 manifest 為 db-v0.12.1，對應 QMAH v0.12.1，補上綠界金流所需的資料結構。

**升級要求：** 本版新增 PaymentAttempts 資料表、放寬付款狀態約束並新增訂單索引，需搭配 v0.12.1 主程式。請以完整 BAK 還原或在乾淨資料庫執行同源 SQL；會員、展示資料與登入帳號沿用 0.12.0 內容，不變。

- 新增 `PaymentAttempts`（每次付款嘗試的紀錄），`Payments` 補上對應欄位，`CK_Payments_Status` 加入 `REFUND_REQUIRED`（待退款），`StoreOrders` 新增 `IX_StoreOrders_Status_CreatedAt`，共 58 張資料表。
- Snapshot 與 EF 工具專案（`tools/QMAH.Infrastructure`）同步主程式的 Payment／PaymentAttempt／StoreOrder 模型。
- SQL／BAK 同源，乾淨重建與結構／逐表資料比對一致（Differences 為空），EF model、Web 啟動、備份驗證及 SQL 決定性檢查通過。
- 完整附件、改動原因與驗證步驟見 [db-v0.12.1 Release](https://github.com/MSIT173-03/QMAH-Database/releases/tag/db-v0.12.1)。

## db-v0.12.0 更新內容

本次完整 SQL 與 manifest 為 db-v0.12.0，對應 QMAH v0.12.0。新版需要對應的遊戲程式，不可只替換 Snapshot 而沿用舊的 int 進度模型。

**本版要求全新還原，不提供增補升級。** 本次重整跨表的展示資料，只補欄位或套部分種子無法重現新版內容。請先備份舊資料庫、停止 API／Web，再以本版完整 BAK 還原；或在乾淨資料庫執行同源 SQL。不要將完整 SQL 疊加在舊資料庫，也不要用舊產生器覆蓋新版內容。新版程式必須與資料庫一起切換。

- 新增鑑賞投票表，擴充既有多人／單人領獎收據及每日活動，進度欄位改為 decimal(12,2)。使用既有表記錄每天 100 點、一次突破額外 30 點與收藏達 80%／全收齊後的鑰匙放緩。
- [ConnectedShowcase](tools/QmahDataTools/ConnectedShowcase/README.md) 是重新設計的關聯式資料工具：512 場、1,536 篇文物回答、96 筆單人完成紀錄，分給 24 個既有帳號；另外重整 170 筆生成訂單、102 篇評論，修正 5 張優惠券、43 篇回顧及 206 則留言。登入 email、密碼與角色保持不變。
- SQL／BAK 同源，57 表乾淨重建與結構／逐表資料比對一致，EF model、Web 啟動、備份驗證及 SQL 決定性檢查通過。種子連續重跑後資料完全一致。
- 完整附件、改動原因與全新還原步驟見 [db-v0.12.0 Release](https://github.com/MSIT173-03/QMAH-Database/releases/tag/db-v0.12.0)。