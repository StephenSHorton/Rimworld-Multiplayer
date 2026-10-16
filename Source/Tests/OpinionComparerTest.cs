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
    public void FaultSignatureIgnoresNumbersAndStacks()
    {
        var a = FaultSignature.Normalize("Exception ticking Pawn1234: NullReferenceException\n   at Foo.Bar() 0x0001");
        var b = FaultSignature.Normalize("Exception ticking Pawn99: NullReferenceException\n   at Foo.Baz()");
        Assert.That(a, Is.EqualTo(b));
        Assert.That(FaultSignature.Hash(a), Is.EqualTo(FaultSignature.Hash(b)));
    }
}
