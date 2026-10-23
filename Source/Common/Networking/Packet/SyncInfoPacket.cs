using System.Collections.Generic;
using Multiplayer.Common.Desyncs;

namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_SyncInfo, allowFragmented: true)]
public record struct ClientSyncInfoPacket : IPacket
{
    // 8MiB. For some big saves, the sync opinion can reach ~2.5MiB, so this includes some leeway.
    public const int MaxLength = 1 << 23;
    public byte[] rawSyncOpinion;

    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

[PacketDefinition(Packets.Server_SyncInfo, allowFragmented: true)]
public record struct ServerSyncInfoPacket : IPacket
{
    public byte[] rawSyncOpinion;
    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: ClientSyncInfoPacket.MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

public record struct SyncOpinion : IPacketBufferable
{
    public int startTick;
    // Full Verse.Rand states (seed in the low 32 bits, call count in the high 32), not just the call count.
    public List<ulong> commandRandomStates;
    public List<ulong> worldRandomStates;
    public List<MapRandomState> mapRandomStates;
    public List<int> traceHashes;
    public bool simulating;
    public RoundModeEnum roundMode;
    // Errors logged while simulating, in order. One side only means one game ran a code path the other didn't.
    public List<FaultEvent> faults;
    // State checksums: each names a part of the game (a map's terrain, the world's factions, ...).
    public List<LaneHash> lanes;
    // Each scope's Rand attribution hash for this window (see RandAttrHash); empty with attribution off
    public List<RandAttrHash> randAttr;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref startTick);

        buf.Bind(ref commandRandomStates, BinderOf.ULong());
        buf.Bind(ref worldRandomStates, BinderOf.ULong());
        buf.Bind(ref mapRandomStates, BinderOf.Identity<MapRandomState>());
        // Max 2M trace hashes. For some big saves, the trace hashes can reach ~600k entries, so this includes
        // some leeway.
        buf.Bind(ref traceHashes, BinderOf.Int(), maxLength: 1<<21);

        buf.Bind(ref simulating);
        buf.BindEnum(ref roundMode);

        // Older code builds opinions without these; send them as empty
        faults ??= [];
        lanes ??= [];
        buf.Bind(ref faults, BinderOf.Identity<FaultEvent>());
        buf.Bind(ref lanes, BinderOf.Identity<LaneHash>());
        randAttr ??= [];
        buf.Bind(ref randAttr, BinderOf.Identity<RandAttrHash>());
    }
}

public record struct MapRandomState : IPacketBufferable
{
    public int mapId;
    public List<ulong> randomStates;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapId);
        buf.Bind(ref randomStates, BinderOf.ULong());
    }
}

/// <summary>An error logged during simulation. The signature hashes the message with numbers and names taken out.</summary>
public record struct FaultEvent : IPacketBufferable
{
    public int tick;
    public int mapId;
    public int signature;
    public string text;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref tick);
        buf.Bind(ref mapId);
        buf.Bind(ref signature);
        buf.Bind(ref text, maxLength: 512);
    }
}

/// <summary>
/// One state checksum. <see cref="lane"/> names what it covers ("terrain", "pawns", ...), <see cref="mapId"/> is
/// -1 for the world. A HARD lane that differs is a desync; a SOFT one is reported but play continues.
/// </summary>
public record struct LaneHash : IPacketBufferable
{
    public int tick;
    public int mapId;
    public string lane;
    public ulong hash;
    public bool hard;
    // What the hash was made of, for lanes that are a handful of values (the time lanes): both games' values go in
    // the verdict when the lane differs. Empty for the others.
    public string detail;

    public const int MaxDetailBytes = 256;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref tick);
        buf.Bind(ref mapId);
        buf.Bind(ref lane, maxLength: 64);
        buf.Bind(ref hash);
        buf.Bind(ref hard);
        detail ??= "";
        buf.Bind(ref detail, maxLength: MaxDetailBytes);
    }
}
