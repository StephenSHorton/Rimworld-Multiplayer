using Multiplayer.Common;
using Multiplayer.Common.Desyncs;

namespace Tests;

[TestFixture]
public class LeafRingTest
{
    [Test]
    public void KeepsTheLastCheckpointsOfEachMap()
    {
        var ring = new LeafRing<string>(3);
        Assert.That(ring.Add(0, 60, "a60"), Is.Null);
        Assert.That(ring.Add(0, 120, "a120"), Is.Null);
        Assert.That(ring.Add(1, 120, "b120"), Is.Null);
        Assert.That(ring.Add(0, 180, "a180"), Is.Null);

        // The fourth checkpoint of map 0 pushes out its oldest, which comes back for reuse; map 1 keeps its own
        Assert.That(ring.Add(0, 240, "a240"), Is.EqualTo("a60"));
        Assert.That(ring.TryGet(0, 60, out _), Is.False);
        Assert.That(ring.TryGet(0, 120, out var at120) && at120 == "a120");
        Assert.That(ring.TryGet(1, 120, out var b) && b == "b120");
        Assert.That(ring.TryGet(1, 180, out _), Is.False);
        Assert.That(ring.TryGet(2, 120, out _), Is.False);
        Assert.That(ring.Ticks(0), Is.EqualTo(new[] { 120, 180, 240 }));
    }

    [Test]
    public void SameTickReplacesAndClearForgets()
    {
        var ring = new LeafRing<string>(2);
        ring.Add(0, 60, "first");
        Assert.That(ring.Add(0, 60, "again"), Is.EqualTo("first"));
        Assert.That(ring.TryGet(0, 60, out var v) && v == "again");
        Assert.That(ring.Ticks(0), Has.Exactly(1).Items);

        ring.Clear();
        Assert.That(ring.TryGet(0, 60, out _), Is.False);
    }

    [Test]
    public void LeafTickIsTheCheckpointWhileItIsKept()
    {
        // Kept lanes: the checkpoint itself, while every game still has it
        Assert.That(SyncReferee.LeafTick("pawns", 300, 500), Is.EqualTo((300, true)));
        Assert.That(SyncReferee.LeafTick("things", 300, 300), Is.EqualTo((300, true)));
        // Too old for the ring (the host runs ahead and drops it first): ahead of the clock as before
        Assert.That(SyncReferee.LeafTick("pawns", 300, 1000), Is.EqualTo((1030, false)));
        // Other lanes are never kept
        Assert.That(SyncReferee.LeafTick("plants", 300, 310), Is.EqualTo((340, false)));
        Assert.That(SyncReferee.LeafTick("pawns", -1, 310), Is.EqualTo((340, false)));
    }
}
