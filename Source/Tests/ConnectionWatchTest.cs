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
    public void ARejoiningPlayerIsNotWatchedUntilItsWorldIsReady()
    {
        var server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });
        try
        {
            var conn = new DummyConnection("Blake");
            var player = new ServerPlayer(1, conn);
            conn.serverPlayer = player;
            conn.ChangeState(ConnectionStateEnum.ServerPlaying);
            server.playerManager.Players.Add(player);
            player.UpdateStatus(PlayerStatus.Playing);
            Assert.That(server.PlayingIngamePlayers, Does.Contain(player));

            // /resync or the debug reset: the game changes scenes and answers no keepalives while it loads
            ((ServerPlayingState)conn.StateObj!).HandleRejoin(new ByteReader([]));
            Assert.That(server.PlayingIngamePlayers, Does.Not.Contain(player));

            ((ServerPlayingState)conn.StateObj!).HandleWorldReady(new ByteReader([]));
            Assert.That(server.PlayingIngamePlayers, Does.Contain(player));
        }
        finally
        {
            MultiplayerServer.instance = null;
        }
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
