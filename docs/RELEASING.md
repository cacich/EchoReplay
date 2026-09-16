# Release 發佈說明

[回到專案首頁](../README.md)

## 給使用者的下載檔案

| 附件 | 用途 |
| --- | --- |
| `EchoReplay-Setup-x64.exe` | 建議使用的安裝程式 |
| `EchoReplay-portable-win-x64.zip` | 解壓縮後直接執行的免安裝版 |
| `SHA256SUMS.txt` | 以上兩份檔案的 SHA-256 |

檔名保持固定，README 的 `/releases/latest/download/…` 連結會導向最新正式版本。EXE／ZIP 由 Release 保存，不加入 Git 歷史。

## 安裝行為

- 安裝器由 Inno Setup 6.5+ 編譯，支援繁體中文與英文。
- Windows x64，最低 Windows 10 1809；與 .NET 10 的 Windows 最低版本需求配合。
- 預設 `%LOCALAPPDATA%\Programs\EchoReplay`，只安裝給目前使用者，不要求管理員權限。
- 建立開始功能表捷徑，桌面捷徑為選用；安裝不直接啟用開機錄音。
- 保留固定 AppId，重跑安裝檔可更新同一安裝位置。
- 程式持有 `Local\EchoReplay.Setup` mutex，安裝器在更新或移除前要求先結束程式。它不會強制結束正在錄製的程式。
- 解除安裝只移除安裝清單中的檔案；設定及其他錄音保留。若 Windows Run 項目指向這個安裝位置，會清理該項目；另一份免安裝程式的啟動設定不會被刪除。

目前尚未使用程式碼簽章憑證，Windows 可能顯示不明發行者或 SmartScreen 提示。簽章屬於後續發佈改善，不影響這份 Release 提供安裝檔的功能。

## 本機建置

安裝 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 與 [Inno Setup](https://jrsoftware.org/isdl.php)，執行：

```powershell
.\build-release.ps1
# 若編譯器不在預設位置：
.\build-release.ps1 -InnoCompiler 'C:\Tools\Inno Setup 6\ISCC.exe'
```

腳本會執行應用程式的 31 項測試，建置含 .NET 的可攜版，將指定的程式與文件加入獨立暫存目錄，再產生安裝 EXE、ZIP 與校驗值至 `artifacts/release`。不會從既有輸出目錄任意打包其他檔案。

版本讀自 `src/EchoReplay/EchoReplay.csproj` 的 `Version`，目前接受 `major.minor.patch` 正式版格式。

## 自動發佈

1. 更新專案版本、README、CHANGELOG，新增 `docs/releases/v版本.md`。
2. 提交並推送 `main`。
3. 對該提交建立對應版本標籤並推送，例如：

```powershell
git tag -a v1.0.2 -m 'EchoReplay v1.0.2'
git push origin v1.0.2
```

標籤只是下一版流程的範例，必須與專案版本一致。

`.github/workflows/release.yml` 會在 Windows runner 上：

1. 檢查標籤與專案版本、Release 說明檔一致。
2. 設定 .NET，執行測試、建置與打包。
3. 驗證安裝、啟動 WPF、剪輯器診斷與回收筒刪除、執行中保護、重裝、解除安裝、資料保留及開機項目歸屬。
4. 保存建置附件與安裝診斷。
5. 建立 Release 草稿，上傳三份附件，成功後才公開為最新版本。

推送繼續使用維護者的個人 SSH；附件發佈由儲存庫自己的 `GITHUB_TOKEN` 執行，不需要把 SSH 私鑰或個人存取權杖放進工作流程。GitHub 顯示的發佈者可能是 `github-actions[bot]`。

可從 Actions 手動執行工作流程進行測試建置；以分支執行時只提供 Actions 附件，不發佈 Release。已公開的版本不會被自動覆寫，請建立新的版本標籤。

## 安裝驗收範圍

`scripts/Test-Installer.ps1` 只允許在可丟棄的 GitHub Actions Windows runner 執行，因為它會測試該 runner 的目前使用者安裝、捷徑與登錄資料。

安裝驗收不代表音效硬體相容性測試。麥克風、遊戲內快捷鍵與長時間同步仍需真人在目標硬體上確認，見 [驗證紀錄](../VALIDATION.md)。
