# Accessi-download

**Accessi-download** 是一個以 [yt-dlp](https://github.com/yt-dlp/yt-dlp) 為核心、優先考慮螢幕閱讀器操作的 Windows 影音下載工具。  
名稱與 **Accessibilibili** 同系列。

目前目標是：不用碰命令列，貼上網址、選畫質或音質、按下載即可。

## 目前功能

- Windows 10 / 11 x64 綠色免安裝版。
- 原生 WinForms 介面，優先支援 NVDA 與完整鍵盤操作。
- 支援 YouTube、Bilibili，以及 yt-dlp 可處理的其他網站。
- 影片：
  - 解析後列出實際可用解析度。
  - 可選「最佳畫質」或限制到指定解析度。
  - 輸出 MP4、MKV、WebM、MOV，或交由 yt-dlp 自動決定。
- 音訊：
  - 解析後列出實際可用音訊來源與位元率（網站有提供時）。
  - 可輸出 M4A、MP3、AAC、Opus、Vorbis、FLAC、WAV、ALAC。
  - 有損格式可選 320 / 256 / 192 / 160 / 128 / 96 / 64 kbps。
- **批量下載**：網址輸入框可一行貼一個網址，下載時依序處理。
- **播放清單 / 合集下載**：可自行開啟；啟用後會以播放清單或合集名稱建立資料夾，並在檔名前加入清單順序。
- **影片 ID 可選**：可決定檔名是否加入 `[影片ID]`，預設關閉。
- **完成後行為可選**：可決定下載完成後是否自動開啟資料夾，預設關閉，不再每次跳出 Yes / No 詢問。
- 可自訂下載資料夾，設定儲存在程式旁的 `settings.ini`，不寫登錄檔。
- 登入 / Cookie：
  - 可直接讀取 Edge、Chrome、Firefox、Brave、Opera、Vivaldi、Chromium Cookie。
  - 可匯入 Netscape 格式 `cookies.txt`。
  - 介面內可直接開啟 YouTube / Bilibili 登入頁。
  - 不要求在 Accessi-download 裡輸入帳號密碼。
- 內建 yt-dlp 更新：
  - Stable / Nightly / Master 三個頻道。
  - 可手動立即更新。
  - 可自行決定是否開啟「每天第一次啟動時詢問更新」。
- Portable 發佈包會自帶：
  - `yt-dlp.exe`
  - `ffmpeg.exe`
  - `ffprobe.exe`
  - `deno.exe`

> 2026 年的 yt-dlp 完整 YouTube 支援除了 FFmpeg，也需要 JavaScript runtime；本專案選用官方文件推薦的 Deno。

## 使用方式

1. 到本 repo 的 **Actions** 下載最新 `Accessi-download-portable-win64` artifact；正式版之後也會放在 **Releases**。
2. 解壓縮整個資料夾，不要只拿單獨的 EXE。
3. 執行 `Accessi-download.exe`。
4. 貼上網址；批量下載時一行貼一個網址。
5. 按「解析第一個網址」取得可用畫質與音質。
6. 選擇影片 / 音訊、畫質 / 音質與輸出格式。
7. 若要下載整份播放清單或合集，勾選播放清單選項；否則保持預設關閉。
8. 選擇儲存位置後按「開始下載」。

如果影片需要登入，到「登入與更新」分頁選擇你已登入的瀏覽器即可。若瀏覽器 Cookie 被鎖定，可先完全關閉瀏覽器再試，或改用 `cookies.txt`。

## 批量下載

網址輸入框支援 **一行一個網址**。貼上多個網址後按「開始下載」，會交給 yt-dlp 依序處理。

「解析第一個網址」只解析第一行，以便建立畫質 / 音質選單。批量下載或播放清單的音訊模式會讓每個項目各自選擇最佳可用來源，再依你選的輸出格式與音質處理，避免把第一支影片專屬的 format ID 套到其他影片。

## 播放清單與合集

「若網址是播放清單或合集，下載全部項目」預設為 **關閉**。這是刻意的：YouTube 單支影片網址有時會附帶 `list=`，若預設開啟，可能一不小心把整份播放清單抓回來。

啟用後：

- 播放清單或合集會下載全部可用項目。
- 會在下載位置下建立以 **播放清單 / 合集名稱** 命名的資料夾。
- 清單內檔案會在檔名前加入播放順序，方便維持原始排列。
- 一般單支影片不會因此被放進多餘的假資料夾。

## 檔名與完成後行為

- 「檔名加入影片 ID，例如 `[BV1rHYx6fEzy]`」預設 **關閉**。一般使用時不需要開；若常遇到同名影片、需要回查來源、整理收藏或避免名稱混淆時才有價值。
- 「下載完成後自動開啟資料夾」預設 **關閉**。
- 不再於每次下載完成後跳出「是否開啟下載資料夾」對話框，避免一直打斷 NVDA 焦點與操作流程。
- 若勾選自動開啟，播放清單完成時會優先開啟實際輸出的播放清單資料夾。

## 自動保存偏好

偏好會自動寫入程式資料夾旁的 `settings.ini`，目前包含：

- 下載位置
- 影片 / 音訊模式
- 影片畫質與輸出格式
- 音訊格式與輸出音質
- 是否下載整份播放清單 / 合集
- 是否在檔名加入影片 ID
- 是否在完成後自動開啟資料夾
- Cookie 來源
- yt-dlp 更新頻道與啟動更新提示

因為設定檔和程式放在一起，整個資料夾搬到另一台 Windows 電腦時可以一起帶走偏好。

## NVDA / 鍵盤操作

介面使用 Windows 原生控制項，所有主要輸入框、下拉選單、核取方塊、按鈕都有 Accessible Name。  
主要快捷方式：

- `Alt+U`：網址輸入區。
- `Alt+A`：解析第一個網址。
- `Alt+V`：影片模式。
- `Alt+O`：只下載音訊。
- `Alt+B`：瀏覽下載資料夾。
- `Alt+D`：開始下載。
- `Alt+C`：取消。
- `F5`：解析第一個網址。
- `Esc`：取消目前解析 / 下載 / 更新。

更完整的測試基準見 [`docs/ACCESSIBILITY.md`](docs/ACCESSIBILITY.md)。

## 開發 / 編譯

專案使用 **C# WinForms + .NET Framework 4.8**，不依賴第三方 UI 套件。

選擇 .NET Framework 4.8 的原因是：
- WinForms 使用原生 Windows 無障礙基礎，NVDA 操作比自繪 UI 更可預期。
- 主程式本身可以保持很小。
- Windows 10/11 通常已有 .NET Framework 4.8，不需要把整套 .NET Runtime 塞進 portable 包。

本機編譯：

```powershell
msbuild AccessiDownload.csproj /p:Configuration=Release
```

GitHub Actions 會自動：
1. 編譯 `Accessi-download.exe`。
2. 下載 yt-dlp Nightly Windows x64。
3. 下載 yt-dlp 官方 FFmpeg build，抽出 FFmpeg / FFprobe。
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
