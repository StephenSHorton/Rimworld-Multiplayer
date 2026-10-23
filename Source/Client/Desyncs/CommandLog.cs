using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Multiplayer.Common;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// The commands this game ran, with the timer it ran them at, for a desync whose time lane or command count
    /// differs: a map clock that moved differently is a time speed command one game ran and the other didn't, and a
    /// command count that differs names which. The snapshot's lists say what the server sent; this says what ran.
    /// </summary>
    public static class CommandLog
    {
        private const int Kept = 1024;

        private readonly struct Entry(int timer, ScheduledCommand cmd)
        {
            public readonly int timer = timer;
            public readonly ScheduledCommand cmd = cmd;
        }

        private static readonly Entry[] entries = new Entry[Kept];
        private static int next;
        private static int count;

        /// <summary>From the tick loop, as a command is taken from its queue.</summary>
        public static void Record(ScheduledCommand cmd)
        {
            entries[next] = new Entry(TickPatch.Timer, cmd);
            next = (next + 1) % Kept;
            if (count < Kept) count++;
        }

        private static IEnumerable<Entry> Recent()
        {
            for (int i = count; i > 0; i--)
                yield return entries[(next - i + Kept) % Kept];
        }

        public static int SyncId(ScheduledCommand cmd) =>
            cmd.type == CommandType.Sync && cmd.data is { Length: >= 4 } ? BitConverter.ToInt32(cmd.data, 0) : -1;

        public static string Describe(ScheduledCommand cmd)
        {
            var id = SyncId(cmd);
            var handler = "";
            if (id >= 0)
            {
                string name;
                try
                {
                    name = id < Sync.handlers.Count ? Sync.handlers[id].ToString() : "unknown";
                }
                catch (Exception e)
                {
                    name = $"unnamed ({e.GetType().Name})";
                }
                handler = $" handler {id} {name}";
            }
            return $"tick {cmd.ticks} {cmd.type} map {cmd.mapId} player {cmd.playerId} faction {cmd.factionId}{handler} ({cmd.data?.Length ?? 0} bytes)";
        }

        /// <summary>The commands this game ran from <paramref name="fromTimer"/> on, oldest first.</summary>
        public static List<string> RanSince(int fromTimer) =>
            Recent().Where(e => e.cmd != null && e.cmd.ticks >= fromTimer)
                .Select(e => $"ran at timer {e.timer}: {Describe(e.cmd)}").ToList();

        /// <summary>The snapshot's commands (what the server sent this game) for the timer ticks given, in tick order.</summary>
        public static List<string> InSnapshot(int fromTick, int toTick)
        {
            var lists = Multiplayer.session?.dataSnapshot?.MapCmds;
            if (lists == null) return [];
            return lists.Values.SelectMany(l => l).Distinct()
                .Where(c => c.ticks >= fromTick && c.ticks <= toTick)
                .OrderBy(c => c.ticks)
                .Select(c => $"in snapshot: {Describe(c)}").ToList();
        }

        /// <summary>
        /// For a desync report or the log: what this game ran since <paramref name="since"/>, and the snapshot's
        /// commands for the window from <paramref name="windowStart"/> next to the ones it ran in that window.
        /// </summary>
        public static string Report(int windowStart, int since, int max = 80)
        {
            var sb = new StringBuilder();
            var ran = RanSince(since);
            sb.AppendLine($"Commands this game ran since timer tick {since} ({ran.Count}, now {TickPatch.Timer}):");
            foreach (var line in ran.Skip(Math.Max(0, ran.Count - max)))
                sb.AppendLine("  " + line);

            var windowEnd = windowStart + 29;
            sb.AppendLine($"Window {windowStart}..{windowEnd}, the server's commands and the ones this game ran:");
            foreach (var line in InSnapshot(windowStart, windowEnd).Take(max))
                sb.AppendLine("  " + line);
            foreach (var line in Recent().Where(e => e.cmd != null && e.cmd.ticks >= windowStart && e.cmd.ticks <= windowEnd)
                         .Select(e => $"ran at timer {e.timer}: {Describe(e.cmd)}").Take(max))
                sb.AppendLine("  " + line);
            return sb.ToString();
        }
    }
}
