namespace Multiplayer.Common.Networking.Packet;

/// <summary>
/// The referee asks a game for its Rand attribution table of one scope and window (entries from..to). Answered at
/// once from what the game kept; nothing is scheduled, so there is no tick to miss.
/// </summary>
[PacketDefinition(Packets.Server_RandAttrRequest)]
public record struct ServerRandAttrRequestPacket : IPacket
{
    public int requestId;
    public int scope;
    public int opinionStart;
    public short entryFrom;
    public short entryTo;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref requestId);
        buf.Bind(ref scope);
        buf.Bind(ref opinionStart);
        buf.Bind(ref entryFrom);
        buf.Bind(ref entryTo);
    }
}

[PacketDefinition(Packets.Client_RandAttr, allowFragmented: true)]
public record struct ClientRandAttrPacket : IPacket
{
    public const int MaxLength = 1 << 22;

    public int requestId;
    public bool missed;
    public byte[] gzTable;
    public byte[] gzStacks;
    public int offThreadCalls;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref requestId);
        buf.Bind(ref missed);
        gzTable ??= [];
        gzStacks ??= [];
        buf.BindBytes(ref gzTable, maxLength: MaxLength);
        buf.BindBytes(ref gzStacks, maxLength: MaxLength);
        buf.Bind(ref offThreadCalls);
    }
}

/// <summary>
/// What differs between the reference game's and a player's Rand attribution tables, sent to both. The watch fields
/// name the record to capture stacks for from now on (by names, which every build resolves the same way).
/// </summary>
[PacketDefinition(Packets.Server_RandAttrDiff, allowFragmented: true)]
public record struct ServerRandAttrDiffPacket : IPacket
{
    public int playerId;
    public string username;
    public int scope;
    public int opinionStart;
    public short entry;
    public int ticksGame;
    /// <summary>The summary is cut to this many UTF-8 bytes (bound at 1024).</summary>
    public const int MaxSummaryBytes = 1000;

    public string summary;
    public byte[] gzDiff;
    public byte[] gzRefTable;
    public byte[] gzOtherTable;
    public byte[] gzStackDiff;

    // The watch to arm; an empty kind arms none
    public string watchKind;
    public string watchDef;
    public string watchMethod;
    public string watchBase;
    public string watchPhase;
    public string watchJob;
    public string watchOwner;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref playerId);
        buf.Bind(ref username);
        buf.Bind(ref scope);
        buf.Bind(ref opinionStart);
        buf.Bind(ref entry);
        buf.Bind(ref ticksGame);
        buf.Bind(ref summary, maxLength: 1024);
        gzDiff ??= [];
        gzRefTable ??= [];
        gzOtherTable ??= [];
        gzStackDiff ??= [];
        buf.BindBytes(ref gzDiff, maxLength: ClientRandAttrPacket.MaxLength);
        buf.BindBytes(ref gzRefTable, maxLength: ClientRandAttrPacket.MaxLength);
        buf.BindBytes(ref gzOtherTable, maxLength: ClientRandAttrPacket.MaxLength);
        buf.BindBytes(ref gzStackDiff, maxLength: ClientRandAttrPacket.MaxLength);
        watchKind ??= "";
        watchDef ??= "";
        watchMethod ??= "";
        watchBase ??= "";
        watchPhase ??= "";
        watchJob ??= "";
        watchOwner ??= "";
        buf.Bind(ref watchKind, maxLength: 64);
        buf.Bind(ref watchDef, maxLength: 256);
        buf.Bind(ref watchMethod, maxLength: 64);
        buf.Bind(ref watchBase, maxLength: 256);
        buf.Bind(ref watchPhase, maxLength: 256);
        buf.Bind(ref watchJob, maxLength: 256);
        buf.Bind(ref watchOwner, maxLength: 256);
    }
}
