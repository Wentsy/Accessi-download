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
                MaximumSize = new Size(780, 0),
                Text = "YouTube、Bilibili 或 Douyin 若需要登入或 Cookie，先在平常使用的瀏覽器開啟網站，再讓 Accessi-download 直接讀取該瀏覽器的 Cookie。程式不會要求你輸入帳號密碼。",
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
            txtCookieFile.Leave += (s, e) => SaveSettingsFromUi();
            btnBrowseCookie = new Button { Text = "選擇檔案", AutoSize = true, AccessibleName = "選擇 cookies.txt 檔案" };
            btnBrowseCookie.Click += (s, e) => BrowseCookieFile();
            layout.Controls.Add(txtCookieFile, 1, 2);
            layout.Controls.Add(btnBrowseCookie, 2, 2);

            var loginPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            var btnYouTubeLogin = new Button { Text = "開啟 YouTube 登入頁", AutoSize = true, AccessibleName = "在瀏覽器開啟 YouTube 登入頁" };
            var btnBilibiliLogin = new Button { Text = "開啟 Bilibili 登入頁", AutoSize = true, AccessibleName = "在瀏覽器開啟 Bilibili 登入頁" };
            var btnDouyinLogin = new Button { Text = "開啟 Douyin", AutoSize = true, AccessibleName = "在瀏覽器開啟抖音 Douyin" };
            btnYouTubeLogin.Click += (s, e) => OpenWebPage("https://accounts.google.com/ServiceLogin?service=youtube");
            btnBilibiliLogin.Click += (s, e) => OpenWebPage("https://passport.bilibili.com/login");
            btnDouyinLogin.Click += (s, e) => OpenWebPage("https://www.douyin.com/");
            loginPanel.Controls.Add(btnYouTubeLogin);
            loginPanel.Controls.Add(btnBilibiliLogin);
            loginPanel.Controls.Add(btnDouyinLogin);
            layout.Controls.Add(CreateLabel("登入頁："), 0, 3);
            layout.Controls.Add(loginPanel, 1, 3);
            layout.SetColumnSpan(loginPanel, 2);

            var cookieTip = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                MaximumSize = new Size(780, 0),
                Text = "提示：Douyin 可能需要新鮮的瀏覽器 Cookie；若讀取失敗，可以先在瀏覽器開啟抖音，再完全關閉瀏覽器後重試。也可改用 Netscape 格式的 cookies.txt。Cookie 檔案請自行妥善保管，不要上傳到公開位置。",
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
                MaximumSize = new Size(780, 0),
                Text = "Portable 版自帶 yt-dlp.exe、FFmpeg、FFprobe 與 Deno。FFmpeg 負責影音合併與轉檔；Deno 提供新版 yt-dlp 的 YouTube JavaScript runtime。更新 yt-dlp 時不會動到你的下載檔案。",
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
    }
}
