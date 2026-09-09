using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AccessiDownload
{
    internal sealed class AppSettings
    {
        private static readonly string ConfigPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

        public string DownloadFolder { get; set; }
        public string CookieSource { get; set; }
        public string CookieFile { get; set; }
        public string UpdateChannel { get; set; }
        public bool PromptUpdateOnStart { get; set; }
        public string VideoContainer { get; set; }
        public string AudioOutputFormat { get; set; }
        public string LastUpdatePromptDate { get; set; }

        public static AppSettings CreateDefault()
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
            {
                videos = AppDomain.CurrentDomain.BaseDirectory;
            }

            return new AppSettings
            {
                DownloadFolder = Path.Combine(videos, "Accessi-download"),
                CookieSource = "none",
                CookieFile = string.Empty,
                UpdateChannel = "nightly",
                PromptUpdateOnStart = false,
                VideoContainer = "mp4",
                AudioOutputFormat = "m4a",
                LastUpdatePromptDate = string.Empty
            };
        }

        public static AppSettings Load()
        {
            AppSettings settings = CreateDefault();
            if (!File.Exists(ConfigPath))
            {
                return settings;
            }

            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string rawLine in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }

                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    values[line.Substring(0, separator).Trim()] =
                        line.Substring(separator + 1);
                }

                settings.DownloadFolder = Get(values, "DownloadFolder", settings.DownloadFolder);
                settings.CookieSource = Get(values, "CookieSource", settings.CookieSource);
                settings.CookieFile = Get(values, "CookieFile", settings.CookieFile);
                settings.UpdateChannel = Get(values, "UpdateChannel", settings.UpdateChannel);
                settings.VideoContainer = Get(values, "VideoContainer", settings.VideoContainer);
                settings.AudioOutputFormat = Get(values, "AudioOutputFormat", settings.AudioOutputFormat);
                settings.LastUpdatePromptDate = Get(values, "LastUpdatePromptDate", settings.LastUpdatePromptDate);

                bool prompt;
                if (bool.TryParse(Get(values, "PromptUpdateOnStart", settings.PromptUpdateOnStart.ToString()), out prompt))
                {
                    settings.PromptUpdateOnStart = prompt;
                }
            }
            catch
            {
                // Portable settings should never prevent the application from starting.
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                var lines = new[]
                {
                    "# Accessi-download portable settings",
                    "DownloadFolder=" + (DownloadFolder ?? string.Empty),
                    "CookieSource=" + (CookieSource ?? "none"),
                    "CookieFile=" + (CookieFile ?? string.Empty),
                    "UpdateChannel=" + (UpdateChannel ?? "nightly"),
                    "PromptUpdateOnStart=" + PromptUpdateOnStart,
                    "VideoContainer=" + (VideoContainer ?? "mp4"),
                    "AudioOutputFormat=" + (AudioOutputFormat ?? "m4a"),
                    "LastUpdatePromptDate=" + (LastUpdatePromptDate ?? string.Empty)
                };

                File.WriteAllLines(ConfigPath, lines, new UTF8Encoding(false));
            }
            catch
            {
                // Running from a read-only folder is allowed; settings simply will not persist.
            }
        }

        private static string Get(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }
    }
}
