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
}
