using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Client.Util;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Injects Rand calls on one game to test Rand attribution end to end. A file MpRandInject.flag in the save data
    /// folder turns it on; it is read outside the simulation every 60 frames into statics, and the simulation reads
    /// only those (the check makes no Rand call). The patch is applied on every game and returns at once without the
    /// file, so the Harmony patches row stays the same. Lines of the file:
    /// <code>
    /// player=Blake                 the game that injects (by username)
    /// map=1
    /// mode=thing|comp:TypeName|swap|offthread
    /// target=Human1234|colonist:first   (colonist:first is the lowest thing id free colonist on the map)
    /// every=600                    (map ticksGame % every == 0)
    /// calls=1
    /// swapTarget=Human1240         (swap: the host injects on target, the player's game on swapTarget)
    /// </code>
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.TickInterval))]
    public static class RandInjectPatch
    {
        public static bool on;
        private static string player = "";
        private static int map = -1;
        private static string mode = "thing";
        private static string compType = "";
        private static string target = "", swapTarget = "";
        private static int every = 600;
        private static int calls = 1;
        private static int frames;
        private static int injected;

        /// <summary>Every frame from RandAttribution.Update (outside the simulation).</summary>
        public static void Poll()
        {
            if (frames++ % 60 != 0) return;
            try
            {
                var path = Path.Combine(GenFilePaths.SaveDataFolderPath, "MpRandInject.flag");
                if (!File.Exists(path))
                {
                    on = false;
                    return;
                }

                var f = File.ReadAllLines(path)
                    .Select(l => l.Split(new[] { '=' }, 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

                player = f.GetValueOrDefault("player", "");
                map = int.TryParse(f.GetValueOrDefault("map", "-1"), out var m) ? m : -1;
                mode = f.GetValueOrDefault("mode", "thing");
                compType = mode.StartsWith("comp:") ? mode.Substring(5) : "";
                target = f.GetValueOrDefault("target", "");
                swapTarget = f.GetValueOrDefault("swapTarget", "");
                every = Math.Max(1, int.TryParse(f.GetValueOrDefault("every", "600"), out var e) ? e : 600);
                calls = Math.Max(1, int.TryParse(f.GetValueOrDefault("calls", "1"), out var c) ? c : 1);
                if (!on) MpLog.Warn($"Rand inject on: {string.Join(", ", f.Select(kv => $"{kv.Key}={kv.Value}"))}");
                on = true;
            }
            catch (Exception ex)
            {
                on = false;
                MpLog.Warn($"Rand inject: couldn't read the flag file: {ex.Message}");
            }
        }

        private static bool IsHost => Multiplayer.LocalServer != null;

        private static bool ThisGameInjects(out string targetHere)
        {
            targetHere = target;
            if (mode == "swap")
            {
                if (IsHost) return true;
                targetHere = swapTarget;
                return Multiplayer.username == player;
            }
            return Multiplayer.username == player;
        }

        private static bool Due(Map m) =>
            m != null && m.uniqueID == map && RandAttribution.active && Rand.stateStack.Count == 1 && Find.TickManager.ticksGameInt % every == 0;

        private static bool IsTarget(Pawn pawn, string t)
        {
            if (t == "colonist:first")
            {
                Pawn first = null;
                foreach (var p in pawn.Map.mapPawns.FreeColonistsSpawned)
                    if (first == null || p.thingIDNumber < first.thingIDNumber)
                        first = p;
                return first == pawn;
            }
            return t == pawn.def.defName + pawn.thingIDNumber || t == pawn.ThingID;
        }

        static void Postfix(Pawn __instance)
        {
            if (!on || compType.Length > 0) return;
            if (!Due(__instance.MapHeld) || !__instance.Spawned) return;
            if (!ThisGameInjects(out var t) || !IsTarget(__instance, t)) return;
            if (ThingContext.Current != __instance) return;
            Inject($"{__instance} at tick {Find.TickManager.ticksGameInt}");
        }

        /// <summary>From RandAttrPatches' map component postfix, inside the component's frame.</summary>
        public static void AfterMapComponent(MapComponent comp)
        {
            if (!on || compType.Length == 0 || comp.GetType().Name != compType) return;
            if (!Due(comp.map) || Multiplayer.username != player) return;
            Inject($"{compType} at tick {Find.TickManager.ticksGameInt}");
        }

        private static void Inject(string where)
        {
            injected++;
            if (mode == "offthread")
            {
                Task.Run(() =>
                {
                    for (int i = 0; i < calls; i++) _ = Rand.Value;
                }).Wait();
            }
            else
            {
                for (int i = 0; i < calls; i++) _ = Rand.Value;
            }

            if (injected <= 20)
                RandAttribution.Note($"Rand inject: {calls} {mode} call(s) on {where} ({injected} so far)");
        }
    }
}
