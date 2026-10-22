using HarmonyLib;
using Multiplayer.Client.Desyncs;
using Multiplayer.Common.Desyncs;
using RimWorld.Planet;

namespace Multiplayer.Client.Patches;

/// <summary>FactionContext and the Rand attribution frame for common WorldObject methods (one prefix per method kind).</summary>
public static class WorldObjectMethodPatches
{
    private static void Push(WorldObject wo, RandAttrMethod method, ref int state)
    {
        state = -1;
        if (Multiplayer.Client == null) return;

        if (wo.def.canHaveFaction)
            FactionContext.Push(wo.Faction);

        if (RandAttribution.active)
            state = RandAttribution.PushOwner(RandAttrKind.WorldObject, wo.ID, wo.def?.shortHash ?? 0, 0, method);
    }

    [HarmonyPriority(MpPriority.MpFirst)]
    public static void Prefix(WorldObject __instance, ref int __state) => Push(__instance, RandAttrMethod.None, ref __state);

    [HarmonyPriority(MpPriority.MpFirst)]
    public static void Prefix_SpawnSetup(WorldObject __instance, ref int __state) => Push(__instance, RandAttrMethod.SpawnSetup, ref __state);

    [HarmonyPriority(MpPriority.MpFirst)]
    public static void Prefix_Tick(WorldObject __instance, ref int __state) => Push(__instance, RandAttrMethod.Tick, ref __state);

    [HarmonyPriority(MpPriority.MpFirst)]
    public static void Prefix_TickInterval(WorldObject __instance, ref int __state) => Push(__instance, RandAttrMethod.TickInterval, ref __state);

    [HarmonyPriority(MpPriority.MpLast)]
    public static void Finalizer(WorldObject __instance, int __state)
    {
        if (Multiplayer.Client == null) return;

        RandAttribution.PopTo(__state);

        if (__instance.def.canHaveFaction)
            FactionContext.Pop();
    }
}