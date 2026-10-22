using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class RandAttrDiffTest
{
    private static string Line(int entry, int ordinal, string kind, string owner, string def, string method, int calls,
        string basePhase = "-", string phase = "-", string job = "-", int toil = -1, string name = "") =>
        new RandAttrLine
        {
            entry = entry, ordinal = ordinal, kind = kind, owner = owner, def = def, cls = kind == "Thing" ? "Pawn" : "-",
            method = method, basePhase = basePhase, phase = phase, job = job, toil = toil, calls = calls, name = name
        }.Format();

    private static string Tick(int entry, int ticksGame, int records) =>
        new RandAttrTickLine { scope = 1, window = 366901, entry = entry, ticksGame = ticksGame, timer = ticksGame, records = records }.Format();

    private static string Table(params string[] lines) => string.Join("\n", lines);

    [Test]
    public void LineRoundTrips()
    {
        var text = Line(8, 57, "Thing", "Human1234", "Human", "TickInterval", 3, phase: "JobDriver_Wait", job: "Wait_Combat", toil: 1, name: "Tynan Q");
        var line = RandAttrLine.Parse(text)!;
        Assert.That(line.Format(), Is.EqualTo(text));
        Assert.That(line.name, Is.EqualTo("Tynan Q"));
        Assert.That(line.Describe(), Does.Contain("Human1234 \"Tynan Q\" (Pawn, Human) TickInterval > JobDriver_Wait job=Wait_Combat toil 1"));
    }

    [Test]
    public void EqualTablesGiveNoDiff()
    {
        var t = Table(Tick(8, 366909, 2), Line(8, 0, "Root", "-", "-", "-", 2, basePhase: "PreTick"), Line(8, 1, "Thing", "Human1", "Human", "TickInterval", 3));
        var diff = RandAttrDiff.Diff(t, t, "host", "Blake", 1, 366901);
        Assert.That(diff.differs, Is.False);
        Assert.That(diff.summary, Does.Contain("same"));
    }

    [Test]
    public void CountDifferenceNamesOwnerAndCounts()
    {
        var a = Table(Tick(8, 366909, 2), Line(8, 0, "Root", "-", "-", "-", 2, basePhase: "PreTick"),
            Line(8, 1, "Thing", "Human1234", "Human", "TickInterval", 3, job: "Wait_Combat", toil: 1, name: "Tynan"));
        var b = Table(Tick(8, 366909, 2), Line(8, 0, "Root", "-", "-", "-", 2, basePhase: "PreTick"),
            Line(8, 1, "Thing", "Human1234", "Human", "TickInterval", 5, job: "Wait_Combat", toil: 1, name: "Tynan"));

        var diff = RandAttrDiff.Diff(a, b, "host", "Blake", 1, 366901);
        Assert.That(diff.differs, Is.True);
        Assert.That(diff.summary, Does.StartWith("map 1 tick 366909 (window 366901 entry 8): Human1234 \"Tynan\" (Pawn, Human) TickInterval job=Wait_Combat toil 1"));
        Assert.That(diff.summary, Does.Contain("made 3 Rand calls on host, 5 on Blake (+2 on Blake)"));
        Assert.That(diff.summary, Does.Contain("record #1 of 2"));
        Assert.That(diff.watchLine!.owner, Is.EqualTo("Human1234"));
    }

    [Test]
    public void ExtraRecordIsRealigned()
    {
        var a = Table(Line(3, 0, "Thing", "Human1", "Human", "TickInterval", 1), Line(3, 1, "Thing", "Human2", "Human", "TickInterval", 1),
            Line(3, 2, "Thing", "Human3", "Human", "TickInterval", 1));
        var b = Table(Line(3, 0, "Thing", "Human1", "Human", "TickInterval", 1), Line(3, 1, "Thing", "Cow9", "Cow", "TickInterval", 2),
            Line(3, 2, "Thing", "Human2", "Human", "TickInterval", 1), Line(3, 3, "Thing", "Human3", "Human", "TickInterval", 1));

        var diff = RandAttrDiff.Diff(a, b, "host", "Blake", 1, 0);
        Assert.That(diff.summary, Does.Contain("Cow9").And.Contain("none on host"));
        Assert.That(diff.refUnitOrdinal, Is.EqualTo(-1));
        Assert.That(diff.otherUnitOrdinal, Is.EqualTo(1));
    }

    [Test]
    public void TierTwoSubRecordsMergeIntoTheirUnit()
    {
        // One game had tier 2 on: its pawn record is split by phase but sums to the same count
        var a = Table(Line(1, 0, "Thing", "Human1", "Human", "TickInterval", 4));
        var b = Table(Line(1, 0, "Thing", "Human1", "Human", "TickInterval", 1, phase: "Pawn_PathFollower.PatherTick"),
            Line(1, 1, "Thing", "Human1", "Human", "TickInterval", 3, phase: "JobDriver_Wait"));
        Assert.That(RandAttrDiff.Diff(a, b, "host", "Blake", 1, 0).differs, Is.False);
    }

    [Test]
    public void ReseedAndBetweenTicksAreCounted()
    {
        var a = Table(Line(0, 0, "BetweenTicks", "-", "-", "-", 4), Line(0, 1, "Reseed", "-", "-", "-", 1));
        var b = Table(Line(0, 0, "Reseed", "-", "-", "-", 1));
        var diff = RandAttrDiff.Diff(a, b, "host", "Blake", 1, 0);
        Assert.That(diff.differs, Is.True);
        Assert.That(diff.summary, Does.Contain("between its ticks"));
        Assert.That(diff.text, Does.Contain("between-tick calls: 4 vs 0"));
        Assert.That(diff.text, Does.Contain("Reseeds: 1 (host) vs 1 (Blake)"));
    }

    private static string Cap(int entry, int unit, int call, params string[] frames) =>
        RandAttrDiff.Capture.Header(entry, unit, call, 100) + "\n" + string.Join("\n", frames.Select(f => f.StartsWith("*") ? " " + f : "  " + f));

    [Test]
    public void FirstDifferingCallFindsTheExtraCall()
    {
        // Frames as Harmony's FullDescription writes them
        const string randValue = "*static System.Single Verse.Rand::get_Value()";
        const string pawnTick = "*virtual System.Void Verse.Pawn::TickInterval(System.Int32 delta)";
        const string inject = "static System.Void Multiplayer.Client.Desyncs.RandInjectPatch::Postfix(Verse.Pawn __instance)";
        var host = string.Join("\n", Cap(8, 1, 1, randValue, pawnTick));
        var blake = string.Join("\n", Cap(8, 1, 1, randValue, pawnTick), Cap(8, 1, 2, randValue, inject, pawnTick));

        var call = RandAttrDiff.FirstDifferingCall(host, blake, 8, 1, 1, "host", "Blake", out var detail);
        Assert.That(call, Is.EqualTo("first extra call (call 2 on Blake): " + inject + " < " + pawnTick.TrimStart('*')));
        Assert.That(detail, Does.Contain("== host").And.Contain("== Blake"));
    }

    [Test]
    public void FirstDifferingCallComparesPatchedFrames()
    {
        var host = Cap(2, 0, 1, "*static System.Single Verse.Rand::get_Value()", "static System.Void Some::Inlined()", "*System.Void Verse.Pawn::TickInterval(System.Int32 delta)");
        var same = Cap(2, 0, 1, "*static System.Single Verse.Rand::get_Value()", "*System.Void Verse.Pawn::TickInterval(System.Int32 delta)");
        Assert.That(RandAttrDiff.FirstDifferingCall(host, same, 2, 0, 0, "host", "Blake", out _), Is.Null);

        var other = Cap(2, 0, 1, "*static System.Single Verse.Rand::get_Value()", "*System.Void Verse.AI.Pawn_JobTracker::JobTrackerTickInterval(System.Int32 delta)",
            "*System.Void Verse.Pawn::TickInterval(System.Int32 delta)");
        Assert.That(RandAttrDiff.FirstDifferingCall(host, other, 2, 0, 0, "host", "Blake", out _), Does.StartWith("call 1 differs"));
    }

    [Test]
    public void NoCapturesSaysSo()
    {
        Assert.That(RandAttrDiff.FirstDifferingCall("", "", 1, 0, 0, "host", "Blake", out var detail), Is.Null);
        Assert.That(detail, Does.Contain("No captured stacks"));
    }

    [Test]
    public void RefereeBuildsDiffAndWatch()
    {
        var a = Table(Tick(8, 366909, 1), Line(8, 0, "Thing", "Human1234", "Human", "TickInterval", 3, job: "Wait_Combat", toil: 1));
        var b = Table(Tick(8, 366909, 1), Line(8, 0, "Thing", "Human1234", "Human", "TickInterval", 4, job: "Wait_Combat", toil: 1));
        var result = SyncReferee.BuildRandAttrDiff(1, 366901, 8,
            new ClientRandAttrPacket { gzTable = LeafDiff.Gzip(a), gzStacks = LeafDiff.Gzip("") },
            new ClientRandAttrPacket { gzTable = LeafDiff.Gzip(b), gzStacks = LeafDiff.Gzip("") }, "host", "Blake");

        Assert.That(result.summary, Does.Contain("+1 on Blake"));
        Assert.That(result.watchKind, Is.EqualTo("Thing"));
        Assert.That(result.watchDef, Is.EqualTo("Human"));
        Assert.That(result.watchJob, Is.EqualTo("Wait_Combat"));
        Assert.That(result.watchOwner, Is.EqualTo("Human1234"));
        Assert.That(result.ticksGame, Is.EqualTo(366909));
    }

    [Test]
    public void RefereeSummaryFitsThePacketInMultibyteNames()
    {
        var name = new string('\u042F', 700); // Cyrillic: 2 UTF-8 bytes each
        var a = Table(Tick(8, 366909, 1), Line(8, 0, "Thing", "Human1234", "Human", "TickInterval", 3, name: name));
        var b = Table(Tick(8, 366909, 1), Line(8, 0, "Thing", "Human1234", "Human", "TickInterval", 4, name: name));
        var result = SyncReferee.BuildRandAttrDiff(1, 366901, 8,
            new ClientRandAttrPacket { gzTable = LeafDiff.Gzip(a), gzStacks = LeafDiff.Gzip("") },
            new ClientRandAttrPacket { gzTable = LeafDiff.Gzip(b), gzStacks = LeafDiff.Gzip("") }, "host", "Blake");

        Assert.That(System.Text.Encoding.UTF8.GetByteCount(result.summary), Is.LessThanOrEqualTo(ServerRandAttrDiffPacket.MaxSummaryBytes));
        Assert.That(result.summary, Does.EndWith("..."));
        Assert.DoesNotThrow(() => result.Serialize());
    }

    [Test]
    public void TruncateUtf8KeepsSurrogatePairsWhole()
    {
        Assert.That(SyncReferee.TruncateUtf8("short", 10), Is.EqualTo("short"));
        var emoji = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 10)); // 4 bytes each
        var cut = SyncReferee.TruncateUtf8(emoji, 13);
        Assert.That(cut, Is.EqualTo("\U0001F600\U0001F600..."));
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(cut), Is.LessThanOrEqualTo(13));
    }

    [Test]
    public void RefereeReportsAMissedAnswer()
    {
        var result = SyncReferee.BuildRandAttrDiff(1, 10, 2, new ClientRandAttrPacket { missed = true }, new ClientRandAttrPacket(), "host", "Blake");
        Assert.That(result.summary, Does.Contain("could not compare").And.Contain("host's game"));
        Assert.That(result.watchKind, Is.Null.Or.Empty);
    }
}
