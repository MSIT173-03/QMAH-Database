# NpmDataImporter

`NpmDataImporter` 是已標準化文物資料包的命令列預檢與匯入工具。一般網站開發不需要執行；管理員日常匯入使用後台的「文物匯入」，命令列工具用於批次資料、CI 前檢查與問題重現。

匯入核心位於 `QMAH.Infrastructure/Infrastructure/CatalogImport/`，因此後台、命令列工具與其他主機使用同一套驗證、同步與冪等規則。工具只接受已存在的 QMAH SQL Server Schema，不建立資料庫、不建立資料表、不執行 EF Migration，也不覆蓋既有圖片。

## 正式資料量與小量驗證

目前參考資料包是 8 個分類、每類 32 件，共 256 件文物；題庫同步與商城商品也各有 256 筆。這只是目前 Snapshot 的基準批次。CLI 預設沿用該批次的篩選量，但 `--artifact-per-category` 與 `--max-products` 接受正整數，實際可處理量由輸入 JSON 的資料筆數與品質檢查決定，沒有另外的固定件數上限。

`--skip-products` 是刻意保留的小量文物／題庫驗證模式，不代表正式匯入只能處理少量資料。後台 UI 不設每類 32 件的 CLI 篩選，會依上傳資料包預檢後處理所有合格項目。

## 使用方式

準備一份由 `NpmArtifactPipeline` 或既有資料處理流程產出的文物 JSON。圖片來源欄位以 `/media/...` 網站路徑表示時，`--media-root` 指向實體的 `wwwroot\media` 資料夾：

```powershell
dotnet run --project tools\QmahDataTools\NpmDataImporter\NpmDataImporter.csproj -- `
  --project C:\path\to\QMAH `
  --artifacts C:\path\to\artifacts.import.json `
  --products C:\path\to\products.import.json `
  --media-root C:\path\to\QMAH\QMAH.Web\wwwroot\media
```

完整 256 件資料包可省略數量參數，使用預設值。只檢查文物與題庫、不讀取商品資料時：

```powershell
dotnet run --project tools\QmahDataTools\NpmDataImporter\NpmDataImporter.csproj -- `
  --project C:\path\to\QMAH `
  --artifacts C:\path\to\artifacts.import.json `
  --media-root C:\path\to\QMAH\QMAH.Web\wwwroot\media `
  --skip-products
```

小量流程驗證可明確降低上限；這是測試選項，不會改變正式資料包的數量：

```powershell
dotnet run --project tools\QmahDataTools\NpmDataImporter\NpmDataImporter.csproj -- `
  --project C:\path\to\QMAH `
  --artifacts C:\path\to\artifacts.import.json `
  --media-root C:\path\to\QMAH\QMAH.Web\wwwroot\media `
  --skip-products `
  --artifact-per-category 1
```

如果收集資料高於目前基準，匯入器的兩個篩選值需要依資料包實際筆數調整。以下以目前基準的參數格式示範；擴充資料時將數值換成預檢輸出所需的每類數量與商品總數：

```powershell
dotnet run --project tools\QmahDataTools\NpmDataImporter\NpmDataImporter.csproj -- `
  --project C:\path\to\QMAH `
  --artifacts C:\path\to\artifacts.import.json `
  --products C:\path\to\products.import.json `
  --media-root C:\path\to\QMAH\QMAH.Web\wwwroot\media `
  --artifact-per-category 32 `
  --max-products 256
```

`--artifact-per-category` 和 `--max-products` 的有效範圍是正 Int32；`--skip-products` 可只驗證文物與題庫。匯入器仍會執行 Schema、唯一鍵、圖片路徑、授權與題庫條件檢查，數量放寬不會跳過品質驗證。數量大於輸入資料可用量時，不會補造文物或商品。

`NpmDataWorkbench` 的文物頁會即時計算 8 類的總目標；匯入頁的每類文物上限與商品上限則另外輸入，避免把來源收集量誤當成匯入量。來源可用資料不足或品質檢查未通過時，實際寫入量仍會低於設定值。

## 預檢與正式套用

第一次執行不會寫入資料庫，只會顯示候選、可新增、可更新、未變更、無效、無法對應與題庫同步數量。資料確認無誤後，複製同一次顯示的 `APPROVAL_TOKEN`，用完全相同的參數加上 `--apply --approve`：

```powershell
dotnet run --project tools\QmahDataTools\NpmDataImporter\NpmDataImporter.csproj -- `
  --project C:\path\to\QMAH `
  --artifacts C:\path\to\artifacts.import.json `
  --products C:\path\to\products.import.json `
  --media-root C:\path\to\QMAH\QMAH.Web\wwwroot\media `
  --apply `
  --approve <預檢輸出的確認碼>
```

匯入規則如下：

- 文物是圖鑑、遊戲與題庫共用的主資料；題庫同步預設開啟，只有明確在後台取消或改用程式設定時才會關閉。
- 勾選商城同步時，商品資料必須通過分類、價格、庫存、圖片與文物關聯檢查；後台沒有提供商品檔時，會依合格文物建立可停用的展示商品。
- 相同故宮編號或商品編號會被辨識為既有資料。來源文字、分類、年代、授權與價格等來源欄位可更新；圖片、庫存與人工上架狀態不由匯入覆蓋。
- 第二次使用相同資料包會顯示 `unchanged`，不會重複建立文物、題庫、商品或複製圖片。
- 來源網址、授權代碼、姓名標示與原始資料快照必須隨資料包保留；年代無法可靠對應時列為無法對應，不猜測。
- 圖片先複製並驗證路徑，資料庫交易成功後才算完成；資料庫失敗時會清理本次已複製的新增資產。

若資料庫不存在、Schema 不完整、圖片缺少、路徑不安全或資料包內容在預檢後被修改，工具會停止，不會建立資料庫或補表。

## 後台操作

管理員登入 `QMAH.Web` 後，從 Catalog 的「文物匯入」進入：

1. 上傳文物 JSON，必要時上傳商品 JSON 與圖片 ZIP。
2. 先按「預覽匯入」，確認數量、警告、題庫同步與商城同步狀態。
3. 題庫同步預設勾選；商城同步預設不勾選，只有確定要建立或更新商品時才開啟。
4. 確認預覽結果後按「確認匯入」。預檢與正式套用使用同一個暫存資料包，不接受手動修改後繞過預檢。

外部資料讀取使用 `IHttpClientFactory`、`System.Text.Json` 與 `CancellationToken`。來源失敗時後台只顯示可理解的處理訊息，不把內部欄位名稱、路徑或例外細節直接放到畫面。

## 相關工具

- `NpmArtifactPipeline`：抓取、整理、年代標準化、圖片下載與產出文物匯入包。
- `ArtifactProductGenerator`：依既有文物產生一對一展示商品資料；`--count all` 使用全部合格文物，不覆蓋已存在的商品營運欄位。
- `NpmShopSampleCollector`：舊商城來源的相容性收集工具，不作為目前文物主檔與商品同步的必要步驟。

各工具輸出放在工作區外或 `_工具輸出`；raw JSON、下載快取、帳密 CSV 或測試資產不提交到 Repository。
