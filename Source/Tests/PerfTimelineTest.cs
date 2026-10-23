using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class PerfTimelineTest
{
    private static readonly DateTime At = new(2026, 10, 21, 12, 0, 5);

    private static List<string> RunSecond(PerfTimeline timeline, float host, float blake, float stpt = 16.7f)
    {
        for (int i = 0; i < MultiplayerServer.NetTicksPerSecond; i++)
            timeline.Sample([("host", host), ("Blake", blake)]);
        return timeline.Second(["host", "Blake"], stpt, At);
    }

    [Test]
    public void WritesFirstSecondThenOnlyChanges()
    {
        var timeline = new PerfTimeline(null);

        var first = RunSecond(timeline, 16f, 20f);
        Assert.That(first, Is.EqualTo(new[] { "12:00:05 Blake 20.0 20.0 16.7", "12:00:05 host 16.0 16.0 16.7" }));

        // Under 20%: nothing
        Assert.That(RunSecond(timeline, 18f, 22f), Is.Empty);

        // Blake's p50 moves from 20 to 40 ms
        var moved = RunSecond(timeline, 16f, 40f, 20f);
        Assert.That(moved, Is.EqualTo(new[] { "12:00:05 Blake 40.0 40.0 20.0" }));
    }

    [Test]
    public void StalePlayerGetsAMarkedLineEverySecond()
    {
        var timeline = new PerfTimeline(null);
        RunSecond(timeline, 16f, 20f);

        // Blake's frame time is frozen at its last value; the line says the connection is quiet
        for (int i = 0; i < MultiplayerServer.NetTicksPerSecond; i++)
            timeline.Sample([("host", 16f), ("Blake", 20f)]);
        var lines = timeline.Second(["host", "Blake"], 16.7f, At, [("Blake", 7.4)]);
        Assert.That(lines, Is.EqualTo(new[] { "12:00:05 Blake 20.0 20.0 16.7 STALE 7s" }));
    }

    [Test]
    public void HeartbeatNamesEveryPlayer()
    {
        var timeline = new PerfTimeline(null);
        List<string> last = [];
        for (int s = 0; s < PerfTimeline.HeartbeatSeconds; s++)
            last = RunSecond(timeline, 16f, 20f);
        Assert.That(last, Has.Count.EqualTo(2));
    }

    [Test]
    public void PercentilesAndMissingFrameTimes()
    {
        var timeline = new PerfTimeline(null);
        for (int i = 0; i < 20; i++)
            timeline.Sample([("host", i < 19 ? 10f : 100f), ("Blake", 0f)]);
        var lines = timeline.Second(["host", "Blake"], 16.7f, At);
        Assert.That(lines, Is.EqualTo(new[] { "12:00:05 host 10.0 100.0 16.7" }));
    }

    [Test]
    public void WritesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mp-perf-timeline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timeline = new PerfTimeline(dir, At);
            RunSecond(timeline, 16f, 20f);
            Assert.That(File.ReadAllLines(timeline.Path!), Has.Length.EqualTo(2));
            Assert.That(Path.GetFileName(timeline.Path), Is.EqualTo("perf-timeline-20261021-120005.txt"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
