namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Server_Command)]
public record struct ServerCommandPacket : IPacket
{
    public CommandType type;
    public int ticks;
    public int factionId;
    public int mapId;
    public int playerId;
    public byte[] data;

    public static ServerCommandPacket From(ScheduledCommand cmd) => new()
    {
        type = cmd.type,
        ticks = cmd.ticks,
        factionId = cmd.factionId,
        mapId = cmd.mapId,
        playerId = cmd.playerId,
        data = cmd.data
    };

    public ScheduledCommand ToCommand() => new(type: type, ticks: ticks, factionId: factionId, mapId: mapId,
        playerId: playerId, data: data);

    public void Bind(PacketBuffer buf)
    {
        buf.BindEnum(ref type);
        buf.Bind(ref ticks);
        buf.Bind(ref factionId);
        buf.Bind(ref mapId);
        buf.Bind(ref playerId);
        buf.BindRemaining(ref data, maxLength: 65535);
    }
}

[PacketDefinition(Packets.Client_Command)]
public record struct ClientCommandPacket(CommandType type, int mapId, byte[] data) : IPacket
{
    public CommandType type = type;
    public int mapId = mapId;
    // Host-led clock: the tick the host already scheduled this command at in its own game; 0 = the server stamps it
    public int hostTicks;
    public byte[] data = data;

    public void Bind(PacketBuffer buf)
    {
        buf.BindEnum(ref type);
        buf.Bind(ref mapId);
        buf.Bind(ref hostTicks);
        buf.BindRemaining(ref data, maxLength: 65535);
    }
}

/// <summary>Host-led clock: the hosting game's timer, sent through the same ordered channel as its commands.</summary>
[PacketDefinition(Packets.Client_HostTimer)]
public record struct ClientHostTimerPacket(int timer) : IPacket
{
    public int timer = timer;

    public void Bind(PacketBuffer buf) => buf.Bind(ref timer);
}
