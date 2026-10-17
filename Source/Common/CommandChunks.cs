using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Multiplayer.Common;

/// <summary>
/// A command too big for one command packet (65,535 bytes) travels compressed, in parts. Each part is a
/// <see cref="CommandType.Chunk"/> command whose data starts with a header: the original type, its sync id (so the
/// server applies the same debug and host-only rules), a group id, the part's index and the part count. Every game
/// receives the parts in the same order and runs the original command once, when its last part comes up, so all
/// games run it on the same tick. Before this, a big command (a large drag, a long settings string) was accepted
/// when sent and then failed on every game, after the sender's interface had already changed (Desync-04).
/// </summary>
public static class CommandChunks
{
    /// <summary>Commands up to this size go as they are; the packet limit is 65,535.</summary>
    public const int MaxSingleCommand = 60_000;
    private const int PayloadPerChunk = 60_000;
    private const int HeaderSize = 1 + 4 + 4 + 2 + 2;

    /// <summary>Above this a command is refused when sent: too big to be a player action.</summary>
    public const int MaxCommandBytes = 16 << 20;

    private static int nextGroupId;

    public static int NextGroupId() => ++nextGroupId;

    public static List<byte[]> Split(CommandType type, byte[] data, int groupId)
    {
        if (data.Length > MaxCommandBytes)
            throw new WriterException($"Command too big to send ({data.Length} > {MaxCommandBytes} bytes)");

        var syncId = type == CommandType.Sync && data.Length >= 4 ? BitConverter.ToInt32(data, 0) : -1;
        var compressed = Compress(data);
        int count = Math.Max(1, (compressed.Length + PayloadPerChunk - 1) / PayloadPerChunk);
        if (count > ushort.MaxValue)
            throw new WriterException($"Command too big to send ({compressed.Length} compressed bytes)");

        var chunks = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int offset = i * PayloadPerChunk;
            int length = Math.Min(PayloadPerChunk, compressed.Length - offset);
            var writer = new ByteWriter();
            writer.WriteByte((byte)type);
            writer.WriteInt32(syncId);
            writer.WriteInt32(groupId);
            writer.WriteUShort((ushort)i);
            writer.WriteUShort((ushort)count);
            writer.WriteFrom(compressed, offset, length);
            chunks.Add(writer.ToArray());
        }

        return chunks;
    }

    public static bool TryReadHeader(byte[] data, out CommandType type, out int syncId)
    {
        type = default;
        syncId = -1;
        if (data == null || data.Length < HeaderSize) return false;
        type = (CommandType)data[0];
        syncId = BitConverter.ToInt32(data, 1);
        return type != CommandType.Chunk;
    }

    /// <summary>Puts the parts back together, per sending player; the same on every game.</summary>
    public class Assembler
    {
        private readonly Dictionary<(int player, int group), byte[][]> parts = new();

        public bool TryAssemble(ScheduledCommand chunk, out ScheduledCommand whole)
        {
            whole = chunk;
            var data = chunk.data;
            if (data == null || data.Length < HeaderSize) return false;

            var reader = new ByteReader(data);
            var type = (CommandType)reader.ReadByte();
            reader.ReadInt32(); // sync id, for the server's rules
            var group = reader.ReadInt32();
            var index = reader.ReadUShort();
            var count = reader.ReadUShort();
            if (type == CommandType.Chunk || count == 0 || index >= count) return false;

            var key = (chunk.playerId, group);
            if (!parts.TryGetValue(key, out var received))
                parts[key] = received = new byte[count][];
            if (received.Length != count) return false;
            received[index] = reader.ReadRaw(data.Length - HeaderSize);

            foreach (var part in received)
                if (part == null)
                    return false;

            parts.Remove(key);
            using var joined = new MemoryStream();
            foreach (var part in received)
                joined.Write(part, 0, part.Length);

            whole = new ScheduledCommand(type, chunk.ticks, chunk.factionId, chunk.mapId, chunk.playerId, Decompress(joined.ToArray()));
            return true;
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal))
            deflate.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
