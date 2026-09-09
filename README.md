# Accessi-download

**Accessi-download** 是一個以 [yt-dlp](https://github.com/yt-dlp/yt-dlp) 為核心、優先考慮螢幕閱讀器操作的 Windows 影音下載工具。名稱與 **Accessibilibili** 同系列。

目標很簡單：不用碰命令列，貼上網址、選畫質或音質、按下載即可。

## 目前功能

- Windows 10 / 11 x64 綠色免安裝版。
- 原生 WinForms 介面，優先支援 NVDA 與完整鍵盤操作。
- 明確支援 YouTube、Bilibili、Douyin（抖音完整影片網址），以及 yt-dlp 可處理的其他網站。
- 不需要先「取得影片資訊」：貼上網址後可直接下載。
- 影片：
  - 可選最佳可用畫質，或設定 4320p / 2160p / 1440p / 1080p / 720p / 480p / 360p / 240p / 144p 畫質上限。
  - yt-dlp 會在上限內自動挑選最佳可用來源。
  - 輸出 MP4、MKV、WebM、MOV，或交由 yt-dlp 自動決定。
- 音訊：
  - 自動選擇最佳可用音訊來源。
  - 可輸出 M4A、MP3、AAC、Opus、Vorbis、FLAC、WAV、ALAC。
  - 有損格式可選 320 / 256 / 192 / 160 / 128 / 96 / 64 kbps。
- 批量下載：網址輸入框可一行貼一個網址，依序處理。
- 播放清單 / 合集：
  - 預設關閉，避免帶 `list=` 的單支 YouTube 網址誤下載整份清單。
  - 啟用後，以播放清單 / 合集名稱建立資料夾，並依清單順序命名項目。
- 檔名可選是否加入影片 ID，例如 `[BV1rHYx6fEzy]`，預設關閉。
- 下載完成後可選是否自動開啟資料夾，預設關閉。
- 下載成功後自動清空網址輸入框；下載失敗或取消時保留網址，方便直接重試。
- 可自訂下載資料夾。
- 偏好自動儲存在程式旁的 `settings.ini`，不寫登錄檔。
- 登入 / Cookie：
  - 可直接讀取 Edge、Chrome、Firefox、Brave、Opera、Vivaldi、Chromium Cookie。
  - 可匯入 Netscape 格式 `cookies.txt`。
  - 介面內可直接開啟 YouTube、Bilibili、Douyin 網站。
  - 不要求在 Accessi-download 裡輸入帳號密碼。
  - Douyin 可能需要新鮮瀏覽器 Cookie；遇到解析失敗時，可先在瀏覽器開啟抖音，再選擇該瀏覽器作為 Cookie 來源。
- 內建 yt-dlp 更新：
  - Stable / Nightly / Master 三個頻道。
  - 可手動立即更新。
  - 可自行決定是否開啟「每天第一次啟動時詢問更新」。
- Portable 發佈包自帶：
  - `yt-dlp.exe`
  - `ffmpeg.exe`
  - `ffprobe.exe`
  - `deno.exe`

## 使用方式

1. 到本 repo 的 **Actions** 下載最新 `Accessi-download-portable-win64` artifact；正式版也可放在 **Releases**。
2. 解壓縮整個資料夾，不要只拿單獨的 EXE。
3. 執行 `Accessi-download.exe`。
4. 貼上網址；批量下載時每行一個。
5. 選擇影片 / 音訊、畫質 / 音質與輸出格式。
6. 選擇儲存位置後按「開始下載」。

YouTube、Bilibili 或 Douyin 若需要 Cookie，到「登入與更新」分頁選擇你平常使用的瀏覽器即可。若瀏覽器 Cookie 被鎖定，可先完全關閉瀏覽器再試，或改用 `cookies.txt`。

## NVDA / 鍵盤操作

介面使用 Windows 原生控制項，所有主要輸入框、下拉選單、核取方塊與按鈕都有 Accessible Name。

主要快捷方式：

- `Alt+U`：網址輸入區。
- `Alt+V`：影片模式。
- `Alt+O`：只下載音訊。
- `Alt+B`：瀏覽下載資料夾。
- `Alt+D`：開始下載。
- `Alt+C`：取消。
- `Esc`：取消目前下載 / 更新。

更完整的測試基準見 [`docs/ACCESSIBILITY.md`](docs/ACCESSIBILITY.md)。

## 開發 / 編譯

專案使用 **C# WinForms + .NET Framework 4.8**，不依賴第三方 UI 套件。

```powershell
msbuild AccessiDownload.csproj /p:Configuration=Release
```

GitHub Actions 會自動：

1. 編譯 `Accessi-download.exe`。
2. 下載 yt-dlp Nightly Windows x64。
3. 下載 FFmpeg build，抽出 FFmpeg / FFprobe。
4. 下載 Deno Windows x64。
5. 組成完整 portable ZIP。
6. 上傳 Actions artifact；若推送 `v*` tag，會同時建立 GitHub Release。

## 專案結構

```text
Accessi-download.exe
yt-dlp.exe
tools/
  ffmpeg.exe
  ffprobe.exe
  deno.exe
settings.ini          # 第一次儲存設定後才產生
```

## 關於 yt-dlp 更新

Accessi-download 不自行修改 yt-dlp；更新功能直接使用 yt-dlp 官方支援的 `--update-to` 機制。預設選擇 Nightly，以較快取得網站變更造成的修正。你可以隨時切回 Stable，也可以完全不開啟啟動更新提示。

## 法律與使用提醒

Accessi-download 只是下載前端。請只下載你有權保存的內容，並遵守網站服務條款、著作權規範及所在地法律。本專案不提供 DRM 破解或付費內容繞過機制。

第三方元件與授權資訊見 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。

## 開發

希希企劃；由網頁版 GPT 協助開發與維護。
