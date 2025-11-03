using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace VoeProxy.Client;

/// <summary>
/// Erkennt Endgeräte anhand der Header und passt Parameter
/// dynamisch auf RTT, Throughput und Subnetz an.
/// </summary>
public sealed class ClientClassifier
{
    private readonly ILogger<ClientClassifier> _log;
    private readonly AdaptiveFeedback _feedback;

    private static readonly Regex iOSRegex = new(@"(iphone|ipad|ios|avplayer)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex androidRegex = new(@"(android|exoplayer|firetv|aft)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex tvRegex = new(@"(smart.?tv|tizen|webos|philips|samsung|sony|bravia|hisense|lg)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex desktopRegex = new(@"(windows nt|macintosh|linux|x11|chrome|firefox|edge|safari)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex toolRegex = new(@"(curl|wget|vlc|ffmpeg)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ClientClassifier(ILogger<ClientClassifier> log)
    {
        _log = log;
        _feedback = new AdaptiveFeedback(log);
    }

    public record ClientInfo(
        string Label,
        int RecommendedParallelSegments,
        int RecommendedTargetBitrateKbps,
        bool IsMobile,
        bool IsBrowser);

    /// <summary>
    /// Ermittelt Startwerte basierend auf User-Agent und Netz, passt sie
    /// dynamisch durch gemessene Werte (RTT, Speed) an.
    /// </summary>
    public ClientInfo Classify(HttpContext ctx)
    {
        var ua = ctx.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(ua))
            return DefaultClient("unknown");

        ua = ua.ToLowerInvariant();
        string label;
        int baseBr;
        int segs;
        bool mobile;
        bool browser;

        if (iOSRegex.IsMatch(ua)) { label = "ios"; baseBr = 2500; segs = 3; mobile = true; browser = false; }
        else if (androidRegex.IsMatch(ua)) { label = "android"; baseBr = 3000; segs = 4; mobile = true; browser = false; }
        else if (tvRegex.IsMatch(ua)) { label = "smarttv"; baseBr = 8000; segs = 6; mobile = false; browser = false; }
        else if (desktopRegex.IsMatch(ua)) { label = "desktop"; baseBr = 4500; segs = 3; mobile = false; browser = true; }
        else if (toolRegex.IsMatch(ua)) { label = "tool"; baseBr = 2000; segs = 2; mobile = false; browser = false; }
        else { label = "generic"; baseBr = 3000; segs = 3; mobile = false; browser = false; }

        // --- Geo-Adaption / WireGuard-Erkennung ---
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is not null)
        {
            if (ip.ToString().StartsWith("10.") || ip.ToString().StartsWith("172.16."))
            {
                baseBr = (int)(baseBr * 0.8);
                label += "-vpn";
            }
            else if (ip.IsIPv6Teredo)
            {
                baseBr = (int)(baseBr * 0.9);
                label += "-ipv6";
            }
        }

        // --- Dynamische Anpassung durch Feedback ---
        var stats = _feedback.GetStats(ip);
        if (stats is not null)
        {
            var adj = stats.AdjustBitrate(baseBr);
            _log.LogTrace("Adaptive adjust: RTT={rtt:F1}ms Speed={spd:F0}kbps => BR={br}",
                stats.AvgRttMs, stats.AvgKbps, adj);
            baseBr = adj;
        }

        return new(label, segs, baseBr, mobile, browser);
    }

    public void RecordSegmentMetrics(IPAddress? ip, long bytes, TimeSpan duration)
        => _feedback.Record(ip, bytes, duration);

    /// <summary>
    /// Meldet direkt RTT eines Clients (z. B. via SegmentResponder)
    /// </summary>
    public void ReportRTT(string clientLabel, TimeSpan rtt)
    {
        if (string.IsNullOrEmpty(clientLabel))
            return;
        _feedback.RecordRTT(clientLabel, rtt);
    }

    private static ClientInfo DefaultClient(string label)
        => new(label, 3, 3000, false, false);
}

// ---------------------------------------------------------------------------
// Feedback-System für RTT- und Durchsatzmessung pro Client-IP
// ---------------------------------------------------------------------------
internal sealed class AdaptiveFeedback
{
    private readonly ConcurrentDictionary<string, ClientNetStats> _stats = new();
    private readonly ILogger _log;
    private static readonly TimeSpan Decay = TimeSpan.FromMinutes(5);

    public AdaptiveFeedback(ILogger log)
    {
        _log = log;
    }

    public void Record(IPAddress? ip, long bytes, TimeSpan duration)
    {
        if (ip is null || duration.TotalMilliseconds < 5) return;
        var key = ip.ToString();

        var speedKbps = (int)((bytes * 8.0) / 1000.0 / Math.Max(0.001, duration.TotalSeconds));
        var rttMs = (int)Math.Clamp(duration.TotalMilliseconds, 1, 8000);

        var entry = _stats.GetOrAdd(key, _ => new ClientNetStats());
        entry.AddSample(rttMs, speedKbps);
    }

    public void RecordRTT(string clientKey, TimeSpan rtt)
    {
        var ms = (int)Math.Clamp(rtt.TotalMilliseconds, 1, 8000);
        var entry = _stats.GetOrAdd(clientKey, _ => new ClientNetStats());
        entry.AddSample(ms, null);
    }

    public ClientNetStats? GetStats(IPAddress? ip)
    {
        if (ip is null) return null;
        if (_stats.TryGetValue(ip.ToString(), out var s))
            return s.IsExpired ? null : s;
        return null;
    }

    internal sealed class ClientNetStats
    {
        private const int MaxSamples = 20;
        private readonly Queue<int> _rtt = new();
        private readonly Queue<int> _kbps = new();
        private DateTime _last = DateTime.UtcNow;

        public bool IsExpired => DateTime.UtcNow - _last > Decay;
        public double AvgRttMs => _rtt.Count == 0 ? 0 : _rtt.Average();
        public double AvgKbps => _kbps.Count == 0 ? 0 : _kbps.Average();

        public void AddSample(int rttMs, int? kbps)
        {
            if (_rtt.Count >= MaxSamples) _rtt.Dequeue();
            _rtt.Enqueue(rttMs);
            if (kbps is { } v)
            {
                if (_kbps.Count >= MaxSamples) _kbps.Dequeue();
                _kbps.Enqueue(v);
            }
            _last = DateTime.UtcNow;
        }

        public int AdjustBitrate(int baseKbps)
        {
            var adj = baseKbps;
            if (AvgRttMs > 400) adj = (int)(adj * 0.7);
            else if (AvgRttMs > 200) adj = (int)(adj * 0.85);

            if (AvgKbps > 0 && AvgKbps < baseKbps * 0.75)
                adj = (int)(adj * 0.8);

            adj = Math.Clamp(adj, 400, 12000);
            return adj;
        }
    }
}

// ---------------------------------------------------------------------------
// HttpContext-Helper
// ---------------------------------------------------------------------------
public static class HttpContextClientExtensions
{
    public static ClientClassifier.ClientInfo GetClientInfo(this HttpContext ctx)
    {
        var classifier = ctx.RequestServices.GetRequiredService<ClientClassifier>();
        return classifier.Classify(ctx);
    }

    public static void RecordSegment(this HttpContext ctx, long bytes, Stopwatch timer)
    {
        var classifier = ctx.RequestServices.GetRequiredService<ClientClassifier>();
        classifier.RecordSegmentMetrics(ctx.Connection.RemoteIpAddress, bytes, timer.Elapsed);
    }

    public static string ToShortLabel(this ClientClassifier.ClientInfo ci)
        => ci.Label;
}
