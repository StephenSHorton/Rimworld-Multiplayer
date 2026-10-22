using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Patches;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Pawns whose job vanilla drops while a join point reloads. A pawn saved while moving resumes its path from
    /// Pawn.SpawnSetup (Pawn_PathFollower.TryResumePathingAfterLoading); when the destination was destroyed or
    /// StartPath's CanReach fails on the half loaded map, PatherFailed stops the pawn and ends its job, the pawn
    /// starts a new one (Wait, a new job id) and drops what it carried. Every game reloads the join point the same
    /// way, so the self-check treats those pawns' job, path and carry changes and the dropped thing as reload
    /// effects, and lists them in a WARN section ("jobs vanilla dropped on reload"): it is still a job the live game
    /// had and the save lost.
    /// </summary>
    public static class ReloadDroppedJobs
    {
        public class Entry
        {
            public string pawn;
            public string job;
            public string dest;
            public string carried;
            public int map;
            public string pos;
        }

        private static bool armed;
        private static readonly Dictionary<string, Entry> byPawn = new();
        private static readonly HashSet<string> droppedThings = new();

        // The pawn leaf fields a dropped job changes (SyncLanes' pawns lane)
        private static readonly HashSet<string> JobFields = ["job", "moving", "next", "path", "dest", "tA", "tB", "carry"];

        public static IReadOnlyCollection<Entry> Entries => byPawn.Values;

        /// <summary>Before a join point's save: forget the last join point's pawns and record during its reload.</summary>
        public static void Arm()
        {
            armed = Multiplayer.Client != null;
            byPawn.Clear();
            droppedThings.Clear();
        }

        /// <summary>After the self-check used them.</summary>
        public static void Disarm() => armed = false;

        /// <summary>A pawn leaf field this pawn's dropped job explains.</summary>
        public static bool Explains(string thingId, string field) => byPawn.ContainsKey(thingId) && JobFields.Contains(field);

        /// <summary>A thing a pawn whose job was dropped carried, now on the ground.</summary>
        public static bool IsDroppedThing(string thingId) => droppedThings.Contains(thingId);

        public static List<string> Report() =>
            byPawn.Values.Select(e =>
                $"{e.pawn} map={e.map} pos={e.pos} job={e.job} dest={e.dest} carried={e.carried ?? "none"}").ToList();

        private static string Fmt(LocalTargetInfo t) =>
            t.HasThing ? t.Thing.ThingID : t.IsValid ? $"({t.Cell.x},{t.Cell.z})" : "none";

        [HarmonyPatch(typeof(Pawn_PathFollower), "PatherFailed")]
        static class PatherFailedPatch
        {
            static void Prefix(Pawn_PathFollower __instance)
            {
                if (!armed || !Multiplayer.reloading || !MapFinalizeLoading.Running) return;
                var pawn = __instance.pawn;
                if (pawn == null || byPawn.ContainsKey(pawn.ThingID)) return;

                var job = pawn.jobs?.curJob;
                var carried = pawn.carryTracker?.CarriedThing;
                byPawn[pawn.ThingID] = new Entry
                {
                    pawn = pawn.ThingID,
                    job = job == null ? "none" : $"{job.def?.defName}#{job.loadID}",
                    dest = Fmt(__instance.Destination),
                    carried = carried?.ThingID,
                    map = pawn.MapHeld?.uniqueID ?? -1,
                    pos = pawn.positionInt.ToString()
                };
                if (carried != null) droppedThings.Add(carried.ThingID);
            }
        }
    }
}
