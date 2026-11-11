# QMAH-Database

[QMAH 清明鑑定屋](https://github.com/MSIT173-03/QMAH) 的 SQL Server 資料庫快照與資料工具。主程式倉庫不放完整資料庫，需要資料時從這裡的 Release 下載。

> Azure 一個月免費展示已結束。本 repo 保留展示快照與本地資料工具，不提供線上資料庫，也不需要重新啟動 Azure。

目前快照：[db-v1.0.10](https://github.com/MSIT173-03/QMAH-Database/releases/tag/db-v1.0.10)。主專案 [v1.1.1](https://github.com/MSIT173-03/QMAH/releases/tag/v1.1.1) 沿用這個下載版本，本輪維護沒有建立新快照或升版資料庫。其他版本的相容性需另行確認。

## 還原資料庫

到 Release 下載 `SHA256SUMS.txt`，以及 `.bak` 或 `.sql` 其中一個檔案：

| 檔案 | 用途 |
| --- | --- |
| `QMAH-1.0.10.bak` | 在 SSMS 使用 Restore Database 還原，目標 SQL Server 必須支援此備份版本 |
| `QMAH-1.0.10.sql` | 在乾淨的 SQL Server 建立結構與資料，不可疊加到已有資料的資料庫 |
| `SHA256SUMS.txt` | 上述兩個檔案的 SHA-256 校驗值 |

先核對下載內容，例如選擇備份檔時：

```powershell
Get-Content .\SHA256SUMS.txt
Get-FileHash .\QMAH-1.0.10.bak -Algorithm SHA256
```

結果必須與校驗檔中同名檔案的值一致，才進行還原。兩種方式擇一即可。不要用公開展示快照覆蓋自己的資料。網站預設資料庫名稱為 `QMAH`，隔離測試可改用其他名稱並自行設定連線字串。

一般網站展示可直接使用 [主專案的本地啟動方式](https://github.com/MSIT173-03/QMAH#快速啟動)：Docker 流程會下載並校驗同一份 Release 備份，不連接舊 Azure 主機。GitHub 自動產生的 Source code 壓縮檔不是 `.bak` 備份。

## 內容

| 路徑 | 說明 |
| --- | --- |
| `QMAH.sql` | 版控內的工作腳本，重現發行快照請以 Release 資產及其校驗值為準 |
| `database/Schema.sql` | 與主專案同步的 62 張表結構參考，不在網站啟動時自動執行 |
| `database/upgrades/`、`database/seeds/` | 版本升級與展示資料腳本 |
| `manifest.json` | 快照版本、來源與備份檔 SHA-256 |
| `tools/QmahDataTools/` | 資料匯入、快照匯出與驗證工具 |
| `tools/Sync-Schema.ps1`、`Verify-SchemaParity.ps1` | 與主倉庫的結構同步與比對 |
| `QMAH.DemoCredentials.csv` | 展示帳號範本，密碼欄空白 |

## 建置工具

只有要匯入、編輯或匯出資料時才需要建置工具。使用 Release 快照不需要先編譯全部工具。

工具使用 .NET 10，SDK 需求依 `global.json`。整個 solution 包含 WPF，請在 Windows 建置。執行資料庫命令時才需準備自己的 SQL Server。

```powershell
dotnet restore .\QMAH.DatabaseTools.sln --locked-mode
dotnet build .\QMAH.DatabaseTools.sln -c Release --no-restore
```

匯出新快照使用 `tools/QmahDataTools/Export-ReferenceDatabase.ps1`，流程會在隔離資料庫完成還原、資料比對與驗證。

工具索引、連線設定及快照產出方式見 [tools/README.md](tools/README.md)。資料庫專用 `tools/QMAH.Infrastructure` 是獨立建置所需的副本，不因主專案也有同名專案而刪除。

### Schema 同步

2026-11-11 已以主專案 [main 提交 e49cd5b5](https://github.com/MSIT173-03/QMAH/blob/e49cd5b55cbfae1de962e23e3a4f9be53fdeb1c5/database/Schema.sql) 為來源同步本檔，兩份 `Schema.sql` 的 SHA-256 一致。兩邊均固定此檔使用 LF，避免 Windows 換行轉換造成假的差異。

補齊的是原參考檔缺少的 `PostBookmarks`、`DirectMessages`、`AiReviewRuntimeStatus`、`AiReviewLogs`，以及 `ContentModerationSettings.AiReviewEnabled` 和配套索引／約束。完整 `QMAH.sql` 已含這些結構，因此沒有重匯出資料、重建備份或增加資料庫版本。這不代表本輪重新驗證過 `.bak` 的還原結果。

`Verify-SchemaParity.ps1` 只提出警告，即使不同也回傳成功。不能將 exit code 0 當成 Schema 相同。若確定要同步，`Sync-Schema.ps1` 必須明確指定 `-Source`，先加 `-WhatIf` 檢視目標，確認後再執行。一般還原快照不需要執行這兩個工具。

## 版本

| 版本 | 發行日（臺灣時間） | 內容 |
| --- | --- | --- |
| db-v1.0.10 | 2026-10-14 | 原展示環境同源快照，62 張資料表、22 種點數兌換券與 846 筆歷史展示訂單，內容依原 manifest 與發行記錄 |

先前的快照已從 Release 頁移除，修改記錄保留在 git 歷史。

資料庫 tag 使用 `db-vMAJOR.MINOR.PATCH`，例如 `db-v1.0.10`。Release 標題為「`db-v1.0.10 — 完整資料庫快照`」。主專案使用 `vMAJOR.MINOR.PATCH`，兩者各自版本，不為了文件或工具修補而重建同名快照。已發布 tag、資產名稱與下載網址不改動，資料內容變更時另發新版本。

### 2026-11-11 維護

- 整理展示結束、還原與版本說明，修正已移除舊 Release 的連結。
- 資料工具明確引用已修補的 `System.Security.Cryptography.Pkcs 10.0.13`，取代有已知弱點的間接 9.0.11。通報：[高風險](https://github.com/advisories/GHSA-846f-f764-76mx)、[中度風險](https://github.com/advisories/GHSA-2fmv-rv2c-59x7)。
- Schema 同步改為必須指定來源，避免省略參數時覆寫另一個 repo。
- 補齊落後的獨立 Schema 參考檔並核對與主專案一致，保留原完整 SQL、備份、tag 與下載網址。
- 8 個專案的直接與間接 NuGet 依賴稽核未發現已知弱點。solution Release 建置通過，0 警告、0 錯誤。`pwsh -File tools/tests/Test-SchemaSync.ps1` 的四個合成檔案情境通過，不連接資料庫。
- 沒有修改快照、升級／種子腳本或 `manifest.json`。沒有連接 SQL Server 或 Azure。歷史 Release 所附 Source code 對應當時提交，維護工具請使用目前 main。

## 資料來源與授權

文物資料取自國立故宮博物院 [Open Data](https://digitalarchive.npm.gov.tw/opendata/)，依各資料頁標示的 CC0 或 CC BY 4.0 使用，授權代碼與姓名標示保存在 `AttributionText` 欄位。展示用的會員、貼文與訂單為虛構資料，不含真實個資。
