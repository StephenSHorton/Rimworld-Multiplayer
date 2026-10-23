using System.Collections.Generic;

namespace Multiplayer.Common;

/// <summary>
/// A player's connection that went quiet. 2026-10-22: Blake's game stopped getting the timer with no Steam error and no
/// disconnect, the host kept playing, and the host only saw Blake's frame time stuck at its last value. A playing
/// game answers the server's keepalive five times a second, so no packet for <see cref="StaleMs"/> means the
/// connection (or the game) is stuck: the server pauses as it does for a player too far behind and tells everyone,
/// and after <see cref="DropMs"/> drops the player, who can rejoin. Join points are left alone for a while, since
/// every game reloads then and a slow machine can be silent for that long.
/// </summary>
public class ConnectionWatch
{
    public const long StaleMs = 5_000;
    public const long DropMs = 180_000;
    public const long JoinPointGraceMs = 60_000;

    public enum Change { None, Lost, Back, Drop }

    private readonly HashSet<int> stale = new();
    private readonly HashSet<int> announced = new();

    public bool IsStale(int playerId) => stale.Contains(playerId);

    public bool AnyStale => stale.Count > 0;

    /// <summary>Once a second for every playing game but the host's.</summary>
    /// <param name="ageMs">Time since the last packet from that game</param>
    /// <param name="joinPointGrace">A join point is being made or ended less than <see cref="JoinPointGraceMs"/> ago</param>
    public Change Check(int playerId, long ageMs, bool joinPointGrace)
    {
        if (ageMs <= StaleMs)
        {
            stale.Remove(playerId);
            return announced.Remove(playerId) ? Change.Back : Change.None;
        }

        // Paused either way; said and dropped only outside a join point's reloads
        stale.Add(playerId);
        if (joinPointGrace) return Change.None;
        if (ageMs > DropMs) return Change.Drop;
        return announced.Add(playerId) ? Change.Lost : Change.None;
    }

    public void Forget(int playerId)
    {
        stale.Remove(playerId);
        announced.Remove(playerId);
    }

    /// <summary>Forgets every player not in <paramref name="present"/> (left, or no longer playing).</summary>
    public void Keep(ICollection<int> present)
    {
        stale.RemoveWhere(id => !present.Contains(id));
        announced.RemoveWhere(id => !present.Contains(id));
    }
}
