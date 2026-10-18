using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Multiplayer.Client.Util;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// State checksums ("lanes") for the sync opinion. Each lane covers one part of the game (a map's terrain, its
    /// pawns, the world's factions, ...), so when two games stop matching the report says which part and which map,
    /// not just "wrong random state". Random states only show a difference once it changes how many numbers are
    /// drawn; a lane shows it at the next checkpoint.
    ///
    /// Rules for a lane: read saved, simulation-owned fields only (no stat caches, no labels, nothing the UI
    /// recomputes); never draw random numbers or change anything; hash floats by their bits; never use runtime
    /// GetHashCode (randomized per process on CoreCLR) - use ids, short hashes and <see cref="Mix"/>; and combine
    /// collections with a sum, so the order of a list never matters (the host's self-check compares the game before
    /// and after a reload, which can reorder lists).
    ///
    /// Mods add lanes with <see cref="RegisterMapLane"/> and <see cref="RegisterWorldLane"/>. Every lane starts
    /// SOFT (reported, play continues); a lane becomes HARD once it has run clean in long test sessions.
    /// </summary>
    public static class SyncLanes
    {
        public const int CheckpointInterval = 60;
        private const double BudgetMs = 4;

        // cadence: computed every cadence-th checkpoint, lanes staggered so the slow ones don't share a checkpoint
        private record MapLane(string name, Func<Map, ulong> hash, bool hard, int cadence);
        private record WorldLane(string name, Func<ulong> hash, bool hard, int cadence);

        /// <summary>For whole-map grids (250x250 cells and up): every fifth checkpoint, about 5 s of game time.</summary>
        public const int GridCadence = 5;

        private static readonly List<MapLane> mapLanes = new();
        private static readonly List<WorldLane> worldLanes = new();
        private static readonly HashSet<string> failedLanes = new();
        private static readonly Stopwatch watch = new();
        private static readonly Stopwatch sinceBudgetWarning = Stopwatch.StartNew();

        public static double LastCheckpointMs { get; private set; }

        private const int StatEvery = 200;
        private static int statCount;
        private static double statSum, statMax;

        static SyncLanes()
        {
            RegisterMapLane("terrain", TerrainLane, cadence: GridCadence);
            RegisterMapLane("roof", RoofLane, cadence: GridCadence);
            RegisterMapLane("fog", FogLane, cadence: GridCadence);
            RegisterMapLane("snow", SnowLane, cadence: GridCadence);
            RegisterMapLane("gas", GasLane, cadence: GridCadence);
            RegisterMapLane("things", ThingsLane);
            RegisterMapLane("plants", PlantsLane, cadence: 2);
            RegisterMapLane("pawns", PawnsLane);
            RegisterMapLane("zones", ZonesLane, cadence: GridCadence);
            RegisterMapLane("designations", DesignationsLane, cadence: GridCadence);
            RegisterMapLane("time", MapTimeLane);

            RegisterWorldLane("factions", FactionsLane);
            RegisterWorldLane("quests", QuestsLane);
            RegisterWorldLane("worldobjects", WorldObjectsLane);
            RegisterWorldLane("worldpawns", WorldPawnsLane);
            RegisterWorldLane("time", WorldTimeLane);
        }

        /// <summary>A lane computed for every map. The name must be unique and the same on every game.</summary>
        public static void RegisterMapLane(string name, Func<Map, ulong> hash, bool hard = false, int cadence = 1)
        {
            mapLanes.RemoveAll(l => l.name == name);
            mapLanes.Add(new MapLane(name, hash, hard, Math.Max(1, cadence)));
        }

        /// <summary>A lane computed once for the world (map id -1).</summary>
        public static void RegisterWorldLane(string name, Func<ulong> hash, bool hard = false, int cadence = 1)
        {
            worldLanes.RemoveAll(l => l.name == name);
            worldLanes.Add(new WorldLane(name, hash, hard, Math.Max(1, cadence)));
        }

        // Lane i of a cadence-n group runs when (checkpoint + i) % n == 0
        private static bool Due(int tick, int index, int cadence) =>
            cadence <= 1 || (tick / CheckpointInterval + index) % cadence == 0;

        /// <summary>Every lane at this tick whatever its cadence; for the report and the host's self-check.</summary>
        public static bool computeAll;

        /// <summary>Every lane at this tick. Called at the same point of the tick on every game.</summary>
        public static void Compute(int tick, List<LaneHash> into)
        {
            watch.Restart();

            var maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                var map = maps[m];
                for (int i = 0; i < mapLanes.Count; i++)
                {
                    var lane = mapLanes[i];
                    if (!computeAll && !Due(tick, i + m, lane.cadence)) continue;
                    into.Add(new LaneHash { tick = tick, mapId = map.uniqueID, lane = lane.name, hash = Safe(lane.name, () => lane.hash(map)), hard = lane.hard });
                }
            }

            for (int i = 0; i < worldLanes.Count; i++)
            {
                var lane = worldLanes[i];
                if (!computeAll && !Due(tick, i, lane.cadence)) continue;
                into.Add(new LaneHash { tick = tick, mapId = -1, lane = lane.name, hash = Safe(lane.name, lane.hash), hard = lane.hard });
            }

            watch.Stop();
            LastCheckpointMs = watch.Elapsed.TotalMilliseconds;
            statCount++;
            statSum += LastCheckpointMs;
            statMax = Math.Max(statMax, LastCheckpointMs);
            if (statCount == StatEvery)
            {
                MpLog.Log($"Sync lanes: {statSum / statCount:F2} ms average, {statMax:F2} ms max over {statCount} checkpoints ({mapLanes.Count} map lanes x {maps.Count} maps, {worldLanes.Count} world lanes); " +
                          "slowest: " + string.Join(", ", laneMaxMs.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value:F2} ms")));
                laneMaxMs.Clear();
                statCount = 0;
                statSum = statMax = 0;
            }
            if (LastCheckpointMs > BudgetMs && sinceBudgetWarning.ElapsedMilliseconds > 60_000)
            {
                sinceBudgetWarning.Restart();
                MpLog.Warn($"Sync lanes took {LastCheckpointMs:F1} ms at tick {tick} (budget {BudgetMs} ms)");
            }
        }

        /// <summary>Every lane for one map, by name. For the host's self-check and the desync report.</summary>
        public static Dictionary<string, ulong> ComputeAll()
        {
            var list = new List<LaneHash>();
            computeAll = true;
            try
            {
                Compute(TickPatch.Timer, list);
            }
            finally
            {
                computeAll = false;
            }
            var result = new Dictionary<string, ulong>();
            foreach (var lane in list)
                result[(lane.mapId == -1 ? "world" : $"map {lane.mapId}") + " / " + lane.lane] = lane.hash;
            return result;
        }

        private static readonly Dictionary<string, double> laneMaxMs = new();

        private static ulong Safe(string name, Func<ulong> hash)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                return hash();
            }
            catch (Exception e)
            {
                if (failedLanes.Add(name))
                    MpLog.Error($"Sync lane {name} failed: {e}");
                return 0xBAD;
            }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                if (!laneMaxMs.TryGetValue(name, out var max) || ms > max)
                    laneMaxMs[name] = ms;
            }
        }

        // splitmix64's finalizer: every input bit affects every output bit
        public static ulong Mix(ulong x)
        {
            unchecked
            {
                x += 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                return x ^ (x >> 31);
            }
        }

        public static ulong Mix(ulong a, ulong b) => Mix(a ^ Mix(b));
        public static ulong Mix(ulong a, int b) => Mix(a, (ulong)(uint)b);
        public static ulong Mix(ulong a, float b) => Mix(a, (ulong)(uint)BitConverter.SingleToInt32Bits(b));
        public static ulong Mix(ulong a, Def def) => Mix(a, def == null ? 0 : def.shortHash + 1);
        public static ulong Mix(ulong a, IntVec3 c) => Mix(Mix(a, c.x), c.z);

        // --- Map lanes ---

        private static ulong DefGrid<T>(T[] grid) where T : Def
        {
            if (grid == null) return 0;
            ulong h = 0xCBF29CE484222325UL;
            unchecked
            {
                for (int i = 0; i < grid.Length; i++)
                    h = (h ^ (ulong)(grid[i]?.shortHash ?? 0)) * 0x100000001B3UL;
            }
            return Mix(h);
        }

        private static ulong TerrainLane(Map map)
        {
            var grid = map.terrainGrid;
            return Mix(Mix(DefGrid(grid.topGrid), DefGrid(grid.underGrid)), DefGrid(grid.foundationGrid));
        }

        private static ulong RoofLane(Map map) => DefGrid(map.roofGrid.roofGrid);

        private static ulong FogLane(Map map)
        {
            var fog = map.fogGrid;
            int n = map.cellIndices.NumGridCells;
            ulong h = 0xCBF29CE484222325UL;
            unchecked
            {
                for (int i = 0; i < n; i++)
                    if (fog.IsFogged(i))
                        h = (h ^ (ulong)i) * 0x100000001B3UL;
            }
            return Mix(h);
        }

        private static ulong SnowLane(Map map)
        {
            var depth = map.snowGrid.DepthGrid_Unsafe;
            ulong h = 0xCBF29CE484222325UL;
            unchecked
            {
                for (int i = 0; i < depth.Length; i++)
                    h = (h ^ (ulong)(uint)BitConverter.SingleToInt32Bits(depth[i])) * 0x100000001B3UL;
            }
            return Mix(h);
        }

        private static ulong GasLane(Map map)
        {
            var gas = map.gasGrid?.gasDensity;
            if (gas == null) return 0;
            ulong h = 0xCBF29CE484222325UL;
            unchecked
            {
                for (int i = 0; i < gas.Length; i++)
                    h = (h ^ gas[i]) * 0x100000001B3UL;
            }
            return Mix(h);
        }

        private static ulong ThingHash(Thing t)
        {
            ulong h = Mix((ulong)(uint)t.thingIDNumber);
            h = Mix(h, t.def);
            h = Mix(h, t.Stuff);
            h = Mix(h, t.positionInt);
            h = Mix(h, LaneRotation(t));
            h = Mix(h, t.stackCount);
            h = Mix(h, t.hitPointsInt);
            return h;
        }

        /// <summary>
        /// A 1x1 door's rotation is set while it is drawn (Building_Door.DoorPreDraw), so it follows each player's
        /// camera; the simulation never reads it for those doors. Left out of the lane.
        /// </summary>
        private static int LaneRotation(Thing t) =>
            t is Building_Door && t.def.size == IntVec2.One ? 0 : t.rotationInt.AsInt;

        private static ulong ThingsLane(Map map)
        {
            ulong sum = 0;
            var things = map.listerThings.AllThings;
            unchecked
            {
                for (int i = 0; i < things.Count; i++)
                    sum += ThingHash(things[i]);
            }
            return Mix(sum, things.Count);
        }

        private static ulong PlantsLane(Map map)
        {
            ulong sum = 0;
            var plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant);
            unchecked
            {
                for (int i = 0; i < plants.Count; i++)
                    if (plants[i] is Plant p)
                        sum += Mix(Mix(Mix((ulong)(uint)p.thingIDNumber, p.growthInt), p.unlitTicks), p.madeLeaflessTick);
            }
            return Mix(sum, plants.Count);
        }

        private static ulong PawnHash(Pawn p)
        {
            ulong h = Mix((ulong)(uint)p.thingIDNumber);
            h = Mix(h, p.positionInt);
            h = Mix(h, p.Faction?.loadID ?? -1);
            h = Mix(h, p.jobs?.curJob?.def);
            h = Mix(h, p.jobs?.curJob?.loadID ?? -1);
            h = Mix(h, p.mindState?.mentalStateHandler?.CurStateDef);
            h = Mix(h, p.drafter?.Drafted == true ? 1 : 0);
            h = Mix(h, p.carryTracker?.CarriedThing?.thingIDNumber ?? -1);
            h = Mix(h, p.inventory?.innerContainer?.Count ?? -1);
            h = Mix(h, p.equipment?.Primary?.thingIDNumber ?? -1);

            ulong hediffs = 0;
            var list = p.health?.hediffSet?.hediffs;
            if (list != null)
                unchecked
                {
                    foreach (var hd in list)
                        hediffs += Mix(Mix(Mix((ulong)(uint)hd.loadID, hd.def), hd.severityInt), hd.Part?.Index ?? -1);
                }
            h = Mix(h, hediffs);

            ulong needs = 0;
            var allNeeds = p.needs?.AllNeeds;
            if (allNeeds != null)
                unchecked
                {
                    foreach (var need in allNeeds)
                        needs += Mix(Mix(0, need.def), need.curLevelInt);
                }
            return Mix(h, needs);
        }

        private static ulong PawnsLane(Map map)
        {
            ulong sum = 0;
            var pawns = map.mapPawns.AllPawnsSpawned;
            unchecked
            {
                for (int i = 0; i < pawns.Count; i++)
                    sum += PawnHash(pawns[i]);
            }
            return Mix(sum, pawns.Count);
        }

        private static ulong ZonesLane(Map map)
        {
            ulong sum = 0;
            unchecked
            {
                foreach (var zone in map.zoneManager.AllZones)
                {
                    ulong cells = 0;
                    foreach (var c in zone.cells)
                        cells += Mix(Mix(0, c));
                    sum += Mix(Mix((ulong)(uint)zone.ID, cells), zone.cells.Count);
                }

                foreach (var area in map.areaManager.AllAreas)
                {
                    ulong cells = 0;
                    var arr = area.innerGrid.arr;
                    for (int i = 0; i < arr.Length; i++)
                        if (arr[i])
                            cells += Mix((ulong)i);
                    sum += Mix(Mix(0xA2EA, area.ID), cells);
                }
            }
            return sum;
        }

        private static ulong DesignationsLane(Map map)
        {
            ulong sum = 0;
            unchecked
            {
                foreach (var pair in map.designationManager.designationsByDef)
                    foreach (var d in pair.Value)
                    {
                        ulong h = Mix(0, d.def);
                        h = d.target.HasThing ? Mix(h, d.target.Thing.thingIDNumber) : Mix(h, d.target.Cell);
                        sum += h;
                    }
            }
            return sum;
        }

        private static ulong MapTimeLane(Map map)
        {
            var comp = map.AsyncTime();
            if (comp == null) return 0;
            return Mix(Mix((ulong)(uint)comp.mapTicks, (int)comp.DesiredTimeSpeed), comp.randState);
        }

        // --- Leaves: the per-entity parts of the entity lanes, for finding what differs ---

        /// <summary>
        /// One line per entity of an entity lane (things, plants, pawns), sorted by id, with each part hashed
        /// separately, so diffing two games' output names the entity and the part that differs.
        /// </summary>
        public static List<string> Leaves(Map map, string lane)
        {
            var lines = new List<(int id, string line)>();
            switch (lane)
            {
                case "things":
                    foreach (var t in map.listerThings.AllThings)
                        lines.Add((t.thingIDNumber, $"{t.ThingID} def={t.def?.defName} stuff={t.Stuff?.defName} pos={t.positionInt} rot={LaneRotation(t)} stack={t.stackCount} hp={t.hitPointsInt}"));
                    break;
                case "plants":
                    foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
                        if (t is Plant p)
                            lines.Add((p.thingIDNumber, $"{p.ThingID} growth={Bits(p.growthInt)} unlit={p.unlitTicks} leafless={p.madeLeaflessTick}"));
                    break;
                case "pawns":
                    foreach (var p in map.mapPawns.AllPawnsSpawned)
                    {
                        var hediffs = p.health?.hediffSet?.hediffs == null ? "" :
                            string.Join(",", p.health.hediffSet.hediffs.Select(h => $"{h.def?.defName}#{h.loadID}@{h.Part?.Index ?? -1}={Bits(h.severityInt)}").OrderBy(x => x, StringComparer.Ordinal));
                        var needs = p.needs?.AllNeeds == null ? "" :
                            string.Join(",", p.needs.AllNeeds.Select(n => $"{n.def?.defName}={Bits(n.curLevelInt)}").OrderBy(x => x, StringComparer.Ordinal));
                        lines.Add((p.thingIDNumber,
                            $"{p.ThingID} pos={p.positionInt} faction={p.Faction?.loadID ?? -1} job={p.jobs?.curJob?.def?.defName}#{p.jobs?.curJob?.loadID ?? -1} " +
                            $"mental={p.mindState?.mentalStateHandler?.CurStateDef?.defName} drafted={p.drafter?.Drafted == true} " +
                            $"carry={p.carryTracker?.CarriedThing?.ThingID} inv={p.inventory?.innerContainer?.Count ?? -1} eq={p.equipment?.Primary?.ThingID} " +
                            $"hediffs=[{hediffs}] needs=[{needs}]"));
                    }
                    break;
                case "terrain":
                {
                    var tg = map.terrainGrid;
                    for (int i = 0; i < tg.topGrid.Length; i++)
                        lines.Add((i, $"c{i} top={tg.topGrid[i]?.defName} under={tg.underGrid?[i]?.defName} foundation={tg.foundationGrid?[i]?.defName}"));
                    break;
                }
                case "roof":
                {
                    var roofs = map.roofGrid.roofGrid;
                    for (int i = 0; i < roofs.Length; i++)
                        if (roofs[i] != null)
                            lines.Add((i, $"c{i} roof={roofs[i].defName}"));
                    break;
                }
                case "fog":
                    for (int i = 0; i < map.cellIndices.NumGridCells; i++)
                        if (map.fogGrid.IsFogged(i))
                            lines.Add((i, $"c{i} fogged"));
                    break;
                case "designations":
                {
                    foreach (var pair in map.designationManager.designationsByDef)
                        foreach (var d in pair.Value)
                            lines.Add((0, $"{d.def?.defName}@{(d.target.HasThing ? d.target.Thing.ThingID : d.target.Cell.x + "," + d.target.Cell.z)} present"));
                    lines.Sort((a, b) => string.CompareOrdinal(a.line, b.line));
                    return lines.Select(l => l.line).ToList();
                }
                case "zones":
                    foreach (var zone in map.zoneManager.AllZones)
                        lines.Add((zone.ID, $"zone{zone.ID} type={zone.GetType().Name} cells={zone.cells.Count}"));
                    foreach (var area in map.areaManager.AllAreas)
                        lines.Add((1_000_000 + area.ID, $"area{area.ID} type={area.GetType().Name} cells={area.TrueCount}"));
                    break;
                case "thoughts":
                    // Diagnosis: memories and the cached situational thoughts as they are, nothing recalculated
                    foreach (var p in map.mapPawns.AllPawnsSpawned)
                    {
                        var handler = p.needs?.mood?.thoughts;
                        if (handler == null) continue;
                        var memories = handler.memories?.Memories == null ? "" :
                            string.Join(",", handler.memories.Memories.Select(m => $"{m.def?.defName}@{m.CurStageIndex}#{m.age}").OrderBy(x => x, StringComparer.Ordinal));
                        var situational = string.Join(",", handler.situational.cachedThoughts
                            .Select(t => $"{t.def?.defName}@{t.CurStageIndex}").OrderBy(x => x, StringComparer.Ordinal));
                        lines.Add((p.thingIDNumber, $"{p.ThingID} dirty={handler.situational.thoughtsDirty} memories=[{memories}] situational=[{situational}]"));
                    }
                    break;
                case "beauty":
                    // Diagnosis only: these calls may fill stat caches, so it runs only from the dump below
                    foreach (var p in map.mapPawns.AllPawnsSpawned)
                        if (p.needs?.beauty != null)
                            lines.Add((p.thingIDNumber, $"{p.ThingID} instantBeauty={Bits(p.needs.beauty.CurrentInstantBeauty())}"));
                    foreach (var t in map.listerThings.AllThings)
                    {
                        if (!BeautyUtility.BeautyRelevant(t.def.category)) continue;
                        var b = t.GetStatValue(StatDefOf.Beauty);
                        var bo = t.def.StatBaseDefined(StatDefOf.BeautyOutdoors) ? t.GetStatValue(StatDefOf.BeautyOutdoors) : 0f;
                        if (b == 0f && bo == 0f) continue;
                        var sg = t.GetSlotGroup();
                        lines.Add((t.thingIDNumber, $"{t.ThingID} pos={t.positionInt} beauty={Bits(b)} outdoors={Bits(bo)} slot={sg?.parent?.GetType().Name}:{sg?.parent?.IgnoreStoredThingsBeauty}"));
                    }
                    break;
            }

            lines.Sort((a, b) => a.id.CompareTo(b.id));
            return lines.Select(l => l.line).ToList();
        }

        private static string Bits(float f) => $"{f:R}/{BitConverter.SingleToInt32Bits(f):X8}";

        /// <summary>
        /// While the file <c>MpLaneDump.flag</c> is in the save data folder, every game writes its entity leaves
        /// every <see cref="DumpEvery"/> timer ticks to MpDesyncs/lanes-[player]-[tick].txt, for diffing.
        /// </summary>
        public const int DumpEvery = 1200;

        private static int dumpEvery = -1;

        public static void DumpLeavesIfFlagged(int tick)
        {
            if (dumpEvery == -1)
            {
                // The flag file may hold the interval in timer ticks
                var flag = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "MpLaneDump.flag");
                dumpEvery = !System.IO.File.Exists(flag) ? 0 :
                    int.TryParse(System.IO.File.ReadAllText(flag).Trim(), out var every) && every > 0 ? every : DumpEvery;
            }

            if (dumpEvery == 0 || tick % dumpEvery != 0) return;

            var sb = new System.Text.StringBuilder();
            foreach (var map in Find.Maps)
                foreach (var lane in new[] { "pawns", "plants", "things", "beauty", "thoughts" })
                {
                    sb.AppendLine($"== map {map.uniqueID} / {lane} at timer tick {tick}, game tick {Find.TickManager.TicksGame}");
                    foreach (var line in Leaves(map, lane))
                        sb.AppendLine(line);
                }

            System.IO.Directory.CreateDirectory(Multiplayer.DesyncsDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(Multiplayer.DesyncsDir, $"lanes-{Multiplayer.username}-{tick}.txt"), sb.ToString());
        }

        // --- World lanes ---

        private static ulong FactionsLane()
        {
            ulong sum = 0;
            unchecked
            {
                foreach (var f in Find.FactionManager.AllFactionsListForReading)
                {
                    ulong h = Mix(Mix((ulong)(uint)f.loadID, f.def), f.defeated ? 1 : 0);
                    h = Mix(h, f.leader?.thingIDNumber ?? -1);
                    ulong rel = 0;
                    foreach (var r in f.relations)
                        rel += Mix(Mix(Mix((ulong)(uint)(r.other?.loadID ?? -1), r.baseGoodwill), (int)r.kind));
                    sum += Mix(h, rel);
                }
            }
            return sum;
        }

        private static ulong QuestsLane()
        {
            ulong sum = 0;
            unchecked
            {
                foreach (var q in Find.QuestManager.QuestsListForReading)
                    sum += Mix(Mix((ulong)(uint)q.id, (int)q.State), q.root);
            }
            return sum;
        }

        private static ulong WorldObjectsLane()
        {
            ulong sum = 0;
            unchecked
            {
                foreach (var wo in Find.WorldObjects.AllWorldObjects)
                    sum += Mix(Mix(Mix((ulong)(uint)wo.ID, wo.def), wo.Tile.tileId), wo.Faction?.loadID ?? -1);
            }
            return sum;
        }

        private static ulong WorldPawnsLane()
        {
            ulong sum = 0;
            int count = 0;
            unchecked
            {
                foreach (var p in Find.WorldPawns.AllPawnsAliveOrDead)
                {
                    sum += Mix(Mix((ulong)(uint)p.thingIDNumber, p.Dead ? 1 : 0), p.Faction?.loadID ?? -1);
                    count++;
                }
            }
            return Mix(sum, count);
        }

        private static ulong WorldTimeLane()
        {
            var comp = Multiplayer.AsyncWorldTime;
            return Mix(Mix(Mix((ulong)(uint)comp.worldTicks, (int)comp.DesiredTimeSpeed), comp.randState), Find.TickManager.TicksGame);
        }
    }
}
