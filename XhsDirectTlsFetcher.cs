using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AccessiDownload
{
    /// <summary>
    /// 小紅書直連抓取器（2026-10-05）：繞過本地 DNS 污染。
    ///
    /// 背景：部分網路會把 www.xiaohongshu.com 劫持到封鎖頁 IP，
    /// 用戶端拿到的是一個「憑證鏈不受信任」的假站，HttpClient/WebView2
    /// 走系統 DNS 永遠只會看到封鎖頁，跟 TLS 版本、憑證回呼怎麼寫無關。
    /// 解法：DoH 拿真實 IP → TcpClient 直連 → SslStream 用真實 host 做
    /// SNI 與憑證驗證（走系統預設驗證，不放寬任何檢查）→ 手動發 HTTP GET。
    /// 這條路只跟真實伺服器說話，封鎖頁碰不到。
    /// </summary>
    internal static class XhsDirectTlsFetcher
    {
        private const int MaxHops = 8;

        private static readonly string[] DohTemplates =
        {
            "https://cloudflare-dns.com/dns-query?name={0}&type=A",
            "https://dns.google/resolve?name={0}&type=A"
        };

        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static readonly Dictionary<string, DnsCacheEntry> DnsCache =
            new Dictionary<string, DnsCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly object DnsCacheLock = new object();

        internal sealed class FetchResult
        {
            public int StatusCode { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public string Body { get; set; }
            public string FinalUrl { get; set; }
        }

        private sealed class DnsCacheEntry
        {
            public string Ip { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }

        /// <summary>
        /// 抓一個 URL，會手動跟隨 3xx（只跟小紅書系網域），回傳最終頁面。
        /// </summary>
        internal static async Task<FetchResult> GetAsync(
            string url,
            string userAgent,
            string referer,
            Func<string, bool> hostAllowed,
            CancellationToken token)
        {
            Uri current;
            if (!Uri.TryCreate(url, UriKind.Absolute, out current))
                throw new InvalidOperationException("小紅書網址格式不正確。");

            for (int hop = 0; hop < MaxHops; hop++)
            {
                token.ThrowIfCancellationRequested();

                if (current.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    return await FollowHttpsAsync(current, userAgent, referer, hostAllowed, token);

                // 非 https（如 xhscdn 的 http 影片直鏈）不需要繞路，
                // 這裡只處理頁面導覽；真的遇到就交給上層原本的下載器。
                throw new InvalidOperationException(
                    "小紅書導覽遇到非 HTTPS 網址：" + current.AbsoluteUri);
            }

            throw new InvalidOperationException("小紅書 HTTP 重新導向次數過多。");
        }

        private static async Task<FetchResult> FollowHttpsAsync(
            Uri start,
            string userAgent,
            string referer,
            Func<string, bool> hostAllowed,
            CancellationToken token)
        {
            Uri current = start;

            for (int hop = 0; hop < MaxHops; hop++)
            {
                token.ThrowIfCancellationRequested();

                RawResponse raw = await GetSingleAsync(current, userAgent, referer, token);

                if (raw.StatusCode >= 300 && raw.StatusCode < 400 && raw.Headers != null)
                {
                    string location;
                    if (!raw.Headers.TryGetValue("location", out location)
                        || string.IsNullOrWhiteSpace(location))
                        throw new InvalidOperationException("小紅書重新導向沒有提供目的網址。");

                    Uri next;
                    if (!Uri.TryCreate(current, location, out next))
                        throw new InvalidOperationException("小紅書重新導向網址無效。");

                    if (hostAllowed != null && !hostAllowed(next.AbsoluteUri))
                        throw new InvalidOperationException(
                            "小紅書重新導向到不受支援的網域：" + next.Host);

                    current = next;
                    continue;
                }

                if (raw.StatusCode < 200 || raw.StatusCode >= 300)
                    throw new InvalidOperationException(
                        "小紅書網頁回應 HTTP " + raw.StatusCode + "。");

                return new FetchResult
                {
                    StatusCode = raw.StatusCode,
                    Headers = raw.Headers,
                    Body = raw.Body,
                    FinalUrl = current.AbsoluteUri
                };
            }

            throw new InvalidOperationException("小紅書 HTTP 重新導向次數過多。");
        }

        private sealed class RawResponse
        {
            public int StatusCode;
            public Dictionary<string, string> Headers;
            public string Body;
        }

        private static async Task<RawResponse> GetSingleAsync(
            Uri uri,
            string userAgent,
            string referer,
            CancellationToken token)
        {
            string ip = await ResolveIpAsync(uri.Host, token);

            var client = new TcpClient();
            try
            {
                using (token.Register(() => { try { client.Close(); } catch { } }))
                {
                    var connectTask = client.ConnectAsync(ip, uri.Port);
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), token);
                    var done = await Task.WhenAny(connectTask, timeoutTask);
                    if (done != connectTask)
                        throw new TimeoutException("連接小紅書伺服器逾時：" + uri.Host);
                    await connectTask;
                    token.ThrowIfCancellationRequested();

                    using (var ssl = new SslStream(
                        client.GetStream(), false, null, null))
                    {
                        await ssl.AuthenticateAsClientAsync(uri.Host);
                        token.ThrowIfCancellationRequested();

                        string path = string.IsNullOrEmpty(uri.PathAndQuery)
                            ? "/"
                            : uri.PathAndQuery;
                        var req = new StringBuilder();
                        req.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
                        req.Append("Host: ").Append(uri.Host).Append("\r\n");
                        req.Append("User-Agent: ").Append(userAgent).Append("\r\n");
                        req.Append("Accept: text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8\r\n");
                        req.Append("Accept-Language: zh-CN,zh;q=0.9,en;q=0.8\r\n");
                        req.Append("Accept-Encoding: identity\r\n");
                        if (!string.IsNullOrWhiteSpace(referer))
                            req.Append("Referer: ").Append(referer).Append("\r\n");
                        req.Append("Connection: close\r\n\r\n");

                        byte[] reqBytes = Encoding.ASCII.GetBytes(req.ToString());
                        await ssl.WriteAsync(reqBytes, 0, reqBytes.Length, token);
                        await ssl.FlushAsync(token);

                        byte[] all = await ReadAllAsync(ssl, token);
                        return ParseResponse(all);
                    }
                }
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static async Task<byte[]> ReadAllAsync(SslStream ssl, CancellationToken token)
        {
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[64 * 1024];
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token))
                {
                    try
                    {
                        while (true)
                        {
                            int n = await ssl.ReadAsync(buf, 0, buf.Length, linked.Token);
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("讀取小紅書頁面逾時。");
                    }
                }
                return ms.ToArray();
            }
        }

        private static RawResponse ParseResponse(byte[] all)
        {
            int headerEnd = IndexOf(all, new byte[] { 13, 10, 13, 10 });
            if (headerEnd < 0)
                throw new InvalidOperationException("小紅書回應標頭不完整。");

            string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                throw new InvalidOperationException("小紅書回應狀態列遺失。");

            string[] statusParts = lines[0].Split(' ');
            int status;
            if (statusParts.Length < 2
                || !int.TryParse(statusParts[1], out status))
                throw new InvalidOperationException("小紅書回應狀態無法解析：" + lines[0]);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i].Substring(0, colon).Trim()] =
                    lines[i].Substring(colon + 1).Trim();
            }

            int bodyStart = headerEnd + 4;
            byte[] bodyBytes;
            string transfer;
            if (headers.TryGetValue("transfer-encoding", out transfer)
                && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bodyBytes = DecodeChunked(all, bodyStart);
            }
            else
            {
                int len = all.Length - bodyStart;
                string cl;
                int claimed;
                if (headers.TryGetValue("content-length", out cl)
                    && int.TryParse(cl.Trim(), out claimed)
                    && claimed >= 0
                    && claimed < len)
                    len = claimed;
                bodyBytes = new byte[Math.Max(0, len)];
                if (len > 0)
                    Buffer.BlockCopy(all, bodyStart, bodyBytes, 0, len);
            }

            string charset = "utf-8";
            string contentType;
            if (headers.TryGetValue("content-type", out contentType))
            {
                int cs = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                if (cs >= 0)
                {
                    charset = contentType.Substring(cs + 8).Trim().Trim('"', '\'', ';', ' ');
                    if (string.IsNullOrWhiteSpace(charset)) charset = "utf-8";
                }
            }

            string body;
            try { body = Encoding.GetEncoding(charset).GetString(bodyBytes); }
            catch { body = Encoding.UTF8.GetString(bodyBytes); }

            return new RawResponse { StatusCode = status, Headers = headers, Body = body };
        }

        private static byte[] DecodeChunked(byte[] all, int start)
        {
            using (var ms = new MemoryStream())
            {
                int pos = start;
                while (true)
                {
                    int lineEnd = IndexOf(all, new byte[] { 13, 10 }, pos);
                    if (lineEnd < 0) break;
                    string sizeText = Encoding.ASCII.GetString(all, pos, lineEnd - pos);
                    int semi = sizeText.IndexOf(';');
                    if (semi >= 0) sizeText = sizeText.Substring(0, semi);
                    int size;
                    if (!int.TryParse(
                        sizeText.Trim(),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out size))
                        break;
                    pos = lineEnd + 2;
                    if (size == 0) break;
                    if (pos + size > all.Length) break;
                    ms.Write(all, pos, size);
                    pos += size + 2;
                }
                return ms.ToArray();
            }
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
        {
            for (int i = start; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { hit = false; break; }
                }
                if (hit) return i;
            }
            return -1;
        }

        private static async Task<string> ResolveIpAsync(string host, CancellationToken token)
        {
            lock (DnsCacheLock)
            {
                DnsCacheEntry cached;
                if (DnsCache.TryGetValue(host, out cached)
                    && cached != null
                    && cached.ExpiresAtUtc > DateTime.UtcNow
                    && !string.IsNullOrWhiteSpace(cached.Ip))
                    return cached.Ip;
            }

            Exception lastError = null;
            foreach (string template in DohTemplates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string ip = await QueryDohAsync(
                        string.Format(template, Uri.EscapeDataString(host)), token);
                    if (!string.IsNullOrWhiteSpace(ip))
                    {
                        lock (DnsCacheLock)
                        {
                            DnsCache[host] = new DnsCacheEntry
                            {
                                Ip = ip,
                                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
                            };
                        }
                        return ip;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException(
                "DoH 解析小紅書網域失敗：" + host,
                lastError);
        }

        private static async Task<string> QueryDohAsync(string dohUrl, CancellationToken token)
        {
            var request = System.Net.WebRequest.CreateHttp(dohUrl);
            request.Method = "GET";
            request.Accept = "application/dns-json";
            request.UserAgent = "Accessi-download/DoH";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            using (token.Register(() => { try { request.Abort(); } catch { } }))
            using (var response = (System.Net.HttpWebResponse)await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                string text = await reader.ReadToEndAsync();
                var doc = Json.Deserialize<Dictionary<string, object>>(text);
                if (doc == null) return null;

                object answerObj;
                if (!doc.TryGetValue("Answer", out answerObj)) return null;
                var answers = answerObj as System.Collections.ArrayList;
                if (answers == null) return null;

                foreach (object raw in answers)
                {
                    var row = raw as Dictionary<string, object>;
                    if (row == null) continue;

                    object typeObj;
                    if (!row.TryGetValue("type", out typeObj)) continue;
                    int type;
                    if (!int.TryParse(
                        Convert.ToString(typeObj, CultureInfo.InvariantCulture),
                        out type)
                        || type != 1)
                        continue;

                    object dataObj;
                    if (!row.TryGetValue("data", out dataObj)) continue;
                    string ip = Convert.ToString(dataObj, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(ip)) return ip.Trim();
                }

                return null;
            }
        }
    }
}
