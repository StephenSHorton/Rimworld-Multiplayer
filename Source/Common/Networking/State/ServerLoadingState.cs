using System.Collections.Generic;
using System.Threading.Tasks;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common;

public class ServerLoadingState : AsyncConnectionState
{
    public ServerLoadingState(ConnectionBase connection) : base(connection)
    {
    }

    // A frame time sent just before the client asked to rejoin; nothing to do with it while loading
    [TypedPacketHandler]
    public void HandleFrameTime(ClientFrameTimePacket packet)
    {
    }

    // A command sent just before the client asked to rejoin (2026-10-22 14:03:52: "No handler for packet
    // Client_Command in state ServerLoading" disconnected the rejoining player). The game it came from is being
    // replaced by the join point, so it is dropped
    [TypedPacketHandler]
    public void HandleClientCommand(ClientCommandPacket packet)
    {
        ServerLog.Log($"Dropped a {packet.type} command from {Player.Username}, who is rejoining");
    }

    [TypedPacketHandler]
    public void HandleClientKeepAlive(ClientKeepAlivePacket packet)
    {
        Player.ticksBehind = packet.ticksBehind;
        Player.ticksBehindReceivedAt = Server.gameTimer;
        Player.simulating = packet.simulating;
        Player.keepAliveAt = Server.NetTimer;

        if (Player.IsHost)
            Server.workTicks = packet.workTicks;

        var idMatched = Player.keepAliveId == packet.id;
        connection.OnKeepAliveArrived(idMatched);
        if (idMatched)
            Player.keepAliveId++;
    }

    protected override async Task RunState()
    {
        await Server.worldData.WaitJoinPoint();
        await EndIfDead();

        SendWorldData();

        Player.SendPlayerList();
        connection.ChangeState(ConnectionStateEnum.ServerPlaying);
    }

    public void SendWorldData()
    {
        connection.Send(Packets.Server_WorldDataStart);

        ByteWriter writer = new ByteWriter();

        writer.WriteInt32(Player.FactionId);
        writer.WriteInt32(Server.gameTimer);
        writer.WriteInt32(Server.commands.SentCmds);
        writer.WriteBool(Server.freezeManager.Frozen);
        writer.WritePrefixedBytes(Server.worldData.savedGame);
        writer.WritePrefixedBytes(Server.worldData.sessionData);

        writer.WriteInt32(Server.worldData.mapCmds.Count);

        foreach (var kv in Server.worldData.mapCmds)
        {
            int mapId = kv.Key;

            //MultiplayerServer.instance.SendCommand(CommandType.CreateMapFactionData, ScheduledCommand.NoFaction, mapId, ByteWriter.GetBytes(factionId));

            List<byte[]> mapCmds = kv.Value;

            writer.WriteInt32(mapId);

            writer.WriteInt32(mapCmds.Count);
            foreach (var arr in mapCmds)
                writer.WritePrefixedBytes(arr);
        }

        writer.WriteInt32(Server.worldData.mapData.Count);

        foreach (var kv in Server.worldData.mapData)
        {
            int mapId = kv.Key;
            byte[] mapData = kv.Value;

            writer.WriteInt32(mapId);
            writer.WritePrefixedBytes(mapData);
        }

        writer.WriteInt32(Server.worldData.syncInfos.Count);
        foreach (var syncInfo in Server.worldData.syncInfos)
            writer.WritePrefixedBytes(syncInfo);

        byte[] packetData = writer.ToArray();
        connection.SendFragmented(Packets.Server_WorldData, packetData);

        ServerLog.Log("World response sent: " + packetData.Length);
    }
}
