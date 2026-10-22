using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Multiplayer.Client.Util;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Util;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// The referee's Rand attribution diffs as this game receives them (the player's and the host's game both get
    /// each one): logged as "[RandAttr] summary", written to MpDesyncs/randdiff-[window]-[scope].txt, kept for the next
    /// desync report, and used to arm the stack watch.
    /// </summary>
    public static class RandAttrReports
    {
        public class Diff
        {
            public ServerRandAttrDiffPacket packet;
            public string diff, refTable, otherTable, stackDiff;
            public DateTime receivedUtc;

            public string Title => $"{RandAttrScope.Name(packet.scope)} window {packet.opinionStart}, host vs {packet.username}";

            public string FullText()
            {
                var sb = new StringBuilder();
                sb.AppendLine(packet.summary);
                sb.AppendLine();
                sb.AppendLine(diff);
                sb.AppendLine();
                sb.AppendLine("== Targeted stacks");
                sb.AppendLine(stackDiff.NullOrEmpty() ? "No targeted stacks" : stackDiff);
                return sb.ToString();
            }
        }

        private static readonly List<Diff> recent = new();

        public static Diff Latest => recent.LastOrDefault();

        public static Diff ForWindow(int opinionStart) => recent.LastOrDefault(d => d.packet.opinionStart == opinionStart);

        public static void OnDiff(ServerRandAttrDiffPacket packet)
        {
            var d = new Diff
            {
                packet = packet,
                diff = Gunzip(packet.gzDiff),
                refTable = Gunzip(packet.gzRefTable),
                otherTable = Gunzip(packet.gzOtherTable),
                stackDiff = Gunzip(packet.gzStackDiff),
                receivedUtc = DateTime.UtcNow
            };
            recent.Add(d);
            if (recent.Count > 10) recent.RemoveAt(0);

            MpLog.Warn("[RandAttr] " + packet.summary);

            try
            {
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                var sb = new StringBuilder(d.FullText());
                sb.AppendLine();
                sb.AppendLine("== host's table");
                sb.AppendLine(d.refTable);
                sb.AppendLine($"== {packet.username}'s table");
                sb.AppendLine(d.otherTable);
                File.WriteAllText(Path.Combine(Multiplayer.DesyncsDir, $"randdiff-{packet.opinionStart}-{packet.scope}.txt"), sb.ToString());
            }
            catch (IOException e)
            {
                MpLog.Warn($"Rand attribution: couldn't write the diff: {e.Message}");
            }
            LeafRequests.PruneOldFiles("randdiff-*.txt", 30);

            // Arm (or count against) the watch: a diff that names a call came with stacks
            bool withStacks = packet.summary != null && (packet.summary.Contains("first extra call") || packet.summary.Contains(" differs: "));
            RandWatch.ArmFrom(packet, withStacks);
            MpLog.Log($"Rand attribution: watch {RandWatch.Describe()}, tier 2 {(RandAttribution.tier2 ? "on" : "off")}");

            Messages.Message($"Rand attribution: {packet.summary}", MessageTypeDefOf.SilentInput, false);
        }

        private static string Gunzip(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            try
            {
                return LeafDiff.Gunzip(data);
            }
            catch (Exception e)
            {
                return $"(unreadable: {e.Message})";
            }
        }

        /// <summary>The zip entries every desync report takes from Rand attribution.</summary>
        public static void AddToZip(System.IO.Compression.ZipArchive zip, int opinionStart, string localName)
        {
            var d = ForWindow(opinionStart) ?? Latest;
            zip.AddEntry("rand_diff.txt", d != null
                ? (d.packet.opinionStart != opinionStart ? $"Note: the newest diff is from window {d.packet.opinionStart}, not {opinionStart}\n\n" : "") + d.FullText()
                : "No Rand attribution diff arrived (the referee asks both games for their tables when a random state differs)");

            bool haveLocal = RandAttribution.Table(opinionStart, null, 0, int.MaxValue, out var localTable, out var localStacks);
            zip.AddEntry("local_rand_table.txt", haveLocal
                ? $"Rand attribution table of {localName}'s game, window {opinionStart}, every scope\n\n{localTable}"
                : $"No Rand attribution records of window {opinionStart} on this game{(RandAttribution.disabled ? " (attribution is off)" : "")}");
            zip.AddEntry("remote_rand_table.txt", d != null ? $"From the referee's diff ({d.Title}): the host's table, then {d.packet.username}'s\n\n== host\n{d.refTable}\n== {d.packet.username}\n{d.otherTable}" : "No remote table");
            zip.AddEntry("targeted_traces.txt",
                (d != null && !d.stackDiff.NullOrEmpty() ? d.stackDiff + "\n\n" : "") +
                (localStacks.Length > 0 ? "== this game's captures for this window\n" + localStacks
                    : RandAttribution.watchArmed ? $"Watch armed ({RandWatch.Describe()}) but no captures for this window" : "No watch armed"));
            zip.AddEntry("rand_attribution_stats.txt", RandAttribution.StatsReport());
        }
    }
}
