using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// Multifaction faction reservations: where a joiner lands, who may move whom, and that only the server
// issues SetPlayerFaction / InitPlayerData commands
[TestFixture]
public class PlayerFactionTest
{
    private const int HostFaction = 10;
    private const int SpectatorFaction = 11;
    private const int ColonyFaction = 12;

    private MultiplayerServer server = null!;
    private int nextPlayerId;

    private void MakeServer(bool multifaction)
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false,
            multifaction = multifaction
        });
        server.worldData.hostFactionId = HostFaction;
        server.worldData.spectatorFactionId = SpectatorFaction;
        nextPlayerId = 0;
    }

    [SetUp]
    public void SetUp() => MakeServer(multifaction: true);

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private ServerPlayer AddPlayer(string username, bool isHost = false, PlayerType type = PlayerType.Normal)
    {
        var conn = new DummyConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn) { type = type };
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);

        if (isHost)
            server.hostUsername = username;

        server.playerManager.Players.Add(player);
        return player;
    }

    private ServerPlayer Join(string username, bool isHost = false, PlayerType type = PlayerType.Normal)
    {
        var player = AddPlayer(username, isHost, type);
        server.playerManager.OnJoin(player);
        return player;
    }

    private static ServerPlayingState StateOf(ServerPlayer player) => (ServerPlayingState)player.conn.StateObj!;

    private List<ScheduledCommand> GlobalCommands(CommandType type) =>
        server.worldData.mapCmds.GetValueOrDefault(ScheduledCommand.Global, new List<byte[]>())
            .Select(bytes => ScheduledCommand.Deserialize(new ByteReader(bytes)))
            .Where(cmd => cmd.type == type)
            .ToList();

    // OnJoin

    [Test]
    public void OnJoin_ReservedUsername_JoinsReservedFaction()
    {
        Join("host", isHost: true);
        server.worldData.playerFactions["client"] = ColonyFaction;

        Assert.That(Join("client").FactionId, Is.EqualTo(ColonyFaction));
    }

    [Test]
    public void OnJoin_UnknownUsername_JoinsSpectator()
    {
        Join("host", isHost: true);
        server.worldData.playerFactions["client"] = ColonyFaction;

        Assert.That(Join("other").FactionId, Is.EqualTo(SpectatorFaction));
    }

    [Test]
    public void OnJoin_PlayerZero_JoinsHostFactionEvenIfReserved()
    {
        server.worldData.playerFactions["host"] = ColonyFaction;

        Assert.That(Join("host", isHost: true).FactionId, Is.EqualTo(HostFaction));
    }

    [Test]
    public void OnJoin_MultifactionOff_IgnoresReservation()
    {
        MakeServer(multifaction: false);
        Join("host", isHost: true);
        server.worldData.playerFactions["client"] = ColonyFaction;

        Assert.That(Join("client").FactionId, Is.EqualTo(HostFaction));
    }

    [Test]
    public void OnJoin_Arbiter_IgnoresReservation()
    {
        Join("host", isHost: true);
        server.worldData.playerFactions["arbiter"] = ColonyFaction;

        Assert.That(Join("arbiter", type: PlayerType.Arbiter).FactionId, Is.EqualTo(SpectatorFaction));
    }

    [Test]
    public void OnJoin_MatchIsExactCase()
    {
        Join("host", isHost: true);
        server.worldData.playerFactions["client"] = ColonyFaction;

        Assert.That(Join("Client").FactionId, Is.EqualTo(SpectatorFaction));
    }

    // HandleSetFaction

    [Test]
    public void SetFaction_NonHostMovingAnother_Ignored()
    {
        Join("host", isHost: true);
        var client = Join("client");
        var other = Join("other");

        StateOf(other).HandleSetFaction(new ClientSetFactionPacket(client.id, ColonyFaction));

        Assert.That(client.FactionId, Is.EqualTo(SpectatorFaction));
        Assert.That(server.worldData.playerFactions, Is.Empty);
        Assert.That(GlobalCommands(CommandType.SetPlayerFaction), Is.Empty);
    }

    [Test]
    public void SetFaction_PlayerMovingItself_AcceptedAndRecorded()
    {
        Join("host", isHost: true);
        var client = Join("client");

        StateOf(client).HandleSetFaction(new ClientSetFactionPacket(client.id, ColonyFaction));

        Assert.That(client.FactionId, Is.EqualTo(ColonyFaction));
        Assert.That(server.worldData.playerFactions["client"], Is.EqualTo(ColonyFaction));

        var cmds = GlobalCommands(CommandType.SetPlayerFaction);
        Assert.That(cmds, Has.Count.EqualTo(1));
        Assert.That(cmds[0].playerId, Is.EqualTo(ScheduledCommand.NoPlayer));
        var reader = new ByteReader(cmds[0].data);
        Assert.That(reader.ReadString(), Is.EqualTo("client"));
        Assert.That(reader.ReadInt32(), Is.EqualTo(ColonyFaction));
    }

    [Test]
    public void SetFaction_HostMovingAnother_Accepted()
    {
        var host = Join("host", isHost: true);
        var client = Join("client");

        StateOf(host).HandleSetFaction(new ClientSetFactionPacket(client.id, ColonyFaction));

        Assert.That(client.FactionId, Is.EqualTo(ColonyFaction));
        Assert.That(server.worldData.playerFactions["client"], Is.EqualTo(ColonyFaction));
        Assert.That(GlobalCommands(CommandType.SetPlayerFaction), Has.Count.EqualTo(1));
    }

    [Test]
    public void SetFaction_MultifactionOff_NothingRecorded()
    {
        MakeServer(multifaction: false);
        var host = Join("host", isHost: true);
        var client = Join("client");

        StateOf(host).HandleSetFaction(new ClientSetFactionPacket(client.id, ColonyFaction));

        Assert.That(client.FactionId, Is.EqualTo(ColonyFaction));
        Assert.That(server.worldData.playerFactions, Is.Empty);
        Assert.That(GlobalCommands(CommandType.SetPlayerFaction), Is.Empty);
    }

    [Test]
    public void SetFaction_RejoinLandsInRecordedFaction()
    {
        Join("host", isHost: true);
        var client = Join("client");
        StateOf(client).HandleSetFaction(new ClientSetFactionPacket(client.id, ColonyFaction));
        server.playerManager.Players.Remove(client);

        Assert.That(Join("client").FactionId, Is.EqualTo(ColonyFaction));
    }

    // CommandHandler.Send

    [TestCase(CommandType.SetPlayerFaction)]
    [TestCase(CommandType.InitPlayerData)]
    public void ClientSentServerOnlyCommand_Dropped(CommandType type)
    {
        var host = AddPlayer("host", isHost: true);
        var client = AddPlayer("client");
        int before = server.commands.SentCmds;

        server.commands.Send(type, 0, ScheduledCommand.Global, ByteWriter.GetBytes("client", ColonyFaction), sourcePlayer: client);
        server.commands.Send(type, 0, ScheduledCommand.Global, ByteWriter.GetBytes("host", ColonyFaction), sourcePlayer: host);

        Assert.That(server.commands.SentCmds, Is.EqualTo(before));
    }

    [TestCase(CommandType.SetPlayerFaction)]
    [TestCase(CommandType.InitPlayerData)]
    public void ClientSentChunkedServerOnlyCommand_Dropped(CommandType type)
    {
        var client = AddPlayer("client");
        int before = server.commands.SentCmds;

        foreach (var chunk in CommandChunks.Split(type, ByteWriter.GetBytes("client", ColonyFaction), CommandChunks.NextGroupId()))
            server.commands.Send(CommandType.Chunk, 0, ScheduledCommand.Global, chunk, sourcePlayer: client);

        Assert.That(server.commands.SentCmds, Is.EqualTo(before));
    }

    [TestCase(CommandType.SetPlayerFaction)]
    [TestCase(CommandType.InitPlayerData)]
    public void ServerSentServerOnlyCommand_Passes(CommandType type)
    {
        AddPlayer("host", isHost: true);
        int before = server.commands.SentCmds;

        server.commands.Send(type, ScheduledCommand.NoFaction, ScheduledCommand.Global, ByteWriter.GetBytes("client", ColonyFaction));

        Assert.That(server.commands.SentCmds, Is.EqualTo(before + 1));
    }
}
