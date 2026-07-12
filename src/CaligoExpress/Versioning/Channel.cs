namespace CaligoExpress.Versioning;

/// <summary>A release channel. Lowercased and validated against the known set.</summary>
public readonly record struct Channel
{
    public string Name { get; }

    private Channel(string name) => Name = name;

    public static readonly Channel Live = new("live");
    public static readonly Channel Beta = new("beta");
    public static readonly Channel Canary = new("canary");

    public static bool IsKnown(string name) => name is "live" or "beta" or "canary";

    public static Channel Parse(string s)
    {
        var n = (s ?? "").Trim().ToLowerInvariant();
        if (!IsKnown(n))
            throw new ArgumentException($"unknown channel '{s}' (expected live|beta|canary)");
        return new Channel(n);
    }

    public static bool TryParse(string? s, out Channel channel)
    {
        var n = (s ?? "").Trim().ToLowerInvariant();
        if (IsKnown(n))
        {
            channel = new Channel(n);
            return true;
        }
        channel = default;
        return false;
    }

    public override string ToString() => Name;
}
