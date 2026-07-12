namespace CaligoExpress.Content;

using CaligoExpress.Abstractions;
using CaligoExpress.Manifests;
using Serilog;

/// <summary>
/// Turns a signed <see cref="ConfigManifest"/>'s mirror list into an ordered
/// <see cref="MirrorContentSource"/>. Mirrors whose region matches the caller's region (or the "*"
/// wildcard) sort first, then by declared priority. The bootstrap config itself is fetched separately
/// from a hard-coded seed list so a regional outage can never lock the client out entirely.
/// </summary>
public static class MirrorResolver
{
    public static IContentSource Build(
        ConfigManifest config,
        HttpClient http,
        string? region = null,
        ILogger? log = null)
    {
        var ordered = OrderMirrors(config.Mirrors, region);
        if (ordered.Count == 0)
            throw new CaligoException("config contains no usable mirrors");

        var sources = ordered
            .Select(IContentSource (m) => new HttpContentSource(m.Host, http, m.Scheme))
            .ToList();

        return new MirrorContentSource(sources, log);
    }

    public static List<MirrorEntry> OrderMirrors(IReadOnlyList<MirrorEntry> mirrors, string? region)
    {
        var r = region?.Trim().ToUpperInvariant();

        return mirrors
            .OrderByDescending(m => RegionRank(m, r))
            .ThenBy(m => m.Priority)
            .ThenBy(m => m.Host, StringComparer.Ordinal)
            .ToList();
    }

    private static int RegionRank(MirrorEntry mirror, string? region)
    {
        if (mirror.Regions is null || mirror.Regions.Count == 0)
            return 1; // unscoped mirror: generally applicable
        if (region is not null && mirror.Regions.Any(x => string.Equals(x, region, StringComparison.OrdinalIgnoreCase)))
            return 2; // exact region match wins
        if (mirror.Regions.Contains("*"))
            return 1;
        return 0; // scoped to other regions: least preferred, but still a fallback
    }
}
