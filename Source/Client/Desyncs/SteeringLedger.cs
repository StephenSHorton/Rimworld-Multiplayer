using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using UnityEngine;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Every pawn rotation change and path request with a short stack, so that when two games' pawns face or walk
    /// different ways the code that turned or sent one of them can be named. A file
    /// <c>MpSteeringLedger.flag</c> in the save data folder turns it on (dev mode alone no longer does: a stack per
    /// rotation change and path request cost Blake's game several ms a tick); off, each patch returns at its first line.
    /// The patches are applied on every game either way, so the Harmony patches row of the join fingerprint stays
    /// the same. A soft pawns or things difference turns it on for <see cref="SyncReferee.LedgerWindow"/>
    /// timer ticks, from the timer tick the referee's verdict names, on both games it goes to. When the server's leaf diff for a map's pawns or things lane arrives (both games get it), each game
    /// writes the entries of the pawns the diff names to MpDesyncs/steering-[player]-[tick]-[lane].txt.
    /// </summary>
    public static class SteeringLedger
    {
        private const int Kept = 8192;
        private const int Frames = 8;

        private readonly struct Entry(char kind, int tick, int timer, ThingDef def, int pawnId, int a, int b, IntVec3 from, Thing destThing, IntVec3 destCell, StackTrace stack, string face = null)
        {
            public readonly string face = face;
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

        // Stacks only with the flag file: an automatic window is on during combat, when rotation changes and path
        // requests come fastest, and a managed stack for each cost Blake 15 ms a tick on 2026-10-22. The window still
        // records each change's tick, values and face inputs, which name the pawn and the field that split.
        private static bool withStacks;

        public static bool FlagFile => File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath, "MpSteeringLedger.flag"));

        // Read again at every checkpoint (once a second of play): removing the file turns it off without a restart.
        // It was read once per process, and with a stack per rotation change it can cost several ms a tick.
        public static void Refresh()
        {
            withStacks = Multiplayer.Client != null && FlagFile;
            Enabled = withStacks || Multiplayer.Client != null && window.On;
        }

        // The window a soft pawns or things difference turned it on for (the referee's verdict carries it), in timer
        // ticks; it switches at the same timer tick on every game the verdict reached in time
        private static readonly LedgerWindow window = new();

        /// <summary>The timer tick the window ends at, or -1 when none is armed or on.</summary>
        public static int AutoUntil => window.Until;

        /// <summary>
        /// A load (join, join point, resync) or the end of the session: the window counts in the old timer. It was
        /// kept, so a window open when a session ended stayed on in the next one, whose timer starts near 0.
        /// </summary>
        public static void ResetWindow()
        {
            int from = window.From, until = window.Until;
            if (window.Reset())
                MpLog.Log($"Steering ledger: window from timer tick {from} to {until} dropped by a load or the end of the session");
            Refresh();
        }

        /// <summary>A verdict's window: armed only while this game is still before its start.</summary>
        public static void Arm(int from, int until, int timer)
        {
            switch (window.Arm(from, until, timer))
            {
                case LedgerWindow.ArmResult.TooLate:
                    MpLog.Warn($"Steering ledger: the window from timer tick {from} to {until} arrived at {timer}, too late to start in step with the other game; left off");
                    break;
                case LedgerWindow.ArmResult.Extended:
                    MpLog.Log($"Steering ledger: window now ends at timer tick {window.Until}");
                    break;
                case LedgerWindow.ArmResult.Armed:
                    MpLog.Log($"Steering ledger: on from timer tick {from} to {until} for a soft pawns or things difference");
                    break;
            }
        }

        /// <summary>Called every timer tick, at the same point of the tick on every game.</summary>
        public static void Tick(int timer)
        {
            if (!window.Armed) return;
            var until = window.Until;
            if (!window.Tick(timer)) return;
            Refresh();
            if (window.On)
                MpLog.Log($"Steering ledger: on at timer tick {timer} until {until}");
            else
                MpLog.Log($"Steering ledger: window ended at timer tick {timer}{(Enabled ? " (still on: MpSteeringLedger.flag)" : "")}");
        }

        // What Pawn_RotationTracker.Face was given while it sets a rotation: the point, the pawn's own DrawPos and
        // the target FaceTarget or a busy stance passed. The ledger named the turn that split on 2026-10-22 and
        // 2026-10-23 (Face < FaceTarget < UpdateRotation from ProcessPostTickVisuals, 1->3 on the host and 1->0 on
        // the client) but not which of its inputs differed; these say which one does.
        private static int faceDepth;
        private static Vector3 faceP;
        private static Vector3 faceSelf;
        private static LocalTargetInfo faceTarget = LocalTargetInfo.Invalid;

        private static string FaceInputs(Pawn pawn)
        {
            if (faceDepth == 0) return null;
            var target = faceTarget;
            if (!target.IsValid && pawn.stances?.curStance is Stance_Busy busy && busy.focusTarg.IsValid)
                target = busy.focusTarg;
            var t = !target.IsValid ? "none" :
                target.HasThing ? $"{target.Thing.ThingID}@({target.Thing.Position.x},{target.Thing.Position.z}) drawPos={V(target.Thing.DrawPos)}" :
                $"({target.Cell.x},{target.Cell.z})";
            return $"face p={V(faceP)} self={V(faceSelf)} angle={(faceP - faceSelf).AngleFlat():F4} target={t}";
        }

        private static string V(Vector3 v) => $"({v.x:F4},{v.z:F4})";

        private static void Record(char kind, Pawn pawn, int a, int b, IntVec3 from, LocalTargetInfo dest)
        {
            entries[next] = new Entry(kind, Find.TickManager?.TicksGame ?? -1, TickPatch.Timer, pawn.def, pawn.thingIDNumber, a, b, from,
                dest.HasThing ? dest.Thing : null, dest.HasThing ? IntVec3.Invalid : dest.Cell, withStacks ? new StackTrace(2, false) : null,
                kind == 'R' ? FaceInputs(pawn) : null);
            next = (next + 1) % Kept;
            if (count < Kept) count++;
        }

        private static string Describe(in Entry e)
        {
            if (e.kind == 'R')
                return $"rot {e.a}->{e.b}" + (e.face != null ? " " + e.face : "");
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
                MpLog.Log($"Steering ledger off on this game, no file for the map {mapId} / {lane} leaf diff at timer tick {tick} (a soft pawns or things difference turns it on for {SyncReferee.LedgerWindow} timer ticks; MpSteeringLedger.flag in the save data folder keeps it on)");
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
                    sb.AppendLine($"{e.kind} tick={e.tick} timer={e.timer} {id} {Describe(e)} {(e.stack != null ? IdLedger.Describe(e.stack, Frames) : "(no stack: automatic window)")}");
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

        [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.Face))]
        static class FacePatch
        {
            static void Prefix(Pawn_RotationTracker __instance, Vector3 p)
            {
                if (!Enabled) return;
                faceDepth++;
                faceP = p;
                faceSelf = __instance.pawn.DrawPos;
            }

            static void Finalizer()
            {
                if (faceDepth > 0) faceDepth--;
            }
        }

        [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.FaceTarget))]
        static class FaceTargetPatch
        {
            static void Prefix(LocalTargetInfo target)
            {
                if (Enabled) faceTarget = target;
            }

            static void Finalizer() => faceTarget = LocalTargetInfo.Invalid;
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
