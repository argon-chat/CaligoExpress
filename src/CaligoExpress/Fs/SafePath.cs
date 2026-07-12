namespace CaligoExpress.Fs;

using CaligoExpress.Abstractions;

/// <summary>
/// Validates and resolves manifest-declared relative paths. Manifests come from untrusted mirrors,
/// so every path is checked for traversal/absolute/drive tricks before being combined with a root.
/// </summary>
public static class SafePath
{
    /// <summary>
    /// Normalizes a manifest path to forward-slash relative form, rejecting anything that could
    /// escape a root (absolute, drive-qualified, "." or ".." segments, embedded NULs).
    /// </summary>
    public static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new UnsafePathException(path ?? "<null>");

        var p = path.Replace('\\', '/').Trim();

        if (p.Length == 0 || p[0] == '/')
            throw new UnsafePathException(path);
        if (p.Length >= 2 && p[1] == ':') // drive letter
            throw new UnsafePathException(path);
        if (p.Contains('\0'))
            throw new UnsafePathException(path);

        var segments = p.Split('/');
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || seg == "." || seg == "..")
                throw new UnsafePathException(path);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Resolves <paramref name="relative"/> under <paramref name="root"/> to a full OS path,
    /// guaranteeing the result stays within the root.
    /// </summary>
    public static string ResolveUnder(string root, string relative)
    {
        var norm = NormalizeRelative(relative);
        var rootFull = Path.GetFullPath(root);
        var combined = Path.Combine(rootFull, norm.Replace('/', Path.DirectorySeparatorChar));
        var full = Path.GetFullPath(combined);

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnsafePathException(relative);

        return full;
    }

    /// <summary>True when the path normalizes cleanly (no exception).</summary>
    public static bool IsSafeRelative(string path)
    {
        try
        {
            NormalizeRelative(path);
            return true;
        }
        catch (UnsafePathException)
        {
            return false;
        }
    }
}
