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
    /// Xiaohongshu/RedNote public video resolver.
    /// Uses the real note page in WebView2 so xhslink.cn short links can follow
    /// the platform redirect and the page's own __INITIAL_STATE__ can expose the
    /// target note media without reproducing XHS signing code.
    /// </summary>
    internal sealed class XhsWebViewService
    {
        private const string MobileUserAgent =
            "Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36";

        private static readonly Regex NoteIdRegex = new Regex(
            @"/(?:explore|discovery/item)/([0-9a-f]{20,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private string FfmpegPath { get { return Path.Combine(baseDirectory, "tools", "ffmpeg.exe"); } }

        public static bool CanHandleUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return false;
            string host = (uri.Host ?? string.Empty).ToLowerInvariant();
            return host == "xhslink.cn"
                || host.EndsWith(".xhslink.cn", StringComparison.Ordinal)
                || host == "xhslink.com"
                || host.EndsWith(".xhslink.com", StringComparison.Ordinal)
                || host == "xiaohongshu.com"
                || host.EndsWith(".xiaohongshu.com", StringComparison.Ordinal)
                || host == "rednote.com"
                || host.EndsWith(".rednote.com", StringComparison.Ordinal);
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
            if (urls.Count == 0) throw new ArgumentException("缺少小紅書網址。");
            if (urls.Any(url => !CanHandleUrl(url)))
                throw new InvalidOperationException("小紅書網頁解析模式只能處理小紅書／RedNote 網址。");
            if (!File.Exists(FfmpegPath) && request.AudioOnly)
                throw new FileNotFoundException("只下載音訊需要 tools\\ffmpeg.exe。", FfmpegPath);

            Directory.CreateDirectory(request.DownloadFolder);

            using (var bridge = new XhsBridgeForm())
            {
                bridge.Show(owner);
                await bridge.InitializeAsync(token);

                var result = new DownloadResult();

                for (int i = 0; i < urls.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string inputUrl = urls[i];
                    log?.Invoke("小紅書網頁解析：正在開啟第 " + (i + 1) + "/" + urls.Count + " 個網址。");

                    XhsResolvedMedia media = await ResolveWithRetryAsync(
                        bridge, inputUrl, log, token);

                    log?.Invoke("小紅書網頁解析成功："
                        + (string.IsNullOrWhiteSpace(media.Title) ? "未命名作品" : media.Title));

                    string finalPath = await DownloadResolvedMediaAsync(
                        media,
                        request,
                        i + 1,
                        urls.Count,
                        progress,
                        log,
                        token);

                    result.FinalPaths.Add(finalPath);
                }

                return result;
            }
        }

        private async Task<XhsResolvedMedia> ResolveWithRetryAsync(
            XhsBridgeForm bridge,
            string url,
            Action<string> log,
            CancellationToken token)
        {
            Exception lastError = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    return await bridge.ResolveAsync(url, log, token);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    lastError = ex;
                    log?.Invoke("小紅書網頁解析第 " + attempt + " 次未成功：" + ex.Message);
                    if (attempt < 2) await Task.Delay(1200, token);
                }
            }

            throw new InvalidOperationException(
                "小紅書目前無法取得這篇公開影片的媒體資料。可能是頁面要求驗證、連結已失效，或小紅書近期更改了頁面結構；網址已保留，可稍後重試。",
                lastError);
        }

        private async Task<string> DownloadResolvedMediaAsync(
            XhsResolvedMedia media,
            DownloadRequest request,
            int itemIndex,
            int itemTotal,
            Action<DownloadProgress> progress,
            Action<string> log,
            CancellationToken token)
        {
            string title = SanitizeFileName(
                string.IsNullOrWhiteSpace(media.Title) ? "小紅書_" + media.Id : media.Title);
            if (request.IncludeMediaId && !string.IsNullOrWhiteSpace(media.Id))
                title += " [" + media.Id + "]";

            string tempPath = Path.Combine(
                request.DownloadFolder,
                "." + Guid.NewGuid().ToString("N") + ".xhs.mp4");

            try
            {
                await DownloadFileAsync(
                    media.VideoUrl,
                    tempPath,
                    media.SourcePageUrl,
                    itemIndex,
                    itemTotal,
                    progress,
                    token);

                if (request.AudioOnly)
                {
                    string format = NormalizeAudioFormat(request.AudioOutputFormat);
                    string ext = AudioExtension(format);
                    string output = GetUniquePath(request.DownloadFolder, title, ext);
                    log?.Invoke("正在從小紅書影片提取音訊：" + Path.GetFileName(output));
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

                string remuxOutput = GetUniquePath(
                    request.DownloadFolder, title, "." + container);
                log?.Invoke("正在轉換小紅書影片容器：" + container.ToUpperInvariant());
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

        private async Task DownloadFileAsync(
            string url,
            string destination,
            string referer,
            int itemIndex,
            int itemTotal,
            Action<DownloadProgress> progress,
            CancellationToken token)
        {
            var request = WebRequest.CreateHttp(url);
            request.Method = "GET";
            request.AllowAutoRedirect = true;
            request.UserAgent = MobileUserAgent;
            request.Referer = string.IsNullOrWhiteSpace(referer)
                ? "https://www.xiaohongshu.com/"
                : referer;
            request.Accept = "video/mp4,video/*,*/*;q=0.8";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;

            using (token.Register(() => { try { request.Abort(); } catch { } }))
            using (var response = (System.Net.HttpWebResponse)await request.GetResponseAsync())
            using (Stream input = response.GetResponseStream())
            using (var output = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
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
                        TimeSpan remaining = TimeSpan.FromSeconds(
                            (total - received) / bytesPerSecond);
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
                if (Regex.IsMatch(q, @"^\d+k$"))
                    args.AddRange(new[] { "-b:a", q });
            }

            args.Add(output);
            await RunProcessAsync(FfmpegPath, args, token);
        }

        private async Task RemuxVideoAsync(
            string input,
            string output,
            string container,
            CancellationToken token)
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

        private static async Task RunProcessAsync(
            string fileName,
            IEnumerable<string> args,
            CancellationToken token)
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
                    throw new InvalidOperationException(
                        "ffmpeg 轉換失敗，結束碼：" + process.ExitCode);
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
            if (string.IsNullOrWhiteSpace(value)) return "小紅書";

            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder();

            foreach (char c in value)
            {
                if (!invalid.Contains(c) && c >= 32) sb.Append(c);
            }

            string cleaned = sb.ToString().Trim().TrimEnd('.');
            if (cleaned.Length > 140)
                cleaned = cleaned.Substring(0, 140).Trim().TrimEnd('.');

            return string.IsNullOrWhiteSpace(cleaned) ? "小紅書" : cleaned;
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
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private sealed class XhsResolvedMedia
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string VideoUrl { get; set; }
            public string SourcePageUrl { get; set; }
        }

        private sealed class XhsBridgeForm : Form
        {
            private readonly WebView2 webView;
            private readonly JavaScriptSerializer json =
                new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            private bool initialized;

            public XhsBridgeForm()
            {
                Text = "Accessi-download 小紅書網頁解析";
                Width = 1000;
                Height = 760;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                ShowInTaskbar = false;
                Opacity = 0;

                webView = new WebView2
                {
                    Dock = DockStyle.Fill,
                    AccessibleName = "小紅書網頁；正在解析影片"
                };

                Controls.Add(webView);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            public async Task InitializeAsync(CancellationToken token)
            {
                if (initialized) return;

                string userDataFolder = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "browser-data",
                    "XhsWebView2");
                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment;
                try
                {
                    environment = await CoreWebView2Environment.CreateAsync(
                        null, userDataFolder);
                    token.ThrowIfCancellationRequested();
                    await webView.EnsureCoreWebView2Async(environment);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "無法啟動 Microsoft Edge WebView2。請確認 Windows 的 Edge／WebView2 Runtime 可正常使用。",
                        ex);
                }

                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.UserAgent = MobileUserAgent;
                initialized = true;
            }

            public async Task<XhsResolvedMedia> ResolveAsync(
                string url,
                Action<string> log,
                CancellationToken token)
            {
                if (!initialized)
                    throw new InvalidOperationException("小紅書 WebView2 尚未初始化。");

                // HTTP-first is important for XHS: some note pages redirect desktop
                // visitors to login while the mobile web still renders the public note.
                try
                {
                    XhsPageData httpData = await ResolveWithHttpAsync(url, log, token);
                    if (httpData != null && !string.IsNullOrWhiteSpace(httpData.VideoUrl))
                    {
                        log?.Invoke("小紅書 HTTP 解析成功，未使用瀏覽器頁面。");
                        return new XhsResolvedMedia
                        {
                            Id = httpData.Id,
                            Title = httpData.Title,
                            VideoUrl = httpData.VideoUrl,
                            SourcePageUrl = httpData.SourcePageUrl
                        };
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    log?.Invoke("小紅書 HTTP 解析未成功，改用 WebView2： " + ex.Message);
                }

                await NavigateAndWaitReadyAsync(url, log, token);

                // Keep the WebView2 path as a final fallback for page structures
                // that expose the media only after client-side rendering.
                for (int i = 0; i < 80; i++)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        XhsPageData data = await ExtractPageDataAsync(token);
                        if (data != null && !string.IsNullOrWhiteSpace(data.VideoUrl))
                        {
                            string page = webView.Source == null
                                ? string.Empty
                                : webView.Source.AbsoluteUri;

                            if (!IsTrustedPageUrl(page))
                                throw new InvalidOperationException(
                                    "小紅書短網址沒有導向有效的公開筆記頁。");

                            data.SourcePageUrl = page;
                            return new XhsResolvedMedia
                            {
                                Id = data.Id,
                                Title = data.Title,
                                VideoUrl = data.VideoUrl,
                                SourcePageUrl = page
                            };
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        if (i == 79) throw;
                    }

                    await Task.Delay(250, token);
                }

                throw new TimeoutException(
                    "小紅書網頁解析逾時；最後頁面：" + GetCurrentUrl());
            }

            private async Task NavigateAndWaitReadyAsync(
                string url,
                Action<string> log,
                CancellationToken token)
            {
                webView.CoreWebView2.Navigate(url);

                string lastSource = string.Empty;

                for (int i = 0; i < 100; i++)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        string source = GetCurrentUrl();
                        if (!string.Equals(source, lastSource, StringComparison.OrdinalIgnoreCase))
                        {
                            lastSource = source;
                            if (!string.IsNullOrWhiteSpace(source))
                                log?.Invoke("小紅書網頁導覽：" + source);
                        }

                        if (IsTrustedPageUrl(source) && await IsDocumentReadyAsync(token))
                            return;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        if (i == 99)
                            log?.Invoke("小紅書網頁導覽最後狀態：" + ex.Message);
                    }

                    await Task.Delay(250, token);
                }

                throw new TimeoutException(
                    "小紅書網頁導覽逾時；最後頁面："
                    + (string.IsNullOrWhiteSpace(lastSource) ? "未知" : lastSource));
            }

            private async Task<bool> IsDocumentReadyAsync(CancellationToken token)
            {
                try
                {
                    string raw = await webView.ExecuteScriptAsync(
                        "(()=>JSON.stringify({ready:document.readyState,body:!!document.body,href:location.href}))()");
                    token.ThrowIfCancellationRequested();

                    string inner = DeserializeString(raw);
                    var state = json.Deserialize<Dictionary<string, object>>(inner);
                    string ready = GetString(state, "ready");
                    bool body = GetBool(state, "body");

                    return body
                        && (string.Equals(ready, "interactive", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ready, "complete", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            }

            private async Task<XhsPageData> ExtractPageDataAsync(CancellationToken token)
            {
                string script = @"
(()=>{try{
 const href=location.href||'';
 const title=document.title||'';
 const state=window.__INITIAL_STATE__;
 const out={href:href,title:'',id:'',type:'',videoUrl:'',videoCandidates:[]};

 const add=(u)=>{
   if(typeof u!=='string') return;
   u=u.trim();
   if(!/^https?:\/\//i.test(u)) return;
   if(!/xhscdn\.com|rednotecdn\.com/i.test(u)) return;
   if(out.videoCandidates.indexOf(u)<0) out.videoCandidates.push(u);
 };

 const addKey=(k)=>{
   if(typeof k!=='string'||!k.trim()) return;
   add('https://sns-video-bd.xhscdn.com/'+k.replace(/^\/+/,''));
 };

 const addStream=(s)=>{
   if(!s||typeof s!=='object') return;
   Object.keys(s).forEach(codec=>{
     const arr=s[codec];
     if(!Array.isArray(arr)) return;
     arr.forEach(item=>{
       if(!item||typeof item!=='object') return;
       add(item.masterUrl);
       if(Array.isArray(item.backupUrls)) item.backupUrls.forEach(add);
       add(item.url);
     });
   });
 };

 const noteMap=state&&state.note&&state.note.noteDetailMap;
 const m=href.match(/\/(?:explore|discovery\/item)\/([0-9a-f]{20,})/i);
 const targetId=m?m[1]:'';

 let note=null;
 if(noteMap&&targetId&&noteMap[targetId])
   note=noteMap[targetId].note||noteMap[targetId];

 if(!note && state&&state.noteData&&state.noteData.data&&state.noteData.data.noteData)
   note=state.noteData.data.noteData;

 if(!note&&noteMap){
   const keys=Object.keys(noteMap);
   if(keys.length===1) note=noteMap[keys[0]].note||noteMap[keys[0]];
 }

 if(note&&typeof note==='object'){
   out.id=note.noteId||note.note_id||targetId||'';
   out.title=note.title||'';
   out.type=note.type||'';
   const v=note.video||{};
   const consumer=v.consumer||{};
   addKey(consumer.originVideoKey);
   addKey(v.originVideoKey);
   if(v.media&&v.media.stream) addStream(v.media.stream);
   if(v.mediaV2&&v.mediaV2.stream) addStream(v.mediaV2.stream);
   add(v.originVideoUrl);
   add(v.url);
   add(v.videoUrl);
 }

 document.querySelectorAll('video').forEach(v=>add(v.currentSrc||v.src));
 document.querySelectorAll('meta[property=""og:video""],meta[name=""og:video""]').forEach(m=>{
   add(m.getAttribute('content')||'');
 });

 try{
   performance.getEntriesByType('resource').forEach(e=>{
     const u=e&&e.name;
     if(typeof u==='string' && (/\.mp4(?:\?|$)/i.test(u)||/xhscdn\.com/i.test(u))) add(u);
   });
 }catch(_){}

 if(!out.title) out.title=title.replace(/\s*[-|·]\s*(小红书|RedNote)\s*$/i,'').trim();
 out.videoUrl=out.videoCandidates.length?out.videoCandidates[0]:'';
 return JSON.stringify(out);
}catch(e){
 return JSON.stringify({error:(e&&e.stack)||String(e),href:location.href||''});
}})()";

                string raw = await webView.ExecuteScriptAsync(script);
                token.ThrowIfCancellationRequested();

                string inner = DeserializeString(raw);
                if (string.IsNullOrWhiteSpace(inner)) return null;

                Dictionary<string, object> wrapper;
                try { wrapper = json.Deserialize<Dictionary<string, object>>(inner); }
                catch { return null; }

                string error = GetString(wrapper, "error");
                if (!string.IsNullOrWhiteSpace(error))
                    return null;

                var data = new XhsPageData
                {
                    Id = GetString(wrapper, "id"),
                    Title = GetString(wrapper, "title"),
                    Type = GetString(wrapper, "type"),
                    VideoUrl = GetString(wrapper, "videoUrl"),
                    SourcePageUrl = GetString(wrapper, "href")
                };

                if (string.Equals(data.Type, "normal", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(data.VideoUrl))
                    return null;

                if (!IsTrustedMediaUrl(data.VideoUrl))
                    return null;

                return data;
            }

            private async Task<XhsPageData> ResolveWithHttpAsync(
                string inputUrl,
                Action<string> log,
                CancellationToken token)
            {
                Exception lastError = null;
                string[] userAgents = { DesktopUserAgentForHttp, MobileUserAgentForHttp };

                foreach (string userAgent in userAgents)
                {
                    try
                    {
                        XhsPageData data = await ResolveHttpWithUserAgentAsync(
                            inputUrl, userAgent, log, token);
                        if (data != null && !string.IsNullOrWhiteSpace(data.VideoUrl))
                            return data;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        lastError = ex;
                        log?.Invoke("小紅書 HTTP " +
                            (userAgent == MobileUserAgentForHttp ? "行動版" : "桌面版")
                            + "解析失敗：" + ex.Message);
                    }
                }

                if (lastError != null) throw lastError;
                return null;
            }

            private async Task<XhsPageData> ResolveHttpWithUserAgentAsync(
                string inputUrl,
                string userAgent,
                Action<string> log,
                CancellationToken token)
            {
                Uri current;
                if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out current)
                    || !CanHandleUrl(inputUrl))
                    throw new InvalidOperationException("小紅書網址格式不正確。");

                for (int hop = 0; hop < 8; hop++)
                {
                    token.ThrowIfCancellationRequested();
                    log?.Invoke("小紅書 HTTP 導覽：" + current.AbsoluteUri);

                    var request = WebRequest.CreateHttp(current);
                    request.Method = "GET";
                    request.AllowAutoRedirect = false;
                    request.UserAgent = userAgent;
                    request.Accept =
                        "text/html,application/xhtml+xml,application/xml;q=0.9,"
                        + "image/avif,image/webp,image/apng,*/*;q=0.8";
                    request.Headers[HttpRequestHeader.AcceptLanguage] = "zh-CN,zh;q=0.9,en;q=0.8";
                    request.Referer = "https://www.xiaohongshu.com/";
                    request.AutomaticDecompression =
                        DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    request.Timeout = 20000;
                    request.ReadWriteTimeout = 20000;

                    using (token.Register(() => { try { request.Abort(); } catch { } }))
                    using (var response = (System.Net.HttpWebResponse)await request.GetResponseAsync())
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status < 400)
                        {
                            string location = response.Headers[HttpResponseHeader.Location];
                            if (string.IsNullOrWhiteSpace(location))
                                throw new InvalidOperationException("小紅書重新導向沒有提供目的網址。");

                            Uri next = new Uri(current, location);
                            if (!CanHandleUrl(next.AbsoluteUri))
                                throw new InvalidOperationException(
                                    "小紅書重新導向到不受支援的網域：" + next.Host);

                            current = next;
                            continue;
                        }

                        if (status < 200 || status >= 300)
                            throw new InvalidOperationException(
                                "小紅書網頁回應 HTTP " + status + "。");

                        string html;
                        using (Stream stream = response.GetResponseStream())
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        {
                            html = await reader.ReadToEndAsync();
                        }

                        Uri htmlRedirect = ExtractHttpHtmlRedirect(html, current);
                        if (htmlRedirect != null
                            && !string.Equals(
                                htmlRedirect.AbsoluteUri,
                                current.AbsoluteUri,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            current = htmlRedirect;
                            continue;
                        }

                        // Some login pages carry the real note URL in redirectPath.
                        // Follow it so the mobile retry can request the actual note.
                        Uri loginRedirect = ExtractLoginRedirect(current);
                        if (loginRedirect != null)
                        {
                            current = loginRedirect;
                            continue;
                        }

                        XhsPageData data = ParseHttpInitialState(html, current.AbsoluteUri);
                        if (data != null && !string.IsNullOrWhiteSpace(data.VideoUrl))
                            return data;

                        // A successful HTML response without media is still useful
                        // information, but the caller will retry with the other UA.
                        return null;
                    }
                }

                throw new InvalidOperationException("小紅書 HTTP 重新導向次數過多。");
            }

            private XhsPageData ParseHttpInitialState(string html, string sourceUrl)
            {
                if (string.IsNullOrWhiteSpace(html)) return null;

                Match match = Regex.Match(
                    html,
                    @"window\.__INITIAL_STATE__\s*=\s*(.*?)</script>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                if (!match.Success) return null;

                string stateText = match.Groups[1].Value
                    .Trim()
                    .TrimEnd(';')
                    .Replace("undefined", "null");

                Dictionary<string, object> state;
                try
                {
                    state = json.Deserialize<Dictionary<string, object>>(stateText);
                }
                catch
                {
                    return null;
                }

                Dictionary<string, object> note = FindHttpNote(state, sourceUrl);
                if (note == null) return null;

                string id = GetString(note, "noteId");
                if (string.IsNullOrWhiteSpace(id)) id = GetString(note, "note_id");
                if (string.IsNullOrWhiteSpace(id)) id = ExtractHttpNoteId(sourceUrl);

                string title = GetString(note, "title");
                if (string.IsNullOrWhiteSpace(title))
                    title = GetString(note, "displayTitle");
                if (string.IsNullOrWhiteSpace(title))
                {
                    Match titleMatch = Regex.Match(
                        html,
                        @"<title[^>]*>(.*?)</title>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (titleMatch.Success)
                    {
                        title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value)
                            .Trim();
                        title = Regex.Replace(
                            title,
                            @"\s*[-|·]\s*(小红书|RedNote)\s*$",
                            string.Empty,
                            RegexOptions.IgnoreCase).Trim();
                    }
                }

                var candidates = new List<string>();
                Dictionary<string, object> video = GetDictionary(note, "video");
                Dictionary<string, object> consumer = GetDictionary(video, "consumer");

                AddHttpOriginCandidate(candidates, GetString(consumer, "originVideoKey"));
                AddHttpOriginCandidate(candidates, GetString(video, "originVideoKey"));
                AddHttpCandidate(candidates, GetString(video, "originVideoUrl"));
                AddHttpCandidate(candidates, GetString(video, "url"));
                AddHttpCandidate(candidates, GetString(video, "videoUrl"));

                AddHttpStreamCandidates(
                    candidates,
                    GetDictionary(GetDictionary(video, "media"), "stream"));
                AddHttpStreamCandidates(
                    candidates,
                    GetDictionary(GetDictionary(video, "mediaV2"), "stream"));

                string best = candidates
                    .Where(IsTrustedMediaUrl)
                    .OrderByDescending(ScoreHttpVideoUrl)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(best)) return null;

                return new XhsPageData
                {
                    Id = id,
                    Title = title,
                    Type = GetString(note, "type"),
                    VideoUrl = best,
                    SourcePageUrl = sourceUrl
                };
            }

            private static Dictionary<string, object> FindHttpNote(
                Dictionary<string, object> state,
                string sourceUrl)
            {
                if (state == null) return null;

                Dictionary<string, object> noteData = GetDictionary(state, "noteData");
                Dictionary<string, object> noteDataData = GetDictionary(noteData, "data");
                Dictionary<string, object> directNote = GetDictionary(noteDataData, "noteData");
                if (directNote != null) return directNote;

                Dictionary<string, object> noteRoot = GetDictionary(state, "note");
                Dictionary<string, object> detailMap = GetDictionary(noteRoot, "noteDetailMap");
                if (detailMap == null) detailMap = GetDictionary(noteRoot, "detailMap");
                if (detailMap == null) return null;

                string noteId = ExtractHttpNoteId(sourceUrl);
                if (!string.IsNullOrWhiteSpace(noteId))
                {
                    Dictionary<string, object> entry = GetDictionary(detailMap, noteId);
                    if (entry != null)
                    {
                        Dictionary<string, object> nested = GetDictionary(entry, "note");
                        return nested ?? entry;
                    }
                }

                foreach (object value in detailMap.Values)
                {
                    Dictionary<string, object> entry = value as Dictionary<string, object>;
                    if (entry == null) continue;
                    Dictionary<string, object> nested = GetDictionary(entry, "note");
                    return nested ?? entry;
                }

                return null;
            }

            private static Dictionary<string, object> GetDictionary(
                Dictionary<string, object> data,
                string key)
            {
                if (data == null) return null;
                object value;
                return data.TryGetValue(key, out value)
                    ? value as Dictionary<string, object>
                    : null;
            }

            private static void AddHttpOriginCandidate(
                List<string> candidates,
                string key)
            {
                if (string.IsNullOrWhiteSpace(key)) return;
                AddHttpCandidate(
                    candidates,
                    "https://sns-video-bd.xhscdn.com/"
                    + key.Trim().TrimStart('/'));
            }

            private static void AddHttpCandidate(
                List<string> candidates,
                string url)
            {
                if (string.IsNullOrWhiteSpace(url)
                    || !IsTrustedMediaUrl(url)
                    || candidates.Any(x => string.Equals(
                        x, url, StringComparison.OrdinalIgnoreCase)))
                    return;

                candidates.Add(url);
            }

            private static void AddHttpStreamCandidates(
                List<string> candidates,
                Dictionary<string, object> stream)
            {
                if (stream == null) return;

                foreach (object rawArray in stream.Values)
                {
                    var array = rawArray as System.Collections.IEnumerable;
                    if (array == null || rawArray is string) continue;

                    foreach (object rawItem in array)
                    {
                        Dictionary<string, object> item =
                            rawItem as Dictionary<string, object>;
                        if (item == null) continue;

                        AddHttpCandidate(candidates, GetString(item, "masterUrl"));
                        AddHttpCandidate(candidates, GetString(item, "url"));

                        object backups;
                        if (item.TryGetValue("backupUrls", out backups))
                        {
                            var backupArray = backups as System.Collections.System.Collections.IEnumerable;
                            if (backupArray != null && !(backups is string))
                            {
                                foreach (object backup in backupArray)
                                    AddHttpCandidate(
                                        candidates,
                                        Convert.ToString(
                                            backup,
                                            CultureInfo.InvariantCulture));
                            }
                        }
                    }
                }
            }

            private static int ScoreHttpVideoUrl(string url)
            {
                string lower = (url ?? string.Empty).ToLowerInvariant();
                int score = 0;
                if (lower.Contains("sns-video-bd.xhscdn.com")) score += 100;
                if (lower.Contains("origin")) score += 50;
                if (lower.Contains("h265")) score += 10;
                if (lower.EndsWith(".mp4")) score += 50;
                if (lower.Contains(".m3u8")) score -= 100;
                return score;
            }

            private static string ExtractHttpNoteId(string url)
            {
                Match match = Regex.Match(
                    url ?? string.Empty,
                    @"/(?:explore|discovery/item)/([^/?]+)",
                    RegexOptions.IgnoreCase);
                return match.Success ? match.Groups[1].Value : string.Empty;
            }

            private static Uri ExtractHttpHtmlRedirect(string html, Uri current)
            {
                if (string.IsNullOrWhiteSpace(html)) return null;

                Match match = Regex.Match(
                    html,
                    @"window\.location(?:\.href)?\s*=\s*['""]([^'""]+)['""]",
                    RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    match = Regex.Match(
                        html,
                        @"window\.location\.replace\(\s*['""]([^'""]+)['""]\s*\)",
                        RegexOptions.IgnoreCase);
                }

                if (!match.Success) return null;

                try
                {
                    Uri target = new Uri(
                        current,
                        System.Net.WebUtility.HtmlDecode(match.Groups[1].Value));
                    return CanHandleUrl(target.AbsoluteUri) ? target : null;
                }
                catch
                {
                    return null;
                }
            }

            private static Uri ExtractLoginRedirect(Uri current)
            {
                try
                {
                    if (!current.AbsolutePath.Equals(
                        "/login",
                        StringComparison.OrdinalIgnoreCase))
                        return null;

                    foreach (string part in current.Query.TrimStart('?').Split('&'))
                    {
                        int equals = part.IndexOf('=');
                        if (equals <= 0) continue;

                        string key = Uri.UnescapeDataString(
                            part.Substring(0, equals));
                        if (!string.Equals(
                            key,
                            "redirectPath",
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        string value = Uri.UnescapeDataString(
                            part.Substring(equals + 1));
                        Uri target;
                        if (!Uri.TryCreate(value, UriKind.Absolute, out target))
                            return null;

                        return CanHandleUrl(target.AbsoluteUri) ? target : null;
                    }
                }
                catch { }

                return null;
            }

            private const string DesktopUserAgentForHttp =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";

            private const string MobileUserAgentForHttp =
                "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) "
                + "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 "
                + "Mobile/15E148 Safari/604.1";

            private string GetCurrentUrl()
            {
                return webView.Source == null ? string.Empty : webView.Source.AbsoluteUri;
            }

            private static bool IsTrustedPageUrl(string url)
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
                string host = (uri.Host ?? string.Empty).ToLowerInvariant();
                return host == "www.xiaohongshu.com"
                    || host == "xiaohongshu.com"
                    || host == "www.rednote.com"
                    || host == "rednote.com";
            }

            private static bool IsTrustedMediaUrl(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return false;
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
                if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;

                string host = (uri.Host ?? string.Empty).ToLowerInvariant();
                return host == "sns-video-bd.xhscdn.com"
                    || host.EndsWith(".xhscdn.com", StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith(".rednotecdn.com", StringComparison.OrdinalIgnoreCase);
            }

            private string DeserializeString(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                try { return json.Deserialize<string>(raw) ?? string.Empty; }
                catch { return raw.Trim('"'); }
            }

            private static string GetString(
                Dictionary<string, object> data,
                string key)
            {
                if (data == null) return string.Empty;
                object value;
                return data.TryGetValue(key, out value) && value != null
                    ? Convert.ToString(value, CultureInfo.InvariantCulture)
                    : string.Empty;
            }

            private static bool GetBool(
                Dictionary<string, object> data,
                string key)
            {
                if (data == null) return false;
                object value;
                if (!data.TryGetValue(key, out value) || value == null) return false;
                bool result;
                return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result)
                    && result;
            }
        }

        private sealed class XhsPageData
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Type { get; set; }
            public string VideoUrl { get; set; }
            public string SourcePageUrl { get; set; }
        }
    }
}
