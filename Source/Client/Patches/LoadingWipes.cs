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
    /// Vanilla's SpawningWipes wipes filth under anything that isn't standable. On load, filth often spawns
    /// before the chunk (PassThroughOnly) or furniture over it: buildings spawn after every other thing, vanilla
    /// spawns compressed things such as chunks after the full ones, and Multiplayer's saves keep things in list
    /// order. So filth that lay under a chunk or a table in the live game vanished at every load: in single player at every reload, and in
    /// multiplayer at every join point, where the self-check found the filth gone on every game. While a map
    /// finalizes loading nothing wipes filth; every game loads the same way, so this stays deterministic, and single
    /// player keeps the filth it saved.
    /// </summary>
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.SpawningWipes))]
    static class KeepFilthWhileLoading
    {
        static void Postfix(ref bool __result, BuildableDef oldEntDef)
        {
            if (__result && MapFinalizeLoading.Running && oldEntDef is ThingDef { category: ThingCategory.Filth })
                __result = false;
        }
    }
}
