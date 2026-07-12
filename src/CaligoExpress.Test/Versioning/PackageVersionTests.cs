namespace CaligoExpress.Test.Versioning;

using CaligoExpress.Versioning;

[TestFixture]
public class PackageVersionTests
{
    [TestCase("1.0.0")]
    [TestCase("0.0.0")]
    [TestCase("10.20.30")]
    [TestCase("1.2.3-alpha")]
    [TestCase("1.2.3-alpha.1")]
    [TestCase("1.2.3-0.3.7")]
    [TestCase("1.2.3-x.7.z.92")]
    [TestCase("1.2.3+build.1")]
    [TestCase("1.2.3-beta+exp.sha.5114f85")]
    public void Parse_AcceptsValidVersions(string s)
        => Assert.That(PackageVersion.TryParse(s, out _), Is.True, s);

    [TestCase("1")]
    [TestCase("1.2")]
    [TestCase("1.2.3.4")]
    [TestCase("01.2.3")]
    [TestCase("1.02.3")]
    [TestCase("1.2.3-")]
    [TestCase("1.2.3-01")]      // leading zero numeric pre-release identifier
    [TestCase("1.2.3-béta")]    // non-ascii
    [TestCase("v1.2.3")]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_RejectsInvalidVersions(string s)
        => Assert.That(PackageVersion.TryParse(s, out _), Is.False, s);

    [Test]
    public void Compare_NumericOrderingNotLexicographic()
    {
        // The classic string-comparison bug: "1.9" must be LESS than "1.10".
        Assert.That(PackageVersion.Parse("1.9.0"), Is.LessThan(PackageVersion.Parse("1.10.0")));
        Assert.That(PackageVersion.Parse("1.0.9"), Is.LessThan(PackageVersion.Parse("1.0.10")));
    }

    [Test]
    public void Compare_PreReleaseIsLowerThanRelease()
        => Assert.That(PackageVersion.Parse("1.0.0-alpha"), Is.LessThan(PackageVersion.Parse("1.0.0")));

    [Test]
    public void Compare_PreReleasePrecedenceChain()
    {
        // From the SemVer spec §11.4.
        var chain = new[]
        {
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"
        };
        for (var i = 0; i < chain.Length - 1; i++)
            Assert.That(PackageVersion.Parse(chain[i]), Is.LessThan(PackageVersion.Parse(chain[i + 1])),
                $"{chain[i]} should precede {chain[i + 1]}");
    }

    [Test]
    public void Equality_IgnoresBuildMetadata()
    {
        Assert.That(PackageVersion.Parse("1.2.3+build.1"), Is.EqualTo(PackageVersion.Parse("1.2.3+build.2")));
        Assert.That(PackageVersion.Parse("1.2.3+build.1") == PackageVersion.Parse("1.2.3"), Is.True);
    }

    [Test]
    public void ReleaseSeq_GovernsUpdateDirection()
    {
        Assert.That(new ReleaseSeq(10).IsForwardFrom(new ReleaseSeq(9)), Is.True);
        Assert.That(new ReleaseSeq(9).IsForwardFrom(new ReleaseSeq(9)), Is.False);
        Assert.That(new ReleaseSeq(8).IsForwardFrom(new ReleaseSeq(9)), Is.False);
    }

    [Test]
    public void Channel_ParsesKnownAndRejectsUnknown()
    {
        Assert.That(Channel.Parse("LIVE"), Is.EqualTo(Channel.Live));
        Assert.That(Channel.TryParse("nightly", out _), Is.False);
    }
}
