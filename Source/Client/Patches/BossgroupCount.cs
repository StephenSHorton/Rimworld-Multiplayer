using HarmonyLib;
using RimWorld;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// <see cref="GameComponent_Bossgroup.NumTimesCalledBossgroup"/> adds a 0 entry for a bossgroup it hasn't seen,
    /// and the save keeps it. The debug menu's bossgroup submenu and the call gizmo's warning text read it on one
    /// player's screen, so one game's save had Diabolus, Warqueen and Apocriton at 0 and the other's none (2026-10-22
    /// join points). It matters to the simulation too: vanilla's Notify_BossgroupCalled adds 0 for a new key but adds
    /// one to an existing entry, so the next call would count differently. Outside the simulation the read returns 0
    /// for a missing entry and stores nothing; reads while ticking or running a command store it as in vanilla (see
    /// <see cref="PolicyDefaultsInInterface"/>).
    /// </summary>
    [HarmonyPatch(typeof(GameComponent_Bossgroup), nameof(GameComponent_Bossgroup.NumTimesCalledBossgroup))]
    static class BossgroupCountInInterface
    {
        static bool Prefix(GameComponent_Bossgroup __instance, BossgroupDef bossgroupDef, ref int __result)
        {
            if (!Multiplayer.OutsideSimulation || __instance.timesCalledBossgroups == null) return true;
            if (__instance.timesCalledBossgroups.ContainsKey(bossgroupDef)) return true;

            __result = 0;
            return false;
        }
    }
}
