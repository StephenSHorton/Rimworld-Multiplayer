using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// Whether a map is spawning its loaded things (<see cref="Map.FinalizeLoading"/>). Everything spawned then was
    /// together on the map when it was saved, so a spawn must not wipe what's already there; vanilla's load order
    /// (every non-building first, then buildings from smallest; compressed things after the full ones) decides
    /// which of two coexisting things spawns second and wipes the first.
    /// </summary>
    public static class MapFinalizeLoading
    {
        private static int depth;

        public static bool Running => depth > 0;

        [HarmonyPatch(typeof(Map), nameof(Map.FinalizeLoading))]
        static class Flag
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix() => depth++;

            [HarmonyPriority(Priority.Last)]
            static void Finalizer() => depth--;
        }
    }

    /// <summary>
    /// Vanilla's SpawningWipes decides which existing thing a spawn destroys (or, for SpawnBuildingAsPossible, whether
    /// the new building is refunded instead). On load everything spawns in vanilla's order, not the order things came
    /// together in play: buildings after every other thing, compressed things such as chunks and rocks after the full
    /// ones, and Multiplayer's saves keep things in list order. So filth that lay under a chunk or a table vanished at
    /// every load, and on 2026-10-22 a Replace Stuff wall frame sharing a cell with MineableSteel destroyed the rock at
    /// a reload (in either spawn order, one of the two wipes the other). Everything a map spawns while it finalizes
    /// loading was together when it was saved, so nothing wipes anything then. Every game loads the same way, so this
    /// stays deterministic; play keeps vanilla's behaviour, and single player keeps what it saved. Only the wipe paths
    /// (<see cref="InWipe"/>) see no wipes: a placement query made while a map loads (GenSpawn.CanSpawnAt, GenConstruct,
    /// a mod's MapComponent.FinalizeInit) gets vanilla's answer.
    /// </summary>
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.SpawningWipes))]
    static class NoWipesWhileLoading
    {
        [HarmonyPriority(Priority.Last)]
        static void Postfix(ref bool __result)
        {
            if (__result && InWipe.depth > 0 && MapFinalizeLoading.Running)
                __result = false;
        }
    }

    /// <summary>The spawn paths that wipe or refund what SpawningWipes names.</summary>
    [HarmonyPatch]
    static class InWipe
    {
        [System.ThreadStatic]
        public static int depth;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.WipeExistingThings));
            yield return AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.WipeAndRefundExistingThings));
            yield return AccessTools.Method(typeof(GenSpawn), nameof(GenSpawn.SpawnBuildingAsPossible));
        }

        [HarmonyPriority(Priority.First)]
        static void Prefix() => depth++;

        [HarmonyPriority(Priority.Last)]
        static void Finalizer() => depth--;
    }

    /// <summary>
    /// A pawn spawning while its map loads faced again at once (Pawn_RotationTracker.Notify_Spawned runs
    /// UpdateRotation), from a job, stance and path that are only half restored then, so the join point self-check
    /// found pawns facing another way after every reload. A pawn respawning after the load keeps the rotation it was
    /// saved with; the next tick faces it as usual. A pawn a mod makes and spawns fresh while the map loads is faced
    /// as vanilla does. Every game loads the same way.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.Notify_Spawned))]
    static class KeepSavedRotationWhileLoading
    {
        static bool Prefix() => !(MapFinalizeLoading.Running && PawnRespawningAfterLoad.value);
    }

    /// <summary>Whether the innermost Pawn.SpawnSetup running is a respawn after a load.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    static class PawnRespawningAfterLoad
    {
        [System.ThreadStatic]
        public static bool value;

        [HarmonyPriority(Priority.First)]
        static void Prefix(bool respawningAfterLoad, out bool __state)
        {
            __state = value;
            value = respawningAfterLoad;
        }

        [HarmonyPriority(Priority.Last)]
        static void Finalizer(bool __state) => value = __state;
    }
}
