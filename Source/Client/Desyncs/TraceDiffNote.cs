using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// What the first differing desync trace was. Traces are Rand calls and also ids taken (UniqueIDsManager),
    /// spawns and despawns; when the first difference is one of those, no Rand call differs there: one game took an
    /// id or spawned a thing the other didn't (or in another order), and the Rand calls the other game made from that
    /// point show what it was doing instead.
    /// </summary>
    public static class TraceDiffNote
    {
        private const int ShownEntries = 8;
        private const int ShownFrames = 8;

        // "123 Tick:456 Hash:789 '[Rand] ...'" as GetFormattedStackTracesForRange writes each entry
        private static readonly Regex EntryHeader = new(@"^(\d+) Tick:(-?\d+) Hash:(-?\d+) '(?:\[(\w+)\] )?(.*)'\s*$", RegexOptions.Compiled);

        public class Entry
        {
            public int index;
            public string kind;
            public string header;
            public readonly List<string> frames = new();
        }

        /// <summary>The entries of a formatted trace range (the host's half of a report), each once.</summary>
        public static List<Entry> Parse(string formatted)
        {
            var result = new List<Entry>();
            if (string.IsNullOrEmpty(formatted)) return result;
            var seen = new HashSet<int>();
            Entry cur = null;
            foreach (var raw in formatted.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var m = EntryHeader.Match(line);
                if (m.Success)
                {
                    int index = int.Parse(m.Groups[1].Value);
                    cur = seen.Add(index) ? new Entry { index = index, kind = m.Groups[4].Success ? m.Groups[4].Value : null, header = line } : null;
                    if (cur != null) result.Add(cur);
                    continue;
                }
                // The host's text puts section headers between entries; they end the entry before them
                if (line.StartsWith("Context traces:") || line.StartsWith("Trace count:") || line.StartsWith("Trace of first"))
                {
                    cur = null;
                    continue;
                }
                if (cur != null && line.Length > 0 && cur.frames.Count < ShownFrames) cur.frames.Add(line);
            }
            return result.OrderBy(e => e.index).ToList();
        }

        /// <summary>
        /// A note for the report when the first differing trace is not a Rand call on either side, with the other
        /// game's Rand entries from that point; empty otherwise.
        /// </summary>
        public static string For(ClientSyncOpinion local, int diffAt, string otherTraces, string otherName)
        {
            if (diffAt < 0) return "";
            var localItem = local.desyncStackTraces.ElementAtOrDefault(diffAt) as StackTraceLogItemRaw;
            var other = Parse(otherTraces);
            var otherAt = other.FirstOrDefault(e => e.index == diffAt);

            var localKind = localItem?.kind;
            var otherKind = otherAt?.kind;
            bool localNotRand = localKind != null && localKind != DeferredStackTracing.TraceKind.Rand;
            bool otherNotRand = otherKind != null && otherKind != DeferredStackTracing.TraceKind.Rand;
            if (!localNotRand && !otherNotRand) return "";

            var sb = new StringBuilder();
            sb.Append($"Note: the first differing trace (#{diffAt}) is not a Rand call: ");
            sb.Append($"a {localKind ?? "missing"} trace on this game, {otherKind ?? (otherAt == null ? "not in the range sent" : "unmarked")} on {otherName}'s. ");
            sb.AppendLine("One game took an id, spawned or despawned something the other didn't at that point (or in another order); " +
                          "no Rand call differs there yet.");

            var following = other.Where(e => e.index >= diffAt && e.kind == DeferredStackTracing.TraceKind.Rand).Take(ShownEntries).ToList();
            if (following.Count == 0)
            {
                sb.AppendLine(otherTraces == null
                    ? $"{otherName}'s traces didn't arrive."
                    : $"{otherName}'s traces from #{diffAt} hold no Rand call (or carry no kinds: a build from before the trace kinds).");
            }
            else
            {
                sb.AppendLine($"{otherName}'s Rand calls from #{diffAt} on:");
                foreach (var e in following)
                {
                    sb.AppendLine(e.header);
                    foreach (var f in e.frames) sb.AppendLine("    " + f);
                }
            }
            sb.AppendLine();
            return sb.ToString();
        }
    }
}
