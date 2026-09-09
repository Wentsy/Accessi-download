using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AccessiDownload
{
    internal sealed class MainForm : Form
    {
        private readonly AppSettings settings;
        private readonly YtDlpService service;
        private CancellationTokenSource activeOperation;
        private MediaInfo currentMedia;
        private TextBox txtUrl;
        private Button btnAnalyze;
        private Label lblMediaInfo;
        private RadioButton rbVideo;
        private RadioButton rbAudio;
        private ComboBox cmbVideoQuality;
        private ComboBox cmbVideoContainer;
        private ComboBox cmbAudioSource;
        private ComboBox cmbAudioFormat;
        private ComboBox cmbAudioQuality;
        private TextBox txtDownloadFolder;
        private Button btnBrowseFolder;
        private Button btnOpenFolder;
        private Button btnDownload;
        private Button btnCancel;
        private ProgressBar progressBar;
        private AccessibleStatusTextBox txtStatus;
        private ComboBox cmbCookieSource;
        private TextBox txtCookieFile;
        private Button btnBrowseCookie;
        private ComboBox cmbUpdateChannel;
        private CheckBox chkPromptUpdate;
        private Label lblYtDlpVersion;
        private Button btnUpdate;
        private Button btnCheckComponents;
        private TextBox txtLog;

        public MainForm()
        {
            settings = AppSettings.Load();
            service = new YtDlpService();
            Text = "Accessi-download";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 580);
            Size = new Size(900, 680);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;
            BuildInterface();
            ApplySettingsToUi();
            UpdateDownloadModeControls();
            KeyDown += MainForm_KeyDown;
            FormClosing += MainForm_FormClosing;
            Shown += MainForm_Shown;
        }

        private void BuildInterface()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill, AccessibleName = "Accessi-download 功能分頁" };
            var downloadTab = new TabPage("下載");
            var authTab = new TabPage("登入與更新");
            var logTab = new TabPage("記錄");
            tabs.TabPages.Add(downloadTab);
            tabs.TabPages.Add(authTab);
            tabs.TabPages.Add(logTab);
            BuildDownloadTab(downloadTab);
            BuildAuthTab(authTab);
            BuildLogTab(logTab);
            Controls.Add(tabs);
        }

        private void BuildDownloadTab(TabPage tab)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(14),
                ColumnCount = 3,
                RowCount = 12
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lblUrl = CreateLabel("影片網址 (&U)：");
            txtUrl = new TextBox
            {
                Dock = DockStyle.Fill,
                AccessibleName = "影片網址",
                AccessibleDescription = "貼上 YouTube、Bilibili 或其他 yt-dlp 支援的影片網址。"
            };
            btnAnalyze = new Button { Text = "解析 (&A)", AutoSize = true, AccessibleName = "解析影片資訊" };
            btnAnalyze.Click += async (s, e) => await AnalyzeAsync();
            layout.Controls.Add(lblUrl, 0, 0);
            layout.Controls.Add(txtUrl, 1, 0);
            layout.Controls.Add(btnAnalyze, 2, 0);

            lblMediaInfo = new Label
            {
                Text = "尚未解析影片。貼上網址後按「解析」。",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 7, 0, 7),
                AccessibleName = "影片資訊"
            };
            layout.Controls.Add(lblMediaInfo, 1, 1);
            layout.SetColumnSpan(lblMediaInfo, 2);

            var lblType = CreateLabel("下載類型：");
            var modePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            rbVideo = new RadioButton { Text = "影片 (&V)", Checked = true, AutoSize = true, AccessibleName = "下載影片" };
            rbAudio = new RadioButton { Text = "只下載音訊 (&O)", AutoSize = true, AccessibleName = "只下載音訊" };
            rbVideo.CheckedChanged += (s, e) => UpdateDownloadModeControls();
            rbAudio.CheckedChanged += (s, e) => UpdateDownloadModeControls();
            modePanel.Controls.Add(rbVideo);
            modePanel.Controls.Add(rbAudio);
            layout.Controls.Add(lblType, 0, 2);
            layout.Controls.Add(modePanel, 1, 2);
            layout.SetColumnSpan(modePanel, 2);

            layout.Controls.Add(CreateLabel("影片畫質："), 0, 3);
            cmbVideoQuality = CreateDropDown("影片畫質");
            cmbVideoQuality.Items.Add(new OptionItem { Key = "auto", Display = "自動（最佳可用畫質）" });
            cmbVideoQuality.SelectedIndex = 0;
            layout.Controls.Add(cmbVideoQuality, 1, 3);
            layout.SetColumnSpan(cmbVideoQuality, 2);

            layout.Controls.Add(CreateLabel("影片格式："), 0, 4);
            cmbVideoContainer = CreateDropDown("影片輸出格式");
            cmbVideoContainer.Items.AddRange(new object[]
            {
                new OptionItem { Key = "mp4", Display = "MP4（推薦，相容性優先）" },
                new OptionItem { Key = "mkv", Display = "MKV（保留來源編碼較彈性）" },
                new OptionItem { Key = "webm", Display = "WebM" },
                new OptionItem { Key = "mov", Display = "MOV" },
                new OptionItem { Key = "auto", Display = "自動（由 yt-dlp 決定）" }
            });
            layout.Controls.Add(cmbVideoContainer, 1, 4);
            layout.SetColumnSpan(cmbVideoContainer, 2);

            layout.Controls.Add(CreateLabel("來源音質："), 0, 5);
            cmbAudioSource = CreateDropDown("來源音訊品質");
            cmbAudioSource.Items.Add(new AudioSourceChoice { FormatId = "bestaudio/best", Display = "自動（最佳可用音質）" });
            cmbAudioSource.SelectedIndex = 0;
            layout.Controls.Add(cmbAudioSource, 1, 5);
            layout.SetColumnSpan(cmbAudioSource, 2);

            layout.Controls.Add(CreateLabel("音訊格式："), 0, 6);
            cmbAudioFormat = CreateDropDown("音訊輸出格式");
            cmbAudioFormat.Items.AddRange(new object[]
            {
                new OptionItem { Key = "m4a", Display = "M4A（推薦）" },
                new OptionItem { Key = "mp3", Display = "MP3" },
                new OptionItem { Key = "aac", Display = "AAC" },
                new OptionItem { Key = "opus", Display = "Opus" },
                new OptionItem { Key = "vorbis", Display = "Vorbis / OGG" },
                new OptionItem { Key = "flac", Display = "FLAC（無損）" },
                new OptionItem { Key = "wav", Display = "WAV（無損、檔案較大）" },
                new OptionItem { Key = "alac", Display = "ALAC（Apple 無損）" },
                new OptionItem { Key = "best", Display = "保留最佳來源格式" }
            });
            cmbAudioFormat.SelectedIndexChanged += (s, e) => UpdateAudioQualityControl();
            layout.Controls.Add(cmbAudioFormat, 1, 6);
            layout.SetColumnSpan(cmbAudioFormat, 2);

            layout.Controls.Add(CreateLabel("輸出音質："), 0, 7);
            cmbAudioQuality = CreateDropDown("音訊輸出品質");
            cmbAudioQuality.Items.AddRange(new object[]
            {
                new OptionItem { Key = "best", Display = "最佳（不限制位元率）" },
                new OptionItem { Key = "320K", Display = "320 kbps" },
                new OptionItem { Key = "256K", Display = "256 kbps" },
                new OptionItem { Key = "192K", Display = "192 kbps" },
                new OptionItem { Key = "160K", Display = "160 kbps" },
                new OptionItem { Key = "128K", Display = "128 kbps" },
                new OptionItem { Key = "96K", Display = "96 kbps" },
                new OptionItem { Key = "64K", Display = "64 kbps" }
            });
            cmbAudioQuality.SelectedIndex = 0;
            layout.Controls.Add(cmbAudioQuality, 1, 7);
            layout.SetColumnSpan(cmbAudioQuality, 2);

            layout.Controls.Add(CreateLabel("儲存位置 (&F)："), 0, 8);
            txtDownloadFolder = new TextBox { Dock = DockStyle.Fill, AccessibleName = "下載後儲存位置" };
            btnBrowseFolder = new Button { Text = "瀏覽 (&B)", AutoSize = true, AccessibleName = "選擇下載資料夾" };
            btnBrowseFolder.Click += (s, e) => BrowseDownloadFolder();
            layout.Controls.Add(txtDownloadFolder, 1, 8);
            layout.Controls.Add(btnBrowseFolder, 2, 8);

            var actionPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            btnDownload = new Button { Text = "開始下載 (&D)", AutoSize = true, AccessibleName = "開始下載" };
            btnCancel = new Button { Text = "取消 (&C)", AutoSize = true, Enabled = false, AccessibleName = "取消目前操作" };
            btnOpenFolder = new Button { Text = "開啟下載資料夾", AutoSize = true, AccessibleName = "開啟下載資料夾" };
            btnDownload.Click += async (s, e) => await DownloadAsync();
            btnCancel.Click += (s, e) => CancelActiveOperation();
            btnOpenFolder.Click += (s, e) => OpenDownloadFolder();
            actionPanel.Controls.Add(btnDownload);
            actionPanel.Controls.Add(btnCancel);
            actionPanel.Controls.Add(btnOpenFolder);
            layout.Controls.Add(CreateLabel("操作："), 0, 9);
            layout.Controls.Add(actionPanel, 1, 9);
            layout.SetColumnSpan(actionPanel, 2);

            progressBar = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, AccessibleName = "下載進度" };
            layout.Controls.Add(CreateLabel("進度："), 0, 10);
            layout.Controls.Add(progressBar, 1, 10);
            layout.SetColumnSpan(progressBar, 2);

            txtStatus = new AccessibleStatusTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                TabStop = true,
                Text = "就緒。",
                AccessibleName = "狀態：就緒。",
                AccessibleDescription = "顯示目前解析、下載或更新狀態。"
            };
            layout.Controls.Add(CreateLabel("狀態："), 0, 11);
            layout.Controls.Add(txtStatus, 1, 11);
            layout.SetColumnSpan(txtStatus, 2);
            for (int i = 0; i < layout.RowCount; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tab.Controls.Add(layout);
        }

        private void BuildAuthTab(TabPage tab)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(14),
                ColumnCount = 3,
                RowCount = 11
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                MaximumSize = new Size(760, 0),
                Text = "YouTube 或 Bilibili 若需要登入，最簡單的方式是先在平常使用的瀏覽器完成登入，再讓 Accessi-download 直接讀取該瀏覽器的 Cookie。程式不會要求你輸入帳號密碼。",
                AccessibleName = "登入說明"
            };
            layout.Controls.Add(intro, 0, 0);
            layout.SetColumnSpan(intro, 3);

            layout.Controls.Add(CreateLabel("Cookie 來源："), 0, 1);
            cmbCookieSource = CreateDropDown("Cookie 來源");
            cmbCookieSource.Items.AddRange(new object[]
            {
                new OptionItem { Key = "none", Display = "不使用登入資訊" },
                new OptionItem { Key = "edge", Display = "Microsoft Edge（從瀏覽器讀取）" },
                new OptionItem { Key = "chrome", Display = "Google Chrome（從瀏覽器讀取）" },
                new OptionItem { Key = "firefox", Display = "Mozilla Firefox（從瀏覽器讀取）" },
                new OptionItem { Key = "brave", Display = "Brave（從瀏覽器讀取）" },
                new OptionItem { Key = "opera", Display = "Opera（從瀏覽器讀取）" },
                new OptionItem { Key = "vivaldi", Display = "Vivaldi（從瀏覽器讀取）" },
                new OptionItem { Key = "chromium", Display = "Chromium（從瀏覽器讀取）" },
                new OptionItem { Key = "cookies", Display = "選擇 cookies.txt 檔案" }
            });
            cmbCookieSource.SelectedIndexChanged += (s, e) => { UpdateCookieControls(); SaveSettingsFromUi(); };
            layout.Controls.Add(cmbCookieSource, 1, 1);
            layout.SetColumnSpan(cmbCookieSource, 2);

            layout.Controls.Add(CreateLabel("cookies.txt："), 0, 2);
            txtCookieFile = new TextBox { Dock = DockStyle.Fill, AccessibleName = "cookies.txt 檔案路徑" };
            btnBrowseCookie = new Button { Text = "選擇檔案", AutoSize = true, AccessibleName = "選擇 cookies.txt 檔案" };
            btnBrowseCookie.Click += (s, e) => BrowseCookieFile();
            layout.Controls.Add(txtCookieFile, 1, 2);
            layout.Controls.Add(btnBrowseCookie, 2, 2);

            var loginPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            var btnYouTubeLogin = new Button { Text = "開啟 YouTube 登入頁", AutoSize = true, AccessibleName = "在瀏覽器開啟 YouTube 登入頁" };
            var btnBilibiliLogin = new Button { Text = "開啟 Bilibili 登入頁", AutoSize = true, AccessibleName = "在瀏覽器開啟 Bilibili 登入頁" };
            btnYouTubeLogin.Click += (s, e) => OpenWebPage("https://accounts.google.com/ServiceLogin?service=youtube");
            btnBilibiliLogin.Click += (s, e) => OpenWebPage("https://passport.bilibili.com/login");
            loginPanel.Controls.Add(btnYouTubeLogin);
            loginPanel.Controls.Add(btnBilibiliLogin);
            layout.Controls.Add(CreateLabel("登入頁："), 0, 3);
            layout.Controls.Add(loginPanel, 1, 3);
            layout.SetColumnSpan(loginPanel, 2);

            var cookieTip = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                MaximumSize = new Size(760, 0),
                Text = "提示：若瀏覽器 Cookie 讀取失敗，可以先完全關閉該瀏覽器後再解析；也可改用 Netscape 格式的 cookies.txt。Cookie 檔案請自行妥善保管，不要上傳到公開位置。",
                AccessibleName = "Cookie 使用提示"
            };
            layout.Controls.Add(cookieTip, 0, 4);
            layout.SetColumnSpan(cookieTip, 3);

            var separator = new Label { Text = "yt-dlp 與必要元件", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 12, 0, 4) };
            layout.Controls.Add(separator, 0, 5);
            layout.SetColumnSpan(separator, 3);

            layout.Controls.Add(CreateLabel("yt-dlp 版本："), 0, 6);
            lblYtDlpVersion = new Label { Text = "讀取中…", AutoSize = true, AccessibleName = "目前 yt-dlp 版本" };
            layout.Controls.Add(lblYtDlpVersion, 1, 6);
            layout.SetColumnSpan(lblYtDlpVersion, 2);

            layout.Controls.Add(CreateLabel("更新頻道："), 0, 7);
            cmbUpdateChannel = CreateDropDown("yt-dlp 更新頻道");
            cmbUpdateChannel.Items.AddRange(new object[]
            {
                new OptionItem { Key = "nightly", Display = "Nightly（推薦，一般使用者較快取得網站修正）" },
                new OptionItem { Key = "stable", Display = "Stable（穩定版）" },
                new OptionItem { Key = "master", Display = "Master（最新開發版，可能有回歸）" }
            });
            cmbUpdateChannel.SelectedIndexChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(cmbUpdateChannel, 1, 7);
            layout.SetColumnSpan(cmbUpdateChannel, 2);

            chkPromptUpdate = new CheckBox
            {
                Text = "每天第一次啟動時詢問是否檢查並安裝 yt-dlp 更新",
                AutoSize = true,
                AccessibleName = "每天第一次啟動時詢問 yt-dlp 更新"
            };
            chkPromptUpdate.CheckedChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(chkPromptUpdate, 1, 8);
            layout.SetColumnSpan(chkPromptUpdate, 2);

            var updatePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            btnUpdate = new Button { Text = "立即更新 yt-dlp", AutoSize = true, AccessibleName = "立即檢查並更新 yt-dlp" };
            btnCheckComponents = new Button { Text = "檢查必要元件", AutoSize = true, AccessibleName = "檢查 yt-dlp、FFmpeg、FFprobe 與 Deno" };
            btnUpdate.Click += async (s, e) => await UpdateYtDlpAsync();
            btnCheckComponents.Click += (s, e) =>
            {
                string summary = service.GetComponentSummary();
                SetStatus(summary);
                MessageBox.Show(this, summary, "元件檢查", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            updatePanel.Controls.Add(btnUpdate);
            updatePanel.Controls.Add(btnCheckComponents);
            layout.Controls.Add(CreateLabel("維護："), 0, 9);
            layout.Controls.Add(updatePanel, 1, 9);
            layout.SetColumnSpan(updatePanel, 2);

            var componentTip = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                MaximumSize = new Size(760, 0),
                Text = "Portable 版會自帶 yt-dlp.exe、FFmpeg、FFprobe 與 Deno。FFmpeg 負責影音合併與轉檔；Deno 是新版 yt-dlp 完整解析 YouTube 所需的 JavaScript runtime。更新 yt-dlp 時不會動到你的下載檔案。",
                AccessibleName = "必要元件說明"
            };
            layout.Controls.Add(componentTip, 0, 10);
            layout.SetColumnSpan(componentTip, 3);
            for (int i = 0; i < layout.RowCount; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tab.Controls.Add(layout);
        }

        private void BuildLogTab(TabPage tab)
        {
            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9F),
                AccessibleName = "下載與更新記錄",
                AccessibleDescription = "顯示 yt-dlp 的執行記錄，遇到錯誤時可複製這裡的內容回報。"
            };
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 2 };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            var btnCopy = new Button { Text = "複製全部", AutoSize = true, AccessibleName = "複製全部記錄" };
            var btnClear = new Button { Text = "清除記錄", AutoSize = true, AccessibleName = "清除記錄" };
            btnCopy.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(txtLog.Text))
                {
                    Clipboard.SetText(txtLog.Text);
                    SetStatus("已複製記錄到剪貼簿。");
                }
            };
            btnClear.Click += (s, e) => txtLog.Clear();
            buttons.Controls.Add(btnCopy);
            buttons.Controls.Add(btnClear);
            panel.Controls.Add(txtLog, 0, 0);
            panel.Controls.Add(buttons, 0, 1);
            tab.Controls.Add(panel);
        }

        private async void MainForm_Shown(object sender, EventArgs e)
        {
            SetStatus(service.GetComponentSummary());
            await RefreshVersionAsync();
            if (settings.PromptUpdateOnStart)
            {
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                if (!string.Equals(settings.LastUpdatePromptDate, today, StringComparison.Ordinal))
                {
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
            }
        }

        private void ApplySettingsToUi()
        {
            txtDownloadFolder.Text = settings.DownloadFolder;
            SelectByKey(cmbVideoContainer, settings.VideoContainer, "mp4");
            SelectByKey(cmbAudioFormat, settings.AudioOutputFormat, "m4a");
            SelectByKey(cmbCookieSource, settings.CookieSource, "none");
            txtCookieFile.Text = settings.CookieFile;
            SelectByKey(cmbUpdateChannel, settings.UpdateChannel, "nightly");
            chkPromptUpdate.Checked = settings.PromptUpdateOnStart;
            UpdateCookieControls();
            UpdateAudioQualityControl();
        }

        private void SaveSettingsFromUi()
        {
            if (txtDownloadFolder == null || cmbCookieSource == null) return;
            settings.DownloadFolder = txtDownloadFolder.Text.Trim();
            settings.CookieSource = GetSelectedOptionKey(cmbCookieSource, "none");
            settings.CookieFile = txtCookieFile.Text.Trim();
            settings.UpdateChannel = GetSelectedOptionKey(cmbUpdateChannel, "nightly");
            settings.PromptUpdateOnStart = chkPromptUpdate.Checked;
            settings.VideoContainer = GetSelectedOptionKey(cmbVideoContainer, "mp4");
            settings.AudioOutputFormat = GetSelectedOptionKey(cmbAudioFormat, "m4a");
            settings.Save();
        }

        private async Task AnalyzeAsync()
        {
            string url = txtUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(this, "請先貼上影片網址。", "缺少網址", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            BeginOperation("正在解析影片資訊…");
            try
            {
                currentMedia = await service.AnalyzeAsync(url, settings, activeOperation.Token);
                PopulateMediaChoices(currentMedia);
                string duration = FormatDuration(currentMedia.DurationSeconds);
                lblMediaInfo.Text =
                    (string.IsNullOrWhiteSpace(currentMedia.Title) ? "未取得標題" : currentMedia.Title) +
                    (string.IsNullOrWhiteSpace(currentMedia.Uploader) ? string.Empty : "；上傳者：" + currentMedia.Uploader) +
                    (string.IsNullOrWhiteSpace(duration) ? string.Empty : "；長度：" + duration);
                lblMediaInfo.AccessibleName = "影片資訊：" + lblMediaInfo.Text;
                SetStatus("解析完成。請選擇畫質、音質與輸出格式後開始下載。");
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
            string url = txtUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(this, "請先貼上影片網址。", "缺少網址", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                Url = url,
                DownloadFolder = folder,
                AudioOnly = rbAudio.Checked,
                MaxVideoHeight = GetSelectedVideoHeight(),
                VideoContainer = GetSelectedOptionKey(cmbVideoContainer, "mp4"),
                AudioSourceFormatId = GetSelectedAudioFormatId(),
                AudioOutputFormat = GetSelectedOptionKey(cmbAudioFormat, "m4a"),
                AudioOutputQuality = GetSelectedOptionKey(cmbAudioQuality, "best"),
                Settings = settings
            };
            BeginOperation(request.AudioOnly ? "開始下載音訊…" : "開始下載影片…");
            progressBar.Value = 0;
            try
            {
                DownloadResult result = await service.DownloadAsync(
                    request,
                    progress => BeginInvokeIfRequired(() =>
                    {
                        progressBar.Value = Math.Max(0, Math.Min(100, progress.Percent));
                        string status = "下載中 " + (string.IsNullOrWhiteSpace(progress.PercentText) ? progress.Percent + "%" : progress.PercentText);
                        if (!string.IsNullOrWhiteSpace(progress.SpeedText)) status += "，速度 " + progress.SpeedText;
                        if (!string.IsNullOrWhiteSpace(progress.EtaText) && !string.Equals(progress.EtaText, "NA", StringComparison.OrdinalIgnoreCase)) status += "，預估剩餘 " + progress.EtaText;
                        SetStatus(status);
                    }),
                    AppendLog,
                    activeOperation.Token);
                progressBar.Value = 100;
                string completed = string.IsNullOrWhiteSpace(result.FinalPath) ? "下載完成。" : "下載完成：" + result.FinalPath;
                SetStatus(completed);
                DialogResult open = MessageBox.Show(this, completed + "\r\n\r\n要開啟下載資料夾嗎？", "下載完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
                if (open == DialogResult.Yes) OpenDownloadFolder();
            }
            catch (OperationCanceledException) { SetStatus("已取消下載。尚未完成的暫存檔可能會由 yt-dlp 留在下載資料夾中。"); }
            catch (Exception ex)
            {
                AppendLog("下載錯誤：" + ex);
                SetStatus("下載失敗：" + ex.Message);
                MessageBox.Show(this, ex.Message + "\r\n\r\n詳細資訊可在「記錄」分頁查看。", "下載失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndOperation(); }
        }

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
            cmbVideoQuality.BeginUpdate();
            try
            {
                cmbVideoQuality.Items.Clear();
                cmbVideoQuality.Items.Add(new OptionItem { Key = "auto", Display = "自動（最佳可用畫質）" });
                foreach (int height in media.VideoHeights) cmbVideoQuality.Items.Add(new OptionItem { Key = height.ToString(), Display = height + "p 或以下" });
                cmbVideoQuality.SelectedIndex = 0;
            }
            finally { cmbVideoQuality.EndUpdate(); }

            cmbAudioSource.BeginUpdate();
            try
            {
                cmbAudioSource.Items.Clear();
                foreach (AudioSourceChoice choice in media.AudioSources) cmbAudioSource.Items.Add(choice);
                if (cmbAudioSource.Items.Count == 0) cmbAudioSource.Items.Add(new AudioSourceChoice { FormatId = "bestaudio/best", Display = "自動（最佳可用音質）" });
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

        private void UpdateDownloadModeControls()
        {
            bool video = rbVideo.Checked;
            cmbVideoQuality.Enabled = video;
            cmbVideoContainer.Enabled = video;
            cmbAudioSource.Enabled = !video;
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

        private void OpenDownloadFolder()
        {
            try
            {
                string folder = txtDownloadFolder.Text.Trim();
                if (string.IsNullOrWhiteSpace(folder)) return;
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "無法開啟資料夾", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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

        private string GetSelectedAudioFormatId()
        {
            AudioSourceChoice item = cmbAudioSource.SelectedItem as AudioSourceChoice;
            return item == null || string.IsNullOrWhiteSpace(item.FormatId) ? "bestaudio/best" : item.FormatId;
        }

        private static string GetSelectedOptionKey(ComboBox combo, string fallback)
        {
            if (combo == null) return fallback;
            OptionItem item = combo.SelectedItem as OptionItem;
            return item == null || string.IsNullOrWhiteSpace(item.Key) ? fallback : item.Key;
        }

        private static void SelectByKey(ComboBox combo, string key, string fallback)
        {
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

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return string.Empty;
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            if (span.TotalHours >= 1) return ((int)span.TotalHours) + " 小時 " + span.Minutes + " 分 " + span.Seconds + " 秒";
            return span.Minutes + " 分 " + span.Seconds + " 秒";
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

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5 && activeOperation == null)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _ = AnalyzeAsync();
            }
            else if (e.KeyCode == Keys.Escape && activeOperation != null)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CancelActiveOperation();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettingsFromUi();
            if (activeOperation != null) activeOperation.Cancel();
        }
    }
}
