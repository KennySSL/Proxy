// File: /VoeProxy/Infrastructure/VoeNetMerged.cs
using System.Buffers;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace VoeProxy.Infrastructure
{
    /// <summary>RFC 4648 Base64URL: '+'→'-', '/'→'_', ohne '=' Padding.</summary>
    public sealed class Base64Url
    {
        public string Encode(ReadOnlySpan<byte> data)
        {
            var b64 = Convert.ToBase64String(data);
            return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        public string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

        public byte[] Decode(string base64Url)
        {
            var s = base64Url.Replace('-', '+').Replace('_', '/');
            var mod = s.Length % 4;
            return mod switch
            {
                2 => Convert.FromBase64String(s + "=="),
                3 => Convert.FromBase64String(s + "="),
                0 => Convert.FromBase64String(s),
                _ => throw new FormatException("invalid base64url length")
            };
        }



        public string DecodeToString(string base64Url) => Encoding.UTF8.GetString(Decode(base64Url));
    }

    /// <summary>Schneller Allow-List Header-Kopierer (Upstream → ASP.NET Response).</summary>
    public sealed class HeadersCopy
    {
        public void CopyAllowList(HttpResponseMessage src, HttpResponse dst, ReadOnlySpan<string> allow)
        {
            foreach (var name in allow)
            {
                if (src.Headers.TryGetValues(name, out var vals))
                    dst.Headers[name] = vals.ToArray();
                else if (src.Content.Headers.TryGetValues(name, out var cvals))
                    dst.Headers[name] = cvals.ToArray();
            }
        }
    }

    /// <summary>URL-Helfer mit sicherer Normalisierung.</summary>
    public sealed class Urls
    {
        public string Combine(string baseUrl, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return baseUrl;
            if (Uri.TryCreate(relative, UriKind.Absolute, out var abs)) return abs.ToString();
            return new Uri(new Uri(baseUrl, UriKind.Absolute), relative).ToString();
        }
    }

    /// <summary>HttpClient für hohen Parallelismus (H2/H3, kein AutoRedirect, keine Cookies).</summary>
    public sealed class HighParallelHttpClient
    {
        public HttpClient Client { get; }

        public HighParallelHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                MaxConnectionsPerServer = 512,
                EnableMultipleHttp2Connections = true,
                EnableMultipleHttp3Connections = true,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(8),
                SslOptions =
                {
                    RemoteCertificateValidationCallback = static (_, __, ___, errors) =>
                        errors == SslPolicyErrors.None
                }
            };

            Client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(90),
                DefaultRequestVersion = HttpVersion.Version30,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
            };
            Client.DefaultRequestHeaders.Accept.TryParseAdd("*/*");
        }
    }
}

namespace VoeProxy.Extraction
{
    public static partial class VoeDecoders
    {
        [GeneratedRegex(@"(?:@\$|\^\^|~@|%\?|\*~|!!|#&)", RegexOptions.CultureInvariant)]
        private static partial Regex JunkRegex();

        public static string Rot13(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            Span<char> buffer = stackalloc char[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                buffer[i] =
                    c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 13) % 26) :
                    c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 13) % 26) :
                    c;
            }
            return new string(buffer);
        }

        public static string StripJunk(string input)
            => JunkRegex().Replace(input ?? string.Empty, string.Empty);

        public static string Base64Decode(string s)
        {
            if (s is null) return string.Empty;
            int maxLen = (s.Length / 4) * 3 + 3;

            if (maxLen <= 1024)
            {
                Span<byte> tmp = stackalloc byte[maxLen];
                if (Convert.TryFromBase64String(s, tmp, out int written))
                    return Encoding.UTF8.GetString(tmp[..written]);
            }

            var bytes = Convert.FromBase64String(s);
            return Encoding.UTF8.GetString(bytes);
        }

        public static string AsciiShift(string input, int delta)
        {
            if (input is null) return string.Empty;
            Span<char> buf = stackalloc char[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                var c = input[i];
                buf[i] = c < 128 ? (char)(c + delta) : c;
            }
            return new string(buf);
        }

        public static string Reverse(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return string.Create(s.Length, s, static (span, src) =>
            {
                for (int i = 0, j = src.Length - 1; j >= 0; i++, j--)
                    span[i] = src[j];
            });
        }

        /// <summary>VOE AppJSON-Kette: ROT13 → JunkStrip → B64 → ASCII-3 → Reverse → B64.</summary>
        public static string DecodeAppJsonPayload(string encoded)
        {
            var step1 = Rot13(encoded);
            var step2 = StripJunk(step1);
            var step3 = Base64Decode(step2);
            var step4 = AsciiShift(step3, -3);
            var step5 = Reverse(step4);
            return Base64Decode(step5);
        }
    }

    // ---------------------------------------------
    // VoeExtractor (.NET 9 modernisiert, Log/Regex unverändert)
    // ---------------------------------------------
    public sealed partial class VoeExtractor
    {
        private readonly HttpClient _client;
        private readonly ILogger<VoeExtractor> _log;
        private readonly string _referer;
        private readonly string _origin;

        [GeneratedRegex(@"https?://[^\s'""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex AnyAbsUrlRegex();

        [GeneratedRegex(@"https://(?:www\.)?voe\.sx/[^\s'""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex VoeUrlRegex();

        [GeneratedRegex(@"<script[^>]*type=(['""])application/json\1[^>]*>(?<json>.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex AppJsonRegex();

        [GeneratedRegex(@"var\s+a168c\s*=\s*'([^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex B64Regex();

        [GeneratedRegex(@"'hls'\s*:\s*'(?<hls>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex HlsRegex();

        public VoeExtractor(HttpClient client, string referer, string origin, ILogger<VoeExtractor> log)
        {
            _client = client;
            _referer = referer;
            _origin = origin;
            _log = log;
        }

        public async Task<(bool ok, string? hls, string? cookie, string? referer, string? error)>
            TryExtractAsync(string sourceUrl)
        {
            try
            {
                var (ok1, html1, req1Uri, loc1, _) =
                    await FetchHtmlWithRedirects(sourceUrl, addVoeHeaders: false, followMax: 2);
                if (!ok1) return (false, null, null, null, "aniworld-fetch-failed");

                var hop2 = !string.IsNullOrWhiteSpace(loc1)
                    ? loc1
                    : ExtractFirstUrlOrFallback(html1, req1Uri);
                if (string.IsNullOrWhiteSpace(hop2))
                    return (false, null, null, null, "no-redirect-url");

                var (ok2, html2, req2Uri, loc2, cookies2) =
                    await FetchHtmlWithRedirects(hop2, addVoeHeaders: true, followMax: 5);
                if (!ok2) return (false, null, null, null, "voe-fetch-failed");

                _log.LogInformation("[EXTRACT] resolved to VOE page: {url}", string.IsNullOrEmpty(loc2) ? req2Uri : loc2);

                if (TryDecodeFromHtml(html2, out var hls))
                    return (true, hls!, cookies2, _referer, null);

                var nested = VoeUrlRegex().Match(html2);
                if (nested.Success && !nested.Value.Equals(req2Uri, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("[EXTRACT] nested VOE URL → {url}", nested.Value);
                    var (ok3, html3, _, _, cookies3) =
                        await FetchHtmlWithRedirects(nested.Value, addVoeHeaders: true, followMax: 5);
                    if (ok3 && TryDecodeFromHtml(html3, out var hls2))
                        return (true, hls2!, string.IsNullOrEmpty(cookies3) ? cookies2 : cookies3, _referer, null);
                }

                _log.LogWarning("[EXTRACT] patterns-not-found after redirect chain: {src} -> {voe}", sourceUrl, hop2);
                return (false, null, null, null, "patterns-not-found");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[EXTRACT] exception for {url}", sourceUrl);
                return (false, null, null, null, ex.GetType().Name);
            }
        }

        private static string ExtractFirstUrlOrFallback(string html, string fallback)
        {
            var m = VoeUrlRegex().Match(html);
            if (m.Success) return m.Value;
            m = AnyAbsUrlRegex().Match(html);
            return m.Success ? m.Value : fallback;
        }

        private bool TryDecodeFromHtml(string html, out string? hls)
        {
            hls = null;
            var htmlSpan = html.AsSpan();

            if (htmlSpan.Contains("application/json".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var match = AppJsonRegex().Match(html);
                    if (match.Success)
                    {
                        var raw = match.Groups["json"].Value.Trim();
                        if (raw.Length >= 4) raw = raw[2..^2];
                        var decoded = VoeDecoders.DecodeAppJsonPayload(raw);
                        using var doc = JsonDocument.Parse(decoded);
                        if (doc.RootElement.TryGetProperty("source", out var src)
                            && src.GetString() is { Length: > 0 } s1)
                        {
                            hls = s1;
                            return true;
                        }
                    }
                }
                catch { }
            }

            if (htmlSpan.Contains("a168c".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var match = B64Regex().Match(html);
                    if (match.Success)
                    {
                        var json = VoeDecoders.Reverse(VoeDecoders.Base64Decode(match.Groups[1].Value));
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("source", out var src)
                            && src.GetString() is { Length: > 0 } s2)
                        {
                            hls = s2;
                            return true;
                        }
                    }
                }
                catch { }
            }

            if (htmlSpan.Contains("'hls'".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var match = HlsRegex().Match(html);
                    if (match.Success)
                    {
                        var candidate = VoeDecoders.Base64Decode(match.Groups["hls"].Value);
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            hls = candidate;
                            return true;
                        }
                    }
                }
                catch { }
            }

            return false;
        }

        private async Task<(bool ok, string html, string requestUri, string? location, string cookies)>
            FetchHtmlWithRedirects(string startUrl, bool addVoeHeaders, int followMax)
        {
            string url = startUrl;
            var cookieSb = new StringBuilder();

            for (int i = 0; i < followMax; i++)
            {
                var (ok, html, req, loc, setCookie) = await FetchOnce(url, addVoeHeaders);
                if (!ok) return (false, string.Empty, startUrl, null, string.Empty);

                if (!string.IsNullOrEmpty(setCookie))
                    cookieSb.Append(cookieSb.Length > 0 ? "; " : "").Append(setCookie);

                if (!string.IsNullOrEmpty(loc))
                {
                    url = loc;
                    if (i == 0) _log.LogInformation("[EXTRACT] 3xx redirect → {url}", url);
                    continue;
                }

                return (true, html, req, null, cookieSb.ToString());
            }

            return await FetchOnce(url, addVoeHeaders);
        }

        private async Task<(bool ok, string html, string requestUri, string? location, string cookies)>
            FetchOnce(string url, bool addVoeHeaders)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            if (addVoeHeaders)
            {
                req.Headers.Referrer = new Uri(_referer);
                req.Headers.TryAddWithoutValidation("Origin", _origin);
            }

            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var reqUri = resp.RequestMessage!.RequestUri!.ToString();

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is not null)
            {
                var locAbs = new Uri(resp.RequestMessage!.RequestUri!, resp.Headers.Location).ToString();
                return (true, string.Empty, reqUri, locAbs, JoinCookies(resp));
            }

            var html = await resp.Content.ReadAsStringAsync();
            return (true, html, reqUri, null, JoinCookies(resp));
        }

        private static string JoinCookies(HttpResponseMessage resp)
        {
            if (!resp.Headers.TryGetValues("Set-Cookie", out var vals)) return string.Empty;
            var sb = new StringBuilder();
            bool first = true;
            foreach (var v in vals)
            {
                int semi = v.IndexOf(';');
                ReadOnlySpan<char> piece = semi >= 0 ? v.AsSpan(0, semi) : v.AsSpan();
                if (!first) sb.Append("; ");
                sb.Append(piece);
                first = false;
            }
            return sb.ToString();
        }
    }
}
