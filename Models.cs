using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AccessiDownload
{
    internal sealed class OptionItem
    {
        public string Key { get; set; }
        public string Display { get; set; }
        public override string ToString() { return Display ?? Key ?? string.Empty; }
    }

    internal sealed class AudioSourceChoice
    {
        public string FormatId { get; set; }
        public string Display { get; set; }
        public double? Abr { get; set; }
        public string Codec { get; set; }
        public string Extension { get; set; }
        public override string ToString() { return Display ?? FormatId ?? string.Empty; }
    }

    internal sealed class MediaInfo
    {
        public string Title { get; set; }
        public string Uploader { get; set; }
        public double DurationSeconds { get; set; }
        public List<int> VideoHeights { get; set; } = new List<int>();
        public List<AudioSourceChoice> AudioSources { get; set; } = new List<AudioSourceChoice>();
    }

    internal sealed class DownloadRequest
    {
        // Url remains for the original v0.1 service; Urls is used by the queue/playlist engine.
        public string Url { get; set; }
        public List<string> Urls { get; set; } = new List<string>();
        public string DownloadFolder { get; set; }
        public bool AudioOnly { get; set; }
        public int? MaxVideoHeight { get; set; }
        public string VideoContainer { get; set; }
        public string AudioSourceFormatId { get; set; }
        public string AudioOutputFormat { get; set; }
        public string AudioOutputQuality { get; set; }
        public bool IncludeMediaId { get; set; }
        public bool DownloadPlaylist { get; set; }
        public AppSettings Settings { get; set; }
    }

    internal sealed class DownloadProgress
    {
        public int Percent { get; set; }
        public string ItemTitle { get; set; }
        public string PercentText { get; set; }
        public string SpeedText { get; set; }
        public string EtaText { get; set; }
    }

    internal sealed class DownloadResult
    {
        public List<string> FinalPaths { get; set; } = new List<string>();
        public string FinalPath
        {
            get
            {
                if (FinalPaths != null && FinalPaths.Count > 0) return FinalPaths.LastOrDefault();
                return legacyFinalPath;
            }
            set { legacyFinalPath = value; }
        }
        private string legacyFinalPath;
    }

    internal sealed class AccessibleStatusTextBox : TextBox
    {
        public void Announce(string message)
        {
            Text = message ?? string.Empty;
            AccessibleName = "狀態：" + Text;
            SelectionStart = TextLength;
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        }
    }
}
