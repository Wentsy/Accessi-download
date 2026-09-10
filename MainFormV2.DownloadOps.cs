using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AccessiDownload
{
    internal sealed partial class MainFormV2 : Form
    {
        private async void MainForm_Shown(object sender, EventArgs e)
        {
            SetStatus(service.GetComponentSummary());
            await RefreshVersionAsync();
            if (!settings.PromptUpdateOnStart) return;

            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (string.Equals(settings.LastUpdatePromptDate, today, StringComparison.Ordinal)) return;

            settings.LastUpdatePromptDate = today;
            settings.Save();
            DialogResult result = MessageBox.Show(
                this,
                "要讓 Accessi-download 現在檢查並安裝 " + GetSelectedOptionKey(cmbUpdateChannel, "nightly") + " 頻道的 yt-dlp 更新嗎？\r\n\r\n選「否」不會影響這次使用，之後也可以在「登入與更新」分頁手動更新。",
                "yt-dlp 更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes) await UpdateYtDlpAsync();
        }

        private void ApplySettingsToUi()
        {
            applyingSettings = true;
            try
            {
                txtDownloadFolder.Text = settings.DownloadFolder;
                SelectByKey(cmbVideoContainer, settings.VideoContainer, "mp4");
                SelectByKey(cmbVideoQuality, settings.VideoQuality, "auto");
                SelectByKey(cmbAudioFormat, settings.AudioOutputFormat, "m4a");
                SelectByKey(cmbAudioQuality, settings.AudioOutputQuality, "best");
                rbAudio.Checked = string.Equals(settings.DownloadMode, "audio", StringComparison.OrdinalIgnoreCase);
                rbVideo.Checked = !rbAudio.Checked;
                chkOpenFolderAfterDownload.Checked = settings.OpenFolderAfterDownload;
                chkIncludeMediaId.Checked = settings.IncludeMediaId;
                chkDownloadPlaylist.Checked = settings.DownloadPlaylist;
                SelectByKey(cmbCookieSource, settings.CookieSource, "none");
                txtCookieFile.Text = settings.CookieFile;
                SelectByKey(cmbUpdateChannel, settings.UpdateChannel, "nightly");
                chkPromptUpdate.Checked = settings.PromptUpdateOnStart;
                UpdateCookieControls();
                UpdateAudioQualityControl();
            }
            finally
            {
                applyingSettings = false;
            }
        }

        private void SaveSettingsFromUi()
        {
            if (applyingSettings || txtDownloadFolder == null || cmbCookieSource == null) return;
            settings.DownloadFolder = txtDownloadFolder.Text.Trim();
            settings.CookieSource = GetSelectedOptionKey(cmbCookieSource, "none");
            settings.CookieFile = txtCookieFile.Text.Trim();
            settings.UpdateChannel = GetSelectedOptionKey(cmbUpdateChannel, "nightly");
            settings.PromptUpdateOnStart = chkPromptUpdate.Checked;
            settings.VideoContainer = GetSelectedOptionKey(cmbVideoContainer, "mp4");
            settings.VideoQuality = GetSelectedOptionKey(cmbVideoQuality, "auto");
            settings.AudioOutputFormat = GetSelectedOptionKey(cmbAudioFormat, "m4a");
            settings.AudioOutputQuality = GetSelectedOptionKey(cmbAudioQuality, "best");
            settings.DownloadMode = rbAudio.Checked ? "audio" : "video";
            settings.OpenFolderAfterDownload = chkOpenFolderAfterDownload.Checked;
            settings.IncludeMediaId = chkIncludeMediaId.Checked;
            settings.DownloadPlaylist = chkDownloadPlaylist.Checked;
            settings.Save();
        }

        private List<string> GetInputUrls()
        {
            return txtUrl.Text
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool ConfirmBrowserCookieAccess()
        {
            string source = GetSelectedOptionKey(cmbCookieSource, "none");
            string processName = GetChromiumBrowserProcessName(source);
            if (string.IsNullOrWhiteSpace(processName) || !IsProcessRunning(processName)) return true;

            string browserName = GetBrowserDisplayName(source);
            string message =
                "目前偵測到 " + browserName + " 仍在執行。\r\n\r\n" +
                "Windows 上的 Chrome／Chromium 系瀏覽器可能會鎖住 Cookie 資料庫，讓 yt-dlp 無法讀取登入資訊。建議先把 " + browserName + " 完全關閉，包含背景執行的程序，再重新按「開始下載」。\r\n\r\n" +
                "Accessi-download 不會自動關閉瀏覽器，以免影響你正在使用的分頁。\r\n\r\n" +
                "仍要繼續嘗試嗎？";

            SetStatus("偵測到 " + browserName + " 仍在執行，可能無法讀取 Cookie。建議先完全關閉瀏覽器。 ");
            DialogResult result = MessageBox.Show(
                this,
                message,
                "瀏覽器 Cookie 可能被鎖定",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            return result == DialogResult.Yes;
        }

        private static string GetChromiumBrowserProcessName(string source)
        {
            switch ((source ?? string.Empty).ToLowerInvariant())
            {
                case "chrome": return "chrome";
                case "edge": return "msedge";
                case "brave": return "brave";
                case "opera": return "opera";
                case "vivaldi": return "vivaldi";
                case "chromium": return "chromium";
                default: return null;
            }
        }

        private static string GetBrowserDisplayName(string source)
        {
            switch ((source ?? string.Empty).ToLowerInvariant())
            {
                case "chrome": return "Google Chrome";
                case "edge": return "Microsoft Edge";
                case "brave": return "Brave";
                case "opera": return "Opera";
                case "vivaldi": return "Vivaldi";
                case "chromium": return "Chromium";
                case "firefox": return "Mozilla Firefox";
                default: return "瀏覽器";
            }
        }

        private static bool IsProcessRunning(string processName)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                try { return processes.Length > 0; }
                finally
                {
                    foreach (Process process in processes) process.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task DownloadAsync()
        {
            List<string> urls = GetInputUrls();
            if (urls.Count == 0)
            {
                MessageBox.Show(this, "請先貼上至少一個影片網址。", "缺少網址", MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtUrl.Focus();
                return;
            }

            string folder = txtDownloadFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                MessageBox.Show(this, "請選擇下載後的儲存位置。", "缺少儲存位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtDownloadFolder.Focus();
                return;
            }
            if (!service.HasRequiredComponents())
            {
                string summary = service.GetComponentSummary();
                SetStatus(summary);
                MessageBox.Show(this, summary, "缺少必要元件", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!ConfirmBrowserCookieAccess()) return;

            SaveSettingsFromUi();
            var request = new DownloadRequest
            {
                Urls = urls,
                DownloadFolder = folder,
                AudioOnly = rbAudio.Checked,
                MaxVideoHeight = GetSelectedVideoHeight(),
                VideoContainer = GetSelectedOptionKey(cmbVideoContainer, "mp4"),
                AudioSourceFormatId = "bestaudio/best",
                AudioOutputFormat = GetSelectedOptionKey(cmbAudioFormat, "m4a"),
                AudioOutputQuality = GetSelectedOptionKey(cmbAudioQuality, "best"),
                IncludeMediaId = chkIncludeMediaId.Checked,
                DownloadPlaylist = chkDownloadPlaylist.Checked,
                Settings = settings
            };

            string operationText = request.AudioOnly ? "音訊" : "影片";
            if (urls.Count > 1)
                BeginOperation("開始批量下載 " + urls.Count + " 個網址的" + operationText + "…");
            else if (request.DownloadPlaylist)
                BeginOperation("開始下載" + operationText + "；若網址是播放清單或合集，將下載全部項目…");
            else
                BeginOperation("開始下載" + operationText + "…");

            ResetProgressDisplay();
            int lastAnnouncedPercent = -1;
            string lastProgressItemKey = null;

            try
            {
                DownloadResult result = await service.DownloadAsync(
                    request,
                    progress => BeginInvokeIfRequired(() =>
                    {
                        int currentPercent = Math.Max(0, Math.Min(100, progress.Percent));
                        string currentItemKey =
                            (progress.ItemIndex.HasValue ? progress.ItemIndex.Value.ToString() : "") + "/" +
                            (progress.ItemTotal.HasValue ? progress.ItemTotal.Value.ToString() : "") + "|" +
                            (progress.ItemTitle ?? string.Empty);

                        if (!string.Equals(lastProgressItemKey, currentItemKey, StringComparison.Ordinal))
                        {
                            lastProgressItemKey = currentItemKey;
                            lastAnnouncedPercent = -1;
                        }

                        bool announceProgress = currentPercent >= 100 ||
                            (currentPercent >= 10 && (lastAnnouncedPercent < 0 || currentPercent - lastAnnouncedPercent >= 10));
                        if (announceProgress) lastAnnouncedPercent = currentPercent;

                        string status;
                        if (progress.ItemIndex.HasValue && progress.ItemTotal.HasValue && progress.ItemTotal.Value > 1)
                        {
                            status = "第 " + progress.ItemIndex.Value + "/" + progress.ItemTotal.Value + " 個影片";
                            if (!string.IsNullOrWhiteSpace(progress.ItemTitle)) status += "：" + progress.ItemTitle;
                            status += "，下載進度 ";
                        }
                        else
                        {
                            status = "下載中";
                            if (!string.IsNullOrWhiteSpace(progress.ItemTitle)) status += "：" + progress.ItemTitle;
                            status += "，";
                        }

                        status += string.IsNullOrWhiteSpace(progress.PercentText)
                            ? progress.Percent + "%"
                            : progress.PercentText.Trim();
                        if (!string.IsNullOrWhiteSpace(progress.SpeedText)) status += "，速度 " + progress.SpeedText;
                        if (!string.IsNullOrWhiteSpace(progress.EtaText) && !string.Equals(progress.EtaText, "NA", StringComparison.OrdinalIgnoreCase)) status += "，預估剩餘 " + progress.EtaText;
                        UpdateProgressDisplay(progress, status, announceProgress);
                    }),
                    AppendLog,
                    activeOperation.Token);

                CompleteProgressDisplay();
                int count = result.FinalPaths.Count;
                string completed;
                if (count > 1) completed = "下載完成，共完成 " + count + " 個檔案。";
                else if (count == 1) completed = "下載完成：" + result.FinalPath;
                else completed = "下載完成。";

                if (count > 0)
                {
                    txtUrl.Clear();
                    completed += " 網址已自動清除。";
                }

                AppendStatus(completed);
                AppendLog(completed);

                if (settings.OpenFolderAfterDownload) OpenCompletedFolder(result);
            }
            catch (OperationCanceledException)
            {
                AppendStatus("已取消下載。網址已保留，可直接重試；尚未完成的暫存檔可能會由 yt-dlp 留在下載資料夾中。");
            }
            catch (Exception ex)
            {
                AppendLog("下載錯誤：" + ex);
                AppendStatus("下載失敗，網址已保留：" + ex.Message);
                MessageBox.Show(this, ex.Message + "\r\n\r\n網址會保留在編輯區，可直接重試。詳細資訊可在「記錄」分頁查看。", "下載失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndOperation(); }
        }
    }
}
