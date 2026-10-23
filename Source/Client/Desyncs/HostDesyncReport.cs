using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
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
            public string commands;
        }

        private static readonly List<Pending> pending = new();

        public static void OnHardVerdict(ServerSyncVerdictPacket verdict)
        {
            RandAttribution.Freeze(verdict.tick);
            if (pending.Any(p => p.verdict.playerId == verdict.playerId && p.verdict.tick == verdict.tick)) return;

            // The host's side of the commands each game ran, next to the player's own list (its log and
            // local_commands.txt), so the one only one game ran is named. Taken now: the log is a ring that keeps
            // rolling while the report waits for the Rand attribution diff. The host doesn't know the player's last
            // valid tick, so from 300 ticks before the window.
            var commands = SaveableDesyncInfo.CommandReport(verdict.tick, -1);
            if (FromCommands(verdict))
                MpLog.Log($"Host's commands for {verdict.username}'s desync at tick {verdict.tick}:\n{commands}");

            pending.Add(new Pending { verdict = verdict, since = Stopwatch.StartNew(), commands = commands });
        }

        /// <summary>A clock or a command count that differs: both come from the commands one game ran.</summary>
        private static bool FromCommands(ServerSyncVerdictPacket verdict) =>
            verdict.lane == "time" ||
            (verdict.message?.Contains(OpinionComparer.CommandsDiffText) ?? false) ||
            (verdict.extra ?? []).Any(e => e?.Contains(OpinionComparer.CommandsDiffText) ?? false);

        /// <summary>Every frame.</summary>
        public static void Update()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (RandAttrReports.ForWindow(p.verdict.tick) == null && p.since.Elapsed.TotalSeconds < MaxWaitSeconds) continue;
                pending.RemoveAt(i);
                Write(p.verdict, p.commands);
            }
        }

        private static void Write(ServerSyncVerdictPacket verdict, string commands)
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

                    // Each entry on its own: one that fails must not cost the rest
                    void Entry(string entry, Func<string> text)
                    {
                        string content;
                        try
                        {
                            content = text();
                        }
                        catch (Exception e)
                        {
                            content = $"Couldn't write {entry}: {e}";
                        }
                        if (content != null) zip.AddEntry(entry, content);
                    }

                    var sync = Multiplayer.game?.sync;
                    Entry("local_flight_recorder.txt", () => sync?.recorder.Dump() ?? "No game");
                    Entry("local_commands.txt", () => commands);
                    RandAttrReports.AddToZip(zip, verdict.tick, Multiplayer.username);
                    Entry("entity_diffs.txt", () => LeafRequests.recentDiffs.Count > 0 ? string.Join("\n\n", LeafRequests.recentDiffs) : "No entity diffs");
                    Entry("local_traces.txt", () =>
                    {
                        var local = sync?.knownClientOpinions.FirstOrDefault(o => o.isLocalClientsOpinion && o.startTick == verdict.tick);
                        return local != null ? local.GetFormattedStackTracesForRange(0) : "This window's opinion is no longer kept";
                    });
                    Entry("local_logs.txt", LogGenerator.PrepareLogData);
                    Entry("local_metadata.txt", MetadataGenerator.Generate);
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
