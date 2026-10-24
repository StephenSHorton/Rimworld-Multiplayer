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

    private static byte[] Opinion(int tick, ulong pawnsHash, string lane = "pawns") => BinderOf.Identity<SyncOpinion>().Serialize(new SyncOpinion
    {
        startTick = tick,
        commandRandomStates = [1],
        worldRandomStates = [2],
        mapRandomStates = [new MapRandomState { mapId = 0, randomStates = [3] }],
        traceHashes = [],
        faults = [],
        lanes = [new LaneHash { tick = tick, mapId = 0, lane = lane, hash = pawnsHash, hard = false }]
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

    private static List<T> Read<T>(ServerPlayer player, Packets type) where T : struct, IPacket =>
        ((RecordingConnection)player.conn).Sent<T>(type);

    [Test]
    public void SoftDifferenceBackAfterAQuietGapIsReportedAgain()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        Exchange(host, blake, 300);
        Exchange(host, blake, 360);
        Assert.That(Sent(blake, Packets.Server_SyncVerdict), Is.EqualTo(1));

        // 2026-10-23: healed, then back much later; the 3rd sighting is not the 51st, but it is news
        Exchange(host, blake, 2400);
        var verdicts = Read<ServerSyncVerdictPacket>(blake, Packets.Server_SyncVerdict);
        Assert.That(verdicts, Has.Count.EqualTo(2));
        Assert.That(verdicts[1].message, Does.EndWith("(seen 3 times, last at tick 360)"));
        Assert.That(Read<ServerSyncVerdictPacket>(host, Packets.Server_SyncVerdict)[1].message, Is.EqualTo(verdicts[1].message));

        // Straight after, it is a repeat again
        Exchange(host, blake, 2460);
        Assert.That(Sent(blake, Packets.Server_SyncVerdict), Is.EqualTo(2));
    }

    [Test]
    public void SoftRepeatsCountsRepeatsAndGaps()
    {
        var repeats = new SoftRepeats(repeatEvery: 3, quietGap: 100);
        Assert.That(repeats.Note("k", 10), Is.EqualTo((SoftRepeats.Kind.First, "")));
        Assert.That(repeats.Note("k", 20).kind, Is.EqualTo(SoftRepeats.Kind.Quiet));
        Assert.That(repeats.Note("k", 30).kind, Is.EqualTo(SoftRepeats.Kind.Quiet));
        Assert.That(repeats.Note("k", 40), Is.EqualTo((SoftRepeats.Kind.Repeat, " (seen 4 times)")));
        // Judged out of order: an older window doesn't move the last sighting back
        Assert.That(repeats.Note("k", 35).kind, Is.EqualTo(SoftRepeats.Kind.Quiet));
        Assert.That(repeats.Note("k", 140).kind, Is.EqualTo(SoftRepeats.Kind.Quiet));
        Assert.That(repeats.Note("k", 241), Is.EqualTo((SoftRepeats.Kind.Recurrence, " (seen 7 times, last at tick 140)")));
        Assert.That(repeats.Note("other", 241).kind, Is.EqualTo(SoftRepeats.Kind.First));

        repeats.RemoveWhere(k => k == "k");
        Assert.That(repeats.Note("k", 250).kind, Is.EqualTo(SoftRepeats.Kind.First));
    }

    [Test]
    public void LeavesAreAskedAtTheCheckpointThatDiffered()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        // Judged 200 ticks after its checkpoint: still kept by every game
        server.referee.OnOpinion(host, Opinion(300, 1), true);
        server.gameTimer = 500;
        server.referee.OnOpinion(blake, Opinion(300, 2), false);
        var request = Read<ServerLeafRequestPacket>(blake, Packets.Server_LeafRequest).Single();
        Assert.That(request.tick, Is.EqualTo(300));
        Assert.That(Read<ServerLeafRequestPacket>(host, Packets.Server_LeafRequest).Single().tick, Is.EqualTo(300));

        // The host's game no longer kept it: both are asked again ahead of the clock
        server.gameTimer = 520;
        server.referee.OnLeaves(host, new ClientLeavesPacket { requestId = request.requestId, missed = true, gzippedLines = LeafDiff.Gzip("") });
        server.referee.OnLeaves(blake, new ClientLeavesPacket { requestId = request.requestId, gzippedLines = LeafDiff.Gzip("Human1 pos=(1, 0, 1)") });
        Assert.That(Sent(blake, Packets.Server_LeafDiff), Is.EqualTo(0));
        var again = Read<ServerLeafRequestPacket>(blake, Packets.Server_LeafRequest);
        Assert.That(again, Has.Count.EqualTo(2));
        Assert.That(again[1].tick, Is.EqualTo(550));
        Assert.That(Read<ServerLeafRequestPacket>(host, Packets.Server_LeafRequest)[1].tick, Is.EqualTo(550));

        // The fallback's answers make the diff
        server.referee.OnLeaves(host, new ClientLeavesPacket { requestId = again[1].requestId, gzippedLines = LeafDiff.Gzip("Human1 pos=(1, 0, 1)") });
        server.referee.OnLeaves(blake, new ClientLeavesPacket { requestId = again[1].requestId, gzippedLines = LeafDiff.Gzip("Human1 pos=(2, 0, 1)") });
        Assert.That(Sent(blake, Packets.Server_LeafDiff), Is.EqualTo(1));
    }

    [Test]
    public void OldCheckpointIsAskedAheadOfTheClock()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        server.referee.OnOpinion(host, Opinion(300, 1), true);
        server.gameTimer = 1000;
        server.referee.OnOpinion(blake, Opinion(300, 2), false);
        Assert.That(Read<ServerLeafRequestPacket>(blake, Packets.Server_LeafRequest).Single().tick, Is.EqualTo(1030));
    }

    [Test]
    public void SoftPawnsDifferenceTurnsTheLedgerOnForBothGames()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        Exchange(host, blake, 300);
        var first = Read<ServerSyncVerdictPacket>(blake, Packets.Server_SyncVerdict).Single();
        Assert.That((first.ledgerFrom, first.ledgerUntil), Is.EqualTo((330, 330 + SyncReferee.LedgerWindow)));
        var hostCopy = Read<ServerSyncVerdictPacket>(host, Packets.Server_SyncVerdict).Single();
        Assert.That((hostCopy.ledgerFrom, hostCopy.ledgerUntil), Is.EqualTo((first.ledgerFrom, first.ledgerUntil)));

        // Back after a gap while the window is still open: reported, the window is left as it is
        Exchange(host, blake, 360);
        Exchange(host, blake, 2400);
        var second = Read<ServerSyncVerdictPacket>(blake, Packets.Server_SyncVerdict)[1];
        Assert.That(second.ledgerUntil, Is.EqualTo(0));

        // Back after the window ended: a new one
        Exchange(host, blake, 6000);
        var third = Read<ServerSyncVerdictPacket>(blake, Packets.Server_SyncVerdict)[2];
        Assert.That((third.ledgerFrom, third.ledgerUntil), Is.EqualTo((6030, 6030 + SyncReferee.LedgerWindow)));
    }

    [Test]
    public void OtherSoftLanesLeaveTheLedgerOff()
    {
        var host = AddPlayer("host", isHost: true);
        var blake = AddPlayer("Blake");

        server.gameTimer = 300;
        server.referee.OnOpinion(host, Opinion(300, 1, "plants"), true);
        server.referee.OnOpinion(blake, Opinion(300, 2, "plants"), false);
        var verdict = Read<ServerSyncVerdictPacket>(blake, Packets.Server_SyncVerdict).Single();
        Assert.That(verdict.ledgerUntil, Is.EqualTo(0));
        Assert.That(Read<ServerLeafRequestPacket>(blake, Packets.Server_LeafRequest).Single().tick, Is.EqualTo(330));
    }
}
