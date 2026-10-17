# 版本紀錄

格式參考 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.1.0/)，版本號遵循 [SemVer](https://semver.org/lang/zh-TW/)。

## [1.0.0] - 2026-10-17

### 新增
- 系統監控：CPU（使用率、時脈、溫度）、GPU（使用率、VRAM、溫度，PDH + DXGI）、記憶體、網路速度、磁碟容量與活動、開機時間、電池。
- 60 秒走勢迷你圖。
- Spotify Web API 整合（PKCE、DPAPI 加密 Token、自適應輪詢、429/離線/授權撤銷處理）：播放資訊、Spotify Connect 裝置顯示與切換、播放控制、音量、拖曳進度、隨機、重複。
- 本機 Spotify 備援（Windows 媒體工作階段），免登入。
- 玻璃效果（壓克力／模糊／系統背景／關閉）、深色／淺色／跟隨系統主題、背景不透明度。
- 展開與精簡模式、等比縮放、永遠置頂、鎖定位置、通知區圖示與選單、開機自動啟動、單一執行個體。
- 多螢幕／PerMonitorV2 DPI 位置保存與還原。
- Inno Setup 安裝檔（每使用者、免管理員）與可攜版；x64 與 ARM64。
- CI：Windows Runner 建置、單元測試、實機啟動煙霧測試與截圖。
