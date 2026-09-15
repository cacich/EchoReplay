# 製作說明

[回到專案首頁](../README.md)

## 專案背景

在 Discord 和朋友聊天、玩遊戲時，有趣的片段往往已經發生，才想到要錄下來。原本的流程是保存 NVIDIA 即時錄影，再用剪輯軟體抽取音訊；錄影功能或驅動出現問題時，可能存到沒有聲音的影片。

EchoReplay 提供純音訊回放：程式事先持續擷取聲音，只保留固定時間，等使用者按快捷鍵時才輸出音檔。第一版聚焦在循環錄音、雙來源、快速保存，以及適合日常使用的背景與啟動設定。

## 技術選擇

| 項目 | 選擇 | 原因 |
| --- | --- | --- |
| 平台 | Windows 桌面 | 主要情境為 Windows 遊戲與 Discord |
| 語言與框架 | C#、.NET 10、WPF | 建立桌面介面，整合 Windows 訊息及系統匣 |
| 擷取 API | WASAPI shared mode | 取得播放裝置的 loopback 音訊與麥克風資料 |
| 音訊函式庫 | NAudio.Wasapi 2.2.1 | 封裝音效裝置、WASAPI client 與 WAV 輸出 |
| 緩衝 | 記憶體中的 PCM 循環緩衝 | 固定容量，不隨執行時間累積檔案 |
| 音檔 | 48 kHz、16-bit PCM WAV | 匯出流程單純，可交給剪輯軟體 |
| 設定 | JSON 檔案 | 方便維護及本機備份 |
| 發佈 | Windows x64 self-contained | 在沒有安裝 .NET 的電腦也能執行 |

## 開發環境

- Windows x64。專案以 Windows 10／11 為使用情境；目前僅在開發機完成短時間驗證。
- .NET 10 **SDK**，不是只有 Runtime。用 `dotnet --list-sdks` 檢查。
- PowerShell 與 Git；可以直接由命令列建置，不要求特定編輯器。
- 首次建置需要連線到 NuGet 下載相依套件。
- 實機測試需要可用的播放裝置，麥克風測試另需輸入裝置與 Windows 麥克風權限。

`build.ps1` 優先使用專案 `.tools/dotnet/dotnet.exe`；若不存在，使用 PATH 中的 `dotnet`。`.tools` 不隨 Git 上傳，從 GitHub 複製專案後通常使用已安裝的 SDK。

腳本將 NuGet 快取放在 `.tools/nuget`、CLI 資料放在 `.tools/cli`，並透過環境變數停用 CLI 遙測。這些都是本機產物。

## 建置與執行

在專案根目錄執行：

```powershell
# 執行測試，再產生包含執行環境的可攜版。
.\build.ps1 -Portable
.\artifacts\EchoReplay-portable\EchoReplay.exe
```

可攜版目錄為 `artifacts/EchoReplay-portable`。EXE 包含 .NET 執行環境，因此檔案較大；文件與第三方授權資料會一起複製到輸出目錄。

目標電腦已安裝相容的 .NET 10 Desktop Runtime 時，可建置精簡版：

```powershell
.\build.ps1
.\artifacts\EchoReplay\EchoReplay.exe
```

開發時也可以直接執行：

```powershell
dotnet run --project .\src\EchoReplay\EchoReplay.csproj
```

測試或發佈失敗時，腳本會中止。修改後請重新建置，避免執行先前留下的 EXE。

## 自動化測試

```powershell
dotnet run --project .\tests\EchoReplay.Tests\EchoReplay.Tests.csproj -c Release
```

這是自訂的主控台測試執行程式，**請使用 `dotnet run`**；目前沒有使用 xUnit／NUnit，不能以 `dotnet test` 的成功輸出推定這些測試已執行。

目前有 13 項測試，覆蓋循環覆蓋、靜音缺口、時間對齊、快照、WAV 內容、混音、快捷鍵衝突與設定檔。全部通過時會輸出 `PASS: 13 tests`，失敗回傳非零結束碼。測試包含真實 Windows 熱鍵 API，因此需要 Windows；若測試快捷鍵被其他程式占用，請先關閉占用程式再測試。

## 實機錄音診斷

以下指令會播放約 1.5 秒低音量測試音，擷取電腦聲音與可用麥克風，使用隔離設定目錄執行。它也會檢查隱藏視窗、暫停、重新開始、輸出失敗，以及設定套用後的緩衝。

```powershell
$testRoot = Join-Path $PWD 'artifacts/test-hardware'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$exe = Join-Path $PWD 'artifacts/EchoReplay-portable/EchoReplay.exe'
$arguments = '--data-dir "{0}" --diagnose "{1}"' -f (Join-Path $testRoot 'profile'), (Join-Path $testRoot 'report.json')
Start-Process -FilePath $exe -ArgumentList $arguments -Wait
Get-Content -LiteralPath (Join-Path $testRoot 'report.json')
```

診斷會寫入測試 WAV 與 `report.json`，完成後結束。檢查 `Success`，也要另外檢查 `SystemPackets`、`MicrophonePackets`、來源狀態及實際音檔。

**目前 `Success` 不要求麥克風有資料，也不做音訊能量或同步精度判定。** 它不能單獨證明雙來源都有聲音。先前測試另行讀取 WAV 取樣，確認電腦聲音包含非零資料；麥克風仍未完成實機驗證。

## 畫面輸出

```powershell
$uiRoot = Join-Path $PWD 'artifacts/test-ui'
New-Item -ItemType Directory -Force -Path "$uiRoot/profile" | Out-Null
'{"RecordOnLaunch":false}' | Set-Content -LiteralPath "$uiRoot/profile/settings.json" -Encoding utf8
$exe = Join-Path $PWD 'artifacts/EchoReplay-portable/EchoReplay.exe'
$arguments = '--data-dir "{0}" --ui-snapshot "{1}"' -f "$uiRoot/profile", "$uiRoot/main.png"
Start-Process -FilePath $exe -ArgumentList $arguments -Wait
```

程式用 WPF 渲染自己的內容，輸出主頁、設定頁與設定底部的 PNG。這不等同於完整桌面操作測試。公開文件中的圖片應先確認沒有使用者路徑或錄音資訊。

## 手動驗收

1. 同時播放遊戲或影片並說話，檢查混音與兩份分軌。
2. 錄超過保留時間，確認只保留最後指定時間。
3. 在遊戲內使用儲存／顯示快捷鍵。
4. 關閉視窗、最小化及從系統匣叫回，確認錄音不中斷。
5. 拔插耳機、切換 Windows 預設裝置，確認狀態與恢復情形。
6. 啟用登入自動執行，重新登入驗證，再取消並確認項目移除。
7. 進行較長時間錄音，檢查記憶體、聲音間隙與雙來源同步。

哪些步驟已完成，請以 [驗證紀錄](../VALIDATION.md) 為準。

## 打包與發佈

```powershell
.\build.ps1 -Portable
Compress-Archive -Path .\artifacts\EchoReplay-portable -DestinationPath .\artifacts\EchoReplay-win-x64.zip -Force
```

需要安裝 EXE、免安裝 ZIP 及校驗值時，安裝 Inno Setup 6.5+ 並執行 `./build-release.ps1`。GitHub Actions 會在版本標籤推送後自動建置、測試安裝與解除安裝，並發佈附件。完整流程見 [Release 發佈說明](RELEASING.md)。

Git 追蹤原始碼、圖示、測試與文件。SDK、NuGet 快取、EXE／ZIP、測試錄音、使用者設定及 SSH 金鑰不應提交。

NAudio 版本固定在專案檔內；SDK 與 .NET runtime 修補版本沒有完全鎖定，所以不同建置環境產生的 EXE 位元內容與大小可能不同。

## 修改功能時從哪裡開始

| 想修改的功能 | 主要檔案（位於 `src/EchoReplay`） |
| --- | --- |
| 介面、設定項目與按鈕操作 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 錄音裝置、格式與重連 | `AudioCapture.cs` |
| 循環緩衝與時間索引 | `TimelineBuffer.cs` |
| 錄音生命週期、快照與混音 | `ReplayEngine.cs` |
| 快捷鍵解析、註冊與恢復 | `HotkeyManager.cs` |
| 設定檔與開機啟動 | `Settings.cs` |
| 啟動參數與單一執行個體 | `App.xaml.cs` |

詳細資料流與設計限制見 [架構說明](ARCHITECTURE.md)。
