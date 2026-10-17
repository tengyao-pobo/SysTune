# 架構與技術決策

## 技術選型

| 面向 | 選擇 | 理由 |
| --- | --- | --- |
| 語言／執行環境 | C# 14 / **.NET 10 (LTS)** | 長期支援至 2028 年；可直接 P/Invoke Win32、DWM、PDH；可在 Linux CI 交叉編譯並在 Windows Runner 實測。 |
| UI 框架 | **WPF** | 成熟穩定、硬體加速、支援 PerMonitorV2 DPI；可完全自訂樣式。WinUI 3 需要 Windows App SDK 執行階段與封裝，對單一小工具負擔較大；Electron/Tauri 記憶體或整合成本較高。 |
| 玻璃效果 | DWM 延伸框架 + `SetWindowCompositionAttribute` 壓克力（Win11）/ 模糊（Win10），可選 `DWMWA_SYSTEMBACKDROP_TYPE` | 小工具幾乎都處於「未聚焦」狀態；DWM 系統背景材質在未聚焦時會變不透明，accent 壓克力不會。非分層（non-layered）視窗避免 `AllowsTransparency` 的軟體合成成本，並保留 Win11 原生圓角與陰影。 |
| CPU 使用率 | `GetSystemTimes` 差值 | 與工作管理員一致、零配置、極低成本。 |
| CPU 時脈 | PDH `Processor Information\% Processor Performance` × 基準時脈 | 工作管理員相同算法，免管理員權限。 |
| GPU 使用率／VRAM | PDH `GPU Engine` / `GPU Adapter Memory` + **DXGI** 列舉介面卡 | 與工作管理員同源；Intel / AMD / NVIDIA 通用，不需廠商 SDK；以 LUID 對應介面卡。 |
| 溫度 | LibreHardwareMonitorLib 0.9.6（GPU：NVAPI/ADL，不需管理員）+ PDH `Thermal Zone Information`（ACPI） | CPU 封裝溫度需要管理員權限與使用者自行安裝的已簽章 PawnIO 驅動；SysTune 不內含任何核心驅動（舊版 LHM 內嵌的 WinRing0 會被 Defender 標記）。無法取得時不顯示並在提示說明來源。 |
| 記憶體 | `GlobalMemoryStatusEx` | 實體記憶體與認可量。 |
| 磁碟 | `DriveInfo`（每 15 秒）+ PDH `PhysicalDisk(_Total)` 活動/讀寫速率 | 只列本機固定/卸除式磁碟，避免網路磁碟阻塞。 |
| 網路 | `NetworkInterface` 位元組計數差值 | 排除迴路、通道與虛擬介面卡；網路變更事件觸發重新列舉。 |
| Spotify | 官方 **Web API**（Player 端點）+ **PKCE** | 官方唯一支援的跨裝置（Spotify Connect）途徑。 |
| 本機備援 | Windows **SMTC**（`GlobalSystemMediaTransportControlsSessionManager`） | 免登入即可顯示本機 Spotify 桌面版；在開發模式限制下仍「開箱即用」。 |
| 機密資料 | **DPAPI**（CurrentUser + entropy） | Windows 原生、綁定使用者與電腦。 |
| MVVM | CommunityToolkit.Mvvm 8.4 | 原始碼產生器、零反射。 |
| 安裝 | Inno Setup（每使用者安裝，免管理員）+ 可攜版 ZIP | 簡單、可靠、支援升級時關閉執行中程式。 |

## 專案結構

```
SysTune.slnx
├─ src/SysTune.Core          跨平台、可測試的核心（net10.0）
│   ├─ Common/               時鐘抽象、指數退避、AsyncSignal
│   ├─ Formatting/           數值/時間格式化
│   ├─ Logging/              滾動檔案記錄器
│   ├─ Media/                NowPlaying 模型、來源選擇邏輯
│   ├─ Monitoring/           快照模型、CPU/速率計算、GPU 引擎彙總、樣本歷史
│   ├─ Settings/             設定模型、原子寫入與防抖儲存
│   └─ Spotify/              PKCE、loopback 回呼伺服器、Token 管理、API 用戶端、輪詢服務
├─ src/SysTune.App           WPF 應用程式（net10.0-windows10.0.19041.0）
│   ├─ Interop/              Win32 / DWM / PDH / DXGI P/Invoke
│   ├─ Monitoring/           SystemMonitor（背景取樣）、HardwareSensors（LHM）
│   ├─ Services/             玻璃效果、主題、托盤、單一執行個體、自動啟動、DPAPI、SMTC、封面載入、視窗位置
│   ├─ ViewModels/           Main / System / NowPlaying / Settings
│   ├─ Views/                MainWindow（展開＋精簡）、SettingsWindow
│   ├─ Controls/             Sparkline、Meter（OnRender 自繪，低配置）
│   └─ Themes/               Dark / Light 色彩、控制項樣式
├─ tests/SysTune.Core.Tests  xUnit 單元/整合測試
├─ installer/                Inno Setup 腳本
├─ tools/                    圖示產生、Windows 煙霧測試腳本
└─ .github/workflows/        CI（Windows 建置/測試/實機啟動）、Release
```

## 資料流與執行緒

```
SystemMonitor (專用背景執行緒, BelowNormal)
   └─ SystemSnapshot (immutable) ──► AppController.OnSnapshot ──(合併, Background 優先權)──► SystemViewModel ──► 綁定

SpotifyPlaybackService (async 迴圈) ─┐
LocalMediaSession (SMTC 事件) ───────┴─► NowPlayingSelector ──► NowPlayingViewModel ──► 綁定
DispatcherTimer 500ms (僅可見時) ──► 進度條外插（不打 API）
```

- **UI 執行緒不做任何 I/O**：硬體取樣、HTTP、圖片解碼都在背景完成；封面以顯示尺寸解碼並 `Freeze()`。
- **快照合併**：若 UI 忙碌，未處理的快照會被新快照取代而不是排隊，避免長時間執行後的延遲累積。
- **隱藏即降載**：小工具隱藏時系統取樣降為 5 秒（只供托盤提示），Spotify 輪詢完全停止，進度計時器停止。
- **自適應輪詢**：播放中每 3 秒、暫停 5 秒、無播放 8 秒；預估歌曲結束時提早喚醒；429 遵守 `Retry-After`；離線時 2→60 秒指數退避；指令後 400ms 主動刷新並先做樂觀更新。

## 可靠性設計

- 每個硬體資料來源各自 try/catch：失敗只記錄一次，該欄位顯示「—」或隱藏，不影響其他來源。
- Spotify 任何失敗都轉成狀態（`Offline`/`RateLimited`/`Error`/`Disconnected`），不會丟到 UI；Refresh Token 被撤銷時自動清除並提示重新連結。
- 設定檔原子寫入（暫存檔 + 取代）、損毀時備份為 `.corrupt` 並使用預設值。
- 視窗位置以 Win32 `WINDOWPLACEMENT`（實體像素）保存，還原前用 `MonitorFromRect` 確認仍在某個螢幕上，避免拔掉螢幕後小工具「消失」。
- 全域例外處理：UI 例外記錄後繼續執行（連續 20 次才放棄）、背景 Task 例外被觀察並記錄。
- 單一執行個體：第二次啟動只會喚醒第一個執行個體。
- 記錄檔：`%LOCALAPPDATA%\SysTune\logs\systune.log`，1 MB 滾動保留 3 份，絕不寫入 Token。

## UI / UX 設計

- **方向**：Minimal / Glassmorphism / Premium / Windows Native。
- **尺寸**：展開 340 DIP 寬（高度依內容），精簡 460×56 DIP 膠囊；右下角拖曳可等比縮放 85%–160%（保持設計比例，而非重新排版）。
- **字體**：Segoe UI（中文退回 Microsoft JhengHei UI）；圖示使用 Segoe Fluent Icons（Win10 退回 Segoe MDL2 Assets），與系統一致且不需額外資源。
- **層次**：玻璃（系統模糊）→ 可調不透明度的色調層 → 頂部微光漸層 → 1px 髮絲邊框；指標卡片使用極淡的白色/黑色疊層。
- **色彩**：每個指標固定一個語意色（CPU 青藍、GPU 紫、記憶體綠、下載藍、上傳粉、磁碟琥珀、Spotify 綠），淺色主題使用加深版本以確保對比。
- **互動**：標題列按鈕只在滑鼠移入時淡入，平時畫面只有資訊；任何空白處可拖曳；Esc 隱藏、空白鍵播放/暫停、Ctrl+←/→ 切歌、Ctrl+, 設定。
- **動畫**：只在有意義的地方使用（按鈕淡入、磁碟條 350ms 緩動、播放鍵微放大），尊重系統「顯示動畫」設定。
