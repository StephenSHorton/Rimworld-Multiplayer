using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class TraceBlocksTest
{
    private static List<int> Traces(int count, int seed = 1) => Enumerable.Range(0, count).Select(i => i * 7919 + seed).ToList();

    [Test]
    public void AWindowOfRandCallsGoesAsOneHashPerBlock()
    {
        // About a raid window at Ultrafast: 30 ticks of 600 Rand calls
        var full = Traces(18_000);
        var blocks = TraceBlocks.Of(full);

        Assert.That(TraceBlocks.IsBlocks(blocks), Is.True);
        Assert.That(TraceBlocks.Count(blocks), Is.EqualTo(18_000));
        Assert.That(blocks.Count, Is.EqualTo(3 + (18_000 + TraceBlocks.Size - 1) / TraceBlocks.Size));
        Assert.That(TraceBlocks.Of(blocks), Is.SameAs(blocks));

        var opinion = new SyncOpinion
        {
            commandRandomStates = [], worldRandomStates = [], mapRandomStates = [], faults = [], lanes = [], randAttr = [],
            traceHashes = blocks
        };
        Assert.That(BinderOf.Identity<SyncOpinion>().Serialize(opinion).Length, Is.LessThan(400));
    }

    [Test]
    public void TheFirstDifferenceNamesTheBlockWhereTracesPart()
    {
        var host = Traces(1000);
        var client = Traces(1000);
        client[600] ^= 1;

        Assert.That(TraceBlocks.FirstDifference(client, TraceBlocks.Of(host), out var found), Is.EqualTo(512));
        Assert.That(found, Is.True);
        Assert.That(TraceBlocks.FirstDifference(host, TraceBlocks.Of(Traces(1000)), out found), Is.EqualTo(-1));
        Assert.That(found, Is.False);
    }

    [Test]
    public void OnlyTheCountDiffersWhenOneWindowEndedSooner()
    {
        Assert.That(TraceBlocks.FirstDifference(Traces(512), TraceBlocks.Of(Traces(768)), out var found), Is.EqualTo(511));
        Assert.That(found, Is.False);
    }

    [Test]
    public void NoTracesOnEitherSideIsNoDifference()
    {
        Assert.That(TraceBlocks.FirstDifference([], TraceBlocks.Of([]), out var found), Is.EqualTo(-1));
        Assert.That(found, Is.False);
        Assert.That(TraceBlocks.Count([]), Is.EqualTo(0));
    }

    [Test]
    public void AFullListThatStartsWithTheMarkerIsStillAFullList()
    {
        var full = Traces(600);
        full[0] = TraceBlocks.Marker;
        Assert.That(TraceBlocks.IsBlocks(full), Is.False);
        Assert.That(TraceBlocks.Count(full), Is.EqualTo(600));
        Assert.That(TraceBlocks.FirstDifference(full, TraceBlocks.Of(full), out _), Is.EqualTo(-1));
    }
}
