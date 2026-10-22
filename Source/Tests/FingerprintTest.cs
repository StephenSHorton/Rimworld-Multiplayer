using Multiplayer.Common;
using Multiplayer.Common.Desyncs;

namespace Tests;

[TestFixture]
public class FingerprintTest
{
    private static FingerprintRow Row(string name, string value, bool warn = false) => new() { name = name, value = value, warn = warn };

    [Test]
    public void DifferingRowIsNamedWithBothValues()
    {
        var (blocking, warnings) = Fingerprint.Compare(
            [Row("Market value: Steel", "1.9"), Row("Language", "English", warn: true)],
            [Row("Market value: Steel", "2.1"), Row("Language", "German", warn: true)],
            "host", "Blake");

        Assert.That(blocking, Is.EqualTo(new[] { "Market value: Steel: 1.9 (host) vs 2.1 (Blake)" }));
        Assert.That(warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void RowOnlyOneSideHasBlocks()
    {
        var (blocking, _) = Fingerprint.Compare([Row("Rimkit: settings", "abc")], [], "host", "Blake");
        Assert.That(blocking[0], Does.Contain("only host's game has it"));
    }

    [Test]
    public void SameRowsAreClean()
    {
        var (blocking, warnings) = Fingerprint.Compare([Row("a", "1")], [Row("a", "1")], "host", "Blake");
        Assert.That(blocking, Is.Empty);
        Assert.That(warnings, Is.Empty);
    }

    private static (FingerprintPairing pairing, List<(int player, string reference, string rows)> judged, List<(int player, int joinPoint)> dropped) Pairing()
    {
        var judged = new List<(int, string, string)>();
        var dropped = new List<(int, int)>();
        var pairing = new FingerprintPairing((p, r, rows) => judged.Add((p, r[0].value, rows[0].value)), (p, jp, _) => dropped.Add((p, jp)));
        return (pairing, judged, dropped);
    }

    [Test]
    public void JoinerFirstWaitsForTheHostsRowsFromTheSameJoinPoint()
    {
        // 2026-10-21, the fifth join point: Blake's game reloaded first and was judged against the host's rows
        // from the fourth
        var (pairing, judged, _) = Pairing();
        pairing.OnReference(183446, [Row("save", "jp4")]);
        pairing.OnPlayer(2, 240446, [Row("save", "jp5")]);
        Assert.That(judged, Is.Empty);

        pairing.OnReference(240446, [Row("save", "jp5")]);
        Assert.That(judged, Is.EqualTo(new[] { (2, "jp5", "jp5") }));
        Assert.That(pairing.Latest![0].value, Is.EqualTo("jp5"));
    }

    [Test]
    public void HostFirstJudgesAtOnce()
    {
        var (pairing, judged, _) = Pairing();
        pairing.OnReference(10, [Row("save", "a")]);
        pairing.OnPlayer(2, 10, [Row("save", "b")]);
        Assert.That(judged, Is.EqualTo(new[] { (2, "a", "b") }));
    }

    [Test]
    public void FirstJoinPointWaitsForTheHost()
    {
        var (pairing, judged, _) = Pairing();
        pairing.OnPlayer(2, 10, [Row("save", "a")]);
        Assert.That(pairing.Latest, Is.Null);
        pairing.OnReference(10, [Row("save", "a")]);
        Assert.That(judged, Has.Count.EqualTo(1));
    }

    [Test]
    public void StaleRowsAreDroppedNotJudged()
    {
        var (pairing, judged, dropped) = Pairing();
        // The host never sent rows for 10 (still waiting when the next join point came)
        pairing.OnPlayer(2, 10, [Row("save", "a")]);
        pairing.OnReference(20, [Row("save", "b")]);
        Assert.That(judged, Is.Empty);
        Assert.That(dropped, Is.EqualTo(new[] { (2, 10) }));

        // Rows from a join point older than the host's latest, with none kept for it
        pairing.OnPlayer(3, 15, [Row("save", "c")]);
        Assert.That(judged, Is.Empty);
        Assert.That(dropped, Has.Count.EqualTo(2));

        // A late joiner's rows for a join point the host's rows are still kept for
        pairing.OnReference(30, [Row("save", "d")]);
        pairing.OnPlayer(4, 20, [Row("save", "b")]);
        Assert.That(judged, Is.EqualTo(new[] { (4, "b", "b") }));
    }

    [Test]
    public void RemovedPlayerIsForgotten()
    {
        var (pairing, judged, dropped) = Pairing();
        pairing.OnPlayer(2, 10, [Row("save", "a")]);
        pairing.Remove(2);
        pairing.OnReference(10, [Row("save", "a")]);
        Assert.That(judged, Is.Empty);
        Assert.That(dropped, Is.Empty);
    }
}
