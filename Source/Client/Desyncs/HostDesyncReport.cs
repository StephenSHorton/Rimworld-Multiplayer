using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Multiplayer.Common.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// The host's own desync report. Before, only the game that desynced wrote one, so the host's side of a desync
    /// was gone by the time anyone looked. On a hard verdict about another player the host keeps its Rand
    /// attribution window, waits up to 10 s for the referee's Rand attribution diff and writes
    /// MpDesyncs/Desync-host-[player]-[tick].zip (newest 20 kept).
    /// </summary>
    public static class HostDesyncReport
    {
        private const float MaxWaitSeconds = 10f;
        private const int Kept = 20;

        private class Pending
        {
            public ServerSyncVerdictPacket verdict;
            public Stopwatch since;
        }

        private static readonly List<Pending> pending = new();

        public static void OnHardVerdict(ServerSyncVerdictPacket verdict)
        {
            RandAttribution.Freeze(verdict.tick);
            if (pending.Any(p => p.verdict.playerId == verdict.playerId && p.verdict.tick == verdict.tick)) return;
            pending.Add(new Pending { verdict = verdict, since = Stopwatch.StartNew() });
        }

        /// <summary>Every frame.</summary>
        public static void Update()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (RandAttrReports.ForWindow(p.verdict.tick) == null && p.since.Elapsed.TotalSeconds < MaxWaitSeconds) continue;
                pending.RemoveAt(i);
                Write(p.verdict);
            }
        }

        private static void Write(ServerSyncVerdictPacket verdict)
        {
            try
            {
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                var name = $"Desync-host-{verdict.username}-{verdict.tick}.zip";
                var path = Path.Combine(Multiplayer.DesyncsDir, name);
                if (File.Exists(path)) File.Delete(path);

                using (var zip = MpZipFile.Open(path, ZipArchiveMode.Create))
                {
                    var info = new StringBuilder();
                    info.AppendLine($"Host report: {verdict.username}'s game differs from the host's at timer tick {verdict.tick}");
                    info.AppendLine($"Verdict|||{verdict.message}");
                    info.AppendLine($"Lane|||{verdict.lane}, map {verdict.mapId}");
                    foreach (var e in verdict.extra ?? [])
                        info.AppendLine($"Also|||{e}");
                    info.AppendLine($"Multiplayer Mod Version|||{MpVersion.Version}");
                    info.AppendLine($"Rimworld Version and Rev|||{VersionControl.CurrentVersionStringWithRev}");
                    zip.AddEntry("desync_info", info.ToString());

                    var sync = Multiplayer.game?.sync;
                    zip.AddEntry("local_flight_recorder.txt", sync?.recorder.Dump() ?? "No game");
                    RandAttrReports.AddToZip(zip, verdict.tick, Multiplayer.username);
                    zip.AddEntry("entity_diffs.txt", LeafRequests.recentDiffs.Count > 0 ? string.Join("\n\n", LeafRequests.recentDiffs) : "No entity diffs");

                    var local = sync?.knownClientOpinions.FirstOrDefault(o => o.isLocalClientsOpinion && o.startTick == verdict.tick);
                    zip.AddEntry("local_traces.txt", local != null ? local.GetFormattedStackTracesForRange(0) : "This window's opinion is no longer kept");

                    var logs = LogGenerator.PrepareLogData();
                    if (logs != null) zip.AddEntry("local_logs.txt", logs);
                    zip.AddEntry("local_metadata.txt", MetadataGenerator.Generate());
                }

                foreach (var old in new DirectoryInfo(Multiplayer.DesyncsDir).GetFiles("Desync-host-*.zip").OrderByDescending(f => f.LastWriteTime).Skip(Kept))
                    old.Delete();

                var diff = RandAttrReports.ForWindow(verdict.tick);
                MpLog.Warn($"Host desync report written: {path}" + (diff != null ? $"; [RandAttr] {diff.packet.summary}" : "; no Rand attribution diff arrived"));
            }
            catch (Exception e)
            {
                MpLog.Error($"Couldn't write the host's desync report: {e}");
            }
        }
    }
}
