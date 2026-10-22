using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Reflection;
using Multiplayer.Client.Util;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client
{
    // Set the map time for GUI methods depending on it
    [HarmonyPatch]
    static class SetMapTimeForUI
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs));
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs));
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleMapClicks));
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleLowPriorityInput));
            yield return AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate));
            yield return AccessTools.Method(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutUpdate));
            yield return AccessTools.Method(typeof(SoundRoot), nameof(SoundRoot.Update));
            yield return AccessTools.Method(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions));
            yield return AccessTools.Method(typeof(Hediff), nameof(Hediff.GetTooltip));
        }

        [HarmonyPriority(MpPriority.MpFirst)]
        internal static void Prefix(ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null || WorldRendererUtility.WorldSelected || Find.CurrentMap == null) return;
            __state = TimeSnapshot.GetAndSetFromMap(Find.CurrentMap);
        }

        [HarmonyPriority(MpPriority.MpLast)]
        internal static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch]
    static class MapUpdateTimePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Map), nameof(Map.MapUpdate));
            yield return AccessTools.Method(typeof(Map), nameof(Map.FinalizeLoading));
        }

        [HarmonyPriority(MpPriority.MpFirst)]
        static void Prefix(Map __instance, ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null) return;
            __state = TimeSnapshot.GetAndSetFromMap(__instance);
        }

        [HarmonyPriority(MpPriority.MpLast)]
        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch]
    static class PawnPortraitMapTime
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PortraitsCache), nameof(PortraitsCache.IsAnimated));
        }

        static void Prefix(Pawn pawn, ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null || Current.ProgramState != ProgramState.Playing) return;
            __state = TimeSnapshot.GetAndSetFromMap(pawn.MapHeld);
        }

        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    // TODO 1.3: set time on the new renderer
    //[HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPortrait))]
    static class PawnRenderPortraitMapTime
    {
        static void Prefix(PawnRenderer __instance, ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null || Current.ProgramState != ProgramState.Playing) return;
            __state = TimeSnapshot.GetAndSetFromMap(__instance.pawn.MapHeld);
        }

        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch(typeof(PawnTweener), nameof(PawnTweener.PreDrawPosCalculation))]
    static class PreDrawPosCalculationMapTime
    {
        static void Prefix(PawnTweener __instance, ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null || Current.ProgramState != ProgramState.Playing) return;
            __state = TimeSnapshot.GetAndSetFromMap(__instance.pawn.Map);
        }

        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch(typeof(DangerWatcher), nameof(DangerWatcher.DangerRating), MethodType.Getter)]
    static class DangerRatingMapTime
    {
        static void Prefix(DangerWatcher __instance, ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null) return;
            __state = TimeSnapshot.GetAndSetFromMap(__instance.map);
        }

        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch]
    static class SustainerUpdateMapTime
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Sustainer), nameof(Sustainer.SustainerUpdate));
            yield return MpMethodUtil.GetLambda(typeof(Sustainer), parentMethodType: MethodType.Constructor, parentArgs: new[] { typeof(SoundDef), typeof(SoundInfo) });
        }

        static void Prefix(Sustainer __instance, ref TimeSnapshot? __state)
        {
            if (Multiplayer.game == null) return;
            __state = TimeSnapshot.GetAndSetFromMap(__instance.info.Maker.Map);
        }

        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch(typeof(Sample), nameof(Sample.Update))]
    static class SampleUpdateMapTime
    {
        static void Prefix(Sample __instance, ref TimeSnapshot? __state)
        {
            if (Multiplayer.game == null) return;
            __state = TimeSnapshot.GetAndSetFromMap(__instance.Map);
        }

        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    [HarmonyPatch(typeof(TipSignal), MethodType.Constructor, new[] { typeof(Func<string>), typeof(int) })]
    static class TipSignalCtor
    {
        static void Prefix(ref Func<string> textGetter)
        {
            if (Multiplayer.game == null) return;

            var current = TimeSnapshot.Current();
            var getter = textGetter;

            textGetter = () =>
            {
                var prev = TimeSnapshot.Current();
                current.Set();
                string s = getter();
                prev.Set();

                return s;
            };
        }
    }

    // Patch to remove endless (every tick!) condition duplicates from the condition causers
    [HarmonyPatch(typeof(CompCauseGameCondition), nameof(CompCauseGameCondition.GetConditionInstance))]
    public static class Patch_CompCauseGameCondition_GetConditionInstance
    {
        public static void Postfix(Map map, CompCauseGameCondition __instance, ref GameCondition __result)
        {
            // Sanity and MP checks. We only checking for conditions that are able to stack
            if (Multiplayer.Client == null
                    || __instance.Props.preventConditionStacking
                    || __result != null
                    || map == null)
                return;

            // Look for an existing active condition on this map that matches both def and causer
            var active = map.GameConditionManager.ActiveConditions
                .FirstOrDefault(c =>
                    c.def == __instance.Props.conditionDef &&
                    c.conditionCauser == __instance.parent);

            if (active != null)
                __result = active;
        }
    }

    [HarmonyPatch(typeof(CompCauseGameCondition), nameof(CompCauseGameCondition.EnforceConditionOn))]
    static class MapConditionCauserMapTime
    {
        static void Prefix(Map map, ref TimeSnapshot? __state)
        {
            if (Multiplayer.Client == null) return;
            __state = TimeSnapshot.GetAndSetFromMap(map);
        }

        static void Finalizer(TimeSnapshot? __state) => __state?.Set();
    }

    // A causer on another map refreshes its condition in this map's clock (above), but only when its own map ticks.
    // Under async time that map can be paused or slower while this one runs out the condition's transition ticks
    // (5000 for a smoke spewer): the condition ended here while the causer still listed it, a save then held a
    // reference to nothing ("Could not resolve GameCondition_SmokeSpewer_N" on loads), and the causer's next tick
    // made a new one. A condition stays while its causer is spawned on another map, active and in range of this one
    [HarmonyPatch(typeof(GameCondition), nameof(GameCondition.Expired), MethodType.Getter)]
    static class ConditionFromOtherMapCauserStays
    {
        static void Postfix(GameCondition __instance, ref bool __result)
        {
            if (!__result || Multiplayer.Client == null) return;
            if (__instance.conditionCauser is not ThingWithComps { Spawned: true } causer) return;

            var map = __instance.gameConditionManager?.ownerMap;
            if (map == null || causer.Map == map) return;

            foreach (var comp in causer.AllComps)
                if (comp is CompCauseGameCondition cause && cause.ConditionDef == __instance.def && cause.Active && cause.InAoE(map.Tile))
                {
                    __result = false;
                    return;
                }
        }
    }

    // Backstop for the same: a save leaves out conditions the causer lists that its map no longer has. Only the save:
    // the live list decides when the causer makes a new condition, and a save can run on one game alone
    [HarmonyPatch(typeof(CompCauseGameCondition), nameof(CompCauseGameCondition.PostExposeData))]
    static class CausedConditionsSaveOnlyActive
    {
        static void Prefix(CompCauseGameCondition __instance, ref Dictionary<Map, GameCondition> __state)
        {
            if (Multiplayer.Client == null || Scribe.mode != LoadSaveMode.Saving) return;

            var caused = __instance.causedConditions;
            Dictionary<Map, GameCondition> saved = null;
            foreach (var (map, condition) in caused)
                if (condition == null || !map.GameConditionManager.ActiveConditions.Contains(condition))
                {
                    saved ??= new Dictionary<Map, GameCondition>(caused);
                    saved.Remove(map);
                }

            if (saved == null) return;
            __state = caused;
            __instance.causedConditions = saved;
        }

        static void Finalizer(CompCauseGameCondition __instance, Dictionary<Map, GameCondition> __state)
        {
            if (__state != null) __instance.causedConditions = __state;
        }
    }

    public struct TimeSnapshot
    {
        public int ticks;
        public TimeSpeed speed;
        public TimeSlower slower;
        public int gameStartAbsTick;

        public void Set()
        {
            Find.TickManager.ticksGameInt = ticks;
            Find.TickManager.slower = slower;
            Find.TickManager.curTimeSpeed = speed;
            Find.TickManager.gameStartAbsTick = gameStartAbsTick;
        }

        public static TimeSnapshot Current()
        {
            return new TimeSnapshot
            {
                ticks = Find.TickManager.ticksGameInt,
                speed = Find.TickManager.curTimeSpeed,
                slower = Find.TickManager.slower,
                gameStartAbsTick = Find.TickManager.gameStartAbsTick
            };
        }

        public static TimeSnapshot? GetAndSetFromMap(Map map)
        {
            if (map == null) return null;

            TimeSnapshot prev = Current();

            var tickManager = Find.TickManager;
            var mapComp = map.AsyncTime();

            tickManager.ticksGameInt = mapComp.mapTicks;
            tickManager.slower = mapComp.slower;
            tickManager.CurTimeSpeed = mapComp.DesiredTimeSpeed;
            tickManager.gameStartAbsTick = mapComp.GameStartAbsTick;

            return prev;
        }
    }

}
