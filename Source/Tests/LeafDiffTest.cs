using Multiplayer.Common.Desyncs;

namespace Tests;

[TestFixture]
public class LeafDiffTest
{
    [Test]
    public void NamesTheEntityAndTheElementThatDiffers()
    {
        var host = "Human1 pos=(1, 0, 2) job=Mine#5 needs=[Beauty=0.1,Food=0.5]\nHuman2 pos=(3, 0, 4) needs=[Food=0.2]";
        var client = "Human1 pos=(1, 0, 2) job=Mine#5 needs=[Beauty=0.2,Food=0.5]\nHuman2 pos=(3, 0, 4) needs=[Food=0.2]";

        var diff = LeafDiff.Diff(host, client, "Stephen", "Blake");

        Assert.That(diff, Does.StartWith("1 differ"));
        Assert.That(diff, Does.Contain("Human1 needs.Beauty: 0.1 (Stephen) vs 0.2 (Blake)"));
        Assert.That(diff, Does.Not.Contain("Human2"));
        Assert.That(diff, Does.Not.Contain("Food"));
    }

    [Test]
    public void ReportsEntitiesOnlyOneGameHas()
    {
        var diff = LeafDiff.Diff("Steel1 stack=5\nSteel2 stack=1", "Steel1 stack=5", "host", "client");
        Assert.That(diff, Does.Contain("Steel2: only in host's game"));
    }

    [Test]
    public void ParenthesizedValuesStayWhole()
    {
        var fields = LeafDiff.Split("Human1 pos=(10, 0, 20) drafted=False");
        Assert.That(fields["pos"], Is.EqualTo("(10, 0, 20)"));
        Assert.That(fields["drafted"], Is.EqualTo("False"));
    }

    [Test]
    public void GzipRoundTrips()
    {
        Assert.That(LeafDiff.Gunzip(LeafDiff.Gzip("a\nb")), Is.EqualTo("a\nb"));
    }
}
