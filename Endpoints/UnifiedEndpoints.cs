// File: /VoeProxy/Endpoints/UnifiedEndpoints.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using VoeProxy.Cache;
using VoeProxy.Client;
using VoeProxy.Config;
using VoeProxy.Extraction;
using VoeProxy.Infrastructure;
using VoeProxy.Sessions;

namespace VoeProxy.Endpoints;

[ApiController]
[Route("/")]
[ResponseCache(NoStore = true, Duration = 0)]
public sealed class UnifiedEndpoints : ControllerBase
{
    private readonly HttpClient _http;
    private readonly ProxyOptions _opt;
    private readonly UnifiedPipelineCache _cache;
    private readonly Base64Url _b64;
    private readonly VoeExtractor _extractor;
    private readonly ClientClassifier _classifier;
    private readonly ILogger<UnifiedEndpoints> _log;

    private static readonly ActivitySource ActivitySrc = new("VoeProxy.Streamer");
    private static readonly Meter Meter = new("VoeProxy.Unified", "1.0");
    private static readonly Counter<long> BytesStreamed = Meter.CreateCounter<long>("voeproxy_streamed_bytes");

    private static readonly ConcurrentDictionary<string, byte> _prefetchSet = new();
    private const int MaxSegmentSizeBytes = 25 * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private static readonly DateTime _start = DateTime.UtcNow;

    public UnifiedEndpoints(
        HttpClient http,
        ProxyOptions opt,
        UnifiedPipelineCache cache,
        Base64Url b64,
        VoeExtractor extractor,
        ClientClassifier classifier,
        ILogger<UnifiedEndpoints> log)
    {
        _http = http;
        _opt = opt;
        _cache = cache;
        _b64 = b64;
        _extractor = extractor;
        _classifier = classifier;
        _log = log;
    }

    // ======================================================================
    // Proxy Entry Point
    // ======================================================================
    [HttpGet("proxy_live")]
    public async Task<IResult> ProxyLive([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest("missing-url");

        var (preferredSid, aliases) = Session.FromUrl(url);
        string sidToUse = preferredSid;

        foreach (var k in aliases)
        {
            if (_cache.TryGetSession(k, out var existing) && existing is not null)
            {
                sidToUse = k;
                Response.Headers["X-Client-Kind"] = existing.Value.ClientLabel ?? "-";
                Response.Headers["X-SID"] = sidToUse;
                var plKeyExisting = $"pl:{_b64.Encode(existing.Value.Hls)}";
                await SmartPlaylistResponderAsync(plKeyExisting, existing.Value.Hls, sidToUse, existing.Value.ClientLabel ?? "-", ct);
                return Results.Empty;
            }
        }

        var (ok, hls, cookie, referer, err) = await _extractor.TryExtractAsync(url);
        if (!ok || string.IsNullOrWhiteSpace(hls))
            return Results.Problem(detail: err ?? "resolve-failed", statusCode: 502);

        var ci = HttpContext.GetClientInfo();
        var label = ci.ToShortLabel();
        var perSidLimit = Math.Min(_opt.PER_SID_UPSTREAM_LIMIT, ci.RecommendedParallelSegments);

        foreach (var alias in aliases)
        {
            _cache.UpsertSession(alias, url, hls!, cookie ?? "", referer ?? "", perSidLimit, label, ci.RecommendedTargetBitrateKbps);
        }

        Response.Headers["X-Client-Kind"] = label;
        Response.Headers["X-SID"] = sidToUse;

        var plKey = $"pl:{_b64.Encode(hls!)}";
        await SmartPlaylistResponderAsync(plKey, hls!, sidToUse, label, ct);
        return Results.Empty;
    }

    // ======================================================================
    // Playlist Handler
    // ======================================================================
    [HttpGet("pl/{token}.m3u8")]
    public async Task<IResult> Playlist([FromRoute] string token, [FromQuery] string? sid, CancellationToken ct)
    {
        string url;
        try { url = _b64.DecodeToString(token); }
        catch { return Results.BadRequest("invalid-token"); }

        var key = $"pl:{token}";
        var label = sid is not null && _cache.TryGetSession(sid, out var e) && e is not null
            ? e.Value.ClientLabel ?? "-"
            : "-";

        await SmartPlaylistResponderAsync(key, EnsureAbsoluteUrl(url, Request), sid, label, ct);
        return Results.Empty;
    }

    [HttpHead("pl/{token}.m3u8")]
    public IResult PlaylistHead([FromRoute] string token, [FromQuery] string? sid)
    {
        var key = $"pl:{token}";
        Response.ContentType = "application/vnd.apple.mpegurl"; // ohne charset für maximale Kompatibilität
        Response.Headers["Cache-Control"] = "no-store";
        if (sid is not null) Response.Headers["X-SID"] = sid;
        if (_cache.TryGetPlaylist(key, out var cached))
            Response.ContentLength = cached.LongLength;
        return Results.Empty;
    }

    // ======================================================================
    // Segment Handler
    // ======================================================================
    [HttpGet("seg/{tokenAndExt}")]
    public async Task<IResult> Segment([FromRoute] string tokenAndExt, [FromQuery] string? sid, CancellationToken ct)
    {
        var dot = tokenAndExt.LastIndexOf('.');
        var token = dot > 0 ? tokenAndExt[..dot] : tokenAndExt;
        var ext = dot > 0 ? tokenAndExt[dot..] : null;

        await StreamSegmentAsync(HttpContext, sid, token, ext, ct);
        return Results.Empty;
    }

    [HttpHead("seg/{tokenAndExt}")]
    public IResult SegmentHead([FromRoute] string tokenAndExt, [FromQuery] string? sid)
    {
        var dot = tokenAndExt.LastIndexOf('.');
        var token = dot > 0 ? tokenAndExt[..dot] : tokenAndExt;
        var ext = dot > 0 ? tokenAndExt[dot..] : null;

        Response.Headers["Accept-Ranges"] = "bytes";
        Response.ContentType = GuessContentType(ext);
        if (sid is not null) Response.Headers["X-SID"] = sid;

        var segKey = $"seg:{token}";
        if (_cache.TryGetSegment(segKey, out var seg))
            Response.ContentLength = seg.LongLength;

        return Results.Empty;
    }

    // ======================================================================
    // Metrics & Health
    // ======================================================================
    [HttpGet("metrics")]
    public IResult Metrics()
    {
        var (sidCount, globalInUse, globalFree, pl, sg, meta) = _cache.GetStats();

        return Results.Json(new
        {
            pid = Environment.ProcessId,
            memoryMB = GC.GetTotalMemory(false) / (1024 * 1024),
            uptime = $"{(DateTime.UtcNow - _start):hh\\:mm\\:ss}",
            cache = new { playlists = pl, segments = sg, meta },
            sids = new { count = sidCount, globalInUse, globalFree },
            limits = new { _opt.GLOBAL_UPSTREAM_LIMIT, _opt.PER_SID_UPSTREAM_LIMIT }
        });
    }

    [HttpGet("health")]
    public static IResult Health() => Results.Ok(new { status = "ok" });

    // ======================================================================
    // Segment Streaming Logic (mit echtem Byte-Range 206)
    // ======================================================================
    private async Task StreamSegmentAsync(HttpContext ctx, string? sid, string token, string? extHint, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sid) || !_cache.TryGetSession(sid, out var entry) || entry is null)
        {
            ctx.Response.StatusCode = 440;
            await ctx.Response.WriteAsync("invalid-sid", ct);
            return;
        }

        using var lease = await _cache.AcquireSessionAsync(sid, ct);
        if (lease is null)
        {
            ctx.Response.StatusCode = 429;
            await ctx.Response.WriteAsync("sid-limit", ct);
            return;
        }

        string upstreamUrl = _b64.DecodeToString(token);
        string segKey = $"seg:{token}";

        byte[] segmentData = await _cache.GetOrFetchAsync(
            segKey,
            async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", "VoeProxy/1.0");
                if (!string.IsNullOrEmpty(entry.Value.Cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", entry.Value.Cookie);
                if (!string.IsNullOrEmpty(entry.Value.Referer))
                    req.Headers.Referrer = new Uri(entry.Value.Referer);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("[UPSTREAM-FAIL] {sid} {url} :: {code}", sid, upstreamUrl, resp.StatusCode);
                    ctx.Response.StatusCode = (int)resp.StatusCode;
                    return Array.Empty<byte>();
                }

                return await resp.Content.ReadAsByteArrayAsync(ct);
            },
            TimeSpan.FromMinutes(5)
        );

        if (segmentData.Length == 0)
            return;

        var timer = Stopwatch.StartNew();

        // Range-Parsing (Single Range)
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        long from = 0, to = segmentData.LongLength - 1;
        bool isRange = TryParseSingleRange(rangeHeader, segmentData.LongLength, out from, out to);

        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.ContentType = GuessContentType(extHint);

        if (isRange)
        {
            if (from < 0 || to >= segmentData.LongLength || from > to)
            {
                ctx.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                ctx.Response.Headers["Content-Range"] = $"bytes */{segmentData.LongLength}";
                return;
            }

            var len = to - from + 1;
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.Headers["Content-Range"] = $"bytes {from}-{to}/{segmentData.LongLength}";
            ctx.Response.ContentLength = len;
            await ctx.Response.StartAsync(ct);

            // Write slice
            int iFrom = (int)from;
            int iLen = (int)len;
            await ctx.Response.Body.WriteAsync(segmentData, iFrom, iLen, ct);
            await ctx.Response.Body.FlushAsync(ct);

            BytesStreamed.Add(len);
            ctx.RecordSegment(len, timer);
            _log.LogDebug("[STREAMED-RANGE] {sid} {kb:F1} KB ({from}-{to})", sid, len / 1024.0, from, to);
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentLength = segmentData.LongLength;
            await ctx.Response.StartAsync(ct);

            await ctx.Response.Body.WriteAsync(segmentData, ct);
            await ctx.Response.Body.FlushAsync(ct);

            BytesStreamed.Add(segmentData.LongLength);
            ctx.RecordSegment(segmentData.LongLength, timer);
            _log.LogDebug("[STREAMED-CACHED] {sid} {kb:F1} KB", sid, segmentData.LongLength / 1024.0);

            if (TryPredictNextSegment(new Uri(upstreamUrl), out var nextUrl))
                _ = PrefetchNextAsync(nextUrl, entry.Value.Cookie, entry.Value.Referer, sid, ct);
        }
    }

    private async Task PrefetchNextAsync(Uri nextUrl, string? cookie, string? referer, string sid, CancellationToken outerToken)
    {
        string key = $"seg:{_b64.Encode(nextUrl.ToString())}";
        if (!_prefetchSet.TryAdd(key, 0)) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            if (_cache.TryGetSegment(key, out _)) return;

            var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "VoeProxy/1.0");
            if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            await using var upstream = await resp.Content.ReadAsStreamAsync(cts.Token);
            await using var ms = new MemoryStream(capacity: BufferSize * 2);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            try
            {
                int read;
                while ((read = await upstream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
                {
                    await ms.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    if (ms.Length > MaxSegmentSizeBytes) return;
                }

                if (ms.Length > 0)
                    _cache.SetSegment(key, ms.ToArray());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _prefetchSet.TryRemove(key, out _);
        }
    }

    private static bool TryPredictNextSegment(Uri current, out Uri next)
    {
        var path = current.AbsoluteUri;
        var match = Regex.Match(path, @"(\d+)(\.[a-z0-9]+)?$", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
        {
            var nextNum = (num + 1).ToString(new string('0', match.Groups[1].Value.Length));
            next = new Uri(path[..match.Groups[1].Index] + nextNum + (match.Groups[2].Success ? match.Groups[2].Value : ""));
            return true;
        }
        next = current;
        return false;
    }

    // ======================================================================
    // Playlist Rewrite
    // ======================================================================
    private async Task SmartPlaylistResponderAsync(string cacheKey, string upstreamUrl, string? sid, string clientLabel, CancellationToken ct)
    {
        if (_cache.TryGetPlaylist(cacheKey, out var cached))
        {
            Response.ContentType = "application/vnd.apple.mpegurl"; // ohne charset
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["X-Client-Kind"] = clientLabel;
            if (sid is not null) Response.Headers["X-SID"] = sid;
            await Response.StartAsync(ct);
            await Response.Body.WriteAsync(cached, ct);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", _opt.USER_AGENT);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Response.StatusCode = (int)resp.StatusCode;
            return;
        }

        await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
        Response.ContentType = "application/vnd.apple.mpegurl"; // ohne charset
        Response.Headers["Cache-Control"] = "no-store";

        await RewritePlaylistToStreamAsync(upstream, Response.Body, upstreamUrl, $"{Request.Scheme}://{Request.Host}/pl/", sid, ct);
    }

    private async Task RewritePlaylistToStreamAsync(Stream upstream, Stream downstream, string upstreamUrl, string selfBasePl, string? sid, CancellationToken ct)
    {
        if (!selfBasePl.EndsWith("/pl/", StringComparison.Ordinal))
            selfBasePl = selfBasePl.TrimEnd('/') + "/pl/";

        string selfBaseSeg = selfBasePl.Replace("/pl/", "/seg/", StringComparison.Ordinal);
        string sidPart = !string.IsNullOrEmpty(sid) ? $"?sid={Uri.EscapeDataString(sid)}" : string.Empty;

        var upstreamUri = new Uri(upstreamUrl);
        var baseUri = new Uri(upstreamUri, ".");

        using var reader = new StreamReader(upstream, Encoding.UTF8, true, 64 * 1024, leaveOpen: true);
        using var cacheBuffer = new MemoryStream(capacity: 64 * 1024);
        byte[] rent = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var lf = new byte[] { (byte)'\n' };

        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                string outLine = RewritePlaylistLine(line, baseUri, selfBasePl, selfBaseSeg, sidPart);
                int written = Encoding.UTF8.GetBytes(outLine, rent);

                await downstream.WriteAsync(rent.AsMemory(0, written), ct);
                await downstream.WriteAsync(lf.AsMemory(), ct);

                await cacheBuffer.WriteAsync(rent.AsMemory(0, written), ct);
                await cacheBuffer.WriteAsync(lf.AsMemory(), ct);
            }

            await downstream.FlushAsync(ct);

            string cacheKey = $"pl:{_b64.Encode(upstreamUrl)}";
            _cache.SetPlaylist(cacheKey, cacheBuffer.ToArray());
            _log.LogTrace("[CACHE-SET] playlist {url} ({kb:F1} KB)", upstreamUrl, cacheBuffer.Length / 1024.0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rent);
        }
    }

    private string RewritePlaylistLine(string line, Uri baseUri, string selfBasePl, string selfBaseSeg, string sidPart)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        if (line[0] == '#')
        {
            if (line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
            {
                return Regex.Replace(line, @"(URI\s*=\s*)([""'])(?<u>[^""']+)\2",
                    m =>
                    {
                        var orig = m.Groups["u"].Value;
                        if (string.IsNullOrWhiteSpace(orig)) return m.Value;
                        string abs = ResolveRelativeUrl(baseUri, orig);
                        string prox = ProxyWithExt(abs, selfBasePl, selfBaseSeg, sidPart);
                        return $"{m.Groups[1].Value}\"{prox}\"";
                    },
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }
            return line;
        }

        string absLine = ResolveRelativeUrl(baseUri, line);
        return ProxyWithExt(absLine, selfBasePl, selfBaseSeg, sidPart);
    }

    private static string ResolveRelativeUrl(Uri baseUri, string maybeRelative)
    {
        if (string.IsNullOrWhiteSpace(maybeRelative)) return maybeRelative;
        if (Uri.TryCreate(maybeRelative, UriKind.Absolute, out var abs)) return abs.AbsoluteUri;

        try { return new Uri(baseUri, maybeRelative).AbsoluteUri; }
        catch { return maybeRelative; }
    }

    private string ProxyWithExt(string absoluteUrl, string plBase, string segBase, string sidPart)
    {
        string path = absoluteUrl;
        int q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) path = path[..q];

        string ext = Path.GetExtension(path).ToLowerInvariant();
        string token = _b64.Encode(absoluteUrl);

        return ext switch
        {
            ".m3u8" => $"{plBase}{token}.m3u8{sidPart}",
            _ => $"{segBase}{token}{ext}{sidPart}"
        };
    }

    // ======================================================================
    // Helpers
    // ======================================================================
    private static bool TryParseSingleRange(string rangeHeader, long totalLength, out long from, out long to)
    {
        from = 0; to = totalLength - 1;
        if (string.IsNullOrEmpty(rangeHeader)) return false;
        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        var v = rangeHeader.Substring(6);
        var parts = v.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        if (!long.TryParse(parts[0], out var start)) return false;
        long end = to;
        if (parts.Length == 2 && long.TryParse(parts[1], out var parsedEnd)) end = parsedEnd;

        start = Math.Clamp(start, 0, totalLength - 1);
        end = Math.Clamp(end, start, totalLength - 1);

        from = start; to = end; return true;
    }

    private static string GuessContentType(string? ext) => ext?.ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        ".m4s" => "video/iso.segment",
        ".mp4" => "video/mp4",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ac3" => "audio/ac3",
        ".eac3" => "audio/eac3",
        ".mp3" => "audio/mpeg",
        ".webm" => "video/webm",
        ".vtt" => "text/vtt",
        _ => "application/octet-stream"
    };

    private static string EnsureAbsoluteUrl(string url, HttpRequest request)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)) return abs.ToString();
        var baseUri = $"{request.Scheme}://{request.Host}";
        return new Uri(new Uri(baseUri + "/"), url.TrimStart('/')).ToString();
    }
}
