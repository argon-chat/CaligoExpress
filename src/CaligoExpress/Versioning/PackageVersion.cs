namespace CaligoExpress.Versioning;

using System.Globalization;

/// <summary>
/// A SemVer 2.0 version with full precedence ordering. Build metadata is preserved but ignored
/// in comparison and equality, per the spec. Used to decide what artifact to fetch; release
/// direction/downgrade protection is governed separately by the monotonic release sequence.
/// </summary>
public sealed class PackageVersion : IComparable<PackageVersion>, IEquatable<PackageVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Dot-separated pre-release identifiers; empty when this is a stable release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>Build metadata after '+', or empty. Ignored for precedence.</summary>
    public string Build { get; }

    /// <summary>The original string this was parsed from.</summary>
    public string Raw { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    private PackageVersion(int major, int minor, int patch, string[] pre, string build, string raw)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = pre;
        Build = build;
        Raw = raw;
    }

    public static PackageVersion Parse(string s)
        => TryParse(s, out var v)
            ? v!
            : throw new FormatException($"'{s}' is not a valid SemVer 2.0 version");

    public static bool TryParse(string? s, out PackageVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var raw = s.Trim();
        var work = raw;

        // Split off build metadata (everything after the first '+').
        var build = "";
        var plus = work.IndexOf('+');
        if (plus >= 0)
        {
            build = work[(plus + 1)..];
            work = work[..plus];
            if (!IsValidDotSeparated(build, allowLeadingZeroNumeric: true))
                return false;
        }

        // Split off pre-release (everything after the first '-').
        var pre = Array.Empty<string>();
        var dash = work.IndexOf('-');
        if (dash >= 0)
        {
            var preStr = work[(dash + 1)..];
            work = work[..dash];
            if (!IsValidDotSeparated(preStr, allowLeadingZeroNumeric: false))
                return false;
            pre = preStr.Split('.');
        }

        var core = work.Split('.');
        if (core.Length != 3)
            return false;
        if (!TryParseCore(core[0], out var major) ||
            !TryParseCore(core[1], out var minor) ||
            !TryParseCore(core[2], out var patch))
            return false;

        version = new PackageVersion(major, minor, patch, pre, build, raw);
        return true;
    }

    private static bool TryParseCore(string part, out int value)
    {
        value = 0;
        if (part.Length == 0)
            return false;
        if (part.Length > 1 && part[0] == '0') // no leading zeros
            return false;
        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidDotSeparated(string s, bool allowLeadingZeroNumeric)
    {
        if (s.Length == 0)
            return false;
        foreach (var id in s.Split('.'))
        {
            if (id.Length == 0)
                return false;
            var numeric = true;
            foreach (var c in id)
            {
                var ok = c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-';
                if (!ok)
                    return false;
                if (c is < '0' or > '9')
                    numeric = false;
            }
            if (numeric && !allowLeadingZeroNumeric && id.Length > 1 && id[0] == '0')
                return false;
        }
        return true;
    }

    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
            return 1;

        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // A version WITH a pre-release has lower precedence than one without.
        if (PreRelease.Count == 0 && other.PreRelease.Count == 0) return 0;
        if (PreRelease.Count == 0) return 1;
        if (other.PreRelease.Count == 0) return -1;

        var n = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (var i = 0; i < n; i++)
        {
            c = ComparePreReleaseIdentifier(PreRelease[i], other.PreRelease[i]);
            if (c != 0) return c;
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    private static int ComparePreReleaseIdentifier(string a, string b)
    {
        var aNum = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var av);
        var bNum = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bv);

        if (aNum && bNum) return av.CompareTo(bv);
        if (aNum) return -1; // numeric identifiers always have lower precedence than alphanumeric
        if (bNum) return 1;
        return string.CompareOrdinal(a, b);
    }

    public bool Equals(PackageVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as PackageVersion);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', PreRelease));
    public override string ToString() => Raw;

    public static bool operator ==(PackageVersion? a, PackageVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(PackageVersion? a, PackageVersion? b) => !(a == b);
    public static bool operator <(PackageVersion a, PackageVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(PackageVersion a, PackageVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(PackageVersion a, PackageVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(PackageVersion a, PackageVersion b) => a.CompareTo(b) >= 0;
}
