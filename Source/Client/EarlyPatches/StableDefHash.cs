using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Patches;
using Multiplayer.Common.Desyncs;
using Verse;

namespace Multiplayer.Client.EarlyPatches
{
    /// <summary>
    /// <see cref="Def.GetHashCode"/> returns <c>defName.GetHashCode()</c>, which CoreCLR (RimWorld 1.7) randomizes
    /// per process, and vanilla and mods seed random numbers with it (raiders' trap knowledge, trade requests,
    /// emerging from water). Two games then draw different numbers from the same state. A stable string hash
    /// makes it the same on every machine. Patched while Multiplayer's Mod class is constructed, before any def
    /// exists, so no hash set ever holds a def under its old hash.
    /// </summary>
    [EarlyPatch]
    [HarmonyPatch(typeof(Def), nameof(Def.ResolveDefNameHash))]
    static class StableDefHash
    {
        static bool Prefix(Def __instance)
        {
            __instance.defNameHash = Hash(__instance.defName);
            return false;
        }

        /// <summary>
        /// Logs the probe the two players compare ("Steel" must give the same def hash on both, whatever the raw
        /// string hash says) and any two defs of one type that share a hash, which Def.Equals would call equal.
        /// </summary>
        public static void CheckAfterDefsLoaded()
        {
            var steel = DefDatabase<ThingDef>.GetNamedSilentFail("Steel");
            Log.Message($"[Multiplayer] Def hash probe: Steel def hash {steel?.GetHashCode()} (stable {Hash("Steel")}), " +
                        $"raw string hash {"Steel".GetHashCode()} (differs per process on CoreCLR)");

            Log.Message("[Multiplayer] Def hash samples: " + string.Join(", ", new[] { "Steel", "WoodLog", "Beer", "TextBook" }
                .Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n)).Where(d => d != null)
                .Select(d => $"{d.defName}={d.defNameHash}/{d.GetHashCode()}/{Hash(d.defName)}")));

            var collisions = GenDefDatabase.AllDefTypesWithDatabases()
                .SelectMany(GenDefDatabase.GetAllDefsInDatabaseForDef)
                .GroupBy(d => (d.GetType(), d.defNameHash))
                .Where(g => g.Count() > 1)
                .Select(g => $"[{g.Key.Item1.Name} {g.Key.Item2}] " + string.Join(" = ", g.Take(8).Select(d => d.defName)) + (g.Count() > 8 ? $" (+{g.Count() - 8})" : ""))
                .ToList();
            if (collisions.Count > 0)
                Log.Error($"[Multiplayer] Defs of one type share a def hash, so Def.Equals treats them as one: {string.Join("; ", collisions)}");
        }

        // Def.Equals compares this hash (and the type), so it must spread well: FNV-1a plus a final avalanche.
        public static int Hash(string defName)
        {
            if (defName == null) return 0;
            unchecked
            {
                uint h = (uint)FaultSignature.Hash(defName);
                h ^= h >> 16;
                h *= 0x85ebca6b;
                h ^= h >> 13;
                h *= 0xc2b2ae35;
                h ^= h >> 16;
                return (int)h;
            }
        }
    }
}
