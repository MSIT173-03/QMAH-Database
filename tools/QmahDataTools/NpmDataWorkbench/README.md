# NpmDataWorkbench

Windows WPF GUI，提供文物 Pipeline、商城 Collector 與安全 Importer 的共同入口。商城頁保留作為舊來源研究介面；目前正式商品改用獨立的 `ArtifactProductGenerator`。

本工作台用於資料估算、收集、預檢與正式匯入。

一般網站開發不需要開啟本工具或執行資料匯入命令。建立開發資料庫時，可在 SSMS 執行 [QMAH-Database 的完整 Snapshot](https://github.com/MSIT173-03/QMAH-Database)；若另有同版本且已驗證的 `.bak`，也可以使用。兩種方式擇一即可。

可直接執行的版本位於工作區根目錄 `_工具輸出/portable-tools/NpmDataWorkbench.exe`。

文物頁固定顯示 8 類：銅器、陶瓷、玉器、琺瑯器、漆器、錢幣、雕刻、繪畫。

匯入區預設沿用目前完整資料包的基準：文物每類 32 筆、商品最多 256 筆。這些是預檢與批次篩選的預設值，不是來源或資料庫上限。文物收集頁的 8 類目標可輸入非負 Int32；按「偵測 API 筆數」後，還能把八類各自的原始來源筆數套用為目標。實際輸出仍受可用欄位、圖片下載、授權與年代規則影響。

「套用 256 件基準」代表 8 類各 32 件，只用於重現目前參考 Snapshot。「套用來源可用上限」使用最近一次估算的 `available` 原始筆數；`question-ready` 候選筆數與最後品質報告才代表可直接進入題庫的數量。

文物收集提供三種選取模式：

| 模式 | 規則 | 用途 |
| --- | --- | --- |
| `diverse`（預設） | 依欄位完整度排序，再輪流取不同年代桶 | 穩定建立多樣性較高的參考資料 |
| `random` | 以固定 seed 對來源編號排序，再輪流取不同年代桶 | 更換 seed 取得不同樣本；相同 seed 可重現 |
| `sequential` | 依來源編號前綴與尾端數字排序 | 檢查來源編號的順序與缺號 |

`sequential` 的結果仍可能缺號，因為缺欄位、年代需人工確認、圖片下載失敗的資料不會進入輸出。每次執行的模式與 seed 會寫入 `manifest.json`。

商城頁的勾選項目來自 `shop-source-catalog.json`，顯示的是商城來源分類，收集後仍會映射到正式 `store.Products.CategoryCode`。

## 操作順序

1. 先按「偵測 API 筆數」或「偵測所選分類商品量」，確認來源仍可讀取。

2. 再用小量收集，查看 `_工具輸出` 的 `quality-report.json`、圖片與重複項目。

3. 文物增加到 8 類各至少 32 筆後，送進「預檢資料與重複項目」；正式文物匯入完成後，再由 `ArtifactProductGenerator --count all` 建立一對一商品。

4. 預檢輸出的確認碼只對應當次資料；資料內容不變且完成確認後，才按「確認後寫入專案」。

GUI 不會在網站啟動時建立 SQL Server 或資料表，也不會自動修改正式分類設定。

## 可自訂參數

文物頁的設定分為三組，彼此獨立：

| 設定組 | 可調整項目 | 對應參數或輸出 |
| --- | --- | --- |
| 收集數量 | 8 個正式分類的個別目標數量、非負 Int32；可套用估算到的來源原始筆數 | `--bronze`、`--ceramic`、`--jade`、`--enamel`、`--lacquer`、`--coins`、`--carvings`、`--painting` |
| 取樣方式 | 多樣性、固定 seed 隨機或來源編號順序 | `--selection-mode`、`--seed` |
| 輸出方式 | 輸出目錄、圖片目錄、是否下載圖片、CSV／HTML 預覽、離線輸入 | `--output`、`--media-root`、`--no-images`、`--readable`、`--offline-input` |
| 匯入篩選 | 每類文物上限、商品上限、是否略過商品 | `--artifact-per-category`、`--max-products`、`--skip-products` |

同時指定 `--per-dataset` 與單類數量時，單類數量優先。離線模式不會重新抓取 API，會對指定的既有資料包重新套用目前的年代與品質規則。`quality-report.json` 和 `manifest.json` 會保留本次參數與輸出檔案雜湊，方便比較不同資料量的結果。

資料庫依 SQL／ERD 建立與驗證，再由同一次匯出流程產生 QMAH-Database 的 `QMAH.sql`；若需要 `.sql`／`.bak` 交付檔，也必須來自同一次輸出。

商城根頁發現的新分類另存為 `source-categories.auto.json`，供審核映射。
