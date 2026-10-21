using System;
using System.Linq;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class CommandHandler
    {
        private MultiplayerServer server;

        public int SentCmds { get; private set; }

        public CommandHandler(MultiplayerServer server)
        {
            this.server = server;
        }

        public void Send(CommandType cmdType, int factionId, int mapId, byte[] data, ServerPlayer? sourcePlayer = null, ServerPlayer? fauxSource = null, int hostTicks = 0)
        {
            // policy
            if (sourcePlayer != null)
            {
                // A chunk carries its command's type and sync id, so the same rules apply to it
                var policyType = cmdType;
                var syncId = cmdType == CommandType.Sync && data.Length >= 4 ? BitConverter.ToInt32(data, 0) : -1;
                if (cmdType == CommandType.Chunk && !CommandChunks.TryReadHeader(data, out policyType, out syncId))
                    return;

                // Only the server issues these; a client sending one (plain or chunked) could rewrite player data
                if (policyType is CommandType.SetPlayerFaction or CommandType.InitPlayerData)
                    return;

                bool debugCmd =
                    policyType == CommandType.DebugTools ||
                    policyType == CommandType.Sync && server.InitData!.DebugOnlySyncCmds.Contains(syncId);
                if (debugCmd && !CanUseDevMode(sourcePlayer))
                    return;

                bool hostOnly = policyType == CommandType.Sync && server.InitData!.HostOnlySyncCmds.Contains(syncId);
                if (hostOnly && !sourcePlayer.IsHost)
                    return;

                if (cmdType is CommandType.MapTimeSpeed or CommandType.GlobalTimeSpeed &&
                    server.settings.timeControl == TimeControl.HostOnly &&
                    !sourcePlayer.IsHost &&
                    server.PlayingPlayers.Any(p => p.IsHost))
                    return;
            }

            // The host's own command already runs at the tick it chose (host-led clock); the rest are stamped now
            bool fromHost = hostTicks > 0 && server.HostLed && sourcePlayer is { IsHost: true };
            if (fromHost && hostTicks <= server.gameTimer)
                ServerLog.Error($"Host command for tick {hostTicks} arrived after the host reported tick {server.gameTimer}");

            var cmd = new ScheduledCommand(
                cmdType,
                fromHost ? hostTicks : server.StampTick,
                factionId,
                mapId,
                sourcePlayer?.id ?? fauxSource?.id ?? ScheduledCommand.NoPlayer,
                data);
            byte[] toSave = ScheduledCommand.Serialize(cmd);

            // todo cull target players if not global
            server.worldData.mapCmds.GetOrAddNew(mapId).Add(toSave);
            server.worldData.tmpMapCmds?.GetOrAddNew(mapId).Add(toSave);

            if (server.CanUseStandaloneMapStreaming(mapId))
            {
                var serialized = ServerCommandPacket.From(cmd).Serialize();
                foreach (var player in server.PlayingPlayers)
                {
                    if (!player.hasReportedCurrentMap || player.currentMapId < 0 || player.currentMapId == mapId)
                        player.conn.Send(serialized, true);
                }
            }
            else
            {
                // The host scheduled its own command already
                server.SendToPlaying(ServerCommandPacket.From(cmd), excluding: fromHost ? sourcePlayer : null);
            }

            SentCmds++;
        }

        public void PauseAll()
        {
            if (server.settings.timeControl == TimeControl.LowestWins)
                Send(
                    CommandType.TimeSpeedVote,
                    ScheduledCommand.NoFaction,
                    ScheduledCommand.Global,
                    ByteWriter.GetBytes(TimeVote.ResetGlobal, -1)
                );
            else
                Send(
                    CommandType.PauseAll,
                    ScheduledCommand.NoFaction,
                    ScheduledCommand.Global,
                    Array.Empty<byte>()
                );
        }

        public bool CanUseDevMode(ServerPlayer player) =>
            server.settings.debugMode && server.settings.devModeScope switch
            {
                DevModeScope.Everyone => true,
                DevModeScope.HostOnly => player.IsHost
            };
    }
}
