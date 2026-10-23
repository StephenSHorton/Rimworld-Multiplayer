using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class ConnectionWatchTest
{
    [Test]
    public void QuietConnectionPausesSaysSoOnceAndComesBack()
    {
        var watch = new ConnectionWatch();

        Assert.That(watch.Check(2, 200, false), Is.EqualTo(ConnectionWatch.Change.None));
        Assert.That(watch.IsStale(2), Is.False);

        Assert.That(watch.Check(2, 6_000, false), Is.EqualTo(ConnectionWatch.Change.Lost));
        Assert.That(watch.IsStale(2), Is.True);
        Assert.That(watch.Check(2, 7_000, false), Is.EqualTo(ConnectionWatch.Change.None));

        Assert.That(watch.Check(2, 100, false), Is.EqualTo(ConnectionWatch.Change.Back));
        Assert.That(watch.IsStale(2), Is.False);
    }

    [Test]
    public void JoinPointReloadsPauseQuietly()
    {
        var watch = new ConnectionWatch();

        Assert.That(watch.Check(2, ConnectionWatch.DropMs + 1, joinPointGrace: true), Is.EqualTo(ConnectionWatch.Change.None));
        Assert.That(watch.IsStale(2), Is.True);
        // Back from a quiet reload: nothing was said, so nothing to take back
        Assert.That(watch.Check(2, 100, false), Is.EqualTo(ConnectionWatch.Change.None));
    }

    [Test]
    public void LongSilenceDropsAndLeaversAreForgotten()
    {
        var watch = new ConnectionWatch();

        Assert.That(watch.Check(2, ConnectionWatch.DropMs + 1, false), Is.EqualTo(ConnectionWatch.Change.Drop));

        watch.Check(3, 6_000, false);
        watch.Keep([4]);
        Assert.That(watch.AnyStale, Is.False);
    }
}
