// ============================================================================
// File: Program.cs (Enterprise Jellyfin Edition)
// HTTP/1.1 only, High Stability, No appsettings, No Metrics, No Telemetry
// ============================================================================
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoeProxy.Cache;
using VoeProxy.Client;
using VoeProxy.Config;
using VoeProxy.Extraction;
using VoeProxy.Infrastructure;
using VoeProxy.Sessions;
using System.Diagnostics;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// HOSTING & Kestrel Setup
// ============================================================================
builder.WebHost.UseUrls("http://0.0.0.0:8000");
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = null;
    k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
});

// ============================================================================
// LOGGING (production-optimized, but keeps Debug for diagnostics)
// ============================================================================
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "[HH:mm:ss] ";
    options.SingleLine = true;
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("VoeProxy", LogLevel.Debug);

// ============================================================================
// INLINE CONFIGURATION (no appsettings.json)
// ============================================================================
var opt = new ProxyOptions
{
    GLOBAL_UPSTREAM_LIMIT = 256,
    PER_SID_UPSTREAM_LIMIT = 16,
    PREFETCH_AHEAD = 4,
    PER_SID_PREFETCH_BYTES = 32 * 1024 * 1024,
    PLAYLIST_TTL_SEC = 180,
    SEG_CACHE_TTL_SEC = 180,
    SEG_CACHE_MAX_ITEM_BYTES = 32 * 1024 * 1024,
    VERIFY_UPSTREAM_SSL = true,
    HTTP_CLIENT_TIMEOUT_SEC = 30,
    PREFETCH_TIMEOUT_SEC = 15
};
opt.ValidateAndNormalize();

// ============================================================================
// HTTP CLIENTS (stream/prefetch pools, HTTP/1.1 only)
// ============================================================================
builder.Services.AddHttpClient("stream")
    .ConfigurePrimaryHttpMessageHandler(_ => opt.CreateHandler(forPrefetch: false))
    .ConfigureHttpClient(c =>
    {
        c.Timeout = opt.HttpClientTimeout;
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opt.USER_AGENT);
    });

builder.Services.AddHttpClient("prefetch")
    .ConfigurePrimaryHttpMessageHandler(_ => opt.CreateHandler(forPrefetch: true))
    .ConfigureHttpClient(c =>
    {
        c.Timeout = opt.PrefetchTimeout;
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opt.USER_AGENT);
    });

// ============================================================================
// CORE SERVICES
// ============================================================================
builder.Services.AddSingleton(opt);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Base64Url>();
builder.Services.AddSingleton<UnifiedPipelineCache>();
builder.Services.AddSingleton<ClientClassifier>();

builder.Services.AddSingleton<VoeExtractor>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("stream");
    var log = sp.GetRequiredService<ILogger<VoeExtractor>>();
    return new VoeExtractor(http, opt.REFERER, opt.ORIGIN, log);
});

// ============================================================================
// PIPELINE CONFIG
// ============================================================================
builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// ============================================================================
var app = builder.Build();

// ============================================================================
app.UseForwardedHeaders();
app.UseCors();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    ctx.Response.Headers["Pragma"] = "no-cache";
    await next();
});

app.MapControllers();

// ============================================================================
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    uptime_s = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds
}));

// ============================================================================
ThreadPool.GetMinThreads(out var w, out var io);
int target = Math.Max(Environment.ProcessorCount * 32, w);
ThreadPool.SetMinThreads(target, Math.Max(target, io));

// ============================================================================
app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"[BOOT] VoeProxy Enterprise HLS Server started at {DateTime.UtcNow:HH:mm:ss}"));

app.Lifetime.ApplicationStopping.Register(() =>
    Console.WriteLine($"[STOP] Graceful shutdown at {DateTime.UtcNow:HH:mm:ss}"));

// ============================================================================
app.Run();
