# 測試策略、結果與已知限制

## 品質標準（可驗證）

| # | 標準 | 驗證方式 |
| --- | --- | --- |
| Q1 | 可在 Windows 實際建置與啟動，無例外終止 | CI：Windows Runner 以 Release 建置（警告視為錯誤），以 `--smoke-test` 實際執行發布版 exe，程式自行結束碼 0 |
| Q2 | 使用真實硬體資料 | 煙霧測試輸出 `smoke-result.json`：由執行中的程式寫出最後一筆真實快照（CPU 型號/使用率、記憶體容量、磁碟、網路…） |
| Q3 | 取不到的感測器正確處理 | 單元測試（null → 「—」）；CI Runner 無實體 GPU、無溫度感測器，畫面須顯示「未偵測到硬體 GPU」且不崩潰 |
| Q4 | Spotify 失效不影響系統監控 | 單元測試覆蓋離線、429、5xx、401→refresh、refresh 撤銷、403 Premium、404 無裝置；Spotify 服務所有錯誤轉為狀態，不拋到 UI |
| Q5 | OAuth 安全 | RFC 7636 官方測試向量、state 不符拒絕、只綁定 127.0.0.1、不傳 client_secret、Token 不出現在 `ToString`/記錄 |
| Q6 | 設定可靠保存 | 單元測試：往返、損毀檔備份、範圍正規化、防抖寫入與結束時寫回 |
| Q7 | 長時間背景運作的資源用量 | CI 煙霧測試記錄工作集、私有記憶體、執行緒數、控制代碼數、3 秒 CPU 平均 |
| Q8 | 外觀 | CI 擷取深色展開、淺色＋設定視窗、精簡模式三張實機截圖 |

## 測試層級

1. **單元／整合測試**（`tests/SysTune.Core.Tests`，xUnit，跨平台）
   - 格式化、CPU/網路速率計算、GPU 引擎彙總（工作管理員演算法）、樣本環形緩衝
   - 設定儲存（原子寫入、損毀復原、防抖）、滾動記錄檔
   - PKCE（RFC 7636 附錄 B 向量）、授權 URL、**真實 TCP** loopback 回呼伺服器、完整 PKCE 登入流程（模擬瀏覽器回呼）、state 偽造拒絕、連接埠衝突、重複連結時取消前一次
   - Spotify API：以錄製格式的 JSON fixture 解析歌曲/Podcast、204、401 自動刷新重試、429 Retry-After、403 Premium、404 無裝置、5xx/斷線、各指令 HTTP 動詞與參數、裝置清單
   - 輪詢服務：狀態轉換、離線退避遞增、限流、授權撤銷自動中斷、隱藏時不輪詢、樂觀更新、自適應間隔
   - 播放來源選擇（Connect → 本機 → 離線時保留最後狀態）
2. **Windows 實機煙霧測試**（`tools/smoke.ps1`，CI 執行）：啟動真實發布版 → 等待取樣 → 量測資源 → 截圖 → 程式自行寫出真實快照並結束。
3. **人工驗證清單**（需要實體硬體／帳號，CI 無法覆蓋）：見下方。

## 結果

### 本機（Linux 交叉編譯環境）

- `dotnet build SysTune.slnx`：成功，0 警告、0 錯誤
- `dotnet test`：**73 / 73 通過**
- `dotnet publish -r win-x64 --self-contained`：成功（輸出 186 MB 資料夾；安裝檔以 LZMA2 壓縮）

### Windows CI（GitHub Actions `windows-latest`：Windows Server 2025 Datacenter build 26100，4 vCPU AMD EPYC，16 GB，無實體 GPU）

| 項目 | 結果 |
| --- | --- |
| Release 建置（`-warnaserror`） | ✅ 0 警告 0 錯誤 |
| 單元測試 | ✅ 73 / 73 通過（`unit-tests.trx` + Cobertura 覆蓋率作為 CI 產物） |
| 煙霧測試：深色展開 / 淺色＋設定視窗 / 深色精簡 | ✅ 三者皆啟動、取樣、截圖並以結束碼 0 自行結束 |
| 真實資料（`smoke-result.json`） | ✅ CPU 型號 `AMD EPYC 7763 64-Core Processor`、使用率與即時時脈；16 GB 記憶體；C:/D: 磁碟容量與活動；網路介面 `Ethernet 3` 與實際流量；無電池；開機時間 |
| 不可用資料 | ✅ 無 GPU → 顯示「未偵測到硬體 GPU」；無溫度感測器 → 不顯示溫度；未設定 Spotify → 顯示設定提示，系統監控正常 |

**資源用量**（啟動 25 秒後量測 10 秒，1 秒取樣間隔，小工具顯示中）：

| 畫面 | 私有記憶體 | 工作集 | 執行緒 | CPU（整機 / 單核心） |
| --- | --- | --- | --- | --- |
| 深色展開 | 54.2 MB | 147.5 MB | 18 | 1.25% / 2.5% |
| 淺色＋設定視窗 | 65.8 MB | 161.0 MB | 17 | 1.88% / 3.75% |
| 深色精簡 | 54.4 MB | 144.2 MB | 17 | 1.25% / 2.5% |

受控堆積約 5–12 MB。工作集大部分是 WPF / .NET 共用映像。CPU 數字包含 WPF 繪製；量測後已移除每秒的 `GetIsNetworkAvailable()` 呼叫（原本為最貴的取樣來源，每次約 10–16 ms），之後的 CI 會以 `sampleCostMs` 持續追蹤每個資料來源的平均成本。小工具隱藏時取樣降為每 5 秒、Spotify 停止輪詢。

**CI 抓到並已修正的問題**（這正是實機測試的價值）：
1. `System.Security.Cryptography.ProtectedData` 套件在 Windows 上被 NuGet 判定為多餘（NU1510）→ 移除（已內建於 Windows Desktop 執行環境）。
2. 第一次取樣時 `now - TimeSpan.MinValue` 溢位，導致所有指標顯示「—」→ 改用 nullable 時間戳。
3. 雲端 VM 的網卡名為「Hyper-V Network Adapter」被誤判為虛擬網卡而顯示離線 → 改以「是否有預設閘道」選擇介面卡。

截圖見 [`docs/screenshots`](screenshots/)（`*-full.png` 為完整螢幕）。CI Runner 的背景是終端機視窗且為伺服器版 Windows，壓克力模糊的實際觀感需在一般桌面確認。

## 人工驗證清單（實體 Windows 電腦）

以下項目依賴實體硬體、使用者帳號或互動，目前**尚未**在實體電腦驗證：

- [ ] Win11 壓克力／Win10 模糊效果的實際觀感、圓角與陰影
- [ ] 實體 GPU（NVIDIA / AMD / Intel）使用率與工作管理員一致；GPU 溫度
- [ ] 以系統管理員執行 + PawnIO 時的 CPU 封裝溫度
- [ ] Spotify Connect 實際登入、跨裝置顯示、切換裝置、音量、拖曳進度（需要 Premium 與開發者應用程式）
- [ ] 本機 Spotify 桌面版（SMTC）顯示與控制
- [ ] 多螢幕、不同 DPI 之間拖曳與重新開機後位置還原
- [ ] 開機自動啟動與「啟動時隱藏」
- [ ] 安裝檔安裝／升級（執行中自動關閉）／解除安裝
- [ ] 24 小時以上長時間執行的記憶體穩定性

## 已知限制

- **CPU 溫度**：一般權限下只能讀取 ACPI 熱區（很多桌機主機板不提供，或數值固定）；CPU 封裝溫度需系統管理員權限 + 使用者自行安裝 PawnIO 驅動。
- **Spotify 開發模式限制（2026/02 起）**：應用程式擁有者需 Premium；每個應用程式最多 5 位使用者；播放控制需 Premium。
- **本機模式**只能看到這台電腦的 Spotify 桌面版，不能切換裝置或調整 Connect 音量；Microsoft Store 版與傳統版都支援。
- **壓克力 accent API**（`SetWindowCompositionAttribute`）是 Windows 殼層長期使用但未公開文件化的 API；若未來 Windows 版本改變行為，可在設定改用「系統背景」（文件化 API）或「模糊」/「關閉」。
- **「系統背景」模式**在視窗未聚焦時會變成不透明（Windows 設計如此）。
- Windows「透明效果」關閉時自動改為不透明背景。
- 執行檔未經程式碼簽章，SmartScreen 會在首次執行時警告。
- 介面目前僅提供繁體中文。
