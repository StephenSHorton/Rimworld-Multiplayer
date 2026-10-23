namespace Multiplayer.Common.Networking.Packet;

/// <summary>
/// A player's game found a desync itself (against the host's opinions it was given, often while catching up after
/// a join, when the server's referee may have nothing to pair). It carries the verdict, so the host logs it, writes
/// its report and the referee asks both games for the entities and Rand tables that differ, as for its own verdicts.
/// </summary>
[PacketDefinition(Packets.Client_Desynced)]
public record struct ClientDesyncedPacket(int tick, int diffAt) : IPacket
{
    public const int MaxMessageBytes = 4000;
    public const int MaxExtra = 16;
    public const int NoRng = int.MinValue;

    public int tick = tick;
    public int diffAt = diffAt;
    public string message = "";
    // The hard difference's lane ("rng" for a random state, "time", ...) and map (-1 for the world)
    public string lane = "";
    public int mapId = -1;
    // The first random state list that differs (a map id, RandAttrScope.World or .Commands) and its entry
    public int rngScope = NoRng;
    public int rngEntry = -1;
    // Every lane that differs, as "mapId|lane"
    public string[] laneDiffs = [];
    public string[] extra = [];

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref tick);
        buf.Bind(ref diffAt);
        message ??= "";
        buf.Bind(ref message, maxLength: 4096);
        lane ??= "";
        buf.Bind(ref lane, maxLength: 64);
        buf.Bind(ref mapId);
        buf.Bind(ref rngScope);
        buf.Bind(ref rngEntry);
        laneDiffs ??= [];
        buf.Bind(ref laneDiffs, BinderOf.String(), maxLength: 64);
        extra ??= [];
        buf.Bind(ref extra, BinderOf.String(), maxLength: MaxExtra);
    }
}
