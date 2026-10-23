using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Common;

/// <summary>
/// Which command queue every command of a snapshot goes in after a load. A loaded game rebuilds all its queues from
/// the snapshot's lists in one place (Loader.PostLoad): before, each map copied its list while it loaded and the world
/// queue was replaced by the global list after, so a command that arrived while the game loaded reached only the
/// snapshot (the map had copied its list already) or only the world queue (which PostLoad then replaced), and never
/// ran (2026-10-22, Desync-01/02: a joiner's map lost a designation, two time speed changes and 15 commands).
/// </summary>
public static class CommandQueuePlan
{
    public class Result
    {
        public readonly List<ScheduledCommand> world = [];
        public readonly Dictionary<int, List<ScheduledCommand>> maps = new();
        public int total;
        public int queued;
        // Before the loaded timer: already in the save (a snapshot's lists keep every command since its join point)
        public int beforeTimer;
        // At the loaded timer exactly: queued, since a join point's save is made before the rest of its tick's commands
        public int atTimer;
        // For a map this game didn't load
        public int unloadedMap;
        public int duplicates;

        public string Describe()
        {
            var parts = new List<string> { $"world {world.Count}" };
            parts.AddRange(maps.OrderBy(kv => kv.Key).Select(kv => $"map {kv.Key}: {kv.Value.Count}"));
            return $"{queued} of {total} queued ({string.Join(", ", parts)}); {atTimer} at the loaded tick, " +
                   $"{beforeTimer} before it left out, {unloadedMap} for maps not loaded, {duplicates} duplicates";
        }
    }

    /// <param name="snapshot">The snapshot's lists by map id (<see cref="ScheduledCommand.Global"/> for the world)</param>
    /// <param name="loadedMaps">The loaded maps in the order the tick loop runs them</param>
    /// <param name="timer">The loaded timer: commands before it are in the save already</param>
    /// <param name="singleQueue">Every command in the world queue (consistent command order), else each in its map's</param>
    public static Result Build(IReadOnlyDictionary<int, List<ScheduledCommand>> snapshot, IList<int> loadedMaps, int timer, bool singleQueue)
    {
        var result = new Result();
        foreach (var map in loadedMaps)
            result.maps[map] = [];

        var seen = new HashSet<ScheduledCommand>(ReferenceEqualityComparer.Instance);

        // The world's list first, then the maps in tick loop order: the order equal ticks ran in when each list had
        // its own queue. Which of two lists' commands at the same tick came first isn't in the snapshot.
        var keys = new List<int>();
        if (snapshot.ContainsKey(ScheduledCommand.Global)) keys.Add(ScheduledCommand.Global);
        keys.AddRange(loadedMaps.Where(snapshot.ContainsKey));
        keys.AddRange(snapshot.Keys.Where(k => !keys.Contains(k)).OrderBy(k => k));

        var world = new List<ScheduledCommand>();
        var maps = loadedMaps.ToDictionary(m => m, _ => new List<ScheduledCommand>());

        foreach (var key in keys)
        {
            foreach (var cmd in snapshot[key])
            {
                result.total++;
                if (!seen.Add(cmd))
                {
                    result.duplicates++;
                    continue;
                }

                if (cmd.ticks < timer)
                {
                    result.beforeTimer++;
                    continue;
                }

                bool global = cmd.mapId == ScheduledCommand.Global;
                if (!global && !maps.ContainsKey(cmd.mapId))
                {
                    result.unloadedMap++;
                    continue;
                }

                if (cmd.ticks == timer) result.atTimer++;
                result.queued++;
                if (global || singleQueue) world.Add(cmd);
                else maps[cmd.mapId].Add(cmd);
            }
        }

        // Stable: equal ticks keep the order above
        result.world.AddRange(world.OrderBy(c => c.ticks));
        foreach (var (map, cmds) in maps)
            result.maps[map].AddRange(cmds.OrderBy(c => c.ticks));

        return result;
    }

    /// <summary>
    /// The snapshot's commands for a tick still ahead (after <paramref name="timer"/>) that no queue holds, for a
    /// loaded map or the world. Each is a command this game will never run.
    /// </summary>
    public static List<ScheduledCommand> Missing(IReadOnlyDictionary<int, List<ScheduledCommand>> snapshot,
        IEnumerable<IEnumerable<ScheduledCommand>> queues, ICollection<int> loadedMaps, int timer)
    {
        var queued = new HashSet<ScheduledCommand>(queues.SelectMany(q => q), ReferenceEqualityComparer.Instance);
        return snapshot.Values.SelectMany(l => l)
            .Where(c => c.ticks > timer && (c.mapId == ScheduledCommand.Global || loadedMaps.Contains(c.mapId)) && !queued.Contains(c))
            .Distinct<ScheduledCommand>(ReferenceEqualityComparer.Instance)
            .ToList();
    }
}
