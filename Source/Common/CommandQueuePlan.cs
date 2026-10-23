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
        // For a map this game didn't load, one the host made after the save (a caravan's or a quest's map) that the
        // catch-up makes again: queued in the world queue, which runs a command on the map it names by its tick
        public int unloadedMap;
        public int duplicates;

        public string Describe()
        {
            var parts = new List<string> { $"world {world.Count}" };
            parts.AddRange(maps.OrderBy(kv => kv.Key).Select(kv => $"map {kv.Key}: {kv.Value.Count}"));
            return $"{queued} of {total} queued ({string.Join(", ", parts)}); {atTimer} at the loaded tick, " +
                   $"{beforeTimer} before it left out, {unloadedMap} for maps not made yet (in the world queue), {duplicates} duplicates";
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

        // Equal ticks run in the order the commands arrived (ScheduledCommand.seq), across the lists: the world's list
        // first and then the maps' put a tick's map commands after its world commands, which ran the host's
        // designator after a time speed change it came before (2026-10-23, every join under a command flood desynced
        // on "Random state from commands"). Commands with no seq (replays from before protocol 64) keep this order.
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
                // Maps no longer take their list when they are made, so a map the catch-up makes gets its commands
                // from the world queue (MultiplayerSession.ScheduleCommand does the same in play)
                bool notMade = !global && !maps.ContainsKey(cmd.mapId);
                if (notMade) result.unloadedMap++;

                if (cmd.ticks == timer) result.atTimer++;
                result.queued++;
                if (global || singleQueue || notMade) world.Add(cmd);
                else maps[cmd.mapId].Add(cmd);
            }
        }

        // Stable: equal ticks and seqs keep the order above
        result.world.AddRange(world.OrderBy(c => c.ticks).ThenBy(c => c.seq));
        foreach (var (map, cmds) in maps)
            result.maps[map].AddRange(cmds.OrderBy(c => c.ticks).ThenBy(c => c.seq));

        return result;
    }

    /// <summary>
    /// The snapshot's commands for a tick still ahead (after <paramref name="timer"/>) that no queue holds, for any
    /// map (one not made yet has its commands in the world queue) or the world. Each is a command this game will
    /// never run.
    /// </summary>
    public static List<ScheduledCommand> Missing(IReadOnlyDictionary<int, List<ScheduledCommand>> snapshot,
        IEnumerable<IEnumerable<ScheduledCommand>> queues, int timer)
    {
        var queued = new HashSet<ScheduledCommand>(queues.SelectMany(q => q), ReferenceEqualityComparer.Instance);
        return snapshot.Values.SelectMany(l => l)
            .Where(c => c.ticks > timer && !queued.Contains(c))
            .Distinct<ScheduledCommand>(ReferenceEqualityComparer.Instance)
            .ToList();
    }
}
