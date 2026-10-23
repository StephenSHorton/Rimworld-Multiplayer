using System.Collections.Generic;

namespace Multiplayer.Common
{
    public class ScheduledCommand(CommandType type, int ticks, int factionId, int mapId, int playerId, byte[] data)
    {
        public const int NoFaction = -1;
        public const int Global = -1;
        public const int NoPlayer = -1;

        public readonly CommandType type = type;
        public readonly int ticks = ticks;
        public readonly int factionId = factionId;
        public readonly int mapId = mapId;
        public readonly int playerId = playerId;
        public readonly byte[] data = data;

        // The order commands with the same tick run in. A joiner rebuilds its queues from per-map lists, which
        // don't say whether a world command or a map command at the same tick came first; sorting by this puts
        // them back the way they arrived (the server's count on a client, the host's own count on the host)
        public long seq;

        public override string ToString() =>
            $"Cmd: {type}, faction: {factionId}, map: {mapId}, ticks: {ticks}, player: {playerId}";

        public static byte[] Serialize(ScheduledCommand cmd)
        {
            ByteWriter writer = new ByteWriter();
            writer.WriteEnum(cmd.type);
            writer.WriteInt32(cmd.ticks);
            writer.WriteInt32(cmd.factionId);
            writer.WriteInt32(cmd.mapId);
            writer.WriteInt32(cmd.playerId);
            writer.WritePrefixedBytes(cmd.data);
            writer.WriteLong(cmd.seq);

            return writer.ToArray();
        }

        public static ScheduledCommand Deserialize(ByteReader data)
        {
            CommandType cmd = data.ReadEnum<CommandType>();
            int ticks = data.ReadInt32();
            int factionId = data.ReadInt32();
            int mapId = data.ReadInt32();
            int playerId = data.ReadInt32();
            byte[] extraBytes = data.ReadPrefixedBytes()!;
            // Replays and desync zips from before protocol 64 end here
            long seq = data.Left >= 8 ? data.ReadLong() : 0;

            return new ScheduledCommand(cmd, ticks, factionId, mapId, playerId, extraBytes) { seq = seq };
        }

        public static List<ScheduledCommand> DeserializeCmds(byte[] data)
        {
            var reader = new ByteReader(data);

            int count = reader.ReadInt32();
            var result = new List<ScheduledCommand>(count);
            for (int i = 0; i < count; i++)
                result.Add(Deserialize(new ByteReader(reader.ReadPrefixedBytes()!)));

            return result;
        }

        public static byte[] SerializeCmds(List<ScheduledCommand> cmds)
        {
            ByteWriter writer = new ByteWriter();

            writer.WriteInt32(cmds.Count);
            foreach (var cmd in cmds)
                writer.WritePrefixedBytes(Serialize(cmd));

            return writer.ToArray();
        }
    }
}
