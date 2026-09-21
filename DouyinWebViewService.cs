using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AccessiDownload
{
    /// <summary>
    /// Douyin-specific fallback that lets the real Douyin web page generate its own
    /// anti-bot signatures. We observe the page's aweme/detail response and then
    /// download the signed media URL directly. This intentionally does not replace
    /// yt-dlp for other sites.
    /// </summary>
    internal sealed class DouyinWebViewService
    {
        private static readonly Regex VideoIdRegex = new Regex(
            @"(?:/video/|[?&](?:modal_id|aweme_id)=)(\d{10,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private string FfmpegPath { get { return Path.Combine(baseDirectory, "tools", "ffmpeg.exe"); } }

        public static bool CanHandleUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return false;
            string host = uri.Host ?? string.Empty;
            return host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<DownloadResult> DownloadAsync(
            IWin32Window owner,
            DownloadRequest request,
            Action<DownloadProgress> progress,
            Action<string> log,
            CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            List<string> urls = request.Urls == null
                ? new List<string>()
                : request.Urls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (urls.Count == 0 && !string.IsNullOrWhiteSpace(request.Url)) urls.Add(request.Url);
            if (urls.Count == 0) throw new ArgumentException("缺少抖音網址。");
            if (urls.Any(url => !CanHandleUrl(url)))
                throw new InvalidOperationException("抖音網頁解析模式只能處理 douyin.com 網址。");
            if (request.DownloadPlaylist)
                throw new InvalidOperationException("目前抖音網頁解析測試版先支援單支影片或分享短網址；合集稍後再接上。");
            if (!File.Exists(FfmpegPath) && request.AudioOnly)
                throw new FileNotFoundException("只下載音訊需要 tools\\ffmpeg.exe。", FfmpegPath);

            Directory.CreateDirectory(request.DownloadFolder);

            using (var bridge = new DouyinBridgeForm())
            {
                bridge.Show(owner);
                await bridge.InitializeAsync(token);

                var result = new DownloadResult();
                for (int i = 0; i < urls.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int itemIndex = i + 1;
                    log?.Invoke("抖音網頁解析：正在開啟第 " + itemIndex + "/" + urls.Count + " 個網址。");

                    DouyinResolvedMedia media = await ResolveWithRetryAsync(
                        bridge, urls[i], request.MaxVideoHeight, log, token);

                    log?.Invoke("抖音網頁解析成功：" + media.Id
                        + (media.Height > 0 ? "，" + media.Height + "p" : string.Empty));

                    string finalPath = await DownloadResolvedMediaAsync(
                        media, request, itemIndex, urls.Count, progress, log, token);
                    result.FinalPaths.Add(finalPath);
                }

                return result;
            }
        }

        private async Task<DouyinResolvedMedia> ResolveWithRetryAsync(
            DouyinBridgeForm bridge,
            string url,
            int? maxHeight,
            Action<string> log,
            CancellationToken token)
        {
            Exception lastError = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    return await bridge.ResolveAsync(url, maxHeight, token);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    lastError = ex;
                    log?.Invoke("抖音網頁解析第 " + attempt + " 次未成功：" + ex.Message);
                    if (attempt < 2) await Task.Delay(1500, token);
                }
            }

            bool retry = await bridge.PromptForVerificationAsync(
                "抖音目前要求網頁驗證。請在下方抖音頁面完成必要的驗證或登入；完成後按「重新嘗試」。",
                token);
            if (!retry)
                throw new InvalidOperationException("抖音網頁驗證尚未完成，已取消解析。", lastError);

            try
            {
                return await bridge.ResolveAsync(url, maxHeight, token);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new InvalidOperationException(
                    "抖音網頁仍無法取得影片資料。可能是抖音再次更改風控；網址已保留，可稍後重試。", ex);
            }
        }

        private async Task<string> DownloadResolvedMediaAsync(
            DouyinResolvedMedia media,
            DownloadRequest request,
            int itemIndex,
            int itemTotal,
            Action<DownloadProgress> progress,
            Action<string> log,
            CancellationToken token)
        {
            string title = SanitizeFileName(string.IsNullOrWhiteSpace(media.Title)
                ? "Douyin_" + media.Id
                : media.Title);
            if (request.IncludeMediaId) title += " [" + media.Id + "]";

            string tempPath = Path.Combine(request.DownloadFolder, "." + Guid.NewGuid().ToString("N") + ".douyin.mp4");
            try
            {
                await DownloadFileAsync(
                    media.VideoUrl,
                    tempPath,
                    itemIndex,
                    itemTotal,
                    progress,
                    token);

                if (request.AudioOnly)
                {
                    string format = NormalizeAudioFormat(request.AudioOutputFormat);
                    string ext = AudioExtension(format);
                    string output = GetUniquePath(request.DownloadFolder, title, ext);
                    log?.Invoke("正在從抖音影片提取音訊：" + Path.GetFileName(output));
                    await ConvertAudioAsync(tempPath, output, format, request.AudioOutputQuality, token);
                    TryDelete(tempPath);
                    return output;
                }

                string container = (request.VideoContainer ?? "mp4").Trim().ToLowerInvariant();
                if (container == "auto") container = "mp4";
                if (container == "mp4")
                {
                    string output = GetUniquePath(request.DownloadFolder, title, ".mp4");
                    File.Move(tempPath, output);
                    return output;
                }

                string remuxOutput = GetUniquePath(request.DownloadFolder, title, "." + container);
                log?.Invoke("正在轉換抖音影片容器：" + container.ToUpperInvariant());
                await RemuxVideoAsync(tempPath, remuxOutput, container, token);
                TryDelete(tempPath);
                return remuxOutput;
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        private static async Task DownloadFileAsync(
            string url,
            string destination,
            int itemIndex,
            int itemTotal,
            Action<DownloadProgress> progress,
            CancellationToken token)
        {
            HttpWebRequest request = WebRequest.CreateHttp(url);
            request.Method = "GET";
            request.AllowAutoRedirect = true;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";
            request.Referer = "https://www.douyin.com/";
            request.Accept = "*/*";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;

            using (token.Register(() => { try { request.Abort(); } catch { } }))
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                long total = response.ContentLength;
                long received = 0;
                byte[] buffer = new byte[1024 * 1024];
                var watch = Stopwatch.StartNew();
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read, token);
                    received += read;

                    int percent = total > 0
                        ? Math.Max(0, Math.Min(100, (int)(received * 100L / total)))
                        : 0;
                    double seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
                    double bytesPerSecond = received / seconds;
                    string speed = FormatSpeed(bytesPerSecond);
                    string eta = string.Empty;
                    if (total > received && bytesPerSecond > 1)
                    {
                        TimeSpan remaining = TimeSpan.FromSeconds((total - received) / bytesPerSecond);
                        eta = remaining.TotalHours >= 1
                            ? remaining.ToString(@"hh\:mm\:ss")
                            : remaining.ToString(@"mm\:ss");
                    }

                    progress?.Invoke(new DownloadProgress
                    {
                        Percent = percent,
                        PercentText = percent + "%",
                        SpeedText = speed,
                        EtaText = eta,
                        ItemIndex = itemIndex,
                        ItemTotal = itemTotal
                    });
                }
            }

            progress?.Invoke(new DownloadProgress
            {
                Percent = 100,
                PercentText = "100%",
                ItemIndex = itemIndex,
                ItemTotal = itemTotal
            });
        }

        private async Task ConvertAudioAsync(
            string input,
            string output,
            string format,
            string quality,
            CancellationToken token)
        {
            var args = new List<string> { "-y", "-i", input, "-vn" };
            switch (format)
            {
                case "mp3": args.AddRange(new[] { "-c:a", "libmp3lame" }); break;
                case "aac": args.AddRange(new[] { "-c:a", "aac" }); break;
                case "opus": args.AddRange(new[] { "-c:a", "libopus" }); break;
                case "vorbis": args.AddRange(new[] { "-c:a", "libvorbis" }); break;
                case "flac": args.AddRange(new[] { "-c:a", "flac" }); break;
                case "wav": args.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
                case "alac": args.AddRange(new[] { "-c:a", "alac" }); break;
                default: args.AddRange(new[] { "-c:a", "aac" }); break;
            }

            if (format != "flac" && format != "wav" && format != "alac"
                && !string.IsNullOrWhiteSpace(quality)
                && !quality.Equals("best", StringComparison.OrdinalIgnoreCase))
            {
                string q = quality.Trim().ToLowerInvariant();
                if (Regex.IsMatch(q, @"^\d+k$")) args.AddRange(new[] { "-b:a", q });
            }

            args.Add(output);
            await RunProcessAsync(FfmpegPath, args, token);
        }

        private async Task RemuxVideoAsync(string input, string output, string container, CancellationToken token)
        {
            var args = new List<string> { "-y", "-i", input };
            if (container == "webm")
            {
                args.AddRange(new[] { "-c:v", "libvpx-vp9", "-c:a", "libopus" });
            }
            else
            {
                args.AddRange(new[] { "-c", "copy" });
            }
            args.Add(output);
            await RunProcessAsync(FfmpegPath, args, token);
        }

        private static async Task RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken token)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = string.Join(" ", args.Select(QuoteArgument)),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process.Start();
                using (token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                }))
                {
                    await Task.Run(() => process.WaitForExit(), token);
                    token.ThrowIfCancellationRequested();
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("ffmpeg 轉換失敗，結束碼：" + process.ExitCode);
            }
        }

        private static string NormalizeAudioFormat(string format)
        {
            string value = (format ?? "m4a").Trim().ToLowerInvariant();
            return value == "best" ? "m4a" : value;
        }

        private static string AudioExtension(string format)
        {
            if (format == "vorbis") return ".ogg";
            return "." + format;
        }

        private static string GetUniquePath(string folder, string title, string extension)
        {
            string path = Path.Combine(folder, title + extension);
            if (!File.Exists(path)) return path;
            for (int i = 2; i < 10000; i++)
            {
                path = Path.Combine(folder, title + " (" + i + ")" + extension);
                if (!File.Exists(path)) return path;
            }
            throw new IOException("找不到可用的輸出檔名。");
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Douyin";
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder();
            foreach (char c in value)
            {
                if (!invalid.Contains(c) && c >= 32) sb.Append(c);
            }
            string cleaned = sb.ToString().Trim().TrimEnd('.');
            if (cleaned.Length > 140) cleaned = cleaned.Substring(0, 140).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(cleaned) ? "Douyin" : cleaned;
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1024 * 1024)
                return (bytesPerSecond / (1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB/s";
            if (bytesPerSecond >= 1024)
                return (bytesPerSecond / 1024).ToString("0", CultureInfo.InvariantCulture) + " KiB/s";
            return bytesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " B/s";
        }

        private static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private sealed class DouyinResolvedMedia
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string VideoUrl { get; set; }
            public int Height { get; set; }
        }

        private sealed class DouyinBridgeForm : Form
        {
            private readonly WebView2 webView;
            private readonly Label instruction;
            private readonly Button retryButton;
            private readonly Button cancelButton;
            private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            private TaskCompletionSource<string> detailResponse;
            private TaskCompletionSource<bool> verificationChoice;
            private bool initialized;

            public DouyinBridgeForm()
            {
                Text = "Accessi-download 抖音網頁驗證";
                Width = 1000;
                Height = 760;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                ShowInTaskbar = false;
                Opacity = 0;

                instruction = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = false,
                    Height = 54,
                    Padding = new Padding(10),
                    Text = "正在使用抖音網頁解析影片…",
                    AccessibleName = "抖音網頁解析狀態"
                };

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 48,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(8)
                };
                retryButton = new Button
                {
                    Text = "完成驗證後重新嘗試 (&R)",
                    AutoSize = true,
                    AccessibleName = "完成抖音驗證後重新嘗試"
                };
                cancelButton = new Button
                {
                    Text = "取消 (&C)",
                    AutoSize = true,
                    AccessibleName = "取消抖音網頁解析"
                };
                retryButton.Click += (s, e) =>
                {
                    verificationChoice?.TrySetResult(true);
                };
                cancelButton.Click += (s, e) =>
                {
                    verificationChoice?.TrySetResult(false);
                };
                buttons.Controls.Add(retryButton);
                buttons.Controls.Add(cancelButton);

                webView = new WebView2
                {
                    Dock = DockStyle.Fill,
                    AccessibleName = "抖音網頁；若出現驗證，請在此完成"
                };

                Controls.Add(webView);
                Controls.Add(instruction);
                Controls.Add(buttons);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            public async Task InitializeAsync(CancellationToken token)
            {
                if (initialized) return;
                string userDataFolder = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "browser-data",
                    "DouyinWebView2");
                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment;
                try
                {
                    environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                    token.ThrowIfCancellationRequested();
                    await webView.EnsureCoreWebView2Async(environment);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "無法啟動 Microsoft Edge WebView2。請確認 Windows 的 Edge／WebView2 Runtime 可正常使用。", ex);
                }

                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;
                initialized = true;
            }

            private async void CoreWebView2_WebResourceResponseReceived(
                object sender,
                CoreWebView2WebResourceResponseReceivedEventArgs e)
            {
                TaskCompletionSource<string> target = detailResponse;
                if (target == null || target.Task.IsCompleted) return;
                string uri = e.Request == null ? string.Empty : e.Request.Uri;
                if (string.IsNullOrWhiteSpace(uri)
                    || uri.IndexOf("/aweme/v1/web/aweme/detail/", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                try
                {
                    using (Stream stream = await e.Response.GetContentAsync())
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        string text = await reader.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(text)
                            && text.IndexOf("\"aweme_detail\"", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            target.TrySetResult(text);
                        }
                    }
                }
                catch { }
            }

            public async Task<DouyinResolvedMedia> ResolveAsync(
                string url,
                int? maxHeight,
                CancellationToken token)
            {
                if (!initialized) throw new InvalidOperationException("抖音 WebView2 尚未初始化。");

                detailResponse = new TaskCompletionSource<string>();
                webView.CoreWebView2.Navigate(url);

                string jsonText = await WaitForPageDetailAsync(token);
                if (string.IsNullOrWhiteSpace(jsonText))
                {
                    string id = await WaitForVideoIdAsync(url, token);
                    if (string.IsNullOrWhiteSpace(id))
                        throw new InvalidOperationException("無法從抖音分享網址取得影片 ID。");
                    jsonText = await FetchDetailFromPageAsync(id, token);
                }

                DouyinResolvedMedia media = ParseDetail(jsonText, maxHeight);
                if (media == null || string.IsNullOrWhiteSpace(media.VideoUrl))
                    throw new InvalidOperationException("抖音網頁沒有回傳可下載的影片來源。");
                return media;
            }

            private async Task<string> WaitForPageDetailAsync(CancellationToken token)
            {
                Task detailTask = detailResponse.Task;
                Task timeout = Task.Delay(9000, token);
                Task completed = await Task.WhenAny(detailTask, timeout);
                token.ThrowIfCancellationRequested();
                return completed == detailTask ? await detailResponse.Task : null;
            }

            private async Task<string> WaitForVideoIdAsync(string originalUrl, CancellationToken token)
            {
                string id = ExtractVideoId(originalUrl);
                if (!string.IsNullOrWhiteSpace(id)) return id;

                for (int i = 0; i < 24; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string current = webView.Source == null ? string.Empty : webView.Source.AbsoluteUri;
                    id = ExtractVideoId(current);
                    if (!string.IsNullOrWhiteSpace(id)) return id;
                    await Task.Delay(500, token);
                }
                return null;
            }

            private async Task<string> FetchDetailFromPageAsync(string videoId, CancellationToken token)
            {
                string script =
                    "(async()=>{try{" +
                    "const r=await window.fetch('/aweme/v1/web/aweme/detail/?aweme_id=" + videoId + "',{credentials:'include'});" +
                    "const t=await r.text();return JSON.stringify({status:r.status,text:t});" +
                    "}catch(e){return JSON.stringify({status:0,text:'',error:String(e)});}})();";

                string raw = await webView.ExecuteScriptAsync(script);
                token.ThrowIfCancellationRequested();

                string inner;
                try { inner = json.Deserialize<string>(raw); }
                catch { inner = raw; }

                var wrapper = json.Deserialize<Dictionary<string, object>>(inner);
                int status = GetInt(wrapper, "status");
                string text = GetString(wrapper, "text");
                if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(text))
                    return text;

                throw new InvalidOperationException(
                    "抖音網頁 detail API 回應 " + status + "；可能需要完成網頁驗證。");
            }

            public async Task<bool> PromptForVerificationAsync(string message, CancellationToken token)
            {
                instruction.Text = message;
                instruction.AccessibleName = "抖音驗證：" + message;
                Opacity = 1;
                Location = new Point(
                    Math.Max(0, (Screen.PrimaryScreen.WorkingArea.Width - Width) / 2),
                    Math.Max(0, (Screen.PrimaryScreen.WorkingArea.Height - Height) / 2));
                WindowState = FormWindowState.Normal;
                BringToFront();
                Activate();

                verificationChoice = new TaskCompletionSource<bool>();
                retryButton.Focus();

                using (token.Register(() => verificationChoice.TrySetCanceled()))
                {
                    bool result = await verificationChoice.Task;
                    if (result)
                    {
                        instruction.Text = "正在重新嘗試抖音網頁解析…";
                        Opacity = 0;
                        Location = new Point(-32000, -32000);
                    }
                    return result;
                }
            }

            private DouyinResolvedMedia ParseDetail(string text, int? maxHeight)
            {
                var root = json.Deserialize<Dictionary<string, object>>(text);
                Dictionary<string, object> detail = GetDictionary(root, "aweme_detail");
                if (detail == null) return null;

                string id = GetString(detail, "aweme_id");
                string title = GetString(detail, "desc");
                Dictionary<string, object> video = GetDictionary(detail, "video");
                if (video == null) return null;

                var candidates = new List<VideoCandidate>();
                object bitRateObj;
                if (video.TryGetValue("bit_rate", out bitRateObj))
                {
                    object[] list = bitRateObj as object[];
                    if (list != null)
                    {
                        foreach (object item in list)
                        {
                            var bitrate = item as Dictionary<string, object>;
                            if (bitrate == null) continue;
                            Dictionary<string, object> play = GetDictionary(bitrate, "play_addr");
                            AddCandidate(candidates, play, GetInt(play, "height"), GetInt(bitrate, "bit_rate"));
                        }
                    }
                }

                Dictionary<string, object> direct = GetDictionary(video, "play_addr");
                AddCandidate(candidates, direct, GetInt(video, "height"), 0);
                AddCandidate(candidates, GetDictionary(video, "play_addr_h264"), GetInt(video, "height"), 0);
                AddCandidate(candidates, GetDictionary(video, "play_addr_265"), GetInt(video, "height"), 0);

                candidates = candidates
                    .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                IEnumerable<VideoCandidate> eligible = candidates;
                if (maxHeight.HasValue)
                {
                    List<VideoCandidate> limited = candidates
                        .Where(c => c.Height <= 0 || c.Height <= maxHeight.Value)
                        .ToList();
                    if (limited.Count > 0) eligible = limited;
                }

                VideoCandidate chosen = eligible
                    .OrderByDescending(c => c.Height)
                    .ThenByDescending(c => c.Bitrate)
                    .FirstOrDefault();
                if (chosen == null) return null;

                return new DouyinResolvedMedia
                {
                    Id = string.IsNullOrWhiteSpace(id) ? ExtractVideoId(webView.Source == null ? null : webView.Source.AbsoluteUri) : id,
                    Title = title,
                    VideoUrl = chosen.Url,
                    Height = chosen.Height
                };
            }

            private static void AddCandidate(
                ICollection<VideoCandidate> list,
                Dictionary<string, object> address,
                int height,
                int bitrate)
            {
                if (address == null) return;
                object urlsObj;
                if (!address.TryGetValue("url_list", out urlsObj)) return;
                object[] urls = urlsObj as object[];
                if (urls == null) return;
                string url = urls.Select(x => x as string)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x.StartsWith("http", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(url)) return;
                list.Add(new VideoCandidate { Url = url, Height = height, Bitrate = bitrate });
            }

            private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
            {
                if (source == null) return null;
                object value;
                return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            }

            private static string GetString(Dictionary<string, object> source, string key)
            {
                if (source == null) return string.Empty;
                object value;
                return source.TryGetValue(key, out value) && value != null
                    ? Convert.ToString(value, CultureInfo.InvariantCulture)
                    : string.Empty;
            }

            private static int GetInt(Dictionary<string, object> source, string key)
            {
                if (source == null) return 0;
                object value;
                if (!source.TryGetValue(key, out value) || value == null) return 0;
                int result;
                return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) ? result : 0;
            }

            private static string ExtractVideoId(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                Match match = VideoIdRegex.Match(url);
                return match.Success ? match.Groups[1].Value : null;
            }

            private sealed class VideoCandidate
            {
                public string Url { get; set; }
                public int Height { get; set; }
                public int Bitrate { get; set; }
            }
        }
    }
}
