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
        private readonly AppSettings settings;
        private readonly YtDlpServiceV2 service;
        private CancellationTokenSource activeOperation;
        private MediaInfo currentMedia;
        private bool applyingSettings;

        private TextBox txtUrl;
        private Button btnAnalyze;
        private Label lblMediaInfo;
        private CheckBox chkDownloadPlaylist;
        private RadioButton rbVideo;
        private RadioButton rbAudio;
        private ComboBox cmbVideoQuality;
        private ComboBox cmbVideoContainer;
        private ComboBox cmbAudioSource;
        private ComboBox cmbAudioFormat;
        private ComboBox cmbAudioQuality;
        private CheckBox chkIncludeMediaId;
        private CheckBox chkOpenFolderAfterDownload;
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

        public MainFormV2()
        {
            settings = AppSettings.Load();
            service = new YtDlpServiceV2();
            Text = "Accessi-download";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 620);
            Size = new Size(940, 760);
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
    }
}
