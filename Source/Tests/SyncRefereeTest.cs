using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class SyncRefereeTest
{
    private MultiplayerServer server = null!;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });
        nextPlayerId = 1;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private ServerPlayer AddPlayer(string username, bool isHost = false)
    {
        var conn = new RecordingConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);

        if (isHost)
            server.hostUsername = username;

        server.playerManager.Players.Add(player);
        return player;
    }

    private static byte[] Opinion(int tick, ulong pawnsHash) => BinderOf.Identity<SyncOpinion>().Serialize(new SyncOpinion
    {
        startTick = tick,
        commandRandomStates = [1],
        worldRandomStates = [2],
        mapRandomStates = [new MapRandomState { mapId = 0, randomStates = [3] }],
        traceHashes = [],
        faults = [],
        lanes = [new LaneHash { tick = tick, mapId = 0, lane = "pawns", hash = pawnsHash, hard = false }]
    });

    private static int Sent(ServerPlayer player, Packets type) =>
        ((RecordingConnection)player.conn).SentPackets.Count(p => p == type);

    private void Exchange(ServerPlayer host, ServerPlayer player, int tick)
    {
        server.gameTimer = tick;
        server.referee.OnOpinion(host, Opinion(tick, 1), true);
        server.referee.OnOpinion(player, Opinion(tick, 2), false);
    }

    [Test]
    public void ResetPlayerReportsTheSameDifferenceAgain()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        Exchange(host, blake, 300);
        Assert.That(Sent(blake, Packets.Server_SyncVerdict), Is.EqualTo(1));
        Assert.That(Sent(blake, Packets.Server_LeafRequest), Is.EqualTo(1));

        // The same difference again is one problem: no new warning, and the leaf request is cooling down
        Exchange(host, blake, 360);
        Assert.That(Sent(blake, Packets.Server_SyncVerdict), Is.EqualTo(1));
        Assert.That(Sent(blake, Packets.Server_LeafRequest), Is.EqualTo(1));

        // After a resync it's news again
        server.referee.OnPlayerReset(blake);
        Exchange(host, blake, 420);
        Assert.That(Sent(blake, Packets.Server_SyncVerdict), Is.EqualTo(2));
        Assert.That(Sent(blake, Packets.Server_LeafRequest), Is.EqualTo(2));
    }

    [Test]
    public void ClientFoundDesyncReachesTheHostAndAsksForTheDiffs()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        var packet = new ClientDesyncedPacket(600, -1)
        {
            message = "map 0 / time lane differs at tick 600 (reference: mapTicks=900; other: mapTicks=899)",
            lane = "time",
            mapId = 0,
            rngScope = 0,
            rngEntry = 4,
            laneDiffs = ["0|time"]
        };
        server.referee.OnClientDesync(blake, packet);

        Assert.That(Sent(host, Packets.Server_SyncVerdict), Is.EqualTo(1));
        Assert.That(Sent(host, Packets.Server_LeafRequest), Is.EqualTo(1));
        Assert.That(Sent(host, Packets.Server_RandAttrRequest), Is.EqualTo(1));
        Assert.That(Sent(blake, Packets.Server_RandAttrRequest), Is.EqualTo(1));

        // Reported once: the referee's own verdict for the same player doesn't go out again
        server.referee.OnClientDesync(blake, packet);
        Assert.That(Sent(host, Packets.Server_SyncVerdict), Is.EqualTo(1));
    }

    [Test]
    public void ResetLeavesOtherPlayersCounts()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");
        var cara = AddPlayer("Cara");

        Exchange(host, blake, 300);
        server.referee.OnOpinion(cara, Opinion(300, 2), false);
        Assert.That(Sent(cara, Packets.Server_SyncVerdict), Is.EqualTo(1));

        server.referee.OnPlayerReset(blake);
        Exchange(host, blake, 360);
        server.referee.OnOpinion(cara, Opinion(360, 2), false);
        Assert.That(Sent(blake, Packets.Server_SyncVerdict), Is.EqualTo(2));
        Assert.That(Sent(cara, Packets.Server_SyncVerdict), Is.EqualTo(1));
    }

    private static byte[] RandOpinion(int tick, ulong mapState, ulong attrHash) => BinderOf.Identity<SyncOpinion>().Serialize(new SyncOpinion
    {
        startTick = tick,
        commandRandomStates = [1],
        worldRandomStates = [2],
        mapRandomStates = [new MapRandomState { mapId = 1, randomStates = [mapState] }],
        traceHashes = [],
        faults = [],
        lanes = [],
        randAttr = [new RandAttrHash { scope = 1, hash = attrHash, records = 10 }]
    });

    [Test]
    public void SoftRandAttrDifferenceLeavesTheHardDesyncItsDiff()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        // Same random state, different attribution: soft, one request
        server.gameTimer = 300;
        server.referee.OnOpinion(host, RandOpinion(300, 3, 1), true);
        server.referee.OnOpinion(blake, RandOpinion(300, 3, 2), false);
        Assert.That(Sent(blake, Packets.Server_RandAttrRequest), Is.EqualTo(1));

        // The soft one again cools down
        server.gameTimer = 330;
        server.referee.OnOpinion(host, RandOpinion(330, 3, 1), true);
        server.referee.OnOpinion(blake, RandOpinion(330, 3, 2), false);
        Assert.That(Sent(blake, Packets.Server_RandAttrRequest), Is.EqualTo(1));

        // The hard desync a little later still gets its request, inside the soft cooldown
        server.gameTimer = 600;
        server.referee.OnOpinion(host, RandOpinion(600, 3, 1), true);
        server.referee.OnOpinion(blake, RandOpinion(600, 4, 2), false);
        Assert.That(Sent(blake, Packets.Server_RandAttrRequest), Is.EqualTo(2));
        Assert.That(Sent(host, Packets.Server_RandAttrRequest), Is.EqualTo(2));

        // and the same desync in the next opinion is not asked again
        server.gameTimer = 630;
        server.referee.OnOpinion(host, RandOpinion(630, 3, 1), true);
        server.referee.OnOpinion(blake, RandOpinion(630, 4, 2), false);
        Assert.That(Sent(blake, Packets.Server_RandAttrRequest), Is.EqualTo(2));
    }

    [Test]
    public void HarmonyRowDifferenceIsAWarningNamingMethods()
    {
        var (_, warnings) = Fingerprint.Compare(
            [new FingerprintRow { name = SyncReferee.HarmonyRow, value = "2 methods, AA: A.M;B.N", warn = true }],
            [new FingerprintRow { name = SyncReferee.HarmonyRow, value = "2 methods, BB: A.M;C.O", warn = true }],
            "host", "Blake");

        var lines = SyncReferee.HarmonyRowWarnings(warnings, "Blake");
        Assert.That(lines, Has.Count.EqualTo(3));
        Assert.That(lines[0], Does.Contain("warning").And.Contain("Harmony patches"));
        Assert.That(lines[1], Does.Contain("host's game: B.N"));
        Assert.That(lines[2], Does.Contain("Blake's game: C.O"));
    }

    [Test]
    public void HarmonyRowWithoutMethodsIsOneWarning()
    {
        var (_, warnings) = Fingerprint.Compare(
            [new FingerprintRow { name = SyncReferee.HarmonyRow, value = "2 methods, AA", warn = true }],
            [new FingerprintRow { name = SyncReferee.HarmonyRow, value = "3 methods, BB", warn = true }],
            "host", "Blake");

        var lines = SyncReferee.HarmonyRowWarnings(warnings, "Blake");
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Contain("2 methods, AA (host) vs 3 methods, BB (Blake)"));
    }
}
