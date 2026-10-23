using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Multiplayer.Client.Util;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Every pawn rotation change and path request with a short stack, so that when two games' pawns face or walk
    /// different ways the code that turned or sent one of them can be named. Dev mode (or a file
    /// <c>MpSteeringLedger.flag</c> in the save data folder) turns it on; off, each patch returns at its first line.
    /// The patches are applied on every game either way, so the Harmony patches row of the join fingerprint stays
    /// the same. When the server's leaf diff for a map's pawns or things lane arrives (both games get it), each game
    /// writes the entries of the pawns the diff names to MpDesyncs/steering-[player]-[tick]-[lane].txt.
    /// </summary>
    public static class SteeringLedger
    {
        private const int Kept = 8192;
        private const int Frames = 8;

        private readonly struct Entry(char kind, int tick, int timer, ThingDef def, int pawnId, int a, int b, IntVec3 from, Thing destThing, IntVec3 destCell, StackTrace stack)
        {
            public readonly char kind = kind;
            public readonly int tick = tick;
            public readonly int timer = timer;
            public readonly ThingDef def = def;
            public readonly int pawnId = pawnId;
            public readonly int a = a;
            public readonly int b = b;
            public readonly IntVec3 from = from;
            public readonly Thing destThing = destThing;
            public readonly IntVec3 destCell = destCell;
            public readonly StackTrace stack = stack;

            public string PawnId => $"{def?.defName}{pawnId}";
        }

        private static readonly Entry[] entries = new Entry[Kept];
        private static int next;
        private static int count;

        /// <summary>Set at every lane checkpoint; the patches read nothing else when it's off.</summary>
        public static bool Enabled { get; private set; }

        public static bool FlagFile => File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath, "MpSteeringLedger.flag"));

        // Read again at every checkpoint (once a second of play): removing the file turns it off without a restart.
        // It was read once per process, and with a stack per rotation change it can cost several ms a tick.
        public static void Refresh()
        {
            Enabled = Multiplayer.Client != null && (Prefs.DevMode || FlagFile);
        }

        private static void Record(char kind, Pawn pawn, int a, int b, IntVec3 from, LocalTargetInfo dest)
        {
            entries[next] = new Entry(kind, Find.TickManager?.TicksGame ?? -1, TickPatch.Timer, pawn.def, pawn.thingIDNumber, a, b, from,
                dest.HasThing ? dest.Thing : null, dest.HasThing ? IntVec3.Invalid : dest.Cell, new StackTrace(2, false));
            next = (next + 1) % Kept;
            if (count < Kept) count++;
        }

        private static string Describe(in Entry e)
        {
            if (e.kind == 'R')
                return $"rot {e.a}->{e.b}";
            var dest = e.destThing != null ? $"{e.destThing.ThingID}@({e.destThing.Position.x},{e.destThing.Position.z})" :
                e.destCell.IsValid ? $"({e.destCell.x},{e.destCell.z})" : "none";
            return $"path from ({e.from.x},{e.from.z}) to {dest} peMode={(PathEndMode)e.a}";
        }

        /// <summary>
        /// The leaf diff of a pawns or things lane: writes this game's entries for the pawns it names. The diff's lines
        /// start with an entity id ("Human538 rot: 1 (host) vs 2 (Blake)", "Human538: only in host's game (...)").
        /// </summary>
        public static void OnLeafDiff(string lane, int mapId, int tick, string diff)
        {
            if (lane != "pawns" && lane != "things") return;
            if (!Enabled)
            {
                // The other game may write its file; say why this one has none
                MpLog.Log($"Steering ledger off on this game, no file for the map {mapId} / {lane} leaf diff at timer tick {tick} (enable dev mode or MpSteeringLedger.flag)");
                return;
            }

            try
            {
                var ids = new HashSet<string>();
                foreach (var raw in diff.Split('\n').Skip(1))
                {
                    var line = raw.TrimEnd('\r');
                    var space = line.IndexOf(' ');
                    if (space <= 0) continue;
                    ids.Add(line.Substring(0, space).TrimEnd(':'));
                }
                // "No entity differs" or "Could not compare": nothing to name
                if (ids.Count == 0) return;

                var sb = new StringBuilder();
                sb.AppendLine($"{Multiplayer.username}: rotation changes (R) and path requests (P) of the pawns the map {mapId} / {lane} leaf diff at timer tick {tick} names, " +
                              $"oldest first, from the last {count} of every pawn. Pawns: {string.Join(", ", ids.OrderBy(i => i, System.StringComparer.Ordinal))}");
                int written = 0;
                for (int i = 0; i < count; i++)
                {
                    var e = entries[(next - count + i + Kept) % Kept];
                    var id = e.PawnId;
                    if (!ids.Contains(id)) continue;
                    written++;
                    sb.AppendLine($"{e.kind} tick={e.tick} timer={e.timer} {id} {Describe(e)} {IdLedger.Describe(e.stack, Frames)}");
                }
                if (written == 0)
                    sb.AppendLine("(none recorded for these pawns)");

                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                var path = Path.Combine(Multiplayer.DesyncsDir, $"steering-{Multiplayer.username}-{tick}-{lane}.txt");
                File.WriteAllText(path, sb.ToString());
                LeafRequests.PruneOldFiles("steering-*.txt", 30);
                MpLog.Log($"Steering ledger: {written} entries for {ids.Count} entities written to {path}");
            }
            catch (System.Exception e)
            {
                MpLog.Error($"Couldn't write the steering ledger: {e}");
            }
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.Rotation), MethodType.Setter)]
        static class RotationPatch
        {
            static void Prefix(Thing __instance, Rot4 value)
            {
                if (!Enabled) return;
                if (__instance is Pawn pawn && value != pawn.rotationInt)
                    Record('R', pawn, pawn.rotationInt.AsInt, value.AsInt, pawn.positionInt, LocalTargetInfo.Invalid);
            }
        }

        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
        static class StartPathPatch
        {
            static void Prefix(Pawn_PathFollower __instance, LocalTargetInfo dest, PathEndMode peMode)
            {
                if (!Enabled) return;
                var pawn = __instance.pawn;
                if (pawn != null)
                    Record('P', pawn, (int)peMode, 0, pawn.positionInt, dest);
            }
        }
    }
}
