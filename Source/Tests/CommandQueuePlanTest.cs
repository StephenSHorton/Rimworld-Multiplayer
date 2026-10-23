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
    public void PastCommandsDuplicatesAndUnloadedMapsAreLeftOut()
    {
        var shared = Cmd(20, 0);
        var snapshot = new Dictionary<int, List<ScheduledCommand>>
        {
            // The host's snapshot keeps the world queue, which holds map commands under consistent order
            [ScheduledCommand.Global] = [shared, Cmd(9, -1)],
            [0] = [shared, Cmd(30, 0)],
            [7] = [Cmd(25, 7)],
        };

        var plan = CommandQueuePlan.Build(snapshot, [0], timer: 10, singleQueue: true);

        Assert.That(plan.world.Select(c => c.ticks), Is.EqualTo(new[] { 20, 30 }));
        Assert.That(plan.duplicates, Is.EqualTo(1));
        Assert.That(plan.beforeTimer, Is.EqualTo(1));
        Assert.That(plan.unloadedMap, Is.EqualTo(1));
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

        var missing = CommandQueuePlan.Missing(snapshot, [new[] { queued }], [0], timer: 35);

        Assert.That(missing, Is.EqualTo(new[] { lost }));
    }
}
