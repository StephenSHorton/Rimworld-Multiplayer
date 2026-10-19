using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Patches
{
    /// <summary>
    /// Keeps motes out of a map's shared thing lists in a session.
    ///
    /// Text motes (Mote_Text, drawGUIOverlay) are listed in ListerThings, including AllThings and every group
    /// that includes them, and which ones spawn follows each player's camera and interface. ListerThings removes
    /// by swapping the last thing into the freed slot, so every mote that comes and goes on one game only
    /// reordered that game's AllThings: the lists held the same things in a different order, the lanes (order
    /// free sums) saw nothing, the saves kept the order across join points, and searches that walk the lists
    /// picked differently (Desync-17/19: a wander and a storage cell search split right after reloads).
    /// A mote now stays in its own def's list and in the GUI overlay group, which only drawing reads.
    /// </summary>
    [HarmonyPatch(typeof(ListerThings), nameof(ListerThings.Add))]
    static class ListerThingsAddMotePatch
    {
        static bool Prefix(ListerThings __instance, Thing t)
        {
            if (Multiplayer.Client == null || t.def.category != ThingCategory.Mote) return true;
            if (!ListerThings.EverListable(t.def, __instance.use)) return false;

            if (!__instance.listsByDef.TryGetValue(t.def, out var byDef))
                __instance.listsByDef.Add(t.def, byDef = new());
            byDef.Add(t);

            const ThingRequestGroup group = ThingRequestGroup.HasGUIOverlay;
            if (__instance.use != ListerThingsUse.Region && group.Includes(t.def))
                (__instance.listsByGroup[(uint)group] ??= new()).Add(t);

            return false;
        }
    }

    [HarmonyPatch(typeof(ListerThings), nameof(ListerThings.Remove))]
    static class ListerThingsRemoveMotePatch
    {
        static bool Prefix(ListerThings __instance, Thing t)
        {
            if (Multiplayer.Client == null || t.def.category != ThingCategory.Mote) return true;

            if (__instance.listsByDef.TryGetValue(t.def, out var byDef))
                byDef.Remove(t);
            __instance.listsByGroup[(uint)ThingRequestGroup.HasGUIOverlay]?.Remove(t);

            return false;
        }
    }
}
