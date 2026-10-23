using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class CommandQueuePlanTest
{
    private static ScheduledCommand Cmd(int ticks, int mapId, byte tag = 0) =>
        new(CommandType.Sync, ticks, 1, mapId, 2, [tag]);

    [Test]
    public void CommandsThatArrivedDuringTheLoadAreQueued()
    {
        // Desync-02: a map's commands came while the game loaded and only reached the snapshot's list
        var snapshot = new Dictionary<int, List<ScheduledCommand>>
        {
            [ScheduledCommand.Global] = [Cmd(700, -1)],
            [0] = [Cmd(706, 0, 1), Cmd(710, 0, 2), Cmd(718, 0, 3)],
        };

        var plan = CommandQueuePlan.Build(snapshot, [0], timer: 700, singleQueue: true);

        Assert.That(plan.world.Select(c => c.ticks), Is.EqualTo(new[] { 700, 706, 710, 718 }));
        Assert.That(plan.maps[0], Is.Empty);
        Assert.That(plan.queued, Is.EqualTo(4));
        Assert.That(plan.atTimer, Is.EqualTo(1));
    }

    [Test]
    public void SameTickCommandsRunInTheOrderTheyArrivedAcrossLists()
    {
        // 2026-10-23: the host ran time speed, designator (map 0), drafts (map 0), player count, time speed at one
        // tick; the joiner's rebuild put the world's three first and desynced on the commands' random state
        ScheduledCommand Seq(int mapId, byte tag, long seq) => new(CommandType.Sync, 721, 1, mapId, 2, [tag]) { seq = seq };
        var snapshot = new Dictionary<int, List<ScheduledCommand>>
        {
            [ScheduledCommand.Global] = [Seq(-1, 1, 10), Seq(-1, 5, 14), Seq(-1, 6, 15)],
            [0] = [Seq(0, 2, 11), Seq(0, 3, 12), Seq(0, 4, 13)],
        };

        var plan = CommandQueuePlan.Build(snapshot, [0], timer: 700, singleQueue: true);

        Assert.That(plan.world.Select(c => c.data[0]), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
    }

    [Test]
    public void ACommandsSeqSurvivesSerializingAndOldCommandsReadAsZero()
    {
        var cmd = new ScheduledCommand(CommandType.Sync, 5, 1, 0, 2, [7]) { seq = 1234567890123 };
        var back = ScheduledCommand.Deserialize(new ByteReader(ScheduledCommand.Serialize(cmd)));
        Assert.That(back.seq, Is.EqualTo(1234567890123));
        Assert.That(back.data, Is.EqualTo(new byte[] { 7 }));

        // The format before protocol 64, as in older replays and desync zips
        var writer = new ByteWriter();
        writer.WriteEnum(CommandType.Sync);
        writer.WriteInt32(5);
        writer.WriteInt32(1);
        writer.WriteInt32(0);
        writer.WriteInt32(2);
        writer.WritePrefixedBytes([7]);
        var old = ScheduledCommand.Deserialize(new ByteReader(writer.ToArray()));
        Assert.That(old.seq, Is.EqualTo(0));
        Assert.That(old.ticks, Is.EqualTo(5));
    }

    [Test]
    public void EachMapGetsItsOwnQueueWithoutConsistentOrder()
    {
        var snapshot = new Dictionary<int, List<ScheduledCommand>>
        {
            [ScheduledCommand.Global] = [Cmd(5, -1)],
            [0] = [Cmd(6, 0)],
            [3] = [Cmd(4, 3)],
        };

        var plan = CommandQueuePlan.Build(snapshot, [3, 0], timer: 0, singleQueue: false);

        Assert.That(plan.world, Has.Count.EqualTo(1));
        Assert.That(plan.maps[0].Single().ticks, Is.EqualTo(6));
        Assert.That(plan.maps[3].Single().ticks, Is.EqualTo(4));
    }

    [Test]
    public void PastCommandsAndDuplicatesAreLeftOut()
    {
        var shared = Cmd(20, 0);
        var snapshot = new Dictionary<int, List<ScheduledCommand>>
        {
            // The host's snapshot keeps the world queue, which holds map commands under consistent order
            [ScheduledCommand.Global] = [shared, Cmd(9, -1)],
            [0] = [shared, Cmd(30, 0)],
        };

        var plan = CommandQueuePlan.Build(snapshot, [0], timer: 10, singleQueue: true);

        Assert.That(plan.world.Select(c => c.ticks), Is.EqualTo(new[] { 20, 30 }));
        Assert.That(plan.duplicates, Is.EqualTo(1));
        Assert.That(plan.beforeTimer, Is.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CommandsForAMapNotMadeYetGoInTheWorldQueue(bool singleQueue)
    {
        // The host made map 7 after the join point and a command for it came while this game loaded: the catch-up
        // makes map 7 again, and maps no longer take their list when made, so the world queue must hold it
        var forMap7 = Cmd(25, 7);
        var snapshot = new Dictionary<int, List<ScheduledCommand>>
        {
            [0] = [Cmd(30, 0)],
            [7] = [Cmd(5, 7), forMap7],
        };

        var plan = CommandQueuePlan.Build(snapshot, [0], timer: 10, singleQueue);

        Assert.That(plan.world, Does.Contain(forMap7));
        Assert.That(plan.maps.ContainsKey(7), Is.False);
        Assert.That(plan.unloadedMap, Is.EqualTo(1));
        Assert.That(plan.beforeTimer, Is.EqualTo(1));
        Assert.That(CommandQueuePlan.Missing(snapshot, [plan.world, ..plan.maps.Values], timer: 10), Is.Empty);
    }

    [Test]
    public void EqualTicksKeepTheWorldFirstThenTheTickLoopsMapOrder()
    {
        var snapshot = new Dictionary<int, List<ScheduledCommand>>
        {
            [0] = [Cmd(50, 0, 1)],
            [ScheduledCommand.Global] = [Cmd(50, -1, 2)],
            [3] = [Cmd(50, 3, 3)],
        };

        // The tick loop runs the last map first
        var plan = CommandQueuePlan.Build(snapshot, [3, 0], timer: 0, singleQueue: true);

        Assert.That(plan.world.Select(c => c.data[0]), Is.EqualTo(new byte[] { 2, 3, 1 }));
    }

    [Test]
    public void MissingNamesCommandsAheadThatNoQueueHolds()
    {
        var queued = Cmd(40, 0);
        var lost = Cmd(41, 0);
        var past = Cmd(30, 0);
        var snapshot = new Dictionary<int, List<ScheduledCommand>> { [0] = [past, queued, lost] };

        var missing = CommandQueuePlan.Missing(snapshot, [new[] { queued }], timer: 35);

        Assert.That(missing, Is.EqualTo(new[] { lost }));
    }
}
