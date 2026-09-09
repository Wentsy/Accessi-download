using System;
using System.Drawing;
using System.Windows.Forms;

namespace AccessiDownload
{
    internal sealed partial class MainFormV2 : Form
    {
        private void BuildDownloadTab(TabPage tab)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(14),
                ColumnCount = 3,
                RowCount = 14
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lblUrl = CreateLabel("網址（可一行一個） (&U)：");
            txtUrl = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                MinimumSize = new Size(0, 64),
                AccessibleName = "影片網址，可一行一個網址",
                AccessibleDescription = "可貼上單一網址，或每行貼一個 YouTube、Bilibili 或其他 yt-dlp 支援網址。下載成功後會自動清空；下載失敗或取消時會保留網址方便重試。"
            };
            layout.Controls.Add(lblUrl, 0, 0);
            layout.Controls.Add(txtUrl, 1, 0);
            layout.SetColumnSpan(txtUrl, 2);

            var tip = new Label
            {
                Text = "可直接開始下載，不需要先取得影片資訊。畫質選項代表上限，yt-dlp 會自動挑選符合條件的最佳來源。",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 4, 0, 6),
                AccessibleName = "下載說明：可直接開始下載，不需要先取得影片資訊。"
            };
            layout.Controls.Add(tip, 1, 1);
            layout.SetColumnSpan(tip, 2);

            chkDownloadPlaylist = new CheckBox
            {
                Text = "若網址是播放清單或合集，下載全部項目並以播放清單／合集名稱建立資料夾",
                AutoSize = true,
                AccessibleName = "下載整個播放清單或合集，並建立同名資料夾"
            };
            chkDownloadPlaylist.CheckedChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(CreateLabel("下載範圍："), 0, 2);
            layout.Controls.Add(chkDownloadPlaylist, 1, 2);
            layout.SetColumnSpan(chkDownloadPlaylist, 2);

            var modePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            rbVideo = new RadioButton { Text = "影片 (&V)", Checked = true, AutoSize = true, AccessibleName = "下載影片" };
            rbAudio = new RadioButton { Text = "只下載音訊 (&O)", AutoSize = true, AccessibleName = "只下載音訊" };
            rbVideo.CheckedChanged += (s, e) => { UpdateDownloadModeControls(); SaveSettingsFromUi(); };
            rbAudio.CheckedChanged += (s, e) => { UpdateDownloadModeControls(); SaveSettingsFromUi(); };
            modePanel.Controls.Add(rbVideo);
            modePanel.Controls.Add(rbAudio);
            layout.Controls.Add(CreateLabel("下載類型："), 0, 3);
            layout.Controls.Add(modePanel, 1, 3);
            layout.SetColumnSpan(modePanel, 2);

            layout.Controls.Add(CreateLabel("影片畫質："), 0, 4);
            cmbVideoQuality = CreateDropDown("影片畫質上限");
            cmbVideoQuality.Items.AddRange(new object[]
            {
                new OptionItem { Key = "auto", Display = "自動（最佳可用畫質）" },
                new OptionItem { Key = "4320", Display = "最高 4320p / 8K" },
                new OptionItem { Key = "2160", Display = "最高 2160p / 4K" },
                new OptionItem { Key = "1440", Display = "最高 1440p" },
                new OptionItem { Key = "1080", Display = "最高 1080p" },
                new OptionItem { Key = "720", Display = "最高 720p" },
                new OptionItem { Key = "480", Display = "最高 480p" },
                new OptionItem { Key = "360", Display = "最高 360p" },
                new OptionItem { Key = "240", Display = "最高 240p" },
                new OptionItem { Key = "144", Display = "最高 144p" }
            });
            cmbVideoQuality.SelectedIndex = 0;
            cmbVideoQuality.SelectedIndexChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(cmbVideoQuality, 1, 4);
            layout.SetColumnSpan(cmbVideoQuality, 2);

            layout.Controls.Add(CreateLabel("影片格式："), 0, 5);
            cmbVideoContainer = CreateDropDown("影片輸出格式");
            cmbVideoContainer.Items.AddRange(new object[]
            {
                new OptionItem { Key = "mp4", Display = "MP4（推薦，相容性優先）" },
                new OptionItem { Key = "mkv", Display = "MKV（保留來源編碼較彈性）" },
                new OptionItem { Key = "webm", Display = "WebM" },
                new OptionItem { Key = "mov", Display = "MOV" },
                new OptionItem { Key = "auto", Display = "自動（由 yt-dlp 決定）" }
            });
            cmbVideoContainer.SelectedIndexChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(cmbVideoContainer, 1, 5);
            layout.SetColumnSpan(cmbVideoContainer, 2);

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
            cmbAudioFormat.SelectedIndexChanged += (s, e) => { UpdateAudioQualityControl(); SaveSettingsFromUi(); };
            layout.Controls.Add(cmbAudioFormat, 1, 6);
            layout.SetColumnSpan(cmbAudioFormat, 2);

            layout.Controls.Add(CreateLabel("音訊品質："), 0, 7);
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
            cmbAudioQuality.SelectedIndexChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(cmbAudioQuality, 1, 7);
            layout.SetColumnSpan(cmbAudioQuality, 2);

            chkIncludeMediaId = new CheckBox
            {
                Text = "檔名加入影片 ID，例如 [BV1rHYx6fEzy]",
                AutoSize = true,
                AccessibleName = "檔名加入影片 ID，預設關閉"
            };
            chkIncludeMediaId.CheckedChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(CreateLabel("檔名："), 0, 8);
            layout.Controls.Add(chkIncludeMediaId, 1, 8);
            layout.SetColumnSpan(chkIncludeMediaId, 2);

            chkOpenFolderAfterDownload = new CheckBox
            {
                Text = "下載完成後自動開啟資料夾",
                AutoSize = true,
                AccessibleName = "下載完成後自動開啟資料夾"
            };
            chkOpenFolderAfterDownload.CheckedChanged += (s, e) => SaveSettingsFromUi();
            layout.Controls.Add(CreateLabel("完成後："), 0, 9);
            layout.Controls.Add(chkOpenFolderAfterDownload, 1, 9);
            layout.SetColumnSpan(chkOpenFolderAfterDownload, 2);

            layout.Controls.Add(CreateLabel("儲存位置 (&F)："), 0, 10);
            txtDownloadFolder = new TextBox { Dock = DockStyle.Fill, AccessibleName = "下載後儲存位置" };
            txtDownloadFolder.Leave += (s, e) => SaveSettingsFromUi();
            btnBrowseFolder = new Button { Text = "瀏覽 (&B)", AutoSize = true, AccessibleName = "選擇下載資料夾" };
            btnBrowseFolder.Click += (s, e) => BrowseDownloadFolder();
            layout.Controls.Add(txtDownloadFolder, 1, 10);
            layout.Controls.Add(btnBrowseFolder, 2, 10);

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
            layout.Controls.Add(CreateLabel("操作："), 0, 11);
            layout.Controls.Add(actionPanel, 1, 11);
            layout.SetColumnSpan(actionPanel, 2);

            progressBar = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, AccessibleName = "目前項目下載進度" };
            layout.Controls.Add(CreateLabel("進度："), 0, 12);
            layout.Controls.Add(progressBar, 1, 12);
            layout.SetColumnSpan(progressBar, 2);

            txtStatus = new AccessibleStatusTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                TabStop = true,
                Text = "就緒。",
                AccessibleName = "狀態：就緒。",
                AccessibleDescription = "顯示目前下載、取消或更新狀態。"
            };
            layout.Controls.Add(CreateLabel("狀態："), 0, 13);
            layout.Controls.Add(txtStatus, 1, 13);
            layout.SetColumnSpan(txtStatus, 2);

            for (int i = 0; i < layout.RowCount; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tab.Controls.Add(layout);
        }
    }
}
