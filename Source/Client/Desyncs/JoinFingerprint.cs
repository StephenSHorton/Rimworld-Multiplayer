using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// This process's fingerprint: what the simulation depends on that neither the save nor the commands carry.
    /// The server compares a joiner's with the host's and refuses the join, naming the rows, when they differ;
    /// after every join point each game sends it again. Rows that can only match once the host's save is loaded
    /// (prices, which settings can change; patches some mods apply on load) are warn rows: at the door they're
    /// only logged, and after a join point, when both games have loaded the same save, every row is compared. Desync-07/08 was such a row: the weapon price cache Blake's
    /// game built at its main menu held different prices than the host's.
    ///
    /// Mods add rows with <see cref="Register"/> (Rimkit adds its sim settings and registered caches).
    /// </summary>
    public static class JoinFingerprint
    {
        private static readonly List<(string source, Func<IEnumerable<(string name, string value)>> rows)> providers = new();

        /// <summary>Rows a mod adds. Names must be the same in every process; values must not depend on the UI.</summary>
        public static void Register(string source, Func<IEnumerable<(string name, string value)>> rows)
        {
            providers.RemoveAll(p => p.source == source);
            providers.Add((source, rows));
        }

        private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        public static FingerprintRow[] Build()
        {
            var rows = new List<FingerprintRow>();
            void Add(string name, string value, bool warn = false) => rows.Add(new FingerprintRow { name = name, value = value ?? "", warn = warn });

            void Try(string what, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Add(what, "failed: " + e.GetType().Name, warn: true);
                }
            }

            Try("Sync handlers", () =>
            {
                ulong h = 0;
                foreach (var handler in Sync.handlers)
                    h = SyncLanes.Mix(h, FaultSignature.Hash($"{handler.GetType().Name}:{handler}"));
                Add("Sync handlers", $"{Sync.handlers.Count} handlers, {h:X16}");
            });

            Try("Harmony patches", () =>
            {
                var patched = Harmony.GetAllPatchedMethods()
                    .Select(m =>
                    {
                        var info = Harmony.GetPatchInfo(m);
                        var owners = info == null ? "" : string.Join(",", info.Owners.OrderBy(o => o, StringComparer.Ordinal));
                        return $"{m.DeclaringType?.FullName}.{m.Name}|{owners}";
                    })
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();
                ulong h = 0;
                foreach (var p in patched)
                    h = SyncLanes.Mix(h, FaultSignature.Hash(p));
                if (Prefs.DevMode)
                {
                    // Dev mode: the list itself, so two players' files show which methods differ
                    try
                    {
                        System.IO.Directory.CreateDirectory(Multiplayer.DesyncsDir);
                        System.IO.File.WriteAllLines(System.IO.Path.Combine(Multiplayer.DesyncsDir, $"harmony-{Multiplayer.username}.txt"), patched);
                    }
                    catch (System.IO.IOException)
                    {
                    }
                }

                // Some mods patch when a game loads, and a joiner is still at the menu: compared after the join point
                Add("Harmony patches", $"{patched.Count} methods, {h:X16}", warn: true);
            });

            Try("Market values", () =>
            {
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (def.category != ThingCategory.Item && def.category != ThingCategory.Building) continue;
                    float value;
                    try
                    {
                        value = def.BaseMarketValue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    // Settings can change prices, and a joiner only gets the host's settings with the save
                    Add($"Market value: {def.defName}", F(value), warn: true);
                }
            });

            // The generators' pair caches keep the price from when they were built (Desync-07/08)
            Try("Weapon price cache", () => AddPairs("Weapon price cache", PawnWeaponGenerator.allWeaponPairs, Add));
            Try("Apparel price cache", () => AddPairs("Apparel price cache", PawnApparelGenerator.allApparelPairs, Add));

            Add("Runtime", $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}", warn: true);
            Add("Language", LanguageDatabase.activeLanguage?.folderName ?? "?", warn: true);

            foreach (var (source, provider) in providers)
                Try(source, () =>
                {
                    foreach (var (name, value) in provider())
                        Add($"{source}: {name}", value);
                });

            return rows.ToArray();
        }

        private static void AddPairs(string name, List<ThingStuffPair> pairs, Action<string, string, bool> add)
        {
            if (pairs == null)
            {
                add(name, "not built yet", true);
                return;
            }

            ulong h = 0;
            foreach (var p in pairs.OrderBy(p => p.thing?.defName, StringComparer.Ordinal).ThenBy(p => p.stuff?.defName ?? "", StringComparer.Ordinal))
                h = SyncLanes.Mix(h, FaultSignature.Hash($"{p.thing?.defName}/{p.stuff?.defName}={F(p.Price)}x{F(p.commonalityMultiplier)}"));
            add(name, $"{pairs.Count} pairs, {h:X16}", true);
        }

        /// <summary>
        /// The sections of this game's last join point save, as XML. Every game saves and reloads at a join point, so
        /// in two games in step they are the same; their hashes go with the fingerprint after the join point, and
        /// the referee names a section that differs (Desync-16/17: the games split seconds after a join point).
        /// </summary>
        private static Dictionary<string, string> saveSections = new();
        private static string saveSectionsAt;

        // Text keeps whichever line endings its game wrote, and archived messages keep the real time and frame
        // they were shown at on this game
        private static readonly System.Text.RegularExpressions.Regex PerPlayer =
            new("<starting(Time|Frame)>[^<]*</starting(Time|Frame)>", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string ForCompare(string xml) => PerPlayer.Replace(xml.Replace("\r", ""), "");

        public static void HashSave(XmlNode gameNode)
        {
            var sections = new Dictionary<string, string>();
            try
            {
                void AddSection(string name, XmlNode node)
                {
                    // Components and the world are lists of unrelated parts; name each one
                    if (node.Name is "components" or "world" && node.HasChildNodes)
                    {
                        int i = 0;
                        foreach (XmlNode child in node.ChildNodes)
                            sections[$"{name}/{child.Attributes?["Class"]?.Value ?? child.Name}#{i++}"] = child.OuterXml;
                        return;
                    }
                    sections[name] = node.OuterXml;
                }

                // Per player: play time, the camera, the letters on screen (dismissing one isn't synced)
                foreach (XmlNode child in gameNode.ChildNodes)
                    if (child.Name is not ("maps" or "currentMapIndex" or "info" or "cameraMap" or "letterStack"))
                        AddSection("game/" + child.Name, child);

                foreach (XmlNode map in gameNode["maps"]?.ChildNodes ?? (System.Collections.IEnumerable)Array.Empty<XmlNode>())
                {
                    var id = map["uniqueID"]?.InnerText ?? "?";
                    foreach (XmlNode child in map.ChildNodes)
                        if (child.Name != "rememberedCameraPos")
                            AddSection($"map {id}/{child.Name}", child);
                }
            }
            catch (Exception e)
            {
                MpLog.Error($"Couldn't hash the join point save: {e}");
            }
            saveSections = sections;
            // Two games' sections only compare if they saved at the same point (2026-10-19: one join point's world
            // pawns were two ticks apart)
            saveSectionsAt = $"world tick {Multiplayer.AsyncWorldTime?.worldTicks}, map ticks {string.Join(",", Find.Maps.Select(m => m.AsyncTime().mapTicks))}";
        }

        /// <summary>Called when the referee reports this game's fingerprint differs after a join point.</summary>
        public static void OnFingerprintDiffers(string message)
        {
            if (!message.Contains("Join point save")) return;
            try
            {
                var names = saveSections.Keys.Where(k => message.Contains(k)).ToList();
                if (names.Count == 0) names = saveSections.Keys.ToList();
                var text = string.Join("\n\n", names.Select(n => $"=== {n}\n{saveSections[n]}"));
                System.IO.Directory.CreateDirectory(Multiplayer.DesyncsDir);
                var path = System.IO.Path.Combine(Multiplayer.DesyncsDir, $"joinsave-{Multiplayer.username}-{Find.TickManager.TicksGame}.xml.gz");
                System.IO.File.WriteAllBytes(path, LeafDiff.Gzip(text));
                MpLog.Warn($"The join point saves differ ({string.Join(", ", names.Take(8))}); this game's sections ({saveSectionsAt}) are in {path}");
            }
            catch (Exception e)
            {
                MpLog.Error($"Couldn't write the join point save sections: {e}");
            }
        }

        /// <summary>
        /// After a join point: send this game's fingerprint again (the server compares it with the host's), and check
        /// that saving and reloading changed none of this game's lanes.
        /// </summary>
        public static void AfterJoinPoint(Dictionary<string, ulong> lanesBefore, Dictionary<string, string> leavesBefore)
        {
            try
            {
                var rows = Build().Concat(saveSections.Select(kv => new FingerprintRow
                {
                    name = "Join point save: " + kv.Key,
                    value = FaultSignature.Hash(ForCompare(kv.Value)).ToString("X8"),
                    warn = true
                })).ToArray();
                Multiplayer.Client?.SendFragmented(new ClientFingerprintPacket { rows = rows }.Serialize());
            }
            catch (Exception e)
            {
                MpLog.Error($"Couldn't send the fingerprint after the join point: {e}");
            }

            if (lanesBefore == null) return;

            var after = SyncLanes.ComputeAll();
            var changed = lanesBefore.Where(kv => after.TryGetValue(kv.Key, out var v) && v != kv.Value).Select(kv => kv.Key).ToList();
            if (changed.Count == 0)
            {
                MpLog.Log($"Join point self-check: all {after.Count} lanes are the same after saving and reloading");
                return;
            }

            var details = new List<string>();
            foreach (var key in changed.ToList())
                if (leavesBefore != null && leavesBefore.TryGetValue(key, out var before))
                {
                    var parts = key.Split(new[] { " / " }, StringSplitOptions.None);
                    var mapId = int.Parse(parts[0].Substring("map ".Length));
                    var map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
                    if (map == null) continue;
                    var afterLeaves = string.Join("\n", SyncLanes.Leaves(map, parts[1]));
                    if (OnlyReloadEffects(before, afterLeaves, mapTicksBefore.TryGetValue(mapId, out var savedAt) ? savedAt : null))
                    {
                        changed.Remove(key);
                        continue;
                    }
                    var diff = LeafDiff.Diff(before, afterLeaves, "before", "after");
                    details.Add($"{key}: {string.Join(" | ", diff.Split('\n').Take(5))}");
                }

            if (changed.Count == 0)
            {
                MpLog.Log($"Join point self-check: all {after.Count} lanes are the same after saving and reloading (plant growth rounded by the save, cold plants made leafless again on load)");
                return;
            }

            MpLog.Error($"Join point self-check: saving and reloading changed {string.Join(", ", changed)}. Some state the " +
                        $"simulation uses isn't saved (or is rebuilt differently), so a joiner's game will differ there.\n{string.Join("\n", details)}");
        }

        /// <summary>
        /// Changes every game makes the same way, since every game reloads at a join point. True when nothing else
        /// differs:
        /// - saves store plant growth as a byte, rounded up to the next 1/255;
        /// - loading runs Plant.PostMapInit, which makes a plant that's cold now leafless again at the map's saved
        ///   tick, while the live game's plants keep the tick of their last load or long tick. Every load also ticks
        ///   each map once (LoadPatch), so a map left paused since the host's reload is one tick past it: the first
        ///   join point after a host at tick 8 showed every cold plant as leafless 8 (before) vs 9 (after), 2026-10-20
        /// </summary>
        private static bool OnlyReloadEffects(string before, string after, int? reloadTick)
        {
            var a = Index(before);
            var b = Index(after);
            if (a.Count != b.Count) return false;

            foreach (var pair in a)
            {
                if (!b.TryGetValue(pair.Key, out var other)) return false;
                if (other == pair.Value) continue;

                var fa = LeafDiff.Split(pair.Value);
                var fb = LeafDiff.Split(other);
                foreach (var key in fa.Keys.Union(fb.Keys))
                {
                    fa.TryGetValue(key, out var va);
                    fb.TryGetValue(key, out var vb);
                    if (va == vb) continue;
                    if (key == "leafless" && reloadTick is { } tick && vb == tick.ToString(CultureInfo.InvariantCulture)) continue;
                    if (key != "growth" || !TryFloat(va, out var x) || !TryFloat(vb, out var y) || Math.Abs(x - y) > 1f / 255f + 1e-6f)
                        return false;
                }
            }

            return true;
        }

        private static Dictionary<string, string> Index(string leaves)
        {
            var result = new Dictionary<string, string>();
            foreach (var line in leaves.Split('\n'))
                if (line.Length > 0)
                    result[line.Split(' ')[0]] = line;
            return result;
        }

        // "0.8482426/3F59266D" -> 0.8482426
        private static bool TryFloat(string leafValue, out float value)
        {
            value = 0;
            if (leafValue == null) return false;
            var slash = leafValue.IndexOf('/');
            return float.TryParse(slash < 0 ? leafValue : leafValue.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // Each map's tick when the join point saved it, which its reload runs PostMapInit at
        private static Dictionary<int, int> mapTicksBefore = new();

        /// <summary>The lanes and entity leaves before a join point's save, for <see cref="AfterJoinPoint"/>.</summary>
        public static (Dictionary<string, ulong> lanes, Dictionary<string, string> leaves) BeforeJoinPoint()
        {
            try
            {
                var lanes = SyncLanes.ComputeAll();
                mapTicksBefore = Find.Maps.ToDictionary(m => m.uniqueID, m => m.AsyncTime().mapTicks);
                var leaves = new Dictionary<string, string>();
                foreach (var map in Find.Maps)
                    foreach (var lane in new[] { "pawns", "things", "plants" })
                        leaves[$"map {map.uniqueID} / {lane}"] = string.Join("\n", SyncLanes.Leaves(map, lane));
                return (lanes, leaves);
            }
            catch (Exception e)
            {
                MpLog.Error($"Join point self-check failed before saving: {e}");
                return (null, null);
            }
        }
    }
}
