using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Client.Util;
using Multiplayer.Common.Desyncs;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>The root phases of a map tick, a world tick and a command.</summary>
    public static class RandAttrPhases
    {
        public static readonly ushort PreTick = RandAttribution.RegisterPhase("PreTick");
        public static readonly ushort PathFinderJobs = RandAttribution.RegisterPhase("PathFinderJobs");
        public static readonly ushort TickListNormal = RandAttribution.RegisterPhase("TickListNormal");
        public static readonly ushort TickListRare = RandAttribution.RegisterPhase("TickListRare");
        public static readonly ushort TickListLong = RandAttribution.RegisterPhase("TickListLong");
        public static readonly ushort Sessions = RandAttribution.RegisterPhase("Sessions");
        public static readonly ushort Storyteller = RandAttribution.RegisterPhase("Storyteller");
        public static readonly ushort StoryWatcher = RandAttribution.RegisterPhase("StoryWatcher");
        public static readonly ushort Quests = RandAttribution.RegisterPhase("Quests");
        public static readonly ushort PostTick = RandAttribution.RegisterPhase("PostTick");
        // Any Rand call in the visuals is itself suspicious: they run on every game but follow the camera
        public static readonly ushort PostTickVisuals = RandAttribution.RegisterPhase("PostTickVisuals");
        public static readonly ushort UpdateManagers = RandAttribution.RegisterPhase("UpdateManagers");
        public static readonly ushort WorldTick = RandAttribution.RegisterPhase("WorldTick");
        public static readonly ushort WorldSessions = RandAttribution.RegisterPhase("WorldSessions");
        public static readonly ushort WorldUpdate = RandAttribution.RegisterPhase("WorldUpdate");

        private static readonly Dictionary<CommandType, ushort> commandPhases = new();
        private static readonly Dictionary<int, ushort> syncPhases = new();

        public static ushort Command(CommandType type)
        {
            if (!commandPhases.TryGetValue(type, out var id))
                commandPhases[type] = id = RandAttribution.RegisterPhase("cmd:" + type);
            return id;
        }

        /// <summary>Called by SyncUtil.HandleCmd once the handler is known.</summary>
        public static void SyncHandler(int syncId, object handler)
        {
            if (!RandAttribution.active) return;
            if (!syncPhases.TryGetValue(syncId, out var id))
            {
                var name = "sync:" + handler;
                if (name.Length > 160) name = name.Substring(0, 160);
                syncPhases[syncId] = id = RandAttribution.RegisterPhase(name);
            }
            RandAttribution.CommandPhase(id);
        }
    }

    /// <summary>
    /// The context boundaries of Rand attribution beyond things (which ThingMethodPatches marks). All are applied on
    /// every game whatever the settings, so the Harmony patches row of the join fingerprint stays the same; tier 2
    /// and the kill switch are plain static gates. Prefix and postfix with pop-to-depth: an exception that skips a
    /// postfix is repaired by the parent's pop.
    /// </summary>
    public static class RandAttrPatches
    {
        private static readonly Dictionary<MethodBase, ushort> systemPhases = new();

        // Map systems called once per tick (tier 1)
        private static readonly (Type type, string method)[] MapSystems =
        [
            (typeof(ItemAvailability), nameof(ItemAvailability.Tick)),
            (typeof(ListerHaulables), nameof(ListerHaulables.ListerHaulablesTick)),
            (typeof(AutoBuildRoofAreaSetter), nameof(AutoBuildRoofAreaSetter.AutoBuildRoofAreaSetterTick_First)),
            (typeof(RoofCollapseBufferResolver), nameof(RoofCollapseBufferResolver.CollapseRoofsMarkedToCollapse)),
            (typeof(WindManager), nameof(WindManager.WindManagerTick)),
            (typeof(MapTemperature), nameof(MapTemperature.MapTemperatureTick)),
            (typeof(TemporaryThingDrawer), nameof(TemporaryThingDrawer.Tick)),
            (typeof(PathFinder), nameof(PathFinder.PathFinderTick)),
            (typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick)),
            (typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick)),
            (typeof(PowerNetManager), nameof(PowerNetManager.PowerNetsTick)),
            (typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick)),
            (typeof(TempTerrainManager), nameof(TempTerrainManager.Tick)),
            (typeof(GasGrid), nameof(GasGrid.Tick)),
            (typeof(PollutionGrid), nameof(PollutionGrid.PollutionTick)),
            (typeof(DeferredSpawner), nameof(DeferredSpawner.DeferredSpawnerTick)),
            (typeof(LordManager), nameof(LordManager.LordManagerTick)),
            (typeof(PassingShipManager), nameof(PassingShipManager.PassingShipManagerTick)),
            (typeof(VoluntarilyJoinableLordsStarter), nameof(VoluntarilyJoinableLordsStarter.VoluntarilyJoinableLordsStarterTick)),
            (typeof(GameConditionManager), nameof(GameConditionManager.GameConditionManagerTick)),
            (typeof(WeatherManager), nameof(WeatherManager.WeatherManagerTick)),
            (typeof(WeatherDecider), nameof(WeatherDecider.WeatherDeciderTick)),
            (typeof(FireWatcher), nameof(FireWatcher.FireWatcherTick)),
            (typeof(FleckManager), nameof(FleckManager.FleckManagerTick)),
            (typeof(EffecterMaintainer), nameof(EffecterMaintainer.EffecterMaintainerTick)),
        ];

        public delegate void TryPatch(MethodBase original, HarmonyMethod prefix, HarmonyMethod postfix);

        private static HarmonyMethod M(string name) =>
            new(AccessTools.Method(typeof(RandAttrPatches), name)) { priority = Priority.First };

        private static HarmonyMethod Last(string name) =>
            new(AccessTools.Method(typeof(RandAttrPatches), name)) { priority = Priority.Last };

        private static bool Patchable(MethodInfo m) =>
            m != null && !m.IsAbstract && !m.IsGenericMethodDefinition && !m.DeclaringType!.ContainsGenericParameters && m.HasMethodBody();

        public static void Apply(TryPatch tryPatch)
        {
            var popPostfix = Last(nameof(Pop));

            // Map systems
            foreach (var (type, name) in MapSystems)
            {
                var method = AccessTools.DeclaredMethod(type, name);
                if (method == null) continue;
                systemPhases[method] = RandAttribution.RegisterPhase($"{type.Name}.{name}");
                tryPatch(method, M(nameof(SystemPrefix)), popPostfix);
            }

            // Mutators run after the map components in MapPostTick
            // Map and world components, by type
            foreach (var t in typeof(MapComponent).AllSubtypesAndSelf().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var m = t.GetMethod(nameof(MapComponent.MapComponentTick), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (Patchable(m) && t != typeof(MapComponent))
                {
                    RandAttribution.PhaseOf(t);
                    tryPatch(m, M(nameof(MapComponentPrefix)), Last(nameof(MapComponentPostfix)));
                }
            }

            foreach (var t in typeof(WorldComponent).AllSubtypesAndSelf().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var m = t.GetMethod(nameof(WorldComponent.WorldComponentTick), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (Patchable(m) && t != typeof(WorldComponent))
                {
                    RandAttribution.PhaseOf(t);
                    tryPatch(m, M(nameof(WorldComponentPrefix)), popPostfix);
                }
            }

            foreach (var t in typeof(GameCondition).AllSubtypesAndSelf())
            {
                var m = t.GetMethod(nameof(GameCondition.GameConditionTick), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (Patchable(m))
                    tryPatch(m, M(nameof(ConditionPrefix)), popPostfix);
            }

            tryPatch(AccessTools.DeclaredMethod(typeof(Lord), nameof(Lord.LordTick)), M(nameof(LordPrefix)), popPostfix);
            tryPatch(AccessTools.DeclaredMethod(typeof(Quest), nameof(Quest.QuestTick)), M(nameof(QuestPrefix)), popPostfix);
            tryPatch(AccessTools.DeclaredMethod(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute)), M(nameof(IncidentPrefix)), popPostfix);

            // Tier 2: pawn trackers, job drivers, comps
            var tier2Pop = Last(nameof(PopTier2));
            foreach (var (type, name, prefix) in Trackers)
            {
                var method = AccessTools.DeclaredMethod(type, name);
                if (method == null || !Patchable(method))
                {
                    Log.Warning($"Rand attribution: no {type.Name}.{name} to mark");
                    continue;
                }
                tryPatch(method, M(prefix), tier2Pop);
            }

            tryPatch(AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.DriverTick)), M(nameof(DriverPrefix)), tier2Pop);
            tryPatch(AccessTools.DeclaredMethod(typeof(JobDriver), nameof(JobDriver.DriverTickInterval)), M(nameof(DriverPrefix)), tier2Pop);

            var compMethods = new[]
            {
                (nameof(ThingComp.CompTick), Type.EmptyTypes),
                (nameof(ThingComp.CompTickRare), Type.EmptyTypes),
                (nameof(ThingComp.CompTickLong), Type.EmptyTypes),
                (nameof(ThingComp.CompTickInterval), new[] { typeof(int) }),
            };
            foreach (var t in typeof(ThingComp).AllSubtypesAndSelf().Where(t => t != typeof(ThingComp)).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                bool any = false;
                foreach (var (name, args) in compMethods)
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, args, null);
                    if (!Patchable(m)) continue;
                    any = true;
                    tryPatch(m, M(nameof(CompPrefix)), tier2Pop);
                }
                if (any) RandAttribution.PhaseOf(t, "comp:");
            }
        }

        // --- Tier 1 ---

        public static void Pop(int __state) => RandAttribution.PopTo(__state);

        public static void SystemPrefix(MethodBase __originalMethod, out int __state)
        {
            __state = RandAttribution.active && systemPhases.TryGetValue(__originalMethod, out var phase)
                ? RandAttribution.PushPhase(phase, false)
                : -1;
        }

        public static void MapComponentPrefix(MapComponent __instance, out int __state) =>
            __state = RandAttribution.active ? RandAttribution.PushOwner(RandAttrKind.MapComp, 0, 0, RandAttribution.PhaseOf(__instance.GetType())) : -1;

        public static void MapComponentPostfix(MapComponent __instance, int __state)
        {
            if (RandInjectPatch.on) RandInjectPatch.AfterMapComponent(__instance);
            RandAttribution.PopTo(__state);
        }

        public static void WorldComponentPrefix(WorldComponent __instance, out int __state) =>
            __state = RandAttribution.active ? RandAttribution.PushOwner(RandAttrKind.WorldComp, 0, 0, RandAttribution.PhaseOf(__instance.GetType())) : -1;

        public static void ConditionPrefix(GameCondition __instance, out int __state) =>
            __state = RandAttribution.active ? RandAttribution.PushOwner(RandAttrKind.Condition, __instance.uniqueID, __instance.def?.shortHash ?? 0, 0) : -1;

        public static void LordPrefix(Lord __instance, out int __state) =>
            __state = RandAttribution.active
                ? RandAttribution.PushOwner(RandAttrKind.Lord, __instance.loadID, 0,
                    __instance.CurLordToil != null ? RandAttribution.PhaseOf(__instance.CurLordToil.GetType()) : (ushort)0)
                : -1;

        public static void QuestPrefix(Quest __instance, out int __state) =>
            __state = RandAttribution.active ? RandAttribution.PushOwner(RandAttrKind.Quest, __instance.id, 0, 0) : -1;

        public static void IncidentPrefix(IncidentWorker __instance, out int __state) =>
            __state = RandAttribution.active ? RandAttribution.PushOwner(RandAttrKind.Incident, 0, __instance.def?.shortHash ?? 0, 0) : -1;

        // --- Tier 2 ---

        public static void PopTier2(int __state) => RandAttribution.PopTo(__state);

        private static readonly (Type, string, string)[] Trackers =
        [
            (typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick), nameof(PatherPrefix)),
            (typeof(VerbTracker), nameof(VerbTracker.VerbsTick), nameof(VerbsPrefix)),
            (typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.StanceTrackerTick), nameof(StancePrefix)),
            (typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick), nameof(JobTrackerPrefix)),
            (typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTickInterval), nameof(JobTrackerIntervalPrefix)),
            (typeof(Pawn_JobTracker), "DetermineNextJob", nameof(ThinkPrefix)),
            (typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick), nameof(HealthPrefix)),
            (typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTickInterval), nameof(HealthIntervalPrefix)),
            (typeof(Pawn_MindState), nameof(Pawn_MindState.MindStateTickInterval), nameof(MindStatePrefix)),
            (typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.CarryHandsTickInterval), nameof(CarryPrefix)),
            (typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTickInterval), nameof(NeedsPrefix)),
            (typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.InteractionsTrackerTickInterval), nameof(InteractionsPrefix)),
            (typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.EquipmentTrackerTick), nameof(EquipmentPrefix)),
        ];

        private static class T2
        {
            public static readonly ushort Pather = RandAttribution.RegisterPhase("Pawn_PathFollower.PatherTick");
            public static readonly ushort Verbs = RandAttribution.RegisterPhase("VerbTracker.VerbsTick");
            public static readonly ushort Stance = RandAttribution.RegisterPhase("Pawn_StanceTracker.StanceTrackerTick");
            public static readonly ushort JobTracker = RandAttribution.RegisterPhase("Pawn_JobTracker.JobTrackerTick");
            public static readonly ushort JobTrackerInterval = RandAttribution.RegisterPhase("Pawn_JobTracker.JobTrackerTickInterval");
            public static readonly ushort Think = RandAttribution.RegisterPhase("think");
            public static readonly ushort Health = RandAttribution.RegisterPhase("Pawn_HealthTracker.HealthTick");
            public static readonly ushort HealthInterval = RandAttribution.RegisterPhase("Pawn_HealthTracker.HealthTickInterval");
            public static readonly ushort MindState = RandAttribution.RegisterPhase("Pawn_MindState.MindStateTickInterval");
            public static readonly ushort Carry = RandAttribution.RegisterPhase("Pawn_CarryTracker.CarryHandsTickInterval");
            public static readonly ushort Needs = RandAttribution.RegisterPhase("Pawn_NeedsTracker.NeedsTrackerTickInterval");
            public static readonly ushort Interactions = RandAttribution.RegisterPhase("Pawn_InteractionsTracker.InteractionsTrackerTickInterval");
            public static readonly ushort Equipment = RandAttribution.RegisterPhase("Pawn_EquipmentTracker.EquipmentTrackerTick");
        }

        private static int T2Push(ushort phase) => RandAttribution.tier2 && RandAttribution.active ? RandAttribution.PushPhase(phase, true) : -1;

        public static void PatherPrefix(out int __state) => __state = T2Push(T2.Pather);
        public static void VerbsPrefix(out int __state) => __state = T2Push(T2.Verbs);
        public static void StancePrefix(out int __state) => __state = T2Push(T2.Stance);
        public static void JobTrackerPrefix(out int __state) => __state = T2Push(T2.JobTracker);
        public static void JobTrackerIntervalPrefix(out int __state) => __state = T2Push(T2.JobTrackerInterval);
        public static void ThinkPrefix(out int __state) => __state = T2Push(T2.Think);
        public static void HealthPrefix(out int __state) => __state = T2Push(T2.Health);
        public static void HealthIntervalPrefix(out int __state) => __state = T2Push(T2.HealthInterval);
        public static void MindStatePrefix(out int __state) => __state = T2Push(T2.MindState);
        public static void CarryPrefix(out int __state) => __state = T2Push(T2.Carry);
        public static void NeedsPrefix(out int __state) => __state = T2Push(T2.Needs);
        public static void InteractionsPrefix(out int __state) => __state = T2Push(T2.Interactions);
        public static void EquipmentPrefix(out int __state) => __state = T2Push(T2.Equipment);

        public static void DriverPrefix(JobDriver __instance, out int __state) =>
            __state = RandAttribution.tier2 && RandAttribution.active ? RandAttribution.PushPhase(RandAttribution.PhaseOf(__instance.GetType()), true) : -1;

        public static void CompPrefix(ThingComp __instance, out int __state) =>
            __state = RandAttribution.tier2 && RandAttribution.active ? RandAttribution.PushPhase(RandAttribution.PhaseOf(__instance.GetType(), "comp:"), true) : -1;
    }
}
