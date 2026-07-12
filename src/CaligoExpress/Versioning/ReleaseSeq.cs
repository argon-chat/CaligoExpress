namespace CaligoExpress.Versioning;

/// <summary>
/// Monotonic release sequence issued by the control plane. This is the authority on update
/// direction and downgrade protection: a target may only be applied when its sequence is
/// strictly greater than the installed one (unless an explicit rollback is requested).
/// </summary>
public readonly record struct ReleaseSeq(long Value) : IComparable<ReleaseSeq>
{
    public int CompareTo(ReleaseSeq other) => Value.CompareTo(other.Value);

    /// <summary>True when moving from <paramref name="installed"/> to this sequence is a forward update.</summary>
    public bool IsForwardFrom(ReleaseSeq installed) => Value > installed.Value;

    public static bool operator <(ReleaseSeq a, ReleaseSeq b) => a.Value < b.Value;
    public static bool operator >(ReleaseSeq a, ReleaseSeq b) => a.Value > b.Value;
    public static bool operator <=(ReleaseSeq a, ReleaseSeq b) => a.Value <= b.Value;
    public static bool operator >=(ReleaseSeq a, ReleaseSeq b) => a.Value >= b.Value;

    public override string ToString() => Value.ToString();
}
