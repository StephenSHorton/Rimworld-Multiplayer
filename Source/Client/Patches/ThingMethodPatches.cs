using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Factions;
using Multiplayer.Common.Desyncs;
using Verse;
using Verse.AI;

namespace Multiplayer.Client
{
    [HarmonyPatch(typeof(PawnComponentsUtility), nameof(PawnComponentsUtility.AddAndRemoveDynamicComponents))]
    public static class AddAndRemoveCompsPatch
    {
        static void Prefix(Pawn pawn, ref Container<Map>? __state)
        {
            if (Multiplayer.Client == null || pawn.Faction == null) return;

            pawn.Map.PushFaction(pawn.Faction);
            __state = pawn.Map;
        }

        static void Finalizer(Pawn pawn, Container<Map>? __state)
        {
            if (__state is { Inner: var map })
                map.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.AddOrRemoveNeedsAsAppropriate))]
    public static class AddRemoveNeedsPatch
    {
        static void Prefix(Pawn ___pawn, ref Container<Map>? __state)
        {
            if (Multiplayer.Client == null || ___pawn.Faction == null) return;

            ___pawn.Map.PushFaction(___pawn.Faction);
            __state = ___pawn.Map;
        }

        static void Finalizer(Container<Map>? __state)
        {
            if (__state is { Inner: var map })
                map.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class JobTrackerStart
    {
        static void Prefix(Pawn_JobTracker __instance, Job newJob, ref Container<Map>? __state)
        {
            if (Multiplayer.Client == null) return;

            if (Multiplayer.InInterface)
            {
                Log.Warning($"Started a job {newJob} on pawn {__instance.pawn} from the interface!");
                return;
            }

            Pawn pawn = __instance.pawn;

            if (pawn.Faction == null || !pawn.Spawned) return;

            pawn.Map.PushFaction(pawn.Faction);
            __state = pawn.Map;
        }

        static void Finalizer(Container<Map>? __state)
        {
            if (__state is { Inner: var map })
                map.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class JobTrackerEndCurrent
    {
        static void Prefix(Pawn_JobTracker __instance, JobCondition condition, ref Container<Map>? __state)
        {
            if (Multiplayer.Client == null) return;
            Pawn pawn = __instance.pawn;

            if (pawn.Faction == null || !pawn.Spawned) return;

            pawn.Map.PushFaction(pawn.Faction);
            __state = pawn.Map;
        }

        static void Finalizer(Container<Map>? __state)
        {
            if (__state is { Inner: var map })
                map.PopFaction();
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.CheckForJobOverride))]
    public static class JobTrackerOverride
    {
        static void Prefix(Pawn_JobTracker __instance, ref ThingMethodPatches.State __state)
        {
            if (Multiplayer.Client == null) return;
            Pawn pawn = __instance.pawn;

            if (pawn.Faction == null || !pawn.Spawned) return;

            pawn.Map.PushFaction(pawn.Faction);
            ThingContext.Push(pawn);
            __state.map = pawn.Map;
            __state.attr = RandAttribution.PushThing(pawn, RandAttrMethod.CheckForJobOverride);
        }

        static void Finalizer(ThingMethodPatches.State __state)
        {
            if (__state.map is { } map)
            {
                RandAttribution.PopTo(__state.attr);
                map.PopFaction();
                ThingContext.Pop();
            }
        }
    }

    /// <summary>
    /// ThingContext and FactionContext for common Thing methods, and the thing's Rand attribution frame. One prefix
    /// per method, so each passes its method kind as a constant.
    /// </summary>
    public static class ThingMethodPatches
    {
        public struct State
        {
            public Map map;
            public int attr;
        }

        private static void Push(Thing thing, Map map, RandAttrMethod method, ref State state)
        {
            if (Multiplayer.Client == null) return;

            state.map = map;
            ThingContext.Push(thing);
            state.attr = RandAttribution.PushThing(thing, method);

            if (thing.def.CanHaveFaction)
                map.PushFaction(thing.Faction);
        }

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix(Thing __instance, ref State __state) => Push(__instance, __instance.Map, RandAttrMethod.None, ref __state);

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_Tick(Thing __instance, ref State __state) => Push(__instance, __instance.Map, RandAttrMethod.Tick, ref __state);

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_TickRare(Thing __instance, ref State __state) => Push(__instance, __instance.Map, RandAttrMethod.TickRare, ref __state);

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_TickLong(Thing __instance, ref State __state) => Push(__instance, __instance.Map, RandAttrMethod.TickLong, ref __state);

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_TickInterval(Thing __instance, ref State __state) => Push(__instance, __instance.Map, RandAttrMethod.TickInterval, ref __state);

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_TakeDamage(Thing __instance, ref State __state) => Push(__instance, __instance.Map, RandAttrMethod.TakeDamage, ref __state);

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_Kill(Thing __instance, ref State __state) => Push(__instance, __instance.Map, RandAttrMethod.Kill, ref __state);

        [HarmonyPriority(MpPriority.MpFirst)]
        public static void Prefix_SpawnSetup(Thing __instance, Map __0, ref State __state) => Push(__instance, __0, RandAttrMethod.SpawnSetup, ref __state);

        /// <summary>The prefix for a thing method by name.</summary>
        public static string PrefixFor(string method) => method switch
        {
            "Tick" => nameof(Prefix_Tick),
            "TickRare" => nameof(Prefix_TickRare),
            "TickLong" => nameof(Prefix_TickLong),
            "TickInterval" => nameof(Prefix_TickInterval),
            "TakeDamage" => nameof(Prefix_TakeDamage),
            "Kill" => nameof(Prefix_Kill),
            _ => nameof(Prefix)
        };

        [HarmonyPriority(MpPriority.MpLast)]
        public static void Finalizer(Thing __instance, State __state)
        {
            if (__state.map is not { } map) return;

            RandAttribution.PopTo(__state.attr);

            if (__instance.def.CanHaveFaction)
                map.PopFaction();

            ThingContext.Pop();
        }
    }

    public static class ThingContext
    {
        public static Stack<(Thing, Map)> stack = new();

        static ThingContext() => Clear();

        public static Thing Current => stack.Peek().Item1;
        public static Pawn CurrentPawn => Current as Pawn;

        public static Map CurrentMap
        {
            get
            {
                var peek = stack.Peek();
                if (peek.Item1 != null && peek.Item1.Map != peek.Item2)
                    Log.ErrorOnce($"Thing {peek.Item1} has changed its map!", peek.Item1.thingIDNumber ^ 57481021);
                return peek.Item2;
            }
        }

        public static void Push(Thing t)
        {
            stack.Push((t, t.Map));
        }

        public static void Pop()
        {
            stack.Pop();
        }

        public static void Clear()
        {
            stack.Clear();
            stack.Push((null, null));
        }
    }
}
