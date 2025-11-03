using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using VoeProxy.Config;

namespace VoeProxy.Cache;

public sealed class UnifiedPipelineCache : IDisposable, IAsyncDisposable
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<UnifiedPipelineCache> _log;
    private readonly ProxyOptions _opt;

    private const long MaxCacheSize = 20L * 1024L * 1024L * 1024L;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultPlaylistTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultSegmentTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultSidTtl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, SidEntry> _sessions = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inflightFetches = new();
    private readonly SemaphoreSlim _globalSem;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _janitorTask;
    private readonly TimeSpan _playlistTtl;
    private readonly TimeSpan _segmentTtl;
    private readonly TimeSpan _sidTtl;
    private readonly int _maxSegmentCacheItemBytes;
    private long _globalInUse;
    private long _playlistCount;
    private long _segmentCount;

    private readonly record struct SidEntry(
        string Url,
        string Hls,
        string Cookie,
        string Referer,
        int PerSidLimit,
        string? ClientLabel,
        int TargetBitrateKbps,
        DateTime Created,
        SemaphoreSlim Semaphore
    );

    public UnifiedPipelineCache(ProxyOptions opt, ILogger<UnifiedPipelineCache> log)
    {
        _opt = opt;
        _log = log;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = MaxCacheSize,
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });

        _globalSem = new SemaphoreSlim(opt.GLOBAL_UPSTREAM_LIMIT);
        _janitorTask = Task.Run(CleanupLoopAsync);

        _playlistTtl = opt.PlaylistTtl >= TimeSpan.Zero ? opt.PlaylistTtl : DefaultPlaylistTtl;
        _segmentTtl = opt.SegmentTtl >= TimeSpan.Zero ? opt.SegmentTtl : DefaultSegmentTtl;
        _sidTtl = opt.SID_TTL_MIN > 0
            ? TimeSpan.FromMinutes(opt.SID_TTL_MIN)
            : DefaultSidTtl;
        _maxSegmentCacheItemBytes = opt.SEG_CACHE_MAX_ITEM_BYTES <= 0
            ? int.MaxValue
            : Math.Max(opt.SEG_CACHE_MAX_ITEM_BYTES, 64 * 1024);
    }

    // Shared, de-duplicated fetch logic
    public async Task<byte[]> GetOrFetchAsync(string key, Func<Task<byte[]>> fetch, TimeSpan? ttl = null)
    {
        if (_cache.TryGetValue(key, out var obj) && obj is byte[] cached)
            return cached;

        var lazyFetch = _inflightFetches.GetOrAdd(key, k =>
            new Lazy<Task<byte[]>>(async () =>
            {
                try
                {
                    var data = await fetch().ConfigureAwait(false);
                    var expiration = ttl ?? GetDefaultTtlForKey(k);
                    if (data.Length > 0 && ShouldCacheItem(k, data.Length) && expiration > TimeSpan.Zero)
                    {
                        _cache.Set(k, data,
                            new MemoryCacheEntryOptions()
                                .SetSize(data.Length)
                                .SetSlidingExpiration(expiration));
                        if (k.StartsWith("pl:", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Increment(ref _playlistCount);
                        else if (k.StartsWith("seg:", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Increment(ref _segmentCount);
                    }
                    return data;
                }
                finally
                {
                    _inflightFetches.TryRemove(k, out _);
                }
            }));

        return await lazyFetch.Value.ConfigureAwait(false);
    }

    public bool TryGetPlaylist(string key, out byte[] data)
    {
        if (_cache.TryGetValue(key, out var obj) && obj is byte[] b)
        { data = b; return true; }
        data = Array.Empty<byte>(); return false;
    }

    public void SetPlaylist(string key, byte[] data)
    {
        if (_playlistTtl <= TimeSpan.Zero)
            return;

        _cache.Set(key, data, new MemoryCacheEntryOptions()
            .SetSize(data.Length)
            .SetSlidingExpiration(_playlistTtl));
        Interlocked.Increment(ref _playlistCount);
    }

    public bool TryGetSegment(string key, out byte[] data)
    {
        if (_cache.TryGetValue(key, out var obj) && obj is byte[] b)
        { data = b; return true; }
        data = Array.Empty<byte>(); return false;
    }

    public void SetSegment(string key, byte[] data, bool extendTtl = false)
    {
        if (!ShouldCacheItem(key, data.Length))
            return;

        var ttl = extendTtl ? TimeSpan.FromHours(1) : _segmentTtl;
        if (ttl <= TimeSpan.Zero)
            return;

        _cache.Set(key, data, new MemoryCacheEntryOptions()
            .SetSize(data.Length)
            .SetSlidingExpiration(ttl));
        Interlocked.Increment(ref _segmentCount);
    }

    // Session management
    public void UpsertSession(string sid, string url, string hls, string cookie,
        string referer, int perSidLimit, string clientLabel, int targetBitrateKbps)
    {
        _sessions.AddOrUpdate(
            sid,
            static (_, arg) => new SidEntry(
                arg.url, arg.hls, arg.cookie, arg.referer,
                arg.perSidLimit, arg.clientLabel, arg.targetBitrateKbps,
                DateTime.UtcNow, new SemaphoreSlim(arg.perSidLimit)),
            static (_, old, arg) =>
            {
                try { while (old.Semaphore.CurrentCount < old.PerSidLimit) old.Semaphore.Release(); }
                catch (SemaphoreFullException) { }
                return old with
                {
                    Url = arg.url,
                    Hls = arg.hls,
                    Cookie = arg.cookie,
                    Referer = arg.referer,
                    PerSidLimit = arg.perSidLimit,
                    ClientLabel = arg.clientLabel,
                    TargetBitrateKbps = arg.targetBitrateKbps,
                    Created = DateTime.UtcNow
                };
            },
            (url, hls, cookie, referer, perSidLimit, clientLabel, targetBitrateKbps));
    }

    public bool TryGetSession(string sid, out SidInfo? info)
    {
        if (_sessions.TryGetValue(sid, out var e))
        {
            info = new SidInfo(e.Url, e.Hls, e.Cookie, e.Referer,
                e.PerSidLimit, e.ClientLabel, e.TargetBitrateKbps);
            return true;
        }
        info = null; return false;
    }

    public async ValueTask<IDisposable?> AcquireSessionAsync(string sid, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sid, out var entry))
            return null;

        await _globalSem.WaitAsync(ct).ConfigureAwait(false);
        await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _globalInUse);

        return new Lease(this, entry.Semaphore);
    }

    private sealed class Lease : IDisposable
    {
        private readonly UnifiedPipelineCache _owner;
        private readonly SemaphoreSlim _sem;
        private int _disposed;
        public Lease(UnifiedPipelineCache owner, SemaphoreSlim sem)
        { _owner = owner; _sem = sem; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _sem.Release(); } catch { }
            try { _owner._globalSem.Release(); } catch { }
            Interlocked.Decrement(ref _owner._globalInUse);
        }
    }

    public (int sids, long globalInUse, long globalFree,
        long playlists, long segments, string meta) GetStats()
    {
        var sidCount = _sessions.Count;
        long inUse = Interlocked.Read(ref _globalInUse);
        long free = Math.Max(0, _opt.GLOBAL_UPSTREAM_LIMIT - inUse);
        var pl = Interlocked.Read(ref _playlistCount);
        var sg = Interlocked.Read(ref _segmentCount);
        var mem = GC.GetTotalMemory(false);
        var meta = $"limit={MaxCacheSize / (1024 * 1024)}MB, used≈{mem / (1024 * 1024)}MB, inflight={_inflightFetches.Count}";
        return (sidCount, inUse, free, pl, sg, meta);
    }

    private async Task CleanupLoopAsync()
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                int removed = 0;
                foreach (var kv in _sessions)
                {
                    if ((DateTime.UtcNow - kv.Value.Created) > _sidTtl &&
                        _sessions.TryRemove(kv.Key, out var e))
                    {
                        e.Semaphore.Dispose();
                        removed++;
                    }
                }
                if (removed > 0)
                    _log.LogDebug("[CLEANUP] removed {count} expired SIDs", removed);
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool ShouldCacheItem(string key, int length)
    {
        if (key.StartsWith("pl:", StringComparison.OrdinalIgnoreCase))
            return _playlistTtl > TimeSpan.Zero;

        if (!key.StartsWith("seg:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (_segmentTtl <= TimeSpan.Zero)
            return false;

        return length <= _maxSegmentCacheItemBytes;
    }

    private TimeSpan GetDefaultTtlForKey(string key)
    {
        if (key.StartsWith("pl:", StringComparison.OrdinalIgnoreCase))
            return _playlistTtl;
        if (key.StartsWith("seg:", StringComparison.OrdinalIgnoreCase))
            return _segmentTtl;
        return _segmentTtl;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _globalSem.Dispose();
        foreach (var e in _sessions.Values)
            try { e.Semaphore.Dispose(); } catch { }
        _cts.Dispose();
        _cache.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _janitorTask; } catch { }
        Dispose();
    }

    public readonly record struct SidInfo(
        string Url, string Hls, string Cookie, string Referer,
        int PerSidLimit, string? ClientLabel, int TargetBitrateKbps);
}
