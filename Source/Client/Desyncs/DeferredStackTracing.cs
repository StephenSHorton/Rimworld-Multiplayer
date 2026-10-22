using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Multiplayer.Client.Patches;
using Multiplayer.Common;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client.Desyncs
{
    [EarlyPatch]
    [HarmonyPatch]
    public static class DeferredStackTracing
    {
        public static int ignoreTraces;
        public static long maxTraceDepth;
        public static int randCalls;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Value));
            yield return AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Int));
        }

        public static int acc;

        private static bool SupportsDeferredStackTracing =>
            RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

        /// <summary>
        /// The postfix on Rand.Value and Rand.Int. Rand attribution's two checks come first and apart from the desync
        /// traces (off by default): a call off the main thread while a scope is recorded, and the armed watch.
        /// </summary>
        [HarmonyPostfix]
        public static void RandPostfix()
        {
            if (RandAttribution.active && System.Environment.CurrentManagedThreadId != RandAttribution.mainThreadId)
                RandAttribution.OffThread();
            if (RandAttribution.watchArmed)
                RandAttribution.OnWatchedCall();
            TraceCore(0, TraceKind.Rand);
        }

        /// <summary>The kinds of desync trace; a report names a first difference that isn't a Rand call.</summary>
        public static class TraceKind
        {
            public const string Rand = "Rand";
            public const string UniqueIds = "UniqueIds";
            public const string Spawn = "Spawn";
            public const string DeSpawn = "DeSpawn";
        }

        /// <summary>Records a desync trace (Rand calls, ids, spawns) when desync tracing is on.</summary>
        public static void Trace(string kind = null) => TraceCore(0, kind);

        // Both callers (RandPostfix, Trace) sit one frame above this; extraFrames is for any deeper caller
        private static void TraceCore(int extraFrames, string kind)
        {
            if (Native.LmfPtr == 0)
            {
                // CoreCLR (RimWorld 1.7) has no Mono LMF to walk. A managed StackTrace is far too slow for every Rand
                // call, so it only runs in the Slow tracing mode. Fast mode still records the tick, random state and
                // thing being ticked, so a desync report shows where the games split instead of no traces at all.
                if (!Native.IsMono && ShouldAddStackTraceForDesyncLog())
                {
                    if (Multiplayer.settings.desyncTracingMode == DesyncTracingMode.Slow)
                        ManagedTrace(extraFrames, kind);
                    else
                        ContextTrace(kind);
                }
                return;
            }

            if (!ShouldAddStackTraceForDesyncLog()) return;

            var logItem = StackTraceLogItemRaw.GetFromPool();
            var trace = logItem.raw;
            int hash = 0;
            // Skip this (DeferredStackTracing.Postfix) frame. Keeping it in the trace doesn't provide any value.
            int depth = DeferredStackTracingImpl.TraceImpl(trace, ref hash, skipFrames: 2 + extraFrames);

            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(logItem, depth, hash, kind: kind);

            acc++;
        }

        private static void ContextTrace(string kind)
        {
            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(StackTraceLogItemRaw.GetFromPool(), 0, 0, hashStack: false, kind: kind);
        }

        private static Dictionary<MethodBase, int> managedNameHashes = new();

        private static void ManagedTrace(int extraFrames, string kind)
        {
            var logItem = StackTraceLogItemManaged.GetFromPool();
            // Skip this frame and Postfix, like the Mono walk skips Postfix
            var frames = new StackTrace(3 + extraFrames, false).GetFrames();
            int hash = 0;
            int depth = 0;

            foreach (var frame in frames)
            {
                if (depth == StackTraceLogItemRaw.MaxDepth) break;

                // Harmony's replacement for a patched method shows as a dynamic method; name it by the original
                var method = Harmony.GetMethodFromStackframe(frame);
                logItem.methods[depth++] = method;

                if (method == null)
                    continue;

                if (!managedNameHashes.TryGetValue(method, out int nameHash))
                    managedNameHashes[method] = nameHash = GenText.StableStringHash(method.FullDescription());

                hash = Gen.HashCombineInt(hash, nameHash);
            }

            // CoreCLR's tiered JIT inlines differently in each process, so the same call can show different
            // frames on the host and a client. Hashing the stack would report those as desyncs.
            Multiplayer.game.sync.TryAddStackTraceForDesyncLogRaw(logItem, depth, hash, hashStack: false, kind: kind);
        }

        public static bool ShouldAddStackTraceForDesyncLog()
        {
            if (!SupportsDeferredStackTracing) return false;
            if (Multiplayer.Client == null) return false;
            if (Multiplayer.settings.desyncTracingMode == DesyncTracingMode.None) return false;
            if (Multiplayer.game == null) return false;

            if (!Multiplayer.game.gameComp.logDesyncTraces) return false;

            if (Rand.stateStack.Count > 1) return false;
            if (Multiplayer.IsReplay) return false;

            if (!Multiplayer.Ticking && !Multiplayer.ExecutingCmds) return false;

            return ignoreTraces == 0;
        }
    }

    [HarmonyPatch(typeof(UniqueIDsManager), nameof(UniqueIDsManager.GetNextID))]
    public static class UniqueIdsPatch
    {
        static void Postfix()
        {
            DeferredStackTracing.Trace(DeferredStackTracing.TraceKind.UniqueIds);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    public static class ThingSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            if (__instance.def.HasThingIDNumber)
                DeferredStackTracing.Trace(DeferredStackTracing.TraceKind.Spawn);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    public static class ThingDeSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            if (__instance.def.HasThingIDNumber)
                DeferredStackTracing.Trace(DeferredStackTracing.TraceKind.DeSpawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class EndCurrentJobPatch
    {
        static void Prefix(Pawn_JobTracker __instance)
        {
            if (MpVersion.IsDebug && __instance.curJob != null && DeferredStackTracing.ShouldAddStackTraceForDesyncLog())
                Multiplayer.game.sync.TryAddInfoForDesyncLog($"EndCurrentJob for {__instance.pawn}: {__instance.curJob}", "");
        }
    }

    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    static class WildAnimalSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick))]
    static class WildPlantSpawnerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick))]
    static class SteadyEnvironmentEffectsTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    [HarmonyPatch(typeof(TempTerrainManager), nameof(TempTerrainManager.Tick))]
    static class TempTerrainManagerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }

    /*[HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellForWorker))]
    static class FindBestStorageCellTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }*/

    [HarmonyPatch(typeof(IntermittentSteamSprayer), nameof(IntermittentSteamSprayer.SteamSprayerTick))]
    static class SteamSprayerTickTraceIgnore
    {
        static void Prefix() => DeferredStackTracing.ignoreTraces++;
        static void Finalizer() => DeferredStackTracing.ignoreTraces--;
    }
}
