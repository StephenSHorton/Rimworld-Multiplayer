using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Which code took each job and thing id, so a game that takes one more than the others can be named.
    ///
    /// In the 2026-10-18 session the host's and the joiner's job ids drifted apart (the same job numbered 1, then 12
    /// apart) with random states still equal: some code takes a real id on one game only. Every real job and thing id
    /// is recorded with a short stack. An id taken outside the simulation is logged at once. The "ids" world lane
    /// compares the counters, and the first time it differs every game writes its ledger to
    /// MpDesyncs/ids-&lt;player&gt;-&lt;tick&gt;.txt; diffing two of those shows the extra one.
    /// </summary>
    public static class IdLedger
    {
        private const int Kept = 4096;
        private const int Frames = 8;

        private readonly struct Entry(char kind, int id, int tick, bool sim, StackTrace stack)
        {
            public readonly char kind = kind;
            public readonly int id = id;
            public readonly int tick = tick;
            public readonly bool sim = sim;
            public readonly StackTrace stack = stack;
        }

        private static readonly Entry[] entries = new Entry[Kept];
        private static int next;
        private static int count;
        private static int outsideLogged;
        private static bool dumped;

        public static ulong LaneHash()
        {
            var ids = Find.UniqueIDsManager;
            return ids == null ? 0 : SyncLanes.Mix(SyncLanes.Mix((ulong)(uint)ids.nextJobID, ids.nextThingID), ids.nextHediffID);
        }

        private static bool InSimulation =>
            Multiplayer.Ticking || Multiplayer.ExecutingCmds || Multiplayer.reloading || Current.ProgramState != ProgramState.Playing;

        private static void Record(char kind, int id)
        {
            if (Multiplayer.Client == null || id < 0) return;

            var sim = InSimulation;
            var stack = new StackTrace(2, false);
            entries[next] = new Entry(kind, id, Find.TickManager?.TicksGame ?? -1, sim, stack);
            next = (next + 1) % Kept;
            if (count < Kept) count++;

            // A real id outside ticking, commands and loading is taken on this game only. Rimkit's Nice Bill Tab
            // makes a throwaway mech for its stats after loading and puts every id counter back afterwards.
            if (!sim && !RollsBack(stack) && outsideLogged++ < 20)
                MpLog.Error($"Took real {(kind == 'J' ? "job" : "thing")} id {id} outside the simulation " +
                            $"(long event: {LongEventHandler.currentEvent?.eventText ?? "none"}); the games' ids will drift apart:\n{Describe(stack, 16)}");
        }

        private static bool RollsBack(StackTrace stack) =>
            stack.GetFrames().Any(f => f.GetMethod()?.DeclaringType?.Name == "StatMechanoidWorker");

        internal static string Describe(StackTrace stack, int frames) =>
            string.Join(" < ", stack.GetFrames().Select(f => f.GetMethod()).Where(m => m != null && m.DeclaringType?.Namespace != "HarmonyLib")
                .Take(frames).Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));

        /// <summary>Called when a game first hears that the ids lane differs.</summary>
        public static void OnLaneDiffers(string message)
        {
            if (dumped || !message.Contains("/ ids lane")) return;
            dumped = true;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{Multiplayer.username}: the last {count} real ids, oldest first. Next job id {Find.UniqueIDsManager.nextJobID}, thing id {Find.UniqueIDsManager.nextThingID}.");
                for (int i = 0; i < count; i++)
                {
                    var e = entries[(next - count + i + Kept) % Kept];
                    sb.AppendLine($"{e.kind}{e.id} tick={e.tick}{(e.sim ? "" : " OUTSIDE-SIM")} {Describe(e.stack, Frames)}");
                }

                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                var path = Path.Combine(Multiplayer.DesyncsDir, $"ids-{Multiplayer.username}-{Find.TickManager.TicksGame}.txt");
                File.WriteAllText(path, sb.ToString());
                MpLog.Warn($"The id counters differ between games; this game's recent ids are in {path}");
            }
            catch (System.Exception e)
            {
                MpLog.Error($"Couldn't write the id ledger: {e}");
            }
        }

        [HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextJobID))]
        static class JobIdPatch
        {
            static void Postfix(int __result) => Record('J', __result);
        }

        [HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextThingID))]
        static class ThingIdPatch
        {
            static void Postfix(int __result) => Record('T', __result);
        }
    }
}
