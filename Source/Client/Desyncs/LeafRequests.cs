using System.Collections.Generic;
using System.IO;
using System.Linq;
using Multiplayer.Client.Util;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// The server asks for a lane's entity list at an upcoming timer tick when that lane differs between two games
    /// (see SyncReferee); both games answer at the same tick and the server sends back what differs, which ends up
    /// in the log, in MpDesyncs and in the next desync report.
    /// </summary>
    public static class LeafRequests
    {
        private static readonly List<ServerLeafRequestPacket> pending = new();
        public static readonly List<string> recentDiffs = new();

        public static void Add(ServerLeafRequestPacket request)
        {
            if (TickPatch.Timer > request.tick)
            {
                Send(request.requestId, null);
                return;
            }

            pending.Add(request);
        }

        /// <summary>Called every timer tick, at the same point of the tick on every game.</summary>
        public static void Tick(int timer)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var request = pending[i];
                if (request.tick > timer) continue;
                pending.RemoveAt(i);

                if (request.tick < timer)
                {
                    Send(request.requestId, null);
                    continue;
                }

                if (request.lane == "save")
                {
                    SaveStateForDebug(request.tick);
                    continue;
                }

                var map = Find.Maps.FirstOrDefault(m => m.uniqueID == request.mapId);
                var lines = map != null ? SyncLanes.Leaves(map, request.lane) : new List<string>();
                Send(request.requestId, string.Join("\n", lines));
            }
        }

        /// <summary>Debug freeze: this game's full state at the tick every game is held at.</summary>
        private static void SaveStateForDebug(int tick)
        {
            try
            {
                var data = ScribeUtil.WriteExposable(Current.Game, "game", true);
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                var path = Path.Combine(Multiplayer.DesyncsDir, $"state-{Multiplayer.username}-{tick}.xml.gz");
                File.WriteAllBytes(path, LeafDiff.Gzip(System.Text.Encoding.UTF8.GetString(data)));
                MpLog.Warn($"Debug freeze: the game is held at timer tick {tick}; its state is in {path}");
                Messages.Message($"Debug freeze: two games differ. Every game is held at tick {tick} and saved its state to MpDesyncs.",
                    MessageTypeDefOf.NegativeEvent, false);
            }
            catch (System.Exception e)
            {
                MpLog.Error($"Debug freeze: couldn't save the game state: {e}");
            }
        }

        private static void Send(int requestId, string text)
        {
            var packet = new ClientLeavesPacket
            {
                requestId = requestId,
                missed = text == null,
                gzippedLines = LeafDiff.Gzip(text ?? "")
            };
            Multiplayer.Client?.SendLowPriority(packet.Serialize());
        }

        internal static void PruneOldFiles(string pattern, int keep)
        {
            try
            {
                foreach (var old in new DirectoryInfo(Multiplayer.DesyncsDir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTime).Skip(keep))
                    old.Delete();
            }
            catch (IOException)
            {
            }
        }

        public static void OnDiff(ServerLeafDiffPacket packet)
        {
            var text = LeafDiff.Gunzip(packet.gzippedText);
            var where = packet.mapId == -1 ? "world" : $"map {packet.mapId}";
            var title = $"{where} / {packet.lane} at timer tick {packet.tick}, host vs {packet.username}";

            recentDiffs.Add(title + "\n" + text);
            if (recentDiffs.Count > 10) recentDiffs.RemoveAt(0);

            var firstLines = text.Split('\n').Take(4).ToList();
            MpLog.Warn($"Sync difference found: {title}: {string.Join(" | ", firstLines)}");

            try
            {
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                File.WriteAllText(Path.Combine(Multiplayer.DesyncsDir, $"leafdiff-{packet.tick}-{packet.mapId}-{packet.lane}.txt"), title + "\n" + text);
            }
            catch (IOException)
            {
            }

            PruneOldFiles("leafdiff-*.txt", 60);
            SteeringLedger.OnLeafDiff(packet.lane, packet.mapId, packet.tick, text);

            var example = firstLines.Skip(1).FirstOrDefault(l => l.Length > 0) ?? firstLines.FirstOrDefault();
            Messages.Message($"Sync difference ({where} / {packet.lane}): {example}", MessageTypeDefOf.SilentInput, false);
        }
    }
}
