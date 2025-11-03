// /VoeProxy/Config/ProxyOptions.cs
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using Microsoft.Extensions.Logging;

namespace VoeProxy.Config;

/// <summary>
/// Zentrale Konfiguration (env-overridebar via Section "Proxy").
/// Alle Werte haben Defaults; zusätzlich existieren typisierte Helper-Properties (TimeSpan).
/// </summary>
public sealed class ProxyOptions
{
    // -----------------------------
    // Limits
    // -----------------------------
    /// <summary>Globales Limit gleichzeitiger Upstream-Requests (harte Kappe).</summary>
    [Range(1, 4096)]
    public int GLOBAL_UPSTREAM_LIMIT { get; set; } = 48;
    public int MAX_SEGMENT_SIZE_MB { get; set; } = 25;
    /// <summary>Pro Session/SID erlaubte gleichzeitige Upstream-Requests.</summary>
    [Range(1, 64)]
    public int PER_SID_UPSTREAM_LIMIT { get; set; } = 8;
    public int PREFETCH_AHEAD_COUNT { get; set; } = 5;
    // In: /VoeProxy/Config/ProxyOptions.cs  -> innerhalb der Klasse ProxyOptions
    /// <summary>
    /// Optionaler HLS-Referer für bestimmte Upstream-CDNs (z.B. VOE).
    /// Wenn gesetzt, wird er als Referrer-Header verwendet.
    /// </summary>
    public string? HlsReferer { get; init; } = null;

    /// <summary>
    /// Optionaler Cookie-String für den Upstream (z.B. Session/Geo-Bits).
    /// Wird 1:1 als "Cookie:" weitergereicht.
    /// </summary>
    public string? UpstreamCookie { get; init; } = null;

    // -----------------------------
    // Prefetch
    // -----------------------------
    /// <summary>Wie viele Segmente im Voraus geholt werden.</summary>
    [Range(0, 16)]
    public int PREFETCH_AHEAD { get; set; } = 2;

    /// <summary>Max. Prefetch-Bytes pro SID im Flug.</summary>
    [Range(0, int.MaxValue)]
    public int PER_SID_PREFETCH_BYTES { get; set; } = 16 * 1024 * 1024;

    // -----------------------------
    // Cache
    // -----------------------------
    /// <summary>Playlist-Metadaten TTL (Sekunden).</summary>
    [Range(0, 600)]
    public int PLAYLIST_TTL_SEC { get; set; } = 15;

    /// <summary>Segment-Cache TTL (Sekunden).</summary>
    [Range(0, 600)]
    public int SEG_CACHE_TTL_SEC { get; set; } = 20;

    /// <summary>Maximale Segmentgröße im Cache (Bytes).</summary>
    [Range(64 * 1024, 256 * 1024 * 1024)]
    public int SEG_CACHE_MAX_ITEM_BYTES { get; set; } = 8 * 1024 * 1024;

    // -----------------------------
    // Chunks (I/O-Buffering)
    // -----------------------------
    /// <summary>Chunk-Größe beim Senden von Playlists (Bytes).</summary>
    [Range(4 * 1024, 4 * 1024 * 1024)]
    public int PLAYLIST_CHUNK { get; set; } = 512 * 1024;

    /// <summary>Chunk-Größe beim Senden von Segmenten (Bytes).</summary>
    [Range(32 * 1024, 8 * 1024 * 1024)]
    public int SEGMENT_CHUNK { get; set; } = 2 * 1024 * 1024;

    // -----------------------------
    // Retries
    // -----------------------------
    [Range(0, 10)]
    public int SEG_RETRY_MAX { get; set; } = 3;

    [Range(0, 60_000)]
    public int SEG_RETRY_BASE_MS { get; set; } = 120;

    [Range(0, 60_000)]
    public int SEG_RETRY_JITTER_MS { get; set; } = 120;

    // -----------------------------
    // Session
    // -----------------------------
    /// <summary>SID-TTL in Minuten.</summary>
    [Range(1, 1440)]
    public int SID_TTL_MIN { get; set; } = 30;

    // -----------------------------
    // Networking / Header Defaults
    // -----------------------------
    /// <summary>Nur informativ; für Logging/Filter. Egress-Bind wird nicht erzwungen.</summary>
    public string PUBLIC_EGRESS_IP { get; set; } = "0.0.0.0";

    /// <summary>SSL-Validierung erzwingen (empfohlen true). Wird ggf. vom HttpClient-Handler verwendet.</summary>
    public bool VERIFY_UPSTREAM_SSL { get; set; } = true;

    /// <summary>Historisch; wird nicht verwendet. Belassen für Backward-Compat.</summary>
    [Obsolete("Ignoriert: Der Proxy verwendet RequestVersionOrHigher (H1/H2/H3).")]
    public bool HTTP2_UPSTREAM { get; set; } = false;

    /// <summary>Default User-Agent für Upstream-Requests (falls Client keinen mitsendet).</summary>
    [Required, MinLength(5)]
    public string USER_AGENT { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128 Safari/537.36";

    [Required, MinLength(5)]
    public string REFERER { get; set; } = "https://voe.sx/";

    [Required, MinLength(5)]
    public string ORIGIN { get; set; } = "https://voe.sx/";

    // -----------------------------
    // Verbindungspools / Timeouts
    // -----------------------------
    /// <summary>Max. Verbindungen je Host im Streaming-Pool (Segmente/Playlists).</summary>
    [Range(1, 4096)]
    public int STREAM_MAX_CONN_PER_HOST { get; set; } = 256;

    /// <summary>Max. Verbindungen je Host im Prefetch-Pool.</summary>
    [Range(1, 256)]
    public int PREFETCH_MAX_CONN_PER_HOST { get; set; } = 6;

    /// <summary>Connect-Timeout (ms) für Upstream.</summary>
    [Range(100, 120_000)]
    public int CONNECT_TIMEOUT_MS { get; set; } = 5000;

    /// <summary>Allgemeiner HttpClient-Timeout (Sekunden) für Stream-Pool.</summary>
    [Range(1, 600)]
    public int HTTP_CLIENT_TIMEOUT_SEC { get; set; } = 30;

    /// <summary>HttpClient-Timeout (Sekunden) für Prefetch-Pool.</summary>
    [Range(1, 600)]
    public int PREFETCH_TIMEOUT_SEC { get; set; } = 15;

    /// <summary>Lebensdauer gepoolter Verbindungen (Minuten) im Stream-Pool.</summary>
    [Range(1, 1440)]
    public int POOLED_CONN_LIFETIME_MIN { get; set; } = 10;

    /// <summary>Idle-Timeout gepoolter Verbindungen (Minuten) im Stream-Pool.</summary>
    [Range(1, 1440)]
    public int POOLED_CONN_IDLE_TIMEOUT_MIN { get; set; } = 2;

    /// <summary>Lebensdauer gepoolter Verbindungen (Minuten) im Prefetch-Pool.</summary>
    [Range(1, 1440)]
    public int PREFETCH_POOLED_CONN_LIFETIME_MIN { get; set; } = 5;

    /// <summary>Idle-Timeout gepoolter Verbindungen (Minuten) im Prefetch-Pool.</summary>
    [Range(1, 1440)]
    public int PREFETCH_POOLED_CONN_IDLE_TIMEOUT_MIN { get; set; } = 1;

    // --------------------------------------------------------------------
    // Abgeleitete (typisierte) Properties – nur lesen, niemals binden
    // --------------------------------------------------------------------
    public TimeSpan PlaylistTtl => TimeSpan.FromSeconds(Math.Max(0, PLAYLIST_TTL_SEC));
    public TimeSpan SegmentTtl => TimeSpan.FromSeconds(Math.Max(0, SEG_CACHE_TTL_SEC));
    public TimeSpan ConnectTimeout => TimeSpan.FromMilliseconds(Math.Max(1, CONNECT_TIMEOUT_MS));
    public TimeSpan HttpClientTimeout => TimeSpan.FromSeconds(Math.Max(1, HTTP_CLIENT_TIMEOUT_SEC));
    public TimeSpan PrefetchTimeout => TimeSpan.FromSeconds(Math.Max(1, PREFETCH_TIMEOUT_SEC));
    public TimeSpan PooledConnLifetime => TimeSpan.FromMinutes(Math.Max(1, POOLED_CONN_LIFETIME_MIN));
    public TimeSpan PooledConnIdleTimeout => TimeSpan.FromMinutes(Math.Max(1, POOLED_CONN_IDLE_TIMEOUT_MIN));
    public TimeSpan PrefetchPooledConnLifetime => TimeSpan.FromMinutes(Math.Max(1, PREFETCH_POOLED_CONN_LIFETIME_MIN));
    public TimeSpan PrefetchPooledConnIdleTimeout => TimeSpan.FromMinutes(Math.Max(1, PREFETCH_POOLED_CONN_IDLE_TIMEOUT_MIN));

    // --------------------------------------------------------------------
    // Validierung & Normalisierung
    // --------------------------------------------------------------------
    /// <summary>
    /// Klemmt riskante Werte auf sinnvolle Grenzen und loggt Korrekturen.
    /// Call einmal beim Boot (nach Binding).
    /// </summary>
    public void ValidateAndNormalize(ILogger? log = null)
    {
        int Clamp(string name, int val, int min, int max)
        {
            if (val < min || val > max)
            {
                int clamped = Math.Clamp(val, min, max);
                log?.LogWarning("[ProxyOptions] {name}={val} außerhalb [{min},{max}] → {clamped}", name, val, min, max, clamped);
                return clamped;
            }
            return val;
        }

        GLOBAL_UPSTREAM_LIMIT = Clamp(nameof(GLOBAL_UPSTREAM_LIMIT), GLOBAL_UPSTREAM_LIMIT, 1, 4096);
        PER_SID_UPSTREAM_LIMIT = Clamp(nameof(PER_SID_UPSTREAM_LIMIT), PER_SID_UPSTREAM_LIMIT, 1, 64);
        PREFETCH_AHEAD = Clamp(nameof(PREFETCH_AHEAD), PREFETCH_AHEAD, 0, 16);
        PER_SID_PREFETCH_BYTES = Math.Max(0, PER_SID_PREFETCH_BYTES);

        PLAYLIST_TTL_SEC = Clamp(nameof(PLAYLIST_TTL_SEC), PLAYLIST_TTL_SEC, 0, 600);
        SEG_CACHE_TTL_SEC = Clamp(nameof(SEG_CACHE_TTL_SEC), SEG_CACHE_TTL_SEC, 0, 600);
        SEG_CACHE_MAX_ITEM_BYTES = Clamp(nameof(SEG_CACHE_MAX_ITEM_BYTES), SEG_CACHE_MAX_ITEM_BYTES, 64 * 1024, 256 * 1024 * 1024);

        PLAYLIST_CHUNK = Clamp(nameof(PLAYLIST_CHUNK), PLAYLIST_CHUNK, 4 * 1024, 4 * 1024 * 1024);
        SEGMENT_CHUNK = Clamp(nameof(SEGMENT_CHUNK), SEGMENT_CHUNK, 32 * 1024, 8 * 1024 * 1024);

        SEG_RETRY_MAX = Clamp(nameof(SEG_RETRY_MAX), SEG_RETRY_MAX, 0, 10);
        SEG_RETRY_BASE_MS = Clamp(nameof(SEG_RETRY_BASE_MS), SEG_RETRY_BASE_MS, 0, 60_000);
        SEG_RETRY_JITTER_MS = Clamp(nameof(SEG_RETRY_JITTER_MS), SEG_RETRY_JITTER_MS, 0, 60_000);

        SID_TTL_MIN = Clamp(nameof(SID_TTL_MIN), SID_TTL_MIN, 1, 1440);

        STREAM_MAX_CONN_PER_HOST = Clamp(nameof(STREAM_MAX_CONN_PER_HOST), STREAM_MAX_CONN_PER_HOST, 1, 4096);
        PREFETCH_MAX_CONN_PER_HOST = Clamp(nameof(PREFETCH_MAX_CONN_PER_HOST), PREFETCH_MAX_CONN_PER_HOST, 1, 256);
        CONNECT_TIMEOUT_MS = Clamp(nameof(CONNECT_TIMEOUT_MS), CONNECT_TIMEOUT_MS, 100, 120_000);
        HTTP_CLIENT_TIMEOUT_SEC = Clamp(nameof(HTTP_CLIENT_TIMEOUT_SEC), HTTP_CLIENT_TIMEOUT_SEC, 1, 600);
        PREFETCH_TIMEOUT_SEC = Clamp(nameof(PREFETCH_TIMEOUT_SEC), PREFETCH_TIMEOUT_SEC, 1, 600);

        POOLED_CONN_LIFETIME_MIN = Clamp(nameof(POOLED_CONN_LIFETIME_MIN), POOLED_CONN_LIFETIME_MIN, 1, 1440);
        POOLED_CONN_IDLE_TIMEOUT_MIN = Clamp(nameof(POOLED_CONN_IDLE_TIMEOUT_MIN), POOLED_CONN_IDLE_TIMEOUT_MIN, 1, 1440);
        PREFETCH_POOLED_CONN_LIFETIME_MIN = Clamp(nameof(PREFETCH_POOLED_CONN_LIFETIME_MIN), PREFETCH_POOLED_CONN_LIFETIME_MIN, 1, 1440);
        PREFETCH_POOLED_CONN_IDLE_TIMEOUT_MIN = Clamp(nameof(PREFETCH_POOLED_CONN_IDLE_TIMEOUT_MIN), PREFETCH_POOLED_CONN_IDLE_TIMEOUT_MIN, 1, 1440);

        if (string.IsNullOrWhiteSpace(USER_AGENT))
            USER_AGENT = "Mozilla/5.0";
        if (string.IsNullOrWhiteSpace(REFERER))
            REFERER = "https://voe.sx/";
        if (string.IsNullOrWhiteSpace(ORIGIN))
            ORIGIN = "https://voe.sx/";
    }

    // --------------------------------------------------------------------
    // Handler/Client Factories – zentral und konsistent
    // --------------------------------------------------------------------
    /// <summary>Erstellt einen vorkonfigurierten SocketsHttpHandler nach diesen Optionen.</summary>
    public SocketsHttpHandler CreateHandler(bool forPrefetch)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,

            MaxConnectionsPerServer = forPrefetch ? PREFETCH_MAX_CONN_PER_HOST : STREAM_MAX_CONN_PER_HOST,
            EnableMultipleHttp2Connections = true,
            EnableMultipleHttp3Connections = true,

            PooledConnectionLifetime = forPrefetch ? PrefetchPooledConnLifetime : PooledConnLifetime,
            PooledConnectionIdleTimeout = forPrefetch ? PrefetchPooledConnIdleTimeout : PooledConnIdleTimeout,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),

            ConnectTimeout = ConnectTimeout,
            SslOptions =
            {
                RemoteCertificateValidationCallback = VERIFY_UPSTREAM_SSL
                    ? static (_, __, ___, errors) => errors == SslPolicyErrors.None
                    : static (_, __, ___, ____) => true
            }
        };

        return h;
    }

    /// <summary>Erstellt einen HttpClient (H1/H2/H3) basierend auf diesem Handler.</summary>
    public HttpClient CreateHttpClient(bool forPrefetch)
    {
        var handler = CreateHandler(forPrefetch);
        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = forPrefetch ? PrefetchTimeout : HttpClientTimeout,
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", USER_AGENT);
        return http;
    }
}
