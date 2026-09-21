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

        private static readonly Regex CollectionIdRegex = new Regex(
            @"/(?:collection|mix)/(\d{10,})",
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
                    string inputUrl = urls[i];
                    int inputIndex = i + 1;
                    log?.Invoke("抖音網頁解析：正在開啟第 " + inputIndex + "/" + urls.Count + " 個網址。");

                    if (request.DownloadPlaylist)
                    {
                        DouyinResolvedCollection collection = await bridge.TryResolveCollectionAsync(
                            inputUrl, request.MaxVideoHeight, log, token);
                        if (collection != null)
                        {
                            if (collection.Items.Count == 0)
                                throw new InvalidOperationException("抖音合集沒有找到可下載作品。");

                            string collectionFolderName = SanitizeFileName(
                                string.IsNullOrWhiteSpace(collection.Name)
                                    ? "抖音合集_" + collection.Id
                                    : collection.Name);
                            string collectionFolder = Path.Combine(request.DownloadFolder, collectionFolderName);
                            Directory.CreateDirectory(collectionFolder);

                            log?.Invoke("抖音合集解析完成：" + collectionFolderName
                                + "，共 " + collection.Items.Count + " 個作品。");

                            DownloadRequest collectionRequest = CloneRequestWithFolder(request, collectionFolder);
                            for (int j = 0; j < collection.Items.Count; j++)
                            {
                                token.ThrowIfCancellationRequested();
                                DouyinResolvedMedia media = collection.Items[j];
                                log?.Invoke("抖音合集：準備下載第 " + (j + 1) + "/"
                                    + collection.Items.Count + " 個作品"
                                    + (string.IsNullOrWhiteSpace(media.Id) ? "" : "，ID " + media.Id) + "。");

                                string finalPath = await DownloadResolvedMediaAsync(
                                    media,
                                    collectionRequest,
                                    j + 1,
                                    collection.Items.Count,
                                    progress,
                                    log,
                                    token);
                                result.FinalPaths.Add(finalPath);
                            }
                            continue;
                        }
                    }

                    DouyinResolvedMedia singleMedia = await ResolveWithRetryAsync(
                        bridge, inputUrl, request.MaxVideoHeight, log, token);

                    log?.Invoke("抖音網頁解析成功：" + singleMedia.Id
                        + (singleMedia.Height > 0 ? "，" + singleMedia.Height + "p" : string.Empty));

                    string singlePath = await DownloadResolvedMediaAsync(
                        singleMedia, request, inputIndex, urls.Count, progress, log, token);
                    result.FinalPaths.Add(singlePath);
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
                    return await bridge.ResolveAsync(url, maxHeight, log, token);
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
                return await bridge.ResolveAsync(url, maxHeight, log, token);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new InvalidOperationException(
                    "抖音網頁仍無法取得影片資料。可能是抖音再次更改風控；網址已保留，可稍後重試。", ex);
            }
        }

        private static DownloadRequest CloneRequestWithFolder(
            DownloadRequest source,
            string folder)
        {
            return new DownloadRequest
            {
                Url = source.Url,
                Urls = source.Urls == null ? new List<string>() : new List<string>(source.Urls),
                DownloadFolder = folder,
                AudioOnly = source.AudioOnly,
                MaxVideoHeight = source.MaxVideoHeight,
                VideoContainer = source.VideoContainer,
                AudioSourceFormatId = source.AudioSourceFormatId,
                AudioOutputFormat = source.AudioOutputFormat,
                AudioOutputQuality = source.AudioOutputQuality,
                IncludeMediaId = source.IncludeMediaId,
                DownloadPlaylist = source.DownloadPlaylist,
                Settings = source.Settings
            };
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

        private sealed class DouyinResolvedCollection
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public List<DouyinResolvedMedia> Items { get; set; } = new List<DouyinResolvedMedia>();
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

            public async Task<DouyinResolvedCollection> TryResolveCollectionAsync(
                string url,
                int? maxHeight,
                Action<string> log,
                CancellationToken token)
            {
                if (!initialized) throw new InvalidOperationException("抖音 WebView2 尚未初始化。");

                string mixId = ExtractCollectionId(url);
                if (string.IsNullOrWhiteSpace(mixId))
                {
                    await NavigateAndWaitReadyAsync(url, log, token);
                    for (int i = 0; i < 32; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        string current = webView.Source == null ? string.Empty : webView.Source.AbsoluteUri;
                        mixId = ExtractCollectionId(current);
                        if (!string.IsNullOrWhiteSpace(mixId)) break;

                        // Once a short link has clearly landed on an ordinary video,
                        // stop probing for a collection and let the single-video path run.
                        if (i >= 4 && !string.IsNullOrWhiteSpace(ExtractVideoId(current)))
                            return null;

                        await Task.Delay(250, token);
                    }
                }

                if (string.IsNullOrWhiteSpace(mixId)) return null;

                log?.Invoke("抖音合集：偵測到合集 ID " + mixId + "，正在建立穩定的抖音網頁環境。");
                await EnsureDouyinPageReadyAsync(log, token);
                log?.Invoke("抖音合集：抖音網頁環境已就緒，正在取得作品清單。");

                string collectionName = null;
                try
                {
                    string detailText = await FetchPageJsonAsync(
                        "/aweme/v1/web/mix/detail/?mix_id=" + mixId,
                        token);
                    var detailRoot = json.Deserialize<Dictionary<string, object>>(detailText);
                    Dictionary<string, object> mixInfo = GetDictionary(detailRoot, "mix_info")
                        ?? GetDictionary(detailRoot, "mix_detail")
                        ?? detailRoot;
                    collectionName = GetString(mixInfo, "mix_name");
                    if (string.IsNullOrWhiteSpace(collectionName))
                        collectionName = GetString(mixInfo, "title");
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    log?.Invoke("抖音合集名稱取得失敗，將以合集 ID 建立資料夾：" + ex.Message);
                }

                var resolved = new DouyinResolvedCollection
                {
                    Id = mixId,
                    Name = string.IsNullOrWhiteSpace(collectionName)
                        ? "抖音合集_" + mixId
                        : collectionName
                };

                var unresolvedIds = new List<string>();
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long cursor = 0;

                for (int page = 1; page <= 500; page++)
                {
                    token.ThrowIfCancellationRequested();
                    string path = "/aweme/v1/web/mix/aweme/?mix_id=" + mixId
                        + "&cursor=" + cursor.ToString(CultureInfo.InvariantCulture)
                        + "&count=20";
                    string pageText = await FetchPageJsonAsync(path, token);
                    var root = json.Deserialize<Dictionary<string, object>>(pageText);

                    object listObject;
                    int addedThisPage = 0;
                    if (root != null && root.TryGetValue("aweme_list", out listObject))
                    {
                        foreach (object rawItem in EnumerateValues(listObject))
                        {
                            var item = rawItem as Dictionary<string, object>;
                            if (item == null) continue;

                            string awemeId = GetString(item, "aweme_id");
                            if (string.IsNullOrWhiteSpace(awemeId) || !seenIds.Add(awemeId))
                                continue;

                            string synthetic = json.Serialize(new Dictionary<string, object>
                            {
                                { "aweme_detail", item }
                            });
                            DouyinResolvedMedia media = ParseDetail(synthetic, maxHeight, null);
                            if (media != null && !string.IsNullOrWhiteSpace(media.VideoUrl))
                            {
                                resolved.Items.Add(media);
                            }
                            else
                            {
                                unresolvedIds.Add(awemeId);
                            }
                            addedThisPage++;
                        }
                    }

                    bool hasMore = GetBool(root, "has_more");
                    long nextCursor = GetLong(root, "max_cursor");
                    if (nextCursor <= 0) nextCursor = GetLong(root, "cursor");

                    log?.Invoke("抖音合集：第 " + page + " 頁取得 " + addedThisPage
                        + " 個新作品，目前共 " + seenIds.Count + " 個。");

                    if (!hasMore) break;
                    if (nextCursor == cursor)
                    {
                        log?.Invoke("抖音合集：游標沒有前進，為避免重複請求已停止翻頁。");
                        break;
                    }
                    cursor = nextCursor;
                }

                // A few list entries may omit direct media URLs. Reuse the already
                // proven single-video page resolver only for those exceptional items.
                foreach (string awemeId in unresolvedIds)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        DouyinResolvedMedia media = await ResolveAsync(
                            "https://www.douyin.com/video/" + awemeId,
                            maxHeight,
                            log,
                            token);
                        if (media != null && !string.IsNullOrWhiteSpace(media.VideoUrl))
                            resolved.Items.Add(media);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        log?.Invoke("抖音合集：作品 " + awemeId + " 解析失敗，將略過：" + ex.Message);
                    }
                }

                resolved.Items = resolved.Items
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.VideoUrl))
                    .GroupBy(
                        x => string.IsNullOrWhiteSpace(x.Id) ? x.VideoUrl : x.Id,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                return resolved;
            }

            private async Task NavigateAndWaitReadyAsync(
                string url,
                Action<string> log,
                CancellationToken token)
            {
                // Do not await NavigationCompleted here. Douyin may redirect several
                // times and the previous event-based implementation could replace the
                // TaskCompletionSource during NavigationStarting, leaving us waiting on
                // a task that could never complete. Poll the actual page instead.
                webView.CoreWebView2.Navigate(url);

                string lastSource = string.Empty;
                for (int i = 0; i < 80; i++)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        string source = webView.Source == null
                            ? string.Empty
                            : webView.Source.AbsoluteUri;
                        if (!string.Equals(source, lastSource, StringComparison.OrdinalIgnoreCase))
                        {
                            lastSource = source;
                            if (!string.IsNullOrWhiteSpace(source))
                                log?.Invoke("抖音網頁導覽：" + source);
                        }

                        Uri uri;
                        bool onDouyin = Uri.TryCreate(source, UriKind.Absolute, out uri)
                            && (uri.Host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
                                || uri.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase));
                        if (onDouyin && await IsDocumentUsableAsync(token))
                        {
                            await WaitForDocumentReadyAsync(log, token);
                            return;
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        if (i == 79)
                            log?.Invoke("抖音網頁導覽最後狀態：" + ex.Message);
                    }

                    await Task.Delay(250, token);
                }

                throw new TimeoutException(
                    "抖音網頁導覽逾時；最後頁面：" + (string.IsNullOrWhiteSpace(lastSource) ? "未知" : lastSource));
            }

            private async Task<bool> IsDocumentUsableAsync(CancellationToken token)
            {
                try
                {
                    string raw = await webView.ExecuteScriptAsync(
                        "(()=>JSON.stringify({ready:document.readyState,origin:location.origin,body:!!document.body}))()");
                    token.ThrowIfCancellationRequested();

                    string inner;
                    try { inner = json.Deserialize<string>(raw); }
                    catch { inner = raw; }

                    var state = json.Deserialize<Dictionary<string, object>>(inner);
                    string ready = GetString(state, "ready");
                    string origin = GetString(state, "origin");
                    bool body = GetBool(state, "body");
                    return body
                        && origin.IndexOf("douyin.com", StringComparison.OrdinalIgnoreCase) >= 0
                        && (string.Equals(ready, "interactive", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            }

            private async Task EnsureDouyinPageReadyAsync(
                Action<string> log,
                CancellationToken token)
            {
                string current = webView.Source == null ? string.Empty : webView.Source.AbsoluteUri;
                Uri uri;
                bool onDouyin = Uri.TryCreate(current, UriKind.Absolute, out uri)
                    && (uri.Host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
                        || uri.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase));

                if (!onDouyin)
                {
                    log?.Invoke("抖音合集：目前 WebView2 不在抖音網域，切換到抖音首頁。");
                    await NavigateAndWaitReadyAsync("https://www.douyin.com/", log, token);
                }
                else
                {
                    await WaitForDocumentReadyAsync(log, token);
                }

                string state = await GetPageStateAsync(token);
                if (string.IsNullOrWhiteSpace(state)
                    || state.IndexOf("https://www.douyin.com", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    log?.Invoke("抖音合集：目前頁面狀態不穩定，重新載入抖音首頁。狀態：" + state);
                    await NavigateAndWaitReadyAsync("https://www.douyin.com/", log, token);
                    state = await GetPageStateAsync(token);
                }

                log?.Invoke("抖音合集：WebView2 頁面狀態=" + state);
            }

            private async Task WaitForDocumentReadyAsync(
                Action<string> log,
                CancellationToken token)
            {
                for (int i = 0; i < 60; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string raw;
                    try
                    {
                        raw = await webView.ExecuteScriptAsync(
                            "(()=>JSON.stringify({ready:document.readyState,origin:location.origin,href:location.href,body:!!document.body}))()");
                    }
                    catch
                    {
                        await Task.Delay(250, token);
                        continue;
                    }

                    string inner;
                    try { inner = json.Deserialize<string>(raw); }
                    catch { inner = raw; }

                    try
                    {
                        var state = json.Deserialize<Dictionary<string, object>>(inner);
                        string ready = GetString(state, "ready");
                        string origin = GetString(state, "origin");
                        bool body = GetBool(state, "body");
                        bool readyEnough =
                            string.Equals(ready, "interactive", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase);
                        if (readyEnough
                            && body
                            && origin.IndexOf("douyin.com", StringComparison.OrdinalIgnoreCase) >= 0)
                            return;
                    }
                    catch { }

                    await Task.Delay(250, token);
                }

                log?.Invoke("抖音合集：等待抖音頁面可用狀態逾時。");
                throw new TimeoutException("抖音網頁尚未進入可用狀態。");
            }

            private async Task<string> GetPageStateAsync(CancellationToken token)
            {
                string raw = await webView.ExecuteScriptAsync(
                    "(()=>JSON.stringify({ready:document.readyState,origin:location.origin,href:location.href}))()");
                token.ThrowIfCancellationRequested();
                string inner;
                try { inner = json.Deserialize<string>(raw); }
                catch { inner = raw; }
                return inner;
            }

            private async Task<string> FetchPageJsonAsync(string pathAndQuery, CancellationToken token)
            {
                string encoded = json.Serialize(pathAndQuery);
                string script =
                    "(async()=>{try{" +
                    "const p=" + encoded + ";" +
                    "const origin=location.origin||'';" +
                    "if(!/https:\\/\\/(?:[^.]+\\.)?douyin\\.com$/i.test(origin))" +
                    " return JSON.stringify({status:0,text:'',error:'wrong origin: '+origin,href:location.href,ready:document.readyState});" +
                    "if(!['interactive','complete'].includes(document.readyState)||!document.body)" +
                    " return JSON.stringify({status:0,text:'',error:'page not ready',href:location.href,ready:document.readyState});" +
                    "const u=new URL(p,origin).href;" +
                    "const r=await window.fetch(u,{credentials:'include',method:'GET'});" +
                    "const t=await r.text();" +
                    "return JSON.stringify({status:r.status,text:t,error:'',href:location.href,ready:document.readyState,url:u});" +
                    "}catch(e){return JSON.stringify({status:0,text:'',error:(e&&e.stack)||String(e),href:location.href,ready:document.readyState});}})();";

                string raw = await webView.ExecuteScriptAsync(script);
                token.ThrowIfCancellationRequested();

                string inner;
                try { inner = json.Deserialize<string>(raw); }
                catch { inner = raw; }

                var wrapper = json.Deserialize<Dictionary<string, object>>(inner);
                int status = GetInt(wrapper, "status");
                string text = GetString(wrapper, "text");
                string error = GetString(wrapper, "error");
                string href = GetString(wrapper, "href");
                string ready = GetString(wrapper, "ready");

                if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(text))
                    return text;

                string detail = "抖音網頁 API 回應 " + status + "：" + pathAndQuery
                    + "；頁面=" + href
                    + "；readyState=" + ready;
                if (!string.IsNullOrWhiteSpace(error))
                    detail += "；JavaScript=" + error;
                throw new InvalidOperationException(detail);
            }

            public async Task<DouyinResolvedMedia> ResolveAsync(
                string url,
                int? maxHeight,
                Action<string> log,
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

                DouyinResolvedMedia media = ParseDetail(jsonText, maxHeight, log);
                if (media != null && !string.IsNullOrWhiteSpace(media.VideoUrl))
                    return media;

                // The JSON schema changes frequently. If no stable URL was found,
                // ask the actual Douyin page which resource its <video> element is
                // playing, then inspect resource-performance entries as a fallback.
                media = await ResolveFromPageMediaAsync(jsonText, log, token);
                if (media != null && !string.IsNullOrWhiteSpace(media.VideoUrl))
                    return media;

                throw new InvalidOperationException(
                    "已取得抖音影片資料，但找不到可下載的影片網址。解析摘要已寫入記錄。");
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

            private DouyinResolvedMedia ParseDetail(
                string text,
                int? maxHeight,
                Action<string> log)
            {
                var root = json.Deserialize<Dictionary<string, object>>(text);
                Dictionary<string, object> detail = GetDictionary(root, "aweme_detail");
                if (detail == null)
                {
                    log?.Invoke("抖音解析摘要：detail JSON 沒有 aweme_detail。");
                    return null;
                }

                string id = GetString(detail, "aweme_id");
                string title = GetString(detail, "desc");
                Dictionary<string, object> video = GetDictionary(detail, "video");
                if (video == null)
                {
                    log?.Invoke("抖音解析摘要：aweme_detail 沒有 video；aweme_type="
                        + GetString(detail, "aweme_type"));
                    return null;
                }

                log?.Invoke("抖音解析摘要：video 欄位="
                    + string.Join(", ", video.Keys.OrderBy(k => k).Take(40)));

                var candidates = new List<VideoCandidate>();

                // Known current Douyin layouts.
                AddAddressCandidates(candidates, GetDictionary(video, "play_addr"), GetInt(video, "height"), 0);
                AddAddressCandidates(candidates, GetDictionary(video, "play_addr_h264"), GetInt(video, "height"), 0);
                AddAddressCandidates(candidates, GetDictionary(video, "play_addr_265"), GetInt(video, "height"), 0);
                AddAddressCandidates(candidates, GetDictionary(video, "play_addr_256"), GetInt(video, "height"), 0);
                AddAddressCandidates(candidates, GetDictionary(video, "download_addr"), GetInt(video, "height"), 0);

                object bitRateObj;
                if (video.TryGetValue("bit_rate", out bitRateObj))
                {
                    foreach (object item in EnumerateValues(bitRateObj))
                    {
                        var bitrate = item as Dictionary<string, object>;
                        if (bitrate == null) continue;
                        Dictionary<string, object> play = GetDictionary(bitrate, "play_addr");
                        int height = Math.Max(GetInt(play, "height"), GetInt(bitrate, "height"));
                        AddAddressCandidates(candidates, play, height, GetInt(bitrate, "bit_rate"));
                    }
                }

                // Future-proof fallback: recursively scan every object under video for
                // media-looking URLs. Restrict scoring so cover/avatar images never win.
                CollectRecursiveMediaCandidates(video, candidates, 0, 0);

                candidates = candidates
                    .Where(c => !string.IsNullOrWhiteSpace(c.Url))
                    .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(x => x.Score).First())
                    .ToList();

                if (candidates.Count == 0)
                {
                    log?.Invoke("抖音解析摘要：video 區塊內沒有找到任何影片 URL。");
                    return null;
                }

                log?.Invoke("抖音解析摘要：找到 " + candidates.Count + " 個影片網址候選。");

                IEnumerable<VideoCandidate> eligible = candidates;
                if (maxHeight.HasValue)
                {
                    List<VideoCandidate> limited = candidates
                        .Where(x => x.Height <= 0 || x.Height <= maxHeight.Value)
                        .ToList();
                    if (limited.Count > 0) eligible = limited;
                }

                VideoCandidate chosen = eligible
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Height)
                    .ThenByDescending(x => x.Bitrate)
                    .FirstOrDefault();
                if (chosen == null) return null;

                return new DouyinResolvedMedia
                {
                    Id = string.IsNullOrWhiteSpace(id)
                        ? ExtractVideoId(webView.Source == null ? null : webView.Source.AbsoluteUri)
                        : id,
                    Title = title,
                    VideoUrl = chosen.Url,
                    Height = chosen.Height
                };
            }

            private async Task<DouyinResolvedMedia> ResolveFromPageMediaAsync(
                string jsonText,
                Action<string> log,
                CancellationToken token)
            {
                string id = null;
                string title = null;
                try
                {
                    var root = json.Deserialize<Dictionary<string, object>>(jsonText);
                    var detail = GetDictionary(root, "aweme_detail");
                    id = GetString(detail, "aweme_id");
                    title = GetString(detail, "desc");
                }
                catch { }

                string script =
                    "(()=>{" +
                    "const out=[];" +
                    "for(const v of document.querySelectorAll('video')){" +
                    " if(v.currentSrc) out.push(v.currentSrc);" +
                    " if(v.src) out.push(v.src);" +
                    " for(const s of v.querySelectorAll('source')) if(s.src) out.push(s.src);" +
                    "}" +
                    "try{for(const e of performance.getEntriesByType('resource')){" +
                    " const u=e.name||'';" +
                    " if(/douyinvod|byte|video\\/tos|aweme\\/v1\\/play/i.test(u)) out.push(u);" +
                    "}}catch(e){}" +
                    "return [...new Set(out)].filter(u=>/^https?:/i.test(u));" +
                    "})()";

                for (int attempt = 0; attempt < 8; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    string raw = await webView.ExecuteScriptAsync(script);
                    object[] urls = null;
                    try { urls = json.Deserialize<object[]>(raw); } catch { }

                    string chosen = urls == null
                        ? null
                        : urls.Select(x => x as string)
                            .Where(IsLikelyVideoUrl)
                            .OrderByDescending(MediaUrlScore)
                            .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(chosen))
                    {
                        log?.Invoke("抖音頁面 fallback：從實際播放資源取得影片網址。");
                        return new DouyinResolvedMedia
                        {
                            Id = string.IsNullOrWhiteSpace(id)
                                ? ExtractVideoId(webView.Source == null ? null : webView.Source.AbsoluteUri)
                                : id,
                            Title = title,
                            VideoUrl = chosen,
                            Height = 0
                        };
                    }

                    await Task.Delay(750, token);
                }

                log?.Invoke("抖音頁面 fallback：DOM 與 resource timing 都沒有找到影片直連。");
                return null;
            }

            private static void AddAddressCandidates(
                ICollection<VideoCandidate> list,
                Dictionary<string, object> address,
                int height,
                int bitrate)
            {
                if (address == null) return;

                object urlsObj;
                if (address.TryGetValue("url_list", out urlsObj))
                {
                    foreach (object value in EnumerateValues(urlsObj))
                    {
                        string url = value as string;
                        if (!IsLikelyVideoUrl(url)) continue;
                        list.Add(new VideoCandidate
                        {
                            Url = url,
                            Height = Math.Max(height, GetInt(address, "height")),
                            Bitrate = bitrate,
                            Score = MediaUrlScore(url)
                        });
                    }
                }

                // Some responses expose a direct URL under alternate property names.
                foreach (string key in new[] { "url", "src", "play_url", "download_url" })
                {
                    string url = GetString(address, key);
                    if (!IsLikelyVideoUrl(url)) continue;
                    list.Add(new VideoCandidate
                    {
                        Url = url,
                        Height = Math.Max(height, GetInt(address, "height")),
                        Bitrate = bitrate,
                        Score = MediaUrlScore(url)
                    });
                }
            }

            private static void CollectRecursiveMediaCandidates(
                object node,
                ICollection<VideoCandidate> list,
                int inheritedHeight,
                int inheritedBitrate)
            {
                if (node == null) return;

                var dict = node as Dictionary<string, object>;
                if (dict != null)
                {
                    int height = GetInt(dict, "height");
                    if (height <= 0) height = inheritedHeight;
                    int bitrate = GetInt(dict, "bit_rate");
                    if (bitrate <= 0) bitrate = inheritedBitrate;

                    foreach (KeyValuePair<string, object> pair in dict)
                    {
                        string text = pair.Value as string;
                        if (IsLikelyVideoUrl(text))
                        {
                            list.Add(new VideoCandidate
                            {
                                Url = text,
                                Height = height,
                                Bitrate = bitrate,
                                Score = MediaUrlScore(text)
                            });
                        }
                        else
                        {
                            CollectRecursiveMediaCandidates(pair.Value, list, height, bitrate);
                        }
                    }
                    return;
                }

                foreach (object value in EnumerateValues(node))
                {
                    if (!ReferenceEquals(value, node))
                        CollectRecursiveMediaCandidates(value, list, inheritedHeight, inheritedBitrate);
                }
            }

            private static IEnumerable<object> EnumerateValues(object value)
            {
                if (value == null) yield break;

                object[] array = value as object[];
                if (array != null)
                {
                    foreach (object item in array) yield return item;
                    yield break;
                }

                var list = value as System.Collections.IEnumerable;
                if (list != null && !(value is string))
                {
                    foreach (object item in list) yield return item;
                    yield break;
                }

                yield return value;
            }

            private static bool IsLikelyVideoUrl(string url)
            {
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return false;

                string lower = url.ToLowerInvariant();
                if (lower.Contains(".jpg") || lower.Contains(".jpeg") || lower.Contains(".png")
                    || lower.Contains(".webp") || lower.Contains("image"))
                    return false;

                return lower.Contains("douyinvod")
                    || lower.Contains("byte")
                    || lower.Contains("/video/")
                    || lower.Contains("/video/tos/")
                    || lower.Contains("/aweme/v1/play")
                    || lower.Contains("mime_type=video")
                    || lower.Contains(".mp4");
            }

            private static int MediaUrlScore(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return 0;
                string lower = url.ToLowerInvariant();
                int score = 0;
                if (lower.Contains("douyinvod")) score += 100;
                if (!lower.Contains("douyin.com/aweme/v1/play")) score += 30;
                if (lower.Contains("watermark=0")) score += 20;
                if (lower.Contains(".mp4") || lower.Contains("mime_type=video")) score += 10;
                if (lower.Contains("playwm") || lower.Contains("watermark=1")) score -= 60;
                return score;
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

            private static bool GetBool(Dictionary<string, object> source, string key)
            {
                if (source == null) return false;
                object value;
                if (!source.TryGetValue(key, out value) || value == null) return false;
                if (value is bool) return (bool)value;
                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                    || text == "1";
            }

            private static long GetLong(Dictionary<string, object> source, string key)
            {
                if (source == null) return 0;
                object value;
                if (!source.TryGetValue(key, out value) || value == null) return 0;
                long result;
                return long.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out result)
                    ? result
                    : 0;
            }

            private static string ExtractCollectionId(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                Match match = CollectionIdRegex.Match(url);
                return match.Success ? match.Groups[1].Value : null;
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
                public int Score { get; set; }
            }
        }
    }
}
