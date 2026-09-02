# QMAH 完整資料庫 Snapshot

[QMAH 專案](https://github.com/MSIT173-03/QMAH) ｜ [開發文件](https://msit173-03.github.io/QMAH-Docs/) ｜ [開發資料庫](https://github.com/MSIT173-03/QMAH-Database)

本 Repository 只管理 QMAH 可直接還原的完整 SQL Server Snapshot 與版本歷史。`QMAH.sql` 包含目前版本的 Schema、共同資料、Identity、管理後台與已驗證的展示資料；SQL 是主要交換格式，適合在 SSMS 或 `sqlcmd` 執行。

## 目前版本

- Database tag：`db-v0.7.0`
- Snapshot：[`QMAH.sql`](QMAH.sql)
- Manifest：[`manifest.json`](manifest.json)
- 來源 QMAH commit：[`511fbfc692adb599606c3c7a41cc840c1d6f1`](https://github.com/MSIT173-03/QMAH/commit/511fbfc692adb599606c3c7a41cc840c1d6f1)

## 一般組員

不需要 clone 本 Repository。直接從 [GitHub 檔案頁](https://github.com/MSIT173-03/QMAH-Database/blob/db-v0.7.0/QMAH.sql) 或 [Raw SQL](https://raw.githubusercontent.com/MSIT173-03/QMAH-Database/db-v0.7.0/QMAH.sql) 取得 `QMAH.sql`，在本機 SQL Server 建立並還原名為 `QMAH` 的資料庫即可。若課程或團隊提供同一版本的 Release `.bak`，`.bak` 與本檔應來自同一次 Snapshot 輸出，擇一還原。

完成還原後，依 [QMAH-Docs 開發環境與資料文件](https://msit173-03.github.io/QMAH-Docs/getting-started/development-environment.html) 設定 `QMAH.Web`、`QMAH.Api` 或 `QMAH.Client`。網站啟動不會建立資料庫、不套用 Migration，也不要求再執行 Patch 或 Seed。

## 維護者

資料庫整合者在 QMAH 的隔離資料庫完成結構與資料驗證後，依 [資料工具參考](https://msit173-03.github.io/QMAH-Docs/reference/data-tools.html) 執行單一 Release pipeline，再以同一次輸出的 SQL 更新本檔。每個版本使用 Git tag（格式為 `db-v<version>`）保存，不另建立每版本 SQL 檔案或平行 Snapshot。

QMAH 主 Repository 的 [`database/Schema.sql`](https://github.com/MSIT173-03/QMAH/blob/main/database/Schema.sql) 是可閱讀的結構契約；本 Repository 的 `QMAH.sql` 才是完整可還原內容。若程式、Schema、manifest 與 Snapshot 不一致，先停止交付並核對來源 commit、資料庫驗證報告與輸出檔案。
