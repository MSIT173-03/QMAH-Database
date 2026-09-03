# NpmDataWorkbench 預設檔

`default-1-256.json` 是目前版本控制中的「預設 1」。兩個 Repository 的同一路徑都保留一份，內容應逐位元組一致。

## 目前設定

| 欄位 | 值 |
| --- | --- |
| 文物分類 | `BRONZE`、`CERAMIC`、`JADE`、`ENAMEL`、`LACQUER`、`COIN`、`CARVING`、`PAINTING` |
| 每類文物目標 | 32 |
| 總文物目標 | 256 |
| 選取模式 | `diverse` |
| seed | 173 |
| 預覽 | `none` |
| 圖片 | `downloadImages: true` |
| 匯入器每類文物上限 | 32 |
| 匯入器商品上限 | 256 |

工作台啟動時自動載入這份檔案；文物頁的「載入預設 1」按鈕可重新套用。它只保存可公開、與本機位置無關的數量與選取設定，不保存輸出資料夾、執行檔、SQL Server 連線字串、帳密、Token、圖片快取或任何本機密碼。

## 可接受範圍

- 文物每類目標：`0`～`2,147,483,647`；`0` 表示略過。
- 匯入器每類文物上限：`1`～`2,147,483,647`。
- 匯入器商品上限：`1`～`2,147,483,647`。
- `selectionMode`：`diverse`、`random`、`sequential`。
- `seed`：`0`～`2,147,483,647`。
- `readable`：`none`、`csv`、`html`、`both`。

這些是輸入驗證範圍，不是保證產量。來源 API 的 `available`、`question-ready`、圖片下載、年代規則、授權與 Schema 檢查會決定實際輸出。`256` 只是預設 1 的參考資料包大小。

## 修改與重用

需要另一組設定時，複製 JSON 並修改 `artifactCounts`、`selectionMode`、`seed` 或匯入篩選欄位。不要在這裡加入本機路徑或密碼。若要讓工作台啟動時使用新設定，需同步更新兩個 Repository 的 `default-1-256.json`，確認逐位元組一致，並同時更新工具與文件說明。

建立大批次前先執行：

```powershell
dotnet run --project ..\..\NpmArtifactPipeline\NpmArtifactPipeline.csproj -- --estimate-only
```

再依各類的 `available` 與 `question-ready` 決定目標；不要把預設 1 的 256 件當成來源上限。
