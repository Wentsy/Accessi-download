using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AccessiDownload
{
    internal sealed class YtDlpServiceV2
    {
        private readonly YtDlpService legacy = new YtDlpService();
        private readonly string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string toolsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        private string YtDlpPath { get { return Path.Combine(baseDirectory, "yt-dlp.exe"); } }
        private string DenoPath { get { return Path.Combine(toolsDirectory, "deno.exe"); } }

        public string GetComponentSummary() { return legacy.GetComponentSummary(); }
        public bool HasRequiredComponents() { return legacy.HasRequiredComponents(); }
        public Task<string> GetVersionAsync(CancellationToken token) { return legacy.GetVersionAsync(token); }
        public Task<string> UpdateAsync(string channel, Action<string> log, CancellationToken token) { return legacy.UpdateAsync(channel, log, token); }

        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, Action<DownloadProgress> progress, Action<string> log, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var urls = request.Urls == null ? new List<string>() : request.Urls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (urls.Count == 0 && !string.IsNullOrWhiteSpace(request.Url)) urls.Add(request.Url);
            if (urls.Count == 0) throw new ArgumentException("缺少網址。");
            if (string.IsNullOrWhiteSpace(request.DownloadFolder)) throw new ArgumentException("缺少下載資料夾。");
            if (!File.Exists(YtDlpPath)) throw new FileNotFoundException("找不到 yt-dlp.exe。", YtDlpPath);

            Directory.CreateDirectory(request.DownloadFolder);
            var args = BuildCommonArguments(request.Settings);
            args.Add(request.DownloadPlaylist ? "--yes-playlist" : "--no-playlist");
            args.Add("--newline");
            args.Add("--no-color");
            args.Add("--retries"); args.Add("10");
            args.Add("--fragment-retries"); args.Add("10");
            args.Add("--skip-playlist-after-errors"); args.Add("5");
            args.Add("-N"); args.Add("4");
            args.Add("-P"); args.Add(request.DownloadFolder);
            args.Add("-o"); args.Add(BuildOutputTemplate(request));
            args.Add("--progress-template");
            args.Add("download:PROGRESS|%(info.title)s|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(info.playlist_index)s|%(info.playlist_count)s");
            args.Add("--print");
            args.Add("after_move:RESULT|%(filepath)s");

            if (request.AudioOnly) BuildAudioArguments(args, request);
            else BuildVideoArguments(args, request);
            args.AddRange(urls);

            var paths = new List<string>();
            ProcessResult result = await RunStreamingAsync(args, line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                if (line.StartsWith("PROGRESS|", StringComparison.Ordinal))
                {
                    DownloadProgress parsed = ParseProgress(line);

                    // For ordinary multi-URL batches, the number of input URLs is the
                    // reliable total. Each completed file advances the current item.
                    if (!request.DownloadPlaylist && urls.Count > 1)
                    {
                        int completed;
                        lock (paths) completed = paths.Count;
                        parsed.ItemIndex = Math.Min(urls.Count, completed + 1);
                        parsed.ItemTotal = urls.Count;
                    }
                    else if (!request.DownloadPlaylist && urls.Count == 1)
                    {
                        parsed.ItemIndex = 1;
                        parsed.ItemTotal = 1;
                    }

                    progress?.Invoke(parsed);
                    return;
                }
                if (line.StartsWith("RESULT|", StringComparison.Ordinal))
                {
                    string path = line.Substring("RESULT|".Length).Trim();
                    if (path.Length > 0)
                    {
                        lock (paths) paths.Add(path);
                        log?.Invoke("完成：" + path);
                    }
                    return;
                }
                log?.Invoke(line);
            }, token).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(FriendlyDownloadError(result.StandardError, request.Settings));
            }
            return new DownloadResult { FinalPaths = paths };
        }

        private string BuildOutputTemplate(DownloadRequest request)
        {
            string id = request.IncludeMediaId ? " [%(id)s]" : string.Empty;
            string file = "%(title).160B" + id + ".%(ext)s";
            if (!request.DownloadPlaylist) return file;
            return "%(playlist&{}/|)s%(playlist_index&{} - |)s" + file;
        }

        private List<string> BuildCommonArguments(AppSettings settings)
        {
            var args = new List<string> { "--ignore-config", "--encoding", "utf-8" };
            if (Directory.Exists(toolsDirectory)) { args.Add("--ffmpeg-location"); args.Add(toolsDirectory); }
            if (File.Exists(DenoPath)) { args.Add("--js-runtimes"); args.Add("deno:" + DenoPath); }
            AddAuthenticationArguments(args, settings);
            return args;
        }

        private static void BuildVideoArguments(List<string> args, DownloadRequest request)
        {
            int? height = request.MaxVideoHeight;
            args.Add("-f");
            if (height.HasValue)
            {
                string h = height.Value.ToString(CultureInfo.InvariantCulture);
                args.Add("bv*[height<=" + h + "]+ba/b[height<=" + h + "]");
            }
            else
            {
                args.Add("bv*+ba/b");
            }

            var sort = new List<string>();
            string container = (request.VideoContainer ?? "auto").ToLowerInvariant();
            if (container == "mp4" || container == "mov")
            {
                sort.Add("vcodec:h264");
                sort.Add("acodec:aac");
            }
            if (sort.Count > 0) { args.Add("-S"); args.Add(string.Join(",", sort)); }
            if (container == "mp4" || container == "mkv" || container == "webm" || container == "mov")
            {
                args.Add("--merge-output-format"); args.Add(container);
                args.Add("--remux-video"); args.Add(container);
            }
            args.Add("--embed-metadata");
        }

        private static void BuildAudioArguments(List<string> args, DownloadRequest request)
        {
            args.Add("-f"); args.Add("bestaudio/best");
            args.Add("-x"); args.Add("--audio-format");
            string format = string.IsNullOrWhiteSpace(request.AudioOutputFormat) ? "m4a" : request.AudioOutputFormat;
            args.Add(format);
            string lower = format.ToLowerInvariant();
            bool lossy = lower != "flac" && lower != "wav" && lower != "alac" && lower != "best";
            if (lossy && !string.IsNullOrWhiteSpace(request.AudioOutputQuality) && request.AudioOutputQuality != "best")
            {
                args.Add("--audio-quality"); args.Add(request.AudioOutputQuality);
            }
            args.Add("--embed-metadata");
        }

        private static void AddAuthenticationArguments(List<string> args, AppSettings settings)
        {
            if (settings == null) return;
            string source = (settings.CookieSource ?? "none").Trim().ToLowerInvariant();
            if (source == "cookies")
            {
                if (!string.IsNullOrWhiteSpace(settings.CookieFile) && File.Exists(settings.CookieFile))
                { args.Add("--cookies"); args.Add(settings.CookieFile); }
                return;
            }
            var browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "chromium" };
            if (browsers.Contains(source)) { args.Add("--cookies-from-browser"); args.Add(source); }
        }

        private static string FriendlyDownloadError(string standardError, AppSettings settings)
        {
            string raw = standardError ?? string.Empty;
            string lower = raw.ToLowerInvariant();
            string source = settings == null ? string.Empty : (settings.CookieSource ?? string.Empty).ToLowerInvariant();
            string browser = BrowserDisplayName(source);

            if (lower.Contains("could not copy") && lower.Contains("cookie database"))
            {
                return "無法讀取 " + browser + " Cookie：瀏覽器仍可能在背景鎖住 Cookie 資料庫。請把 " + browser + " 完全關閉，包含背景執行的程序，再重新按「開始下載」。如果仍失敗，可在「登入與更新」把 Cookie 來源改成 Mozilla Firefox，或改用 cookies.txt。Accessi-download 不會自動關閉你的瀏覽器。";
            }

            if (lower.Contains("failed to decrypt with dpapi") ||
                (lower.Contains("nonetype") && lower.Contains("decode") && lower.Contains("cookie")))
            {
                return "已找到 " + browser + " Cookie，但 Windows／Chromium 的 Cookie 加密方式讓 yt-dlp 無法解密。這是 yt-dlp 已知的 Chromium Cookie 問題。請優先改用 Mozilla Firefox 的 Cookie，或使用你自行匯出的 cookies.txt，再重新下載。";
            }

            string last = LastUsefulLine(raw);
            return string.IsNullOrWhiteSpace(last) ? "下載失敗。" : last;
        }

        private static string BrowserDisplayName(string source)
        {
            switch ((source ?? string.Empty).ToLowerInvariant())
            {
                case "chrome": return "Google Chrome";
                case "edge": return "Microsoft Edge";
                case "brave": return "Brave";
                case "opera": return "Opera";
                case "vivaldi": return "Vivaldi";
                case "chromium": return "Chromium";
                case "firefox": return "Mozilla Firefox";
                default: return "瀏覽器";
            }
        }

        private static DownloadProgress ParseProgress(string line)
        {
            string[] p = line.Split(new[] { '|' }, 7);
            string percentText = p.Length > 2 ? p[2].Trim() : string.Empty;
            double value;
            double.TryParse(percentText.Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return new DownloadProgress
            {
                ItemTitle = p.Length > 1 ? p[1].Trim() : string.Empty,
                PercentText = percentText,
                SpeedText = p.Length > 3 ? p[3].Trim() : string.Empty,
                EtaText = p.Length > 4 ? p[4].Trim() : string.Empty,
                ItemIndex = p.Length > 5 ? ParseNullableInt(p[5]) : null,
                ItemTotal = p.Length > 6 ? ParseNullableInt(p[6]) : null,
                Percent = Math.Max(0, Math.Min(100, (int)Math.Round(value)))
            };
        }

        private static int? ParseNullableInt(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string value = text.Trim();
            if (string.Equals(value, "NA", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)) return null;
            int number;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0
                ? (int?)number
                : null;
        }

        private async Task<ProcessResult> RunStreamingAsync(IEnumerable<string> arguments, Action<string> onLine, CancellationToken token)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = YtDlpPath,
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                    WorkingDirectory = baseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var errors = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (errors) { if (errors.Length < 16000) errors.AppendLine(e.Data); }
                    onLine?.Invoke(e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (token.Register(() => KillProcessTree(process)))
                {
                    await Task.Run(() => process.WaitForExit(), token).ConfigureAwait(false);
                    process.WaitForExit();
                    token.ThrowIfCancellationRequested();
                    return new ProcessResult { ExitCode = process.ExitCode, StandardError = errors.ToString() };
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"')) return value;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                if (slashes > 0) { sb.Append('\\', slashes); slashes = 0; }
                sb.Append(c);
            }
            if (slashes > 0) sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                if (process == null || process.HasExited) return;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/PID " + process.Id + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(3000);
            }
            catch { try { if (!process.HasExited) process.Kill(); } catch { } }
        }

        private static string LastUsefulLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--) if (!string.IsNullOrWhiteSpace(lines[i])) return lines[i].Trim();
            return text.Trim();
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardError { get; set; }
        }
    }
}
