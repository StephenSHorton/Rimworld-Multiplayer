using System.Collections.Generic;

namespace Multiplayer.Common.Desyncs;

/// <summary>
/// What each game kept of its last few lane checkpoints per map, keyed by map and timer tick, so a leaf request for
/// a checkpoint that already passed is answered with the entities as they were then. The referee asked for the
/// leaves 30 ticks after it heard of a difference, and a soft difference in combat had healed by then ("No entity
/// differs", 2026-10-23, twice). Adding a checkpoint past <see cref="Depth"/> for a map hands back the oldest
/// value, for the caller to reuse.
/// </summary>
public class LeafRing<T> where T : class
{
    public readonly int Depth;

    private readonly Dictionary<int, List<(int tick, T value)>> byMap = new();

    public LeafRing(int depth)
    {
        Depth = System.Math.Max(1, depth);
    }

    /// <summary>Keeps <paramref name="value"/> for that map and tick; returns the value it replaced or evicted, or null.</summary>
    public T? Add(int mapId, int tick, T value)
    {
        if (!byMap.TryGetValue(mapId, out var list))
            byMap[mapId] = list = new List<(int, T)>(Depth);

        for (int i = 0; i < list.Count; i++)
            if (list[i].tick == tick)
            {
                var old = list[i].value;
                list[i] = (tick, value);
                return ReferenceEquals(old, value) ? null : old;
            }

        T? evicted = null;
        if (list.Count >= Depth)
        {
            evicted = list[0].value;
            list.RemoveAt(0);
        }
        list.Add((tick, value));
        return evicted;
    }

    public bool TryGet(int mapId, int tick, out T value)
    {
        value = null!;
        if (!byMap.TryGetValue(mapId, out var list)) return false;
        foreach (var entry in list)
            if (entry.tick == tick)
            {
                value = entry.value;
                return true;
            }
        return false;
    }

    /// <summary>The ticks kept for a map, oldest first.</summary>
    public IEnumerable<int> Ticks(int mapId)
    {
        if (!byMap.TryGetValue(mapId, out var list)) yield break;
        foreach (var entry in list)
            yield return entry.tick;
    }

    /// <summary>A new game (load, resync): what the old one kept is another game's state.</summary>
    public void Clear() => byMap.Clear();
}
