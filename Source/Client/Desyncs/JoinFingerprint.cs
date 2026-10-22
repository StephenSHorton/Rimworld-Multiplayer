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

            // Style weights come from these tags (StyleItemChoiceLikelihoodFromTags sums every matching tag, so a tag
            // listed twice doubles it). 2026-10-21: NoBeard weighed 1200 on the host's game and 600 on the joiner's with
            // the same random state. Warn rows: a mod can change defs when a game loads, so they're compared after
            // the join point, when the referee names any that differ to both players
            Try("Style tags", () =>
            {
                foreach (var kind in DefDatabase<PawnKindDef>.AllDefsListForReading)
                    if (!kind.styleItemTags.NullOrEmpty())
                        Add($"Style tags: kind {kind.defName}", StyleRolls.Tags(kind.styleItemTags.OrderBy(t => t.Tag, StringComparer.Ordinal).ToList()), warn: true);

                // Each kind of style item has its own database; a duplicate tag is kept, it doubles a weight
                void AddItems(string what, IEnumerable<StyleItemDef> defs)
                {
                    foreach (var def in defs)
                        Add($"Style tags: {what} {def.defName}",
                            string.Join(",", (def.styleTags ?? new List<string>()).OrderBy(t => t, StringComparer.Ordinal)), warn: true);
                }

                AddItems("beard", DefDatabase<BeardDef>.AllDefsListForReading);
                AddItems("hair", DefDatabase<HairDef>.AllDefsListForReading);
                AddItems("tattoo", DefDatabase<TattooDef>.AllDefsListForReading);
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

        private static string ForCompare(string xml, SaveNoise noise)
        {
            var text = xml.Replace("\r", "");
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(text);
                noise.Remove(doc.DocumentElement);
                text = doc.DocumentElement!.OuterXml;
            }
            catch (XmlException)
            {
            }
            return PerPlayer.Replace(text, "");
        }

        /// <summary>
        /// Parts of a join point save that differ between two games in step and that the simulation never reads as
        /// saved; left out of the section hashes only, the saves stay as they are. From the 2026-10-21 join points:
        /// - a pawn's reading and food policy is null until something reads it, and the getter then sets the
        ///   default (the database's first policy): one game's interface had read it, the other's hadn't;
        /// - a pawn's verbs are null until something reads them, and the getter makes them fresh, so a list of
        ///   never-used verbs is the same as none;
        /// - a 1x1 door's (and its blueprint's and frame's) rotation follows the camera (see SyncLanes).
        /// Later on 2026-10-21:
        /// - the apparel and drug policy the same way (curOutfit, curAssignedDrugs: the first policy);
        /// - a pawn's inventory stock entry is made with the group's default on first read (the Assign tab, and
        ///   every load fills them all), so an entry equal to the default is the same as none;
        /// - a non-player faction's mech color is set to its color on first read (drawing);
        /// - a battle's name is made on first read (the combat log tab);
        /// - the view toggles in the play settings (overlays, icons, north up, day and night on the planet) are
        ///   each player's own; autoHomeArea, autoRebuild, useWorkPriorities and the default care are kept;
        /// - a game, world or map component whose class has an attribute named ClientOnlyAttribute (Rimkit marks
        ///   components that only one game's interface fills, such as BetterArchitect.MysteryUnlockTracker) is
        ///   compared by its class only.
        /// </summary>
        private class SaveNoise
        {
            private readonly HashSet<string> defaultPolicies = new();
            private readonly HashSet<string> oneCellDoors;

            private static HashSet<string> doorDefs;
            private static readonly Dictionary<string, bool> clientOnly = new();

            public SaveNoise()
            {
                // Not DefaultReadingPolicy/DefaultFoodRestriction: they make a policy when there's none
                void AddFirst<T>(List<T> list) where T : Policy
                {
                    if (list is { Count: > 0 }) defaultPolicies.Add(list[0].GetUniqueLoadID());
                }

                AddFirst(Current.Game?.readingPolicyDatabase?.AllReadingPolicies);
                AddFirst(Current.Game?.foodRestrictionDatabase?.AllFoodRestrictions);
                AddFirst(Current.Game?.outfitDatabase?.AllOutfits);
                AddFirst(Current.Game?.drugPolicyDatabase?.AllPolicies);
                // With multifaction each faction has its own food, apparel and drug policies
                foreach (var data in Multiplayer.WorldComp?.factionData.Values ?? Enumerable.Empty<FactionWorldData>())
                {
                    AddFirst(data.foodRestrictionDatabase?.AllFoodRestrictions);
                    AddFirst(data.outfitDatabase?.AllOutfits);
                    AddFirst(data.drugPolicyDatabase?.AllPolicies);
                }

                oneCellDoors = doorDefs ??= DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(d => SyncLanes.IsOneCellDoor(d) || d.entityDefToBuild is ThingDef built && SyncLanes.IsOneCellDoor(built))
                    .Select(d => d.defName)
                    .ToHashSet();
            }

            public void Remove(XmlElement root)
            {
                var stack = new Stack<XmlElement>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var e = stack.Pop();

                    if (e.GetAttribute("Class") is { Length: > 0 } cls && IsClientOnly(cls))
                    {
                        e.RemoveAll();
                        e.SetAttribute("Class", cls);
                        e.IsEmpty = true;
                        continue;
                    }

                    switch (e.Name)
                    {
                        case "curAssignment" when e.ParentNode?.Name == "reading":
                        case "curRestriction" when e.ParentNode?.Name == "foodRestriction":
                        case "curOutfit" when e.ParentNode?.Name == "outfits":
                        case "curAssignedDrugs" when e.ParentNode?.Name == "drugs":
                            if (defaultPolicies.Contains(e.InnerText)) e.InnerText = "null";
                            continue;
                        case "stockEntries" when e.ParentNode?.Name == "inventoryStock":
                            RemoveDefaultStock(e);
                            continue;
                        case "li" when e.ParentNode?.Name == "allFactions":
                            if (e["mechColor"] is { } mechColor &&
                                DefDatabase<FactionDef>.GetNamedSilentFail(e["def"]?.InnerText ?? "") is { isPlayer: false })
                                e.RemoveChild(mechColor);
                            break;
                        case "li" when e.ParentNode?.Name == "battles":
                            if (e["battleName"] is { } battleName) e.RemoveChild(battleName);
                            break;
                        case "playSettings":
                            foreach (var toggle in e.ChildNodes.OfType<XmlElement>().Where(c => IsViewToggle(c.Name)).ToList())
                                e.RemoveChild(toggle);
                            continue;
                        case "verbs" when e.ParentNode?.Name == "verbTracker":
                            if (e.GetAttribute("IsNull") != "True" && e.ChildNodes.Cast<XmlNode>().All(IsUnusedVerb))
                            {
                                e.RemoveAll();
                                e.SetAttribute("IsNull", "True");
                            }
                            continue;
                    }

                    if (e["rot"] is { } rot && e["id"] != null && e["def"] is { } def && oneCellDoors.Contains(def.InnerText))
                        e.RemoveChild(rot);

                    foreach (XmlNode child in e.ChildNodes)
                        if (child is XmlElement element)
                            stack.Push(element);
                }
            }

            private static bool IsViewToggle(string name) =>
                name.StartsWith("show", StringComparison.Ordinal) || name is "lockNorthUp" or "usePlanetDayNightSystem";

            // Matched by name: Multiplayer can't reference the mods that mark their components
            private static bool IsClientOnly(string className)
            {
                if (!clientOnly.TryGetValue(className, out var result))
                {
                    var type = GenTypes.GetTypeInAnyAssembly(className);
                    result = type != null && type.GetCustomAttributes(true).Any(a => a.GetType().Name == "ClientOnlyAttribute");
                    clientOnly[className] = result;
                }
                return result;
            }

            // A dictionary saves as parallel keys and values lists; an entry as made by CreateDefaultEntryFor
            // (count the group's min, saved only when not 0, and its default thing) goes from both
            private static void RemoveDefaultStock(XmlElement entries)
            {
                if (entries["keys"] is not { } keys || entries["values"] is not { } values) return;
                var keyList = keys.ChildNodes.OfType<XmlElement>().ToList();
                var valueList = values.ChildNodes.OfType<XmlElement>().ToList();
                if (keyList.Count != valueList.Count) return;

                for (int i = 0; i < keyList.Count; i++)
                {
                    if (DefDatabase<InventoryStockGroupDef>.GetNamedSilentFail(keyList[i].InnerText) is not { } group) continue;
                    var count = valueList[i]["count"]?.InnerText ?? "0";
                    var thing = valueList[i]["thingDef"]?.InnerText;
                    if (count == group.min.ToString(CultureInfo.InvariantCulture) && thing == group.DefaultThingDef?.defName)
                    {
                        keys.RemoveChild(keyList[i]);
                        values.RemoveChild(valueList[i]);
                    }
                }

                // A game that had none saved <keys />, one emptied here would write <keys></keys>
                if (!keys.HasChildNodes) keys.IsEmpty = true;
                if (!values.HasChildNodes) values.IsEmpty = true;
            }

            // As made: never aimed or fired (the values a fresh Verb saves)
            private static bool IsUnusedVerb(XmlNode li)
            {
                if (li is not XmlElement { Name: "li" }) return false;
                foreach (XmlNode field in li.ChildNodes)
                {
                    var unused = field.Name switch
                    {
                        "loadID" => true,
                        "currentTarget" or "currentDestination" => field.InnerText == "(0, 0, 0)",
                        "lastShotTick" => field.InnerText == "-999999",
                        "canHitNonTargetPawnsNow" => field.InnerText == "True",
                        _ => false
                    };
                    if (!unused) return false;
                }
                return true;
            }
        }

        public static void HashSave(XmlNode gameNode)
        {
            var sections = new Dictionary<string, string>();
            try
            {
                void AddSection(string name, XmlNode node)
                {
                    // Components and the world are lists of unrelated parts; name each one, and each of the world's
                    // components (game/world/components#10/DefensivePositions.WorldData#14)
                    if (node.Name is "components" or "world" && node.HasChildNodes)
                    {
                        int i = 0;
                        foreach (XmlNode child in node.ChildNodes)
                        {
                            var childName = $"{name}/{child.Attributes?["Class"]?.Value ?? child.Name}#{i++}";
                            if (node.Name == "world" && child.Name == "components" && child.HasChildNodes)
                                AddSection(childName, child);
                            else
                                sections[childName] = child.OuterXml;
                        }
                        return;
                    }
                    // The history's archive holds the letters and messages each player was shown (and pinned); the
                    // rest of it (graph recorders, history events) is the simulation's
                    if (name == "game/history")
                    {
                        foreach (XmlNode child in node.ChildNodes)
                            if (child.Name != "archive")
                                sections[$"{name}/{child.Name}"] = child.OuterXml;
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
            clockAtHash = Clock();
            // Two games' sections only compare if they saved at the same point (2026-10-19: one join point's world
            // pawns were two ticks apart)
            saveSectionsAt = $"world tick {Multiplayer.AsyncWorldTime?.worldTicks}, map ticks {string.Join(",", Find.Maps.Select(m => m.AsyncTime().mapTicks))}";
        }

        // The world's clock and the tick manager's, live: before the join point saved, and when its save was hashed
        // (after the reload). 2026-10-21: a map's tick count left in the tick manager made the world clock jump back
        private static string clockBeforeSave = "(not recorded)";
        private static string clockAtHash = "(not recorded)";

        private static string Clock()
        {
            try
            {
                var tm = Find.TickManager;
                return tm == null ? "no tick manager" :
                    $"worldTicks={Multiplayer.AsyncWorldTime?.worldTicks} worldGameStartAbsTick={Multiplayer.AsyncWorldTime?.gameStartAbsTick} " +
                    $"ticksGameInt={tm.ticksGameInt} gameStartAbsTick={tm.gameStartAbsTick} " +
                    $"curTimeSpeed={tm.curTimeSpeed} mapTicks={string.Join(",", Find.Maps.Select(m => $"{m.uniqueID}:{m.AsyncTime()?.mapTicks}"))}";
            }
            catch (Exception e)
            {
                return $"failed: {e.GetType().Name}";
            }
        }

        /// <summary>Called when the referee reports this game's fingerprint differs after a join point.</summary>
        public static void OnFingerprintDiffers(ServerSyncVerdictPacket packet)
        {
            var message = packet.message ?? "";
            if (!message.Contains("Join point save") && packet.saveSections is not { Length: > 0 }) return;
            try
            {
                // Every differing section the referee listed (the message names six); failing that, the ones the
                // message names (the rows read "Join point save: <section>: <hash> (host) vs ...")
                var names = packet.saveSections is { Length: > 0 }
                    ? packet.saveSections.Where(saveSections.ContainsKey).ToList()
                    : saveSections.Keys.Where(k => message.Contains(k + ":")).ToList();
                if (names.Count == 0) names = saveSections.Keys.ToList();

                // Every dump also has the clocks and the tick manager's section
                const string tickManager = "game/tickManager";
                var header = $"=== clock\nbefore saving: {clockBeforeSave}\nwhen hashed (after reloading): {clockAtHash}\n\n";
                if (!names.Contains(tickManager) && saveSections.TryGetValue(tickManager, out var tmSection))
                    header += $"=== {tickManager} (for reference, not different)\n{tmSection}\n\n";
                var text = header + string.Join("\n\n", names.Select(n => $"=== {n}\n{saveSections[n]}"));
                System.IO.Directory.CreateDirectory(Multiplayer.DesyncsDir);
                var path = System.IO.Path.Combine(Multiplayer.DesyncsDir, $"joinsave-{Multiplayer.username}-{Find.TickManager.TicksGame}.xml.gz");
                System.IO.File.WriteAllBytes(path, LeafDiff.Gzip(text));
                MpLog.Warn($"The join point saves differ in {names.Count} sections ({string.Join(", ", names.Take(8))}); this game's sections ({saveSectionsAt}) are in {path}");
            }
            catch (Exception e)
            {
                MpLog.Error($"Couldn't write the join point save sections: {e}");
            }
        }

        /// <summary>The tick of the join point command being run; every game gets the command with the same tick.</summary>
        public static int joinPointTick;

        /// <summary>
        /// After a join point: send this game's fingerprint again (the server compares it with the host's from the
        /// same join point), and check that saving and reloading changed none of this game's lanes.
        /// </summary>
        public static void AfterJoinPoint(Dictionary<string, ulong> lanesBefore, Dictionary<string, string> leavesBefore)
        {
            try
            {
                var noise = new SaveNoise();
                var rows = Build().Concat(saveSections.Select(kv => new FingerprintRow
                {
                    name = "Join point save: " + kv.Key,
                    value = FaultSignature.Hash(ForCompare(kv.Value, noise)).ToString("X8"),
                    warn = true
                })).ToArray();
                Multiplayer.Client?.SendFragmented(new ClientFingerprintPacket { joinPoint = joinPointTick, rows = rows }.Serialize());
            }
            catch (Exception e)
            {
                MpLog.Error($"Couldn't send the fingerprint after the join point: {e}");
            }

            // What was destroyed or despawned while loading and in the first frame after goes in the same file
            var selfCheckPath = System.IO.Path.Combine(Multiplayer.DesyncsDir, $"selfcheck-{Multiplayer.username}-{Find.TickManager.TicksGame}.txt");
            JoinPointDrops.StopAfterNextFrame(selfCheckPath);

            if (lanesBefore == null) return;

            try
            {
                SelfCheck(lanesBefore, leavesBefore, selfCheckPath);
            }
            catch (Exception e)
            {
                MpLog.Error($"Join point self-check failed: {e}");
            }
        }

        private static void SelfCheck(Dictionary<string, ulong> lanesBefore, Dictionary<string, string> leavesBefore, string selfCheckPath)
        {
            var after = SyncLanes.ComputeAll();
            var changed = lanesBefore.Where(kv => after.TryGetValue(kv.Key, out var v) && v != kv.Value).Select(kv => kv.Key).ToList();
            if (changed.Count == 0)
            {
                MpLog.Log($"Join point self-check: all {after.Count} lanes are the same after saving and reloading");
                return;
            }

            var details = new List<string>();
            var full = new System.Text.StringBuilder();
            foreach (var key in changed.ToList())
                if (key.StartsWith("world / ", StringComparison.Ordinal))
                {
                    // The world lanes' inputs, as numbers
                    var valuesAfter = WorldValues();
                    var lines = worldValuesBefore.Keys.Union(valuesAfter.Keys)
                        .Where(k => k.StartsWith(key.Substring("world / ".Length) + ".", StringComparison.Ordinal))
                        .Select(k => (k, b: worldValuesBefore.GetValueOrDefault(k, "(none)"), a: valuesAfter.GetValueOrDefault(k, "(none)")))
                        .Select(v => $"{v.k}: {v.b} -> {v.a}{(v.b == v.a ? "" : "  CHANGED")}")
                        .ToList();
                    var text = lines.Count == 0 ? "(only this lane's checksum is kept before the save)" : string.Join("\n", lines);
                    details.Add($"{key}: {string.Join(" | ", lines.Where(l => l.EndsWith("CHANGED")).Take(6))}");
                    full.Append($"=== {key}\n{text}\n\n");
                }
                else if (leavesBefore != null && leavesBefore.TryGetValue(key, out var before))
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
                    full.Append($"=== {key}\n{diff}\n\n");
                }
                else
                {
                    full.Append($"=== {key}\n(only this lane's checksum is kept before the save)\n\n");
                }

            if (changed.Count == 0)
            {
                MpLog.Log($"Join point self-check: all {after.Count} lanes are the same after saving and reloading (plant growth rounded by the save, cold plants made leafless again on load)");
                return;
            }

            var written = false;
            try
            {
                System.IO.Directory.CreateDirectory(Multiplayer.DesyncsDir);
                System.IO.File.WriteAllText(selfCheckPath,
                    $"{Multiplayer.username}: join point self-check at tick {Find.TickManager.TicksGame}, every lane that saving and reloading " +
                    $"changed (before = the live game, after = the reloaded one)\n\n{full}");
                written = true;
            }
            catch (Exception e)
            {
                MpLog.Error($"Couldn't write the join point self-check: {e}");
            }

            MpLog.Error($"Join point self-check: saving and reloading changed {string.Join(", ", changed)}. Some state the " +
                        $"simulation uses isn't saved (or is rebuilt differently), so a joiner's game will differ there." +
                        (written ? $" The full diff is in {selfCheckPath}" : "") + $"\n{string.Join("\n", details)}");
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

        private static Dictionary<string, string> worldValuesBefore = new();

        /// <summary>
        /// What the world's time and ids lanes are made of, by "lane.name": the id counters (every int field of
        /// the UniqueIDsManager) and the world's clock with the tick manager's.
        /// </summary>
        private static Dictionary<string, string> WorldValues()
        {
            var values = new Dictionary<string, string>();
            if (Find.UniqueIDsManager is { } ids)
                foreach (var field in AccessTools.GetDeclaredFields(typeof(UniqueIDsManager)).Where(f => !f.IsStatic && f.FieldType == typeof(int)))
                    values[$"ids.{field.Name}"] = ((int)field.GetValue(ids)).ToString(CultureInfo.InvariantCulture);

            var world = Multiplayer.AsyncWorldTime;
            var tm = Find.TickManager;
            if (world != null)
            {
                values["time.worldTicks"] = world.worldTicks.ToString(CultureInfo.InvariantCulture);
                values["time.worldGameStartAbsTick"] = world.gameStartAbsTick.ToString(CultureInfo.InvariantCulture);
                values["time.desiredTimeSpeed"] = world.DesiredTimeSpeed.ToString();
                values["time.randState"] = world.randState.ToString("X16");
            }
            if (tm != null)
            {
                values["time.TicksGame"] = tm.TicksGame.ToString(CultureInfo.InvariantCulture);
                values["time.gameStartAbsTick"] = tm.gameStartAbsTick.ToString(CultureInfo.InvariantCulture);
            }
            return values;
        }

        /// <summary>The lanes and entity leaves before a join point's save, for <see cref="AfterJoinPoint"/>.</summary>
        public static (Dictionary<string, ulong> lanes, Dictionary<string, string> leaves) BeforeJoinPoint()
        {
            JoinPointDrops.Arm();
            clockBeforeSave = Clock();
            try
            {
                var lanes = SyncLanes.ComputeAll();
                mapTicksBefore = Find.Maps.ToDictionary(m => m.uniqueID, m => m.AsyncTime().mapTicks);
                worldValuesBefore = WorldValues();
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
