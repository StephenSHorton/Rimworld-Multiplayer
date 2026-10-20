using HarmonyLib;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// A pawn's life stage index is worked out on first read after every load, and that first computation counts as
    /// a life stage change: it sets lifeStageChange (which makes the next age tick run health state checks), dirties
    /// the graphics, can rename an animal and adds or removes dynamic comps. When the interface read it first (the
    /// world pawns behind a letter, a tooltip), that game alone did all of this, at a moment of its own; the join point
    /// saves of 2026-10-19 differed in lifeStageChange on world animals. Outside the simulation the index is computed
    /// fresh and not cached, so the first simulation read does the rest on every game alike.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.CurLifeStageIndex), MethodType.Getter)]
    static class LifeStageIndexInInterface
    {
        static bool Prefix(Pawn_AgeTracker __instance, ref int __result)
        {
            if (__instance.cachedLifeStageIndex >= 0 || !NeedCachesOutsideSimulation.InInterface) return true;

            __result = Compute(__instance);
            return false;
        }

        // RecalculateLifeStageIndex without its side effects
        static int Compute(Pawn_AgeTracker tracker)
        {
            if (tracker.lockedLifeStageIndex >= 0) return tracker.lockedLifeStageIndex;

            var ages = tracker.pawn.RaceProps.lifeStageAges;
            var growth = tracker.growth >= 0f ? tracker.growth : Mathf.Clamp01(tracker.AgeBiologicalYearsFloat / ages[ages.Count - 1].minAge);
            var age = tracker.pawn.RaceProps.Humanlike ? tracker.AgeBiologicalYears : Mathf.Lerp(0f, ages[ages.Count - 1].minAge, growth);

            for (int i = ages.Count - 1; i >= 0; i--)
                if (ages[i].minAge <= age + 1E-06f)
                    return i;
            return 0;
        }
    }
}
