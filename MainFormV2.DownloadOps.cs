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

        private async Task AnalyzeAsync()
        {
            List<string> urls = GetInputUrls();
            if (urls.Count == 0)
            {
                MessageBox.Show(this, "請先貼上至少一個影片網址。", "缺少網址", MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtUrl.Focus();
                return;
            }
            if (!service.HasRequiredComponents())
            {
                string summary = service.GetComponentSummary();
                SetStatus(summary);
                MessageBox.Show(this, summary, "缺少必要元件", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SaveSettingsFromUi();
            BeginOperation(urls.Count > 1 ? "偵測到 " + urls.Count + " 個網址，正在解析第一個網址…" : "正在解析影片資訊…");
            try
            {
                currentMedia = await service.AnalyzeAsync(urls[0], settings, activeOperation.Token);
                PopulateMediaChoices(currentMedia);
                string duration = FormatDuration(currentMedia.DurationSeconds);
                lblMediaInfo.Text =
                    (string.IsNullOrWhiteSpace(currentMedia.Title) ? "未取得標題" : currentMedia.Title) +
                    (string.IsNullOrWhiteSpace(currentMedia.Uploader) ? string.Empty : "；上傳者：" + currentMedia.Uploader) +
                    (string.IsNullOrWhiteSpace(duration) ? string.Empty : "；長度：" + duration) +
                    (urls.Count > 1 ? "；批量共 " + urls.Count + " 個網址" : string.Empty);
                lblMediaInfo.AccessibleName = "影片資訊：" + lblMediaInfo.Text;
                SetStatus(urls.Count > 1
                    ? "第一個網址解析完成。已偵測到 " + urls.Count + " 個網址；選好格式後會依序批量下載。"
                    : "解析完成。請選擇畫質、音質與輸出格式後開始下載。");
            }
            catch (OperationCanceledException) { SetStatus("已取消解析。"); }
            catch (Exception ex)
            {
                AppendLog("解析錯誤：" + ex);
                SetStatus("解析失敗：" + ex.Message);
                MessageBox.Show(this, ex.Message, "解析失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndOperation(); }
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

            SaveSettingsFromUi();
            var request = new DownloadRequest
            {
                Urls = urls,
                DownloadFolder = folder,
                AudioOnly = rbAudio.Checked,
                MaxVideoHeight = GetSelectedVideoHeight(),
                VideoContainer = GetSelectedOptionKey(cmbVideoContainer, "mp4"),
                AudioSourceFormatId = GetSelectedAudioFormatId(),
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

            progressBar.Value = 0;
            try
            {
                DownloadResult result = await service.DownloadAsync(
                    request,
                    progress => BeginInvokeIfRequired(() =>
                    {
                        progressBar.Value = Math.Max(0, Math.Min(100, progress.Percent));
                        string status = "下載中";
                        if (!string.IsNullOrWhiteSpace(progress.ItemTitle)) status += "：" + progress.ItemTitle;
                        status += "，" + (string.IsNullOrWhiteSpace(progress.PercentText) ? progress.Percent + "%" : progress.PercentText);
                        if (!string.IsNullOrWhiteSpace(progress.SpeedText)) status += "，速度 " + progress.SpeedText;
                        if (!string.IsNullOrWhiteSpace(progress.EtaText) && !string.Equals(progress.EtaText, "NA", StringComparison.OrdinalIgnoreCase)) status += "，預估剩餘 " + progress.EtaText;
                        SetStatus(status);
                    }),
                    AppendLog,
                    activeOperation.Token);

                progressBar.Value = 100;
                int count = result.FinalPaths.Count;
                string completed;
                if (count > 1) completed = "下載完成，共完成 " + count + " 個檔案。";
                else if (count == 1) completed = "下載完成：" + result.FinalPath;
                else completed = "下載完成。";

                SetStatus(completed);
                AppendLog(completed);

                // The persistent checkbox replaces the old Yes/No completion dialog.
                if (settings.OpenFolderAfterDownload) OpenCompletedFolder(result);
            }
            catch (OperationCanceledException)
            {
                SetStatus("已取消下載。尚未完成的暫存檔可能會由 yt-dlp 留在下載資料夾中。");
            }
            catch (Exception ex)
            {
                AppendLog("下載錯誤：" + ex);
                SetStatus("下載失敗：" + ex.Message);
                MessageBox.Show(this, ex.Message + "\r\n\r\n詳細資訊可在「記錄」分頁查看。", "下載失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndOperation(); }
        }
    }
}
