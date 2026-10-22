using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// A room caches its open-roof count, its vacuum exposure and its stats and role on first read, and the
    /// interface reads them all the time (the temperature readout under the mouse, the inspect pane, room stats).
    /// A cache the interface filled on one player's game can outlive a change that should have cleared it (a roof
    /// set while the regions were dirty doesn't reach the room), so that game kept a stale value the other game
    /// never had: rooms flipped between indoors and outdoors on one side, and with them the beauty, room size and
    /// mood of the pawns in them (found by the pawns lane on Quaunt Amalgamation). Outside the simulation these
    /// reads still compute fresh values but leave the room's caches as they were.
    /// </summary>
    static class RoomCacheGuard
    {
        public static bool InInterface => Multiplayer.OutsideSimulation;
    }

    [HarmonyPatch(typeof(Room), nameof(Room.OpenRoofCount), MethodType.Getter)]
    static class RoomOpenRoofCountInInterface
    {
        static void Prefix(Room __instance, out int? __state) =>
            __state = RoomCacheGuard.InInterface ? __instance.cachedOpenRoofCount : null;

        static void Postfix(Room __instance, int? __state)
        {
            if (__state.HasValue) __instance.cachedOpenRoofCount = __state.Value;
        }
    }

    [HarmonyPatch(typeof(Room), nameof(Room.ExposedCountStopAt))]
    static class RoomExposedCountInInterface
    {
        static void Prefix(Room __instance, out (int count, int threshold)? __state) =>
            __state = RoomCacheGuard.InInterface ? (__instance.cachedExposedCount, __instance.cachedExposedThreshold) : null;

        static void Postfix(Room __instance, (int count, int threshold)? __state)
        {
            if (!__state.HasValue) return;
            __instance.cachedExposedCount = __state.Value.count;
            __instance.cachedExposedThreshold = __state.Value.threshold;
        }
    }

    /// <summary>
    /// A district counts its open-roof and vacuum-exposed cells lazily, keeping an enumerator and a running count
    /// between calls, so a read from the interface advanced the simulation's count in place (restoring a reference
    /// can't rewind an enumerator). Outside the simulation these count the cells afresh and touch nothing; found
    /// when NeedRoomSize thoughts appeared on one game only (rooms read as indoors on one side).
    /// </summary>
    [HarmonyPatch(typeof(District), nameof(District.OpenRoofCountStopAt))]
    static class DistrictOpenRoofCountInInterface
    {
        static bool Prefix(District __instance, int threshold, ref int __result)
        {
            if (!RoomCacheGuard.InInterface) return true;

            var roofs = __instance.Map.roofGrid;
            int count = 0;
            foreach (var c in __instance.Cells)
                if (!roofs.Roofed(c) && ++count >= threshold)
                    break;
            __result = count;
            return false;
        }
    }

    [HarmonyPatch(typeof(District), nameof(District.ExposedVacuumCount))]
    static class DistrictExposedCountInInterface
    {
        static bool Prefix(District __instance, int threshold, ref int __result)
        {
            if (!RoomCacheGuard.InInterface) return true;

            var roofs = __instance.Map.roofGrid;
            var terrain = __instance.Map.terrainGrid;
            int count = 0;
            foreach (var c in __instance.Cells)
                if ((!roofs.Roofed(c) || terrain.TerrainAt(c).exposesToVacuum) && ++count >= threshold)
                    break;
            __result = count;
            return false;
        }
    }

    [HarmonyPatch(typeof(District), nameof(District.CellCount), MethodType.Getter)]
    static class DistrictCellCountInInterface
    {
        static void Prefix(District __instance, out int? __state) =>
            __state = RoomCacheGuard.InInterface ? __instance.cachedCellCount : null;

        static void Postfix(District __instance, int? __state)
        {
            if (__state.HasValue) __instance.cachedCellCount = __state.Value;
        }
    }

    [HarmonyPatch(typeof(Room), nameof(Room.CellCount), MethodType.Getter)]
    static class RoomCellCountInInterface
    {
        static void Prefix(Room __instance, out int? __state) =>
            __state = RoomCacheGuard.InInterface ? __instance.cachedCellCount : null;

        static void Postfix(Room __instance, int? __state)
        {
            if (__state.HasValue) __instance.cachedCellCount = __state.Value;
        }
    }

    [HarmonyPatch]
    static class RoomStatsInInterface
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Room), nameof(Room.Role));
            yield return AccessTools.Method(typeof(Room), nameof(Room.GetStat));
            yield return AccessTools.Method(typeof(Room), nameof(Room.GetRoomRoleIfBuildingPlaced));
        }

        // The update would refill the room's own stats map; give it a new one to fill and put the old one back after
        static void Prefix(Room __instance, out (DefMap<RoomStatDef, float> stats, RoomRoleDef role)? __state)
        {
            __state = null;
            if (!__instance.statsAndRoleDirty || !RoomCacheGuard.InInterface) return;

            __state = (__instance.stats, __instance.role);
            __instance.stats = null;
        }

        static void Postfix(Room __instance, (DefMap<RoomStatDef, float> stats, RoomRoleDef role)? __state)
        {
            if (!__state.HasValue) return;
            __instance.stats = __state.Value.stats;
            __instance.role = __state.Value.role;
            __instance.statsAndRoleDirty = true;
        }
    }
}
