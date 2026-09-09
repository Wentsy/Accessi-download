using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AccessiDownload
{
    internal sealed class YtDlpService
    {
        private readonly string baseDirectory;
        private readonly string toolsDirectory;
        private readonly string ytDlpPath;
        private readonly string ffmpegPath;
        private readonly string ffprobePath;
        private readonly string denoPath;

        public YtDlpService()
        {
            baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            toolsDirectory = Path.Combine(baseDirectory, "tools");
            ytDlpPath = Path.Combine(baseDirectory, "yt-dlp.exe");
            ffmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            ffprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            denoPath = Path.Combine(toolsDirectory, "deno.exe");
        }

        public string GetComponentSummary()
        {
            var missing = new List<string>();
            if (!File.Exists(ytDlpPath)) missing.Add("yt-dlp.exe");
            if (!File.Exists(ffmpegPath)) missing.Add("tools\\ffmpeg.exe");
            if (!File.Exists(ffprobePath)) missing.Add("tools\\ffprobe.exe");
            if (!File.Exists(denoPath)) missing.Add("tools\\deno.exe");

            if (missing.Count == 0)
            {
                return "元件完整：yt-dlp、FFmpeg、FFprobe、Deno 均已找到。";
            }

            return "缺少元件：" + string.Join("、", missing) + "。請重新下載完整的 portable ZIP。";
        }

        public bool HasRequiredComponents()
        {
            return File.Exists(ytDlpPath) &&
                   File.Exists(ffmpegPath) &&
                   File.Exists(ffprobePath) &&
                   File.Exists(denoPath);
        }

        public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
        {
            EnsureYtDlpExists();
            ProcessResult result = await RunCaptureAsync(
                new[] { "--ignore-config", "--version" },
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.StandardError.Trim());
            }

            return result.StandardOutput.Trim();
        }

        public async Task<string> UpdateAsync(
            string channel,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            EnsureYtDlpExists();

            string safeChannel = NormalizeUpdateChannel(channel);
            var args = new List<string>
            {
                "--ignore-config",
                "--update-to",
                safeChannel
            };

            ProcessResult result = await RunStreamingAsync(
                args,
                line =>
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        log?.Invoke(line);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? "yt-dlp 更新失敗。"
                        : result.StandardError.Trim());
            }

            return await GetVersionAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<MediaInfo> AnalyzeAsync(
            string url,
            AppSettings settings,
            CancellationToken cancellationToken)
        {
            EnsureYtDlpExists();

            var args = BuildCommonArguments(settings);
            args.Add("--no-playlist");
            args.Add("--no-warnings");
            args.Add("--dump-single-json");
            args.Add("--skip-download");
            args.Add(url);

            ProcessResult result = await RunCaptureAsync(args, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    FriendlyError(result.StandardError, "無法解析這個網址。"));
            }

            string json = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("yt-dlp 沒有回傳影片資訊。");
            }

            return ParseMediaInfo(json);
        }

        public async Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            Action<DownloadProgress> progress,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            EnsureYtDlpExists();

            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url)) throw new ArgumentException("缺少網址。");
            if (string.IsNullOrWhiteSpace(request.DownloadFolder)) throw new ArgumentException("缺少下載資料夾。");

            Directory.CreateDirectory(request.DownloadFolder);

            var args = BuildCommonArguments(request.Settings);
            args.Add("--no-playlist");
            args.Add("--newline");
            args.Add("--no-color");
            args.Add("--retries");
            args.Add("10");
            args.Add("--fragment-retries");
            args.Add("10");
            args.Add("-N");
            args.Add("4");
            args.Add("-P");
            args.Add(request.DownloadFolder);
            args.Add("-o");
            args.Add("%(title).180B [%(id)s].%(ext)s");
            args.Add("--progress-template");
            args.Add("download:PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s");
            args.Add("--print");
            args.Add("after_move:RESULT|%(filepath)s");

            if (request.AudioOnly)
            {
                BuildAudioArguments(args, request);
            }
            else
            {
                BuildVideoArguments(args, request);
            }

            args.Add(request.Url);

            string finalPath = null;
            ProcessResult result = await RunStreamingAsync(
                args,
                line =>
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        return;
                    }

                    if (line.StartsWith("PROGRESS|", StringComparison.Ordinal))
                    {
                        DownloadProgress parsed = ParseProgress(line);
                        progress?.Invoke(parsed);
                        return;
                    }

                    if (line.StartsWith("RESULT|", StringComparison.Ordinal))
                    {
                        finalPath = line.Substring("RESULT|".Length).Trim();
                        log?.Invoke("完成：" + finalPath);
                        return;
                    }

                    log?.Invoke(line);
                },
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    FriendlyError(result.StandardError, "下載失敗。"));
            }

            return new DownloadResult { FinalPath = finalPath };
        }

        private List<string> BuildCommonArguments(AppSettings settings)
        {
            var args = new List<string>
            {
                "--ignore-config"
            };

            if (Directory.Exists(toolsDirectory))
            {
                args.Add("--ffmpeg-location");
                args.Add(toolsDirectory);
            }

            if (File.Exists(denoPath))
            {
                args.Add("--js-runtimes");
                args.Add("deno:" + denoPath);
            }

            AddAuthenticationArguments(args, settings);
            return args;
        }

        private static void BuildVideoArguments(List<string> args, DownloadRequest request)
        {
            args.Add("-f");
            args.Add("bv*+ba/b");

            var sortParts = new List<string>();
            if (request.MaxVideoHeight.HasValue)
            {
                sortParts.Add("res:" + request.MaxVideoHeight.Value.ToString(CultureInfo.InvariantCulture));
            }

            string container = (request.VideoContainer ?? "auto").ToLowerInvariant();
            if (container == "mp4" || container == "mov")
            {
                sortParts.Add("vcodec:h264");
                sortParts.Add("acodec:aac");
            }

            if (sortParts.Count > 0)
            {
                args.Add("-S");
                args.Add(string.Join(",", sortParts));
            }

            if (container == "mp4" || container == "mkv" || container == "webm" || container == "mov")
            {
                args.Add("--merge-output-format");
                args.Add(container);
                args.Add("--remux-video");
                args.Add(container);
            }

            args.Add("--embed-metadata");
        }

        private static void BuildAudioArguments(List<string> args, DownloadRequest request)
        {
            args.Add("-f");
            args.Add(string.IsNullOrWhiteSpace(request.AudioSourceFormatId)
                ? "bestaudio/best"
                : request.AudioSourceFormatId);

            args.Add("-x");
            args.Add("--audio-format");
            args.Add(string.IsNullOrWhiteSpace(request.AudioOutputFormat)
                ? "m4a"
                : request.AudioOutputFormat);

            string format = (request.AudioOutputFormat ?? string.Empty).ToLowerInvariant();
            string quality = request.AudioOutputQuality ?? string.Empty;
            bool bitrateMakesSense =
                format != "flac" &&
                format != "wav" &&
                format != "alac" &&
                format != "best";

            if (bitrateMakesSense && !string.IsNullOrWhiteSpace(quality) && quality != "best")
            {
                args.Add("--audio-quality");
                args.Add(quality);
            }

            args.Add("--embed-metadata");
        }

        private static DownloadProgress ParseProgress(string line)
        {
            string[] parts = line.Split(new[] { '|' }, 4);
            string percentText = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            string speed = parts.Length > 2 ? parts[2].Trim() : string.Empty;
            string eta = parts.Length > 3 ? parts[3].Trim() : string.Empty;

            double percentValue = 0;
            string numeric = percentText.Replace("%", string.Empty).Trim();
            double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out percentValue);

            int percent = Math.Max(0, Math.Min(100, (int)Math.Round(percentValue)));
            return new DownloadProgress
            {
                Percent = percent,
                PercentText = percentText,
                SpeedText = speed,
                EtaText = eta
            };
        }

        private static MediaInfo ParseMediaInfo(string json)
        {
            var serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 100
            };

            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidOperationException("影片資訊格式無法辨識。");
            }

            var info = new MediaInfo
            {
                Title = GetString(root, "title"),
                Uploader = FirstNonEmpty(GetString(root, "uploader"), GetString(root, "channel")),
                DurationSeconds = GetDouble(root, "duration") ?? 0
            };

            var heights = new HashSet<int>();
            var audio = new List<AudioSourceChoice>
            {
                new AudioSourceChoice
                {
                    FormatId = "bestaudio/best",
                    Display = "自動（最佳可用音質）"
                }
            };

            object formatsObject;
            if (root.TryGetValue("formats", out formatsObject))
            {
                object[] formats = formatsObject as object[];
                if (formats != null)
                {
                    foreach (object raw in formats)
                    {
                        var format = raw as Dictionary<string, object>;
                        if (format == null) continue;

                        string formatId = GetString(format, "format_id");
                        string vcodec = GetString(format, "vcodec");
                        string acodec = GetString(format, "acodec");
                        string ext = GetString(format, "ext");

                        double? height = GetDouble(format, "height");
                        if (height.HasValue && height.Value >= 100 && !IsNone(vcodec))
                        {
                            heights.Add((int)Math.Round(height.Value));
                        }

                        if (!string.IsNullOrWhiteSpace(formatId) && IsNone(vcodec) && !IsNone(acodec))
                        {
                            double? abr = GetDouble(format, "abr") ?? GetDouble(format, "tbr");
                            string abrText = abr.HasValue
                                ? Math.Round(abr.Value).ToString(CultureInfo.InvariantCulture) + " kbps"
                                : "位元率未知";

                            string codecText = string.IsNullOrWhiteSpace(acodec) ? "audio" : acodec;
                            string extText = string.IsNullOrWhiteSpace(ext) ? "來源格式" : ext.ToUpperInvariant();

                            audio.Add(new AudioSourceChoice
                            {
                                FormatId = formatId,
                                Abr = abr,
                                Codec = acodec,
                                Extension = ext,
                                Display = abrText + " · " + extText + " · " + codecText
                            });
                        }
                    }
                }
            }

            info.VideoHeights = heights.OrderByDescending(v => v).ToList();

            var dedupedAudio = audio
                .GroupBy(a => a.FormatId)
                .Select(g => g.First())
                .OrderByDescending(a => a.FormatId == "bestaudio/best")
                .ThenByDescending(a => a.Abr ?? -1)
                .ToList();

            info.AudioSources = dedupedAudio;
            return info;
        }

        private static void AddAuthenticationArguments(List<string> args, AppSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            string source = (settings.CookieSource ?? "none").Trim().ToLowerInvariant();
            if (source == "cookies")
            {
                if (!string.IsNullOrWhiteSpace(settings.CookieFile) && File.Exists(settings.CookieFile))
                {
                    args.Add("--cookies");
                    args.Add(settings.CookieFile);
                }

                return;
            }

            var browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "chromium"
            };

            if (browsers.Contains(source))
            {
                args.Add("--cookies-from-browser");
                args.Add(source);
            }
        }

        private async Task<ProcessResult> RunCaptureAsync(
            IEnumerable<string> arguments,
            CancellationToken cancellationToken)
        {
            using (var process = new Process())
            {
                process.StartInfo = CreateStartInfo(arguments);
                process.Start();

                using (cancellationToken.Register(() => KillProcessTree(process)))
                {
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();
                    return new ProcessResult
                    {
                        ExitCode = process.ExitCode,
                        StandardOutput = stdout.Result,
                        StandardError = stderr.Result
                    };
                }
            }
        }

        private async Task<ProcessResult> RunStreamingAsync(
            IEnumerable<string> arguments,
            Action<string> onLine,
            CancellationToken cancellationToken)
        {
            using (var process = new Process())
            {
                process.StartInfo = CreateStartInfo(arguments);
                var errorBuffer = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        onLine?.Invoke(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (errorBuffer)
                        {
                            if (errorBuffer.Length < 12000)
                            {
                                errorBuffer.AppendLine(e.Data);
                            }
                        }

                        onLine?.Invoke(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (cancellationToken.Register(() => KillProcessTree(process)))
                {
                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
                    process.WaitForExit();
                    cancellationToken.ThrowIfCancellationRequested();

                    return new ProcessResult
                    {
                        ExitCode = process.ExitCode,
                        StandardOutput = string.Empty,
                        StandardError = errorBuffer.ToString()
                    };
                }
            }
        }

        private ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
        {
            return new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = JoinArguments(arguments),
                WorkingDirectory = baseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        private static string JoinArguments(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(QuoteArgument));
        }

        private static string QuoteArgument(string argument)
        {
            if (argument == null) return "\"\"";
            if (argument.Length > 0 &&
                argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return argument;
            }

            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;

            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(c);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                if (process == null || process.HasExited) return;

                using (var killer = new Process())
                {
                    killer.StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    killer.Start();
                    killer.WaitForExit(3000);
                }
            }
            catch
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch
                {
                }
            }
        }

        private void EnsureYtDlpExists()
        {
            if (!File.Exists(ytDlpPath))
            {
                throw new FileNotFoundException(
                    "找不到 yt-dlp.exe。請使用 Releases / Actions 提供的完整 portable ZIP，而不是只下載 Accessi-download.exe。",
                    ytDlpPath);
            }
        }

        private static string NormalizeUpdateChannel(string channel)
        {
            string value = (channel ?? "nightly").Trim().ToLowerInvariant();
            if (value != "stable" && value != "nightly" && value != "master")
            {
                return "nightly";
            }

            return value;
        }

        private static string FriendlyError(string stderr, string fallback)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return fallback;
            }

            string[] lines = stderr
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(4)
                .ToArray();

            return lines.Length > 0
                ? string.Join(Environment.NewLine, lines)
                : stderr.Trim();
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!dict.TryGetValue(key, out value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            object value;
            if (!dict.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsNone(string codec)
        {
            return string.IsNullOrWhiteSpace(codec) ||
                   string.Equals(codec, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
        }

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }
    }
}
