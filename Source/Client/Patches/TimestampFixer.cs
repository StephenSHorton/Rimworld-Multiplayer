using System;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Patches;

public static class TimestampFixer
{
    delegate ref int FieldGetter<in T>(T obj) where T : IExposable;

    private static Dictionary<Type, List<FieldGetter<IExposable>>> timestampFields = new();

    private static void Add<T>(FieldGetter<T> t) where T : IExposable
    {
        timestampFields.GetOrAddNew(typeof(T)).Add(obj => ref t((T)obj));
    }

    static TimestampFixer()
    {
        Add((Pawn_MindState mind) => ref mind.canSleepTick);
        Add((Pawn_MindState mind) => ref mind.canLovinTick);
        Add((Pawn_GuestTracker guest) => ref guest.ticksWhenAllowedToEscapeAgain);
        Add((Pawn_GuestTracker guest) => ref guest.lastPrisonBreakTicks);
    }

    private static readonly List<Thing> tmpHeld = new();

    public static void FixPawn(Pawn p, Map oldMap, Map newMap)
    {
        var oldTime = oldMap?.AsyncTime().mapTicks ?? Multiplayer.AsyncWorldTime.worldTicks;
        var newTime = newMap?.AsyncTime().mapTicks ?? Multiplayer.AsyncWorldTime.worldTicks;
        var offset = newTime - oldTime;

        MpLog.Debug($"Fixing pawn timestamps for {p} moving from {oldMap?.ToString() ?? "World"}:{oldTime} to {newMap?.ToString() ?? "World"}:{newTime}");

        // This used to reach the trackers with a debug save of the whole pawn (a deep save per map move or death,
        // warning "Deep-saving destroyed thing" for the dead). Now the same trackers directly: the pawn's and those
        // of every pawn it holds (carried, in its inventory), which the deep save reached too. The debug save only
        // ran outside other saves and loads
        if (Scribe.mode != LoadSaveMode.Inactive) return;

        Shift(p, offset);
        ThingOwnerUtility.GetAllThingsRecursively(p, tmpHeld);
        foreach (var held in tmpHeld)
            if (held is Pawn heldPawn && heldPawn != p)
                Shift(heldPawn, offset);
        tmpHeld.Clear();
    }

    private static void Shift(Pawn p, int offset)
    {
        if (p.mindState != null) Shift(p.mindState, offset);
        if (p.guest != null) Shift(p.guest, offset);
    }

    private static void Shift(IExposable exposable, int offset)
    {
        if (timestampFields.TryGetValue(exposable.GetType(), out var fieldGetters))
            foreach (var del in fieldGetters)
                del(exposable) += offset;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
static class PawnDespawn_RememberMap
{
    static void Prefix(Pawn __instance)
    {
        if (Multiplayer.Client == null) return;
        __instance.GetComp<MultiplayerPawnComp>().lastMap = __instance.Map.uniqueID;
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.RemovePawn))]
static class WorldPawnsRemovePawn_RememberTick
{
    static void Prefix(Pawn p)
    {
        if (Multiplayer.Client == null) return;
        p.GetComp<MultiplayerPawnComp>().worldPawnRemoveTick = Multiplayer.AsyncWorldTime.worldTicks;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
static class PawnSpawn_FixTimestamp
{
    static void Postfix(Pawn __instance)
    {
        if (Multiplayer.Client == null) return;
        if (__instance.Map == null) return;

        var comp = __instance.GetComp<MultiplayerPawnComp>();
        if (comp.worldPawnRemoveTick != -1)
        {
            TimestampFixer.FixPawn(__instance, null, __instance.Map);
            comp.worldPawnRemoveTick = -1;
        }
    }
}

[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.AddPawn))]
static class WorldPawnsAddPawn_FixTimestamp
{
    static void Prefix(Pawn p)
    {
        if (Multiplayer.Client == null) return;

        var lastMap = p.GetComp<MultiplayerPawnComp>().lastMap;
        if (lastMap != -1)
        {
            TimestampFixer.FixPawn(p, Find.Maps.FirstOrDefault(m => m.uniqueID == lastMap), null);
            p.GetComp<MultiplayerPawnComp>().lastMap = -1;
        }
    }
}
