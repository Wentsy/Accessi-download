using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace AccessiDownload
{
    internal sealed partial class MainFormV2 : Form
    {
        private void UpdateDownloadModeControls()
        {
            if (rbVideo == null || rbAudio == null) return;
            bool video = rbVideo.Checked;
            cmbVideoQuality.Enabled = video;
            cmbVideoContainer.Enabled = video;
            cmbAudioFormat.Enabled = !video;
            cmbAudioQuality.Enabled = !video && AudioQualityIsApplicable();
        }

        private void UpdateAudioQualityControl()
        {
            if (cmbAudioQuality == null || rbAudio == null) return;
            cmbAudioQuality.Enabled = rbAudio.Checked && AudioQualityIsApplicable();
        }

        private bool AudioQualityIsApplicable()
        {
            string format = GetSelectedOptionKey(cmbAudioFormat, "m4a");
            return format != "flac" && format != "wav" && format != "alac" && format != "best";
        }

        private void UpdateCookieControls()
        {
            if (txtCookieFile == null || btnBrowseCookie == null) return;
            bool fileMode = GetSelectedOptionKey(cmbCookieSource, "none") == "cookies";
            txtCookieFile.Enabled = fileMode;
            btnBrowseCookie.Enabled = fileMode;
        }

        private void BrowseDownloadFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "選擇下載後的影片或音訊儲存位置";
                dialog.SelectedPath = Directory.Exists(txtDownloadFolder.Text) ? txtDownloadFolder.Text : settings.DownloadFolder;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    txtDownloadFolder.Text = dialog.SelectedPath;
                    SaveSettingsFromUi();
                }
            }
        }

        private void BrowseCookieFile()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "選擇 cookies.txt";
                dialog.Filter = "Cookie 文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    txtCookieFile.Text = dialog.FileName;
                    SaveSettingsFromUi();
                }
            }
        }

        private void OpenCompletedFolder(DownloadResult result)
        {
            try
            {
                string folder = txtDownloadFolder.Text.Trim();
                if (result != null && !string.IsNullOrWhiteSpace(result.FinalPath))
                {
                    string parent = Path.GetDirectoryName(result.FinalPath);
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent)) folder = parent;
                }
                OpenFolder(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "無法開啟資料夾", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenDownloadFolder()
        {
            try { OpenFolder(txtDownloadFolder.Text.Trim()); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "無法開啟資料夾", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private static void OpenFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        private void OpenWebPage(string url)
        {
            try { Process.Start(url); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "無法開啟瀏覽器", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private int? GetSelectedVideoHeight()
        {
            OptionItem item = cmbVideoQuality.SelectedItem as OptionItem;
            if (item == null || string.Equals(item.Key, "auto", StringComparison.OrdinalIgnoreCase)) return null;
            int value;
            return int.TryParse(item.Key, out value) ? (int?)value : null;
        }

        private static string GetSelectedOptionKey(ComboBox combo, string fallback)
        {
            if (combo == null) return fallback;
            OptionItem item = combo.SelectedItem as OptionItem;
            return item == null || string.IsNullOrWhiteSpace(item.Key) ? fallback : item.Key;
        }

        private static void SelectByKey(ComboBox combo, string key, string fallback)
        {
            if (combo == null) return;
            string wanted = string.IsNullOrWhiteSpace(key) ? fallback : key;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                OptionItem item = combo.Items[i] as OptionItem;
                if (item != null && string.Equals(item.Key, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void AppendLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            BeginInvokeIfRequired(() =>
            {
                if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 250000);
                txtLog.AppendText(text + Environment.NewLine);
            });
        }

        private void SetStatus(string status) { BeginInvokeIfRequired(() => txtStatus.Announce(status)); }

        private void SetProgressStatus(string status, bool announce)
        {
            BeginInvokeIfRequired(() =>
            {
                if (announce) txtStatus.AnnounceProgress(status);
                else txtStatus.SetMessage(status);
            });
        }

        private void ResetProgressDisplay()
        {
            BeginInvokeIfRequired(() =>
            {
                progressBar.Value = 0;
                progressBar.AccessibleName = "目前項目下載進度 0%";
                lblProgressPercent.Text = "0%";
                lblProgressPercent.AccessibleName = "目前項目下載進度：0%";
            });
        }

        private void UpdateProgressDisplay(DownloadProgress progress, string status, bool announce)
        {
            if (progress == null) return;
            int percent = Math.Max(0, Math.Min(100, progress.Percent));
            string percentText = string.IsNullOrWhiteSpace(progress.PercentText)
                ? percent + "%"
                : progress.PercentText.Trim();

            progressBar.Value = percent;
            progressBar.AccessibleName = "目前項目下載進度 " + percentText;
            lblProgressPercent.Text = percentText;
            lblProgressPercent.AccessibleName = "目前項目下載進度：" + percentText;
            SetProgressStatus(status, announce);
        }

        private void CompleteProgressDisplay()
        {
            BeginInvokeIfRequired(() =>
            {
                progressBar.Value = 100;
                progressBar.AccessibleName = "目前項目下載進度 100%";
                lblProgressPercent.Text = "100%";
                lblProgressPercent.AccessibleName = "目前項目下載進度：100%";
            });
        }

        private void FocusUrlEditor()
        {
            if (tabs != null) tabs.SelectedIndex = 0;
            if (txtUrl == null) return;
            txtUrl.Focus();
            txtUrl.SelectAll();
        }

        private void FocusCurrentStatus()
        {
            if (tabs != null) tabs.SelectedIndex = 0;
            if (txtStatus == null) return;
            txtStatus.Focus();
            txtStatus.SelectAll();
        }

        private void SwitchToTab(int index)
        {
            if (tabs == null || index < 0 || index >= tabs.TabPages.Count) return;
            tabs.SelectedIndex = index;
            if (index == 0 && txtUrl != null) txtUrl.Focus();
            else if (index == 1 && cmbCookieSource != null) cmbCookieSource.Focus();
            else if (index == 2 && txtLog != null) txtLog.Focus();
        }

        private void BeginInvokeIfRequired(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action); else action();
        }

        private static Label CreateLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 5, 8, 5) };
        }

        private static ComboBox CreateDropDown(string accessibleName)
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = accessibleName, IntegralHeight = true };
        }

        private bool HandleApplicationShortcut(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.L))
            {
                FocusUrlEditor();
                return true;
            }

            if (keyData == (Keys.Control | Keys.Enter))
            {
                if (activeOperation == null) _ = DownloadAsync();
                return true;
            }

            if (keyData == (Keys.Control | Keys.J))
            {
                FocusCurrentStatus();
                return true;
            }

            if (keyData == (Keys.Control | Keys.Shift | Keys.O))
            {
                OpenDownloadFolder();
                return true;
            }

            if (keyData == (Keys.Control | Keys.D1) || keyData == (Keys.Control | Keys.NumPad1))
            {
                SwitchToTab(0);
                return true;
            }

            if (keyData == (Keys.Control | Keys.D2) || keyData == (Keys.Control | Keys.NumPad2))
            {
                SwitchToTab(1);
                return true;
            }

            if (keyData == (Keys.Control | Keys.D3) || keyData == (Keys.Control | Keys.NumPad3))
            {
                SwitchToTab(2);
                return true;
            }

            if (keyData == Keys.Escape && activeOperation != null)
            {
                CancelActiveOperation();
                return true;
            }

            return false;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (HandleApplicationShortcut(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (HandleApplicationShortcut(e.KeyData))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettingsFromUi();
            if (activeOperation != null) activeOperation.Cancel();
        }
    }
}
