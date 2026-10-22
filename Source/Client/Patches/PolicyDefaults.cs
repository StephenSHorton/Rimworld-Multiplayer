using HarmonyLib;
using RimWorld;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// A pawn's apparel, drug, food and reading policy is null until something reads it, and the getter then stores
    /// the database's default policy. The save keeps what was stored, and Multiplayer syncs only the setters. So the
    /// Assign tab or an inspect read on one player's screen filled the field on that game only (2026-10-21 join
    /// point saves: default reading and food policies on one game, null on the other), and after a synced change of
    /// the default policy the pawns left null followed the new default while the filled ones kept the old: the
    /// games then disagree on what those pawns wear, take, eat and read. Outside the simulation the getter still
    /// returns the default, but leaves the field null; reads while ticking or running a command store it as in
    /// vanilla, at the same point on every game. "Outside the simulation" is Multiplayer.OutsideSimulation (not
    /// ticking, no command), not Multiplayer.InInterface: that one is also false in long events and loads that run on
    /// one game only (the host's autosave, a joiner's first load), where a store would again be one game's.
    /// </summary>
    static class PolicyDefaultsInInterface
    {
        public static bool KeepNull(bool wasNull) => wasNull && Multiplayer.OutsideSimulation;
    }

    [HarmonyPatch(typeof(Pawn_OutfitTracker), nameof(Pawn_OutfitTracker.CurrentApparelPolicy), MethodType.Getter)]
    static class ApparelPolicyDefaultInInterface
    {
        static void Prefix(Pawn_OutfitTracker __instance, out bool __state) => __state = __instance.curApparelPolicy == null;

        static void Postfix(Pawn_OutfitTracker __instance, bool __state)
        {
            if (PolicyDefaultsInInterface.KeepNull(__state)) __instance.curApparelPolicy = null;
        }
    }

    [HarmonyPatch(typeof(Pawn_DrugPolicyTracker), nameof(Pawn_DrugPolicyTracker.CurrentPolicy), MethodType.Getter)]
    static class DrugPolicyDefaultInInterface
    {
        static void Prefix(Pawn_DrugPolicyTracker __instance, out bool __state) => __state = __instance.curPolicy == null;

        static void Postfix(Pawn_DrugPolicyTracker __instance, bool __state)
        {
            if (PolicyDefaultsInInterface.KeepNull(__state)) __instance.curPolicy = null;
        }
    }

    [HarmonyPatch(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.CurrentFoodPolicy), MethodType.Getter)]
    static class FoodPolicyDefaultInInterface
    {
        static void Prefix(Pawn_FoodRestrictionTracker __instance, out bool __state) => __state = __instance.curPolicy == null;

        static void Postfix(Pawn_FoodRestrictionTracker __instance, bool __state)
        {
            if (PolicyDefaultsInInterface.KeepNull(__state)) __instance.curPolicy = null;
        }
    }

    [HarmonyPatch(typeof(Pawn_ReadingTracker), nameof(Pawn_ReadingTracker.CurrentPolicy), MethodType.Getter)]
    static class ReadingPolicyDefaultInInterface
    {
        static void Prefix(Pawn_ReadingTracker __instance, out bool __state) => __state = __instance.curPolicy == null;

        static void Postfix(Pawn_ReadingTracker __instance, bool __state)
        {
            if (PolicyDefaultsInInterface.KeepNull(__state)) __instance.curPolicy = null;
        }
    }
}
