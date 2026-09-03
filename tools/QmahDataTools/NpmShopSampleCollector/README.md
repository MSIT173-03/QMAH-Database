# NpmShopSampleCollector

工具以低頻率、有限並行讀取故宮商城公開頁面，並保留來源分類與正式分類的對應資料，產生 QMAH 商城來源研究樣本。

工具不建立資料庫、不繞過 robots.txt，也不把來源網站的即時庫存當成專題庫存。目前正式參考資料的 256 件商城商品由 `ArtifactProductGenerator` 依 QMAH 文物產生；本工具不是正式商品數量或商品圖片的來源。

## 快速使用

雙擊沒有參數的執行檔會立即結束，不會抓取資料；使用命令列或 `NpmDataWorkbench.exe` 呼叫。

```powershell
.\NpmShopSampleCollector.exe --help
.\NpmShopSampleCollector.exe --discover-structure --settings .\sample-settings.json --source-catalog .\shop-source-catalog.json
.\NpmShopSampleCollector.exe --estimate-only --settings .\sample-settings.json --source-categories ZC523,ZC524
.\NpmShopSampleCollector.exe --count 60 --delay-ms 600 --cooldown-every 30 --cooldown-ms 10000 --max-pages 3 --settings .\sample-settings.json --output .\output\products --media-root .\output\media
```

省略 `--source-categories` 會依設定檔使用全部已核定的商城入口。若只指定 `ZC523,ZC524`，只適合做小量連線測試。

上述 `60` 是這個舊來源收集器的示範目標，不是 QMAH 正式資料量。

預設輸出是工作區 `_工具輸出/products` 與 `_工具輸出/media`，可用 `QMAH_TOOL_OUTPUT`、`--output` 或 `--media-root` 覆寫。離線重整既有資料時：

線上執行預設會對商城根頁做一次有上限的分類探測。發現新入口時，結果另存到 `_工具輸出/NpmShopSampleCollector/source-categories.auto.json`，不會自動修改 SQL Server 或正式分類設定。

確認映射後，再寫回 `sample-settings.json`。停用探測使用 `--no-auto-discover`；手動更新觀察快照使用 `--discover-structure`。

```powershell
.\NpmShopSampleCollector.exe --dry-run --offline-input .\output\products\products.import.json --output .\output\products-offline --readable both
```

## 來源分類一定會保留

商城的「來源分類」和 QMAH 的「正式分類」是兩個概念：

- `shop-source-catalog.json`：記錄觀察到的入口、`cn` 分類代碼／名稱、頁碼參數、商品連結證據與 `mappedCategoryCode`。
- `source-categories.auto.json`：線上根頁探測到的新入口快照，只供開發者審核，不會直接改正式設定。
- raw：每筆保留 `sourceEntryCode`、`sourceEntryName`、`categoryCode`、`categoryName` 與 `sourceListUrl`。processed 保留來源入口與正式映射；quality report 保存分類統計、accepted／excluded 與缺口原因。
- `products.import.json`／SQL Server：只寫入正式 `store.Products.CategoryCode`，保留網站要查詢的映射結果，不另外建立 ProductCategories。

保留這些欄位後，可以追溯商品的來源商城分類與正式分類映射。觀察快照不取代正式資料庫設計。

## 主要參數

```text
--settings <json>              節流、排除詞、入口與分類設定
--count <數量>                 此次來源收集的商品目標，預設 60
--source-categories <代碼>     實際商城入口分類（不是 DB 分類）
--categories <代碼>            正式 store.Products 分類映射
--delay-ms <毫秒>              每次請求最低間隔
--jitter-ms <毫秒>             額外隨機延遲
--cooldown-every <次數>        每幾次請求額外冷卻，0=關閉
--cooldown-ms <毫秒>           額外冷卻時間
--max-pages <頁數>             0=不限，仍受來源耗盡與品質規則限制
--estimate-only                只估算分類頁連結，不開商品頁、不下載圖片、不寫 output
--discover-structure           低成本更新 shop-source-catalog.json
--auto-discover                線上預設開啟；根頁探測新入口（最多 24 個）
--no-auto-discover             關閉本次自動根頁探測
--dry-run --offline-input      離線檢查既有匯入 JSON
--readable <none|csv|html|both> 是否額外產生人類閱讀版
```

設定檔的 `targetTotal`、`categories`、`sourceEntries`、`excludeTerms`、`imageRequired`、`missingImagePolicy`、`maxConcurrentRequests` 與 `maxPages` 共同決定收集上限。

每個來源映射分類目前預留最多 16 筆，讓來源收集目標不會被單一分類上限卡住；這不是資料庫分類數量限制。若分類上限加總不足，`autoExpandCategoryMaximum=true` 會記錄調整；來源真的不足時只回報 `targetGap`，不複製或虛構商品。

工具會排除期刊、雜誌、訂閱品、停售／缺貨、缺圖、重複與價格不明商品。正式收集遵守 robots.txt、429／5xx 退避與有限並行；預設間隔為 600ms，每 30 次請求冷卻 10 秒。

## `--discover-structure` 做什麼

只讀根頁與目前設定的少量分類頁，不開商品頁、不下載圖片、不建立正式 output。

它更新 `shop-source-catalog.json` 的入口選擇器、`cn`／`pn`／`prd` 參數、分頁與目前商品連結量證據。GUI 會列出這份 JSON 的實際商城分類，勾選後用 `--source-categories` 傳給收集器。

## 輸出與品質報告

```text
<output>/
├─ raw/products.raw.json／products.raw.csv
├─ processed/products.processed.json／products.processed.csv
├─ products.import.json／products.import.csv
├─ products.upsert.sql        # 只供契約核對
├─ manifest.json
├─ products.manifest.json
└─ quality-report.json
```

`quality-report.json` 會列出來源分類、正式映射分類、accepted／excluded、targetGap、缺圖與請求調整。選 `csv`、`html` 或 `both` 才會額外建立 preview。

所有 output、raw、快取、log 與圖片只放 `_工具輸出`，不進 Git。

## 送進 QMAH 前

`NpmDataImporter` 會檢查匯入資料中的商品數量、圖片與來源欄位、`ExternalRef` 重複以及 SQL Server Schema 是否已存在。

它不存在固定的最少商品筆數門檻。第一次不帶 `--apply` 只產生 `PROFILE` 與 `APPROVAL_TOKEN`；確認同一份資料後才可加上 `--apply`。

匯入預設只新增、重複略過，不覆蓋營運中的 `Stock`。
