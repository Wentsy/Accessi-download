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
        private async Task UpdateYtDlpAsync()
        {
            if (activeOperation != null)
            {
                MessageBox.Show(this, "請先等目前操作完成或按取消。", "目前忙碌中", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SaveSettingsFromUi();
            string channel = GetSelectedOptionKey(cmbUpdateChannel, "nightly");
            BeginOperation("正在檢查並更新 yt-dlp（" + channel + "）…");
            try
            {
                string version = await service.UpdateAsync(channel, AppendLog, activeOperation.Token);
                lblYtDlpVersion.Text = version;
                lblYtDlpVersion.AccessibleName = "目前 yt-dlp 版本：" + version;
                SetStatus("yt-dlp 更新完成，目前版本 " + version + "。");
                MessageBox.Show(this, "yt-dlp 已完成檢查／更新。\r\n目前版本：" + version, "更新完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { SetStatus("已取消 yt-dlp 更新。"); }
            catch (Exception ex)
            {
                AppendLog("更新錯誤：" + ex);
                SetStatus("yt-dlp 更新失敗：" + ex.Message);
                MessageBox.Show(this, ex.Message, "更新失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndOperation(); }
        }

        private async Task RefreshVersionAsync()
        {
            try
            {
                if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe")))
                {
                    lblYtDlpVersion.Text = "找不到 yt-dlp.exe";
                    return;
                }
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    string version = await service.GetVersionAsync(cts.Token);
                    lblYtDlpVersion.Text = version;
                    lblYtDlpVersion.AccessibleName = "目前 yt-dlp 版本：" + version;
                }
            }
            catch (Exception ex)
            {
                lblYtDlpVersion.Text = "無法讀取";
                AppendLog("讀取 yt-dlp 版本失敗：" + ex.Message);
            }
        }

        private void PopulateMediaChoices(MediaInfo media)
        {
            string wantedQuality = settings.VideoQuality;
            cmbVideoQuality.BeginUpdate();
            try
            {
                cmbVideoQuality.Items.Clear();
                cmbVideoQuality.Items.Add(new OptionItem { Key = "auto", Display = "自動（最佳可用畫質）" });
                foreach (int height in media.VideoHeights)
                    cmbVideoQuality.Items.Add(new OptionItem { Key = height.ToString(), Display = height + "p 或以下" });
                SelectByKey(cmbVideoQuality, wantedQuality, "auto");
            }
            finally { cmbVideoQuality.EndUpdate(); }

            cmbAudioSource.BeginUpdate();
            try
            {
                cmbAudioSource.Items.Clear();
                foreach (AudioSourceChoice choice in media.AudioSources) cmbAudioSource.Items.Add(choice);
                if (cmbAudioSource.Items.Count == 0)
                    cmbAudioSource.Items.Add(new AudioSourceChoice { FormatId = "bestaudio/best", Display = "自動（最佳可用音質）" });
                cmbAudioSource.SelectedIndex = 0;
            }
            finally { cmbAudioSource.EndUpdate(); }
        }

        private void BeginOperation(string status)
        {
            activeOperation = new CancellationTokenSource();
            btnAnalyze.Enabled = false;
            btnDownload.Enabled = false;
            btnUpdate.Enabled = false;
            btnCancel.Enabled = true;
            SetStatus(status);
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + status);
        }

        private void EndOperation()
        {
            if (activeOperation != null)
            {
                activeOperation.Dispose();
                activeOperation = null;
            }
            btnAnalyze.Enabled = true;
            btnDownload.Enabled = true;
            btnUpdate.Enabled = true;
            btnCancel.Enabled = false;
        }

        private void CancelActiveOperation()
        {
            if (activeOperation != null && !activeOperation.IsCancellationRequested)
            {
                SetStatus("正在取消目前操作…");
                activeOperation.Cancel();
            }
        }
    }
}
