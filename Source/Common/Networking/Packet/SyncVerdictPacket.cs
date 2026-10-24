namespace Multiplayer.Common.Networking.Packet;

/// <summary>
/// The server compared a player's sync opinion with the host's and they differ. Sent to the host and to that
/// player, so both see it whichever game diverged.
/// </summary>
[PacketDefinition(Packets.Server_SyncVerdict)]
public record struct ServerSyncVerdictPacket : IPacket
{
    public int playerId;
    public string username;
    public int tick;
    public bool hard;
    public string lane;
    public int mapId;
    public string message;
    /// <summary>
    /// After a join point: every join point save section whose hash differs (the message names only a few), for
    /// each game to write them all to its joinsave dump.
    /// </summary>
    public string[] saveSections;

    /// <summary>What else the comparison found besides the hard difference: other random states, lanes, errors.</summary>
    public string[] extra;

    /// <summary>
    /// When <see cref="ledgerUntil"/> is past <see cref="ledgerFrom"/>: the steering ledger records on both games it
    /// goes to from timer tick <see cref="ledgerFrom"/> until <see cref="ledgerUntil"/> (a soft pawns or things
    /// difference). A game that is already past the start leaves it off, so the two never record different spans.
    /// </summary>
    public int ledgerFrom;
    public int ledgerUntil;

    public const int MaxSaveSections = 400;
    public const int MaxExtra = 32;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref playerId);
        buf.Bind(ref username);
        buf.Bind(ref tick);
        buf.Bind(ref hard);
        buf.Bind(ref lane);
        buf.Bind(ref mapId);
        buf.Bind(ref message, maxLength: 4096);
        saveSections ??= [];
        buf.Bind(ref saveSections, BinderOf.String(), maxLength: MaxSaveSections);
        extra ??= [];
        buf.Bind(ref extra, BinderOf.String(), maxLength: MaxExtra);
        buf.Bind(ref ledgerFrom);
        buf.Bind(ref ledgerUntil);
    }
}
