namespace Multiplayer.Common.Networking.Packet;

/// <summary>
/// Two games' lane differs: at timer tick <see cref="tick"/> each of them lists the entities (or cells) of that
/// lane, one line each, and sends the list back, so the server can say which entity and which field differs.
/// </summary>
[PacketDefinition(Packets.Server_LeafRequest)]
public record struct ServerLeafRequestPacket : IPacket
{
    public int requestId;
    public int tick;
    public int mapId;
    public string lane;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref requestId);
        buf.Bind(ref tick);
        buf.Bind(ref mapId);
        buf.Bind(ref lane, maxLength: 64);
    }
}

[PacketDefinition(Packets.Client_Leaves, allowFragmented: true)]
public record struct ClientLeavesPacket : IPacket
{
    public const int MaxLength = 1 << 24;

    public int requestId;
    public bool missed;
    public byte[] gzippedLines;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref requestId);
        buf.Bind(ref missed);
        buf.BindBytes(ref gzippedLines, maxLength: MaxLength);
    }
}

/// <summary>What differs between the reference game's and a player's entities of one lane.</summary>
[PacketDefinition(Packets.Server_LeafDiff, allowFragmented: true)]
public record struct ServerLeafDiffPacket : IPacket
{
    public int playerId;
    public string username;
    public int tick;
    public int mapId;
    public string lane;
    public byte[] gzippedText;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref playerId);
        buf.Bind(ref username);
        buf.Bind(ref tick);
        buf.Bind(ref mapId);
        buf.Bind(ref lane, maxLength: 64);
        buf.BindBytes(ref gzippedText, maxLength: 1 << 22);
    }
}
