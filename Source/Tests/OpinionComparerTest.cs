using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class OpinionComparerTest
{
    private static SyncOpinion Opinion(ulong mapState = 0x0000000500000007UL) => new()
    {
        startTick = 300,
        commandRandomStates = [1],
        worldRandomStates = [2],
        mapRandomStates = [new MapRandomState { mapId = 0, randomStates = [mapState] }],
        traceHashes = [],
        faults = [],
        lanes = []
    };

    [Test]
    public void EqualOpinionsAreClean()
    {
        Assert.That(OpinionComparer.Compare(Opinion(), Opinion()).Clean, Is.True);
    }

    [Test]
    public void SameCallCountDifferentSeedIsADesync()
    {
        // The old check compared only the top 32 bits (the call count) and missed this
        var verdict = OpinionComparer.Compare(Opinion(0x0000000500000007UL), Opinion(0x0000000500000008UL));
        Assert.That(verdict.hard, Does.Contain("map 0").And.Contain("seed"));
        Assert.That(verdict.hardMapId, Is.EqualTo(0));
    }

    [Test]
    public void DifferentCallCountNamesTheCounts()
    {
        var verdict = OpinionComparer.Compare(Opinion(0x0000000500000007UL), Opinion(0x0000000600000007UL));
        Assert.That(verdict.hard, Does.Contain("5 vs 6 random calls"));
    }

    [Test]
    public void OneSidedErrorIsSoft()
    {
        var a = Opinion();
        var b = Opinion();
        b.faults = [new FaultEvent { tick = 310, mapId = 0, signature = FaultSignature.Hash("boom"), text = "boom" }];

        var verdict = OpinionComparer.Compare(a, b);
        Assert.That(verdict.hard, Is.Null);
        Assert.That(verdict.soft, Has.Count.EqualTo(1));
        Assert.That(verdict.soft[0], Does.Contain("only in the other game").And.Contain("boom"));
    }

    [Test]
    public void HardLaneMismatchIsADesyncAndSoftLaneIsNot()
    {
        var a = Opinion();
        var b = Opinion();
        a.lanes = [new LaneHash { tick = 330, mapId = 0, lane = "terrain", hash = 1, hard = true },
                   new LaneHash { tick = 330, mapId = 0, lane = "fog", hash = 1, hard = false }];
        b.lanes = [new LaneHash { tick = 330, mapId = 0, lane = "terrain", hash = 2, hard = true },
                   new LaneHash { tick = 330, mapId = 0, lane = "fog", hash = 2, hard = false }];

        var verdict = OpinionComparer.Compare(a, b);
        Assert.That(verdict.hard, Does.Contain("map 0 / terrain"));
        Assert.That(verdict.hardLane, Is.EqualTo("terrain"));
        Assert.That(verdict.soft, Has.Count.EqualTo(1));
        Assert.That(verdict.soft[0], Does.Contain("fog"));
    }

    [Test]
    public void RandomStateDifferenceStillReportsLanesAndOtherScopes()
    {
        var a = Opinion(0x0000000500000007UL);
        var b = Opinion(0x0000000600000007UL);
        b.worldRandomStates = [3];
        a.lanes = [new LaneHash { tick = 330, mapId = 0, lane = "pawns", hash = 1, hard = false }];
        b.lanes = [new LaneHash { tick = 330, mapId = 0, lane = "pawns", hash = 2, hard = false }];

        var verdict = OpinionComparer.Compare(a, b);
        Assert.That(verdict.hardLane, Is.EqualTo("rng"));
        Assert.That(verdict.rngDiffs, Is.EqualTo(new List<(int, int)> { (0, 0), (RandAttrScope.World, 0) }));
        Assert.That(verdict.laneDiffs, Has.Count.EqualTo(1));
        Assert.That(verdict.hard, Does.StartWith("Wrong random state on map 0").And.Contain("also:").And.Contain("map 0 / pawns lane differs"));
        Assert.That(verdict.extra, Has.Some.Contains("for the world"));
    }

    [Test]
    public void AttributionDifferenceWithEqualStatesIsSoft()
    {
        var a = Opinion();
        var b = Opinion();
        a.randAttr = [new RandAttrHash { scope = 0, hash = 11, records = 4 }];
        b.randAttr = [new RandAttrHash { scope = 0, hash = 12, records = 4 }];

        var verdict = OpinionComparer.Compare(a, b);
        Assert.That(verdict.hard, Is.Null);
        Assert.That(verdict.randAttrDiffs, Is.EqualTo(new List<int> { 0 }));
        Assert.That(verdict.soft, Has.Some.Contains("map 0 Rand attribution differs (same random state"));
    }

    [Test]
    public void AttributionOnOneGameOnlyIsNoDifference()
    {
        var a = Opinion();
        a.randAttr = [new RandAttrHash { scope = 0, hash = 11, records = 4 }];
        Assert.That(OpinionComparer.Compare(a, Opinion()).Clean, Is.True);
    }

    [Test]
    public void FaultSignatureIgnoresNumbersAndStacks()
    {
        var a = FaultSignature.Normalize("Exception ticking Pawn1234: NullReferenceException\n   at Foo.Bar() 0x0001");
        var b = FaultSignature.Normalize("Exception ticking Pawn99: NullReferenceException\n   at Foo.Baz()");
        Assert.That(a, Is.EqualTo(b));
        Assert.That(FaultSignature.Hash(a), Is.EqualTo(FaultSignature.Hash(b)));
    }
}
