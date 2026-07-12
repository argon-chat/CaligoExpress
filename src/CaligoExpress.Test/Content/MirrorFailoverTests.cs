namespace CaligoExpress.Test.Content;

using CaligoExpress.Abstractions;
using CaligoExpress.Content;
using CaligoExpress.Manifests;
using CaligoExpress.Test.Support;

[TestFixture]
public class MirrorFailoverTests
{
    private static Dictionary<string, byte[]> Map() =>
        new(StringComparer.Ordinal) { ["k"] = System.Text.Encoding.UTF8.GetBytes("payload") };

    [Test]
    public async Task GetBytes_FailsOverToHealthyMirror()
    {
        var dead = new InMemoryContentSource(fail: true);
        var alive = new InMemoryContentSource(Map());
        var mirror = new MirrorContentSource([dead, alive], raceWidth: 1);

        var bytes = await mirror.GetBytesAsync("k");

        Assert.That(System.Text.Encoding.UTF8.GetString(bytes), Is.EqualTo("payload"));
    }

    [Test]
    public async Task Download_FailsOverSequentially()
    {
        var dead = new InMemoryContentSource(fail: true);
        var alive = new InMemoryContentSource(Map());
        var mirror = new MirrorContentSource([dead, alive]);

        var dest = Path.Combine(Path.GetTempPath(), "caligo-mirror-" + Guid.NewGuid().ToString("N"));
        try
        {
            await mirror.DownloadToFileAsync("k", dest);
            Assert.That(await File.ReadAllTextAsync(dest), Is.EqualTo("payload"));
            Assert.That(dead.DownloadCalls, Is.EqualTo(1), "the dead mirror must have been tried first");
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Test]
    public async Task UnhealthyMirror_IsPenalizedAndSkippedNextTime()
    {
        // A stall/timeout/refused surfaces as a non-404 health failure (here an IOException).
        var flaky = new InMemoryContentSource(Map()) { FailWith = new IOException("stalled") };
        var alive = new InMemoryContentSource(Map());
        var mirror = new MirrorContentSource([flaky, alive]);

        // First fetch: flaky is tried (fails health), then alive serves.
        await mirror.GetBytesAsync("k");
        Assert.That(flaky.GetCalls, Is.EqualTo(1));

        // Second fetch: flaky was penalized, so alive is tried first and the dead mirror is not hit again —
        // a stalled mirror must not block every subsequent request.
        await mirror.GetBytesAsync("k");
        Assert.That(flaky.GetCalls, Is.EqualTo(1), "a penalized mirror is skipped while a healthy one serves");
        Assert.That(alive.GetCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task NotFound_DoesNotPenalize_MirrorStaysFirst()
    {
        var first = new InMemoryContentSource(new(StringComparer.Ordinal) { ["a"] = "A"u8.ToArray() });
        var second = new InMemoryContentSource(new(StringComparer.Ordinal) { ["a"] = "A"u8.ToArray(), ["b"] = "B"u8.ToArray() });
        var mirror = new MirrorContentSource([first, second]);

        // 'b' is only on the second mirror, so the first 404s — a key-specific miss, not a health failure.
        Assert.That(System.Text.Encoding.UTF8.GetString(await mirror.GetBytesAsync("b")), Is.EqualTo("B"));

        // The 404 must NOT have penalized the first mirror: it is still tried first for a key it does have.
        Assert.That(System.Text.Encoding.UTF8.GetString(await mirror.GetBytesAsync("a")), Is.EqualTo("A"));
        Assert.That(first.GetCalls, Is.EqualTo(2), "the 404 did not penalize the first mirror");
    }

    [Test]
    public void AllMirrorsDown_ThrowsContentNotFound()
    {
        var mirror = new MirrorContentSource([new InMemoryContentSource(fail: true), new InMemoryContentSource(fail: true)]);
        Assert.ThrowsAsync<ContentNotFoundException>(async () => await mirror.GetBytesAsync("k"));
    }

    [Test]
    public void Resolver_PrefersRegionMatchThenPriority()
    {
        var mirrors = new List<MirrorEntry>
        {
            new("global.cdn", ["*"], 5),
            new("ru.cdn", ["RU"], 9),
            new("eu.cdn", ["EU"], 0),
        };

        var ordered = MirrorResolver.OrderMirrors(mirrors, region: "RU");

        Assert.That(ordered[0].Host, Is.EqualTo("ru.cdn"), "regional mirror wins even with worse priority");
        Assert.That(ordered[1].Host, Is.EqualTo("global.cdn"), "wildcard mirror is the next fallback");
        Assert.That(ordered[2].Host, Is.EqualTo("eu.cdn"), "other-region mirror is last but still usable");
    }
}
