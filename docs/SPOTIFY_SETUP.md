# Spotify 帳戶連結指南

SysTune 有兩種方式顯示 Spotify 播放資訊：

| 模式 | 需要設定 | 能做什麼 |
| --- | --- | --- |
| **本機模式**（預設開啟） | 不需要。開啟 Spotify 桌面版即可 | 顯示**此電腦**上 Spotify 的歌曲、歌手、封面、進度；播放／暫停、上下首、隨機、重複、拖曳進度 |
| **Spotify Connect 模式** | 建立自己的 Spotify 開發者應用程式並登入一次 | 顯示**所有裝置**（手機、喇叭、電視、網頁播放器…）的播放；顯示播放裝置；切換裝置；調整 Connect 音量；全部播放控制 |

兩者可同時啟用：已連結帳戶時優先顯示 Spotify Connect；未連結、離線或沒有任何裝置在播放時，會自動退回本機模式。

## 為什麼需要自己的 Client ID？

Spotify Web API 規定每個應用程式都必須註冊。2026 年 2 月起，**開發模式（Development Mode）**的應用程式：

- 應用程式**擁有者必須有 Spotify Premium**（訂閱中止時應用程式停止運作，恢復後自動恢復）；
- 每個應用程式最多 **5 位**授權使用者（需在 Dashboard 的 *User Management* 加入）。

因此 SysTune 不內建共用的 Client ID（共用 ID 會立即超過 5 位使用者上限），而是讓每位使用者用自己的帳號建立一個免費的開發者應用程式。整個流程約 3 分鐘。

> 參考：[Spotify February 2026 Web API Dev Mode Changes – Migration Guide](https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide)

## 步驟

1. 前往 <https://developer.spotify.com/dashboard>，用你的 Spotify 帳號登入並同意開發者條款。
2. 按 **Create app**：
   - **App name**：任意，例如 `SysTune (我的電腦)`
   - **App description**：任意
   - **Redirect URIs**：填入
     ```
     http://127.0.0.1:47821/callback
     ```
     （必須完全一致；這是 SysTune 設定頁顯示的 Redirect URI。若你在 SysTune 改了回呼連接埠，這裡也要一起改。）
   - **Which API/SDKs are you planning to use?**：勾選 **Web API**
   - 同意條款後按 **Save**。
3. 在應用程式頁面按 **Settings**，複製 **Client ID**（**不需要** Client Secret）。
4. 開啟 SysTune → 設定（齒輪）→ **Spotify**：
   - 貼上 Client ID；
   - 按 **連結 Spotify**，瀏覽器會開啟 Spotify 授權頁，按「同意」。
   - 看到「✓ 已連結 Spotify」即可關閉分頁。
5. （選用）若要讓家人也使用你的應用程式，在 Dashboard → *User Management* 加入對方的 Spotify 帳號 Email（上限 5 位）。

## SysTune 申請的權限

| Scope | 用途 |
| --- | --- |
| `user-read-playback-state` | 讀取目前播放狀態與裝置清單 |
| `user-read-currently-playing` | 讀取正在播放的歌曲 |
| `user-modify-playback-state` | 播放／暫停、上下首、拖曳進度、音量、隨機、重複、切換裝置 |

SysTune 不讀取你的播放清單、收藏、Email 或任何其他個人資料。

## 安全性

- 使用 **Authorization Code + PKCE**（RFC 7636）：不需要也不保存 Client Secret。
- 授權回呼只在 `127.0.0.1` 監聽（不使用 `localhost`，符合 Spotify 2025 年後的 Redirect URI 規定），收到回呼即關閉；以隨機 `state` 參數防止 CSRF。
- Access / Refresh Token 以 **Windows DPAPI（目前使用者範圍）**加密後存放於 `%LOCALAPPDATA%\SysTune\spotify.token`，只有同一台電腦上的同一個 Windows 使用者能解密。Token 永遠不會寫入記錄檔。
- 在設定頁按 **中斷連結** 會刪除本機 Token。若要完全撤銷授權，請到 <https://www.spotify.com/account/apps/> 移除應用程式。

## 疑難排解

| 狀況 | 原因與解法 |
| --- | --- |
| 授權頁顯示 `INVALID_CLIENT: Invalid redirect URI` | Dashboard 的 Redirect URI 與 SysTune 設定頁顯示的不一致（注意是 `127.0.0.1` 不是 `localhost`、連接埠、`/callback` 路徑）。 |
| 「Client ID 無效」 | Client ID 貼錯或應用程式已刪除。 |
| 「無法監聽 127.0.0.1:47821」 | 連接埠被其他程式占用。在 SysTune 設定改用其他連接埠（例如 47822），並同步修改 Dashboard 的 Redirect URI。 |
| 「Spotify 不允許此操作…應用程式未授權此使用者」 | 登入的帳號不是應用程式擁有者，且未加入 *User Management*；或擁有者的 Premium 已到期。 |
| 「此操作需要 Spotify Premium」 | Spotify 只允許 Premium 帳號用 API 控制播放；仍可顯示播放資訊。 |
| 「找不到可用的播放裝置」 | 所有裝置上的 Spotify 都已關閉或閒置太久。先在任一裝置開始播放。 |
| 顯示「請求受限，稍後重試」 | 收到 HTTP 429，SysTune 會依 `Retry-After` 自動等待後恢復。 |
| 網路中斷 | 系統監控不受影響；Spotify 區塊顯示最後狀態並以指數退避（2 秒～60 秒）自動重試。 |
