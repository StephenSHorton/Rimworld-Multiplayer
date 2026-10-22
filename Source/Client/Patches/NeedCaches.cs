using System.Collections.Generic;
using HarmonyLib;
using RimWorld;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// Beauty and mood cache their instant level for the current tick, and the needs card's change arrow reads it
    /// (Need_Seeker.GUIChangeArrow). With async time the interface can see a tick number its map hasn't simulated
    /// yet, so the player looking at the card filled the cache with a value from before that tick, and their
    /// simulation then used it while the other player's computed it fresh: their beauty and mood needs drifted
    /// apart (found by the pawns lane, Quaunt Amalgamation). Outside the simulation the interface still gets a fresh
    /// value, but the cache is put back as it was.
    /// </summary>
    static class NeedCachesOutsideSimulation
    {
        public static bool InInterface => Multiplayer.OutsideSimulation;
    }

    [HarmonyPatch(typeof(Need_Beauty), nameof(Need_Beauty.CurInstantLevel), MethodType.Getter)]
    static class NeedBeautyCacheInInterface
    {
        static void Prefix(Need_Beauty __instance, out (bool restore, int tick, float value) __state) =>
            __state = (NeedCachesOutsideSimulation.InInterface, __instance.lastInstantBeautyCheckTick, __instance.lastInstantBeauty);

        static void Postfix(Need_Beauty __instance, (bool restore, int tick, float value) __state)
        {
            if (!__state.restore) return;
            __instance.lastInstantBeautyCheckTick = __state.tick;
            __instance.lastInstantBeauty = __state.value;
        }
    }

    [HarmonyPatch(typeof(Need_Mood), nameof(Need_Mood.CurInstantLevel), MethodType.Getter)]
    static class NeedMoodCacheInInterface
    {
        static void Prefix(Need_Mood __instance, out (bool restore, int tick, float value) __state) =>
            __state = (NeedCachesOutsideSimulation.InInterface, __instance.lastInstantMoodCheckTick, __instance.lastInstantMood);

        static void Postfix(Need_Mood __instance, (bool restore, int tick, float value) __state)
        {
            if (!__state.restore) return;
            __instance.lastInstantMoodCheckTick = __state.tick;
            __instance.lastInstantMood = __state.value;
        }
    }

    /// <summary>
    /// Situational mood thoughts are rebuilt by whoever reads them first while they're dirty (after every load),
    /// and reading one def's thoughts recalculates and even creates them. Thought alerts (Alert_Thought) and the
    /// needs card read them from the interface, on each player's own frame schedule, so the games' mood thoughts
    /// changed at different ticks and their moods drifted a step apart (found by the pawns lane). Outside the
    /// simulation these reads use the cached thoughts as they are.
    /// </summary>
    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.AppendMoodThoughts), typeof(List<Thought>))]
    static class SituationalMoodThoughtsInInterface
    {
        static bool Prefix(SituationalThoughtHandler __instance, List<Thought> outThoughts)
        {
            if (!NeedCachesOutsideSimulation.InInterface) return true;

            foreach (var thought in __instance.cachedThoughts)
                if (thought.Active)
                    outThoughts.Add(thought);
            return false;
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), nameof(SituationalThoughtHandler.AppendMoodThoughts), typeof(ThoughtDef), typeof(List<Thought>))]
    static class SituationalMoodThoughtsOfDefInInterface
    {
        static bool Prefix(SituationalThoughtHandler __instance, ThoughtDef def, List<Thought> thoughts)
        {
            if (!NeedCachesOutsideSimulation.InInterface) return true;

            foreach (var thought in __instance.cachedThoughts)
                if (thought.def == def && thought.Active)
                    thoughts.Add(thought);
            return false;
        }
    }
}
