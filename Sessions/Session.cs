// File: /VoeProxy/Sessions/Session.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VoeProxy.Sessions;

/// <summary>
/// Stellt Methoden bereit, um SIDs (Session IDs) aus URLs abzuleiten
/// und kanonisch darzustellen. Vereinigt frühere Logik aus
/// CanonicalSid.cs und SidFactory.cs.
/// </summary>
public static partial class Session
{
    // ------------------------------------------------------------------------
    // Canonicalization (vormals CanonicalSid.cs)
    // ------------------------------------------------------------------------
    private static readonly Regex VoeSlug =
        new(@"voe\.sx/(?:e/)?(?<id>[a-z0-9]{10,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RedirectNum =
        new(@"/redirect/(?<id>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Liefert eine bevorzugte SID und mögliche Aliasformen aus einer Quell-URL.
    /// </summary>
    public static (string preferred, string[] aliases) FromUrl(string url)
    {
        url = url.Trim();
        var u = new Uri(url, UriKind.Absolute);
        var candidates = new List<string>(3);

        if (u.Host.Contains("voe.sx", StringComparison.OrdinalIgnoreCase))
        {
            var m = VoeSlug.Match(url);
            if (m.Success) candidates.Add(m.Groups["id"].Value);
        }
        else if (u.Host.Contains("aniworld.to", StringComparison.OrdinalIgnoreCase) ||
                 u.Host.EndsWith(".s.to", StringComparison.OrdinalIgnoreCase) ||
                 u.Host.Equals("s.to", StringComparison.OrdinalIgnoreCase))
        {
            var m = RedirectNum.Match(url);
            if (m.Success) candidates.Add(m.Groups["id"].Value);
        }

        // Fallback: vollständige URL als stabile SID
        if (candidates.Count == 0)
            candidates.Add(url);

        var preferred = candidates[0];
        return (preferred, candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // ------------------------------------------------------------------------
    // Derivation (vormals SidFactory.cs)
    // ------------------------------------------------------------------------
    [GeneratedRegex(@"https?://(?:www\.)?voe\.sx/(?:e|v)/(?<slug>[a-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VoePageSlug();

    [GeneratedRegex(@"/(?<slug>[a-z0-9]{8,})/[^/]*\.m3u8",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HlsSlug();

    [GeneratedRegex(@"https?://(?:www\.)?(?:aniworld\.to|s\.to)/redirect/(?<rid>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RedirectId();

    /// <summary>
    /// Leitet eine SID aus URL-Kombinationen ab. Nutzt Slug-, Redirect- oder Hash-Strategien.
    /// </summary>
    public static string Derive(string sourceUrl, string hlsUrl)
    {
        var m1 = VoePageSlug().Match(sourceUrl);
        if (m1.Success) return m1.Groups["slug"].Value.ToLowerInvariant();

        var m2 = HlsSlug().Match(hlsUrl);
        if (m2.Success) return m2.Groups["slug"].Value.ToLowerInvariant();

        var m3 = RedirectId().Match(sourceUrl);
        if (m3.Success) return $"rd-{m3.Groups["rid"].Value}";

        // Fallback: stabiler Hash der kanonisierten HLS-URL
        var canon = Canonicalize(hlsUrl);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canon));
        return Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    private static string Canonicalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return url.Trim();

        var builder = new UriBuilder(u) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.ToString().ToLowerInvariant();
    }
}
