using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Multiplayer.Client.Util;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// The frame to capture Rand stacks for, armed by a Rand attribution diff: kind, def, tick method, tier 1 phase,
    /// tier 2 phase (or any) and job. The owner is kept for the report but not required, since the next occurrence
    /// may be another pawn of the same def. Written by names to MpDesyncs/rand-watch.txt on every game and loaded
    /// at startup, so it outlives a resync, a process restart and new sessions. Cleared after 3 diffs that came with
    /// stacks, when it expires, or by the debug action. A watch takes at most <see cref="MaxTotalCaptures"/> stacks
    /// (the count is saved with it, so a restart doesn't reset it); a new diff arms a new watch with a new budget.
    /// </summary>
    public static class RandWatch
    {
        public const int ClearAfterDiffsWithStacks = 3;
        public const int MaxTotalCaptures = 4096;
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

        private static Dictionary<string, string> fields;
        private static bool resolved;
        private static RandAttrKind kind;
        private static ushort def, basePhase, phase, job;
        private static RandAttrMethod method;
        private static bool anyPhase;
        private static DateTime expiresUtc;
        private static int diffsWithStacks;
        private static int scope;
        private static bool anyScope;
        private static bool dirty;
        public static int captures;

        private static string FilePath => Path.Combine(Multiplayer.DesyncsDir, "rand-watch.txt");

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var read = File.ReadAllLines(FilePath)
                    .Select(l => l.Split(new[] { '=' }, 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
                Arm(read, save: false);
            }
            catch (Exception e)
            {
                Log.Warning($"Rand attribution: couldn't read {FilePath}: {e.Message}");
            }
        }

        private static void Arm(Dictionary<string, string> f, bool save)
        {
            if (!f.TryGetValue("kind", out var k) || k.NullOrEmpty()) return;
            if (f.TryGetValue("expires", out var exp) && DateTime.TryParse(exp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e))
                expiresUtc = e;
            else
                expiresUtc = DateTime.UtcNow + Lifetime;
            if (DateTime.UtcNow > expiresUtc)
            {
                Clear("it expired");
                return;
            }

            diffsWithStacks = f.TryGetValue("diffsWithStacks", out var d) && int.TryParse(d, out var n) ? n : 0;
            captures = f.TryGetValue("captures", out var c) && int.TryParse(c, out var cn) ? cn : 0;
            fields = f;
            resolved = false;
            dirty = false;
            // A watch whose budget is spent stays on file (its diff count still clears it) but captures nothing
            RandAttribution.watchArmed = captures < MaxTotalCaptures;
            if (f.TryGetValue("tier2", out var t2) && t2 == "1")
                RandAttribution.tier2 = true;
            if (save) Save();
        }

        public static void ArmFrom(ServerRandAttrDiffPacket diff, bool withStacks)
        {
            if (withStacks && fields != null && ++diffsWithStacks >= ClearAfterDiffsWithStacks)
            {
                Clear($"{diffsWithStacks} diffs came with stacks");
                return;
            }

            if (diff.watchKind.NullOrEmpty()) return;

            // The first diff also turns tier 2 on (on both games: both get the diff)
            RandAttribution.tier2 = true;
            Arm(new Dictionary<string, string>
            {
                ["kind"] = diff.watchKind,
                ["def"] = diff.watchDef,
                ["method"] = diff.watchMethod,
                ["base"] = diff.watchBase,
                ["phase"] = diff.watchPhase,
                ["job"] = diff.watchJob,
                ["owner"] = diff.watchOwner,
                ["scope"] = diff.scope.ToString(),
                ["tier2"] = "1",
                ["diffsWithStacks"] = diffsWithStacks.ToString(),
                ["captures"] = "0",
                ["expires"] = (DateTime.UtcNow + Lifetime).ToString("o", CultureInfo.InvariantCulture)
            }, save: true);
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                fields["diffsWithStacks"] = diffsWithStacks.ToString();
                fields["captures"] = captures.ToString();
                File.WriteAllLines(FilePath, fields.Select(kv => $"{kv.Key}={kv.Value}"));
            }
            catch (IOException e)
            {
                Log.Warning($"Rand attribution: couldn't write {FilePath}: {e.Message}");
            }
        }

        /// <summary>One stack taken (from the simulation): counts it against the budget; the file is written later.</summary>
        public static void NoteCapture()
        {
            captures++;
            if (captures >= MaxTotalCaptures)
            {
                RandAttribution.watchArmed = false;
                RandAttribution.Note($"Rand attribution: the watch took its {MaxTotalCaptures} stacks; no more until a new diff arms one");
                dirty = true;
            }
            else if ((captures & 255) == 0)
                dirty = true;
        }

        /// <summary>Every frame, outside the simulation: saves the capture count when it changed enough.</summary>
        public static void SaveIfDirty()
        {
            if (!dirty) return;
            dirty = false;
            if (fields != null) Save();
        }

        public static void Clear(string why)
        {
            fields = null;
            dirty = false;
            resolved = false;
            RandAttribution.watchArmed = false;
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (IOException)
            {
            }
            MpLog.Log($"Rand attribution: watch cleared ({why})");
        }

        private static string Field(string key) => fields != null && fields.TryGetValue(key, out var v) && v != "-" ? v : "";

        private static void Resolve()
        {
            resolved = true;
            if (fields == null) return;
            Enum.TryParse(Field("kind"), out kind);
            Enum.TryParse(Field("method"), out method);
            def = kind switch
            {
                RandAttrKind.Thing => RandAttribution.DefHash<ThingDef>(Field("def")),
                RandAttrKind.WorldObject => RandAttribution.DefHash<WorldObjectDef>(Field("def")),
                RandAttrKind.Incident => RandAttribution.DefHash<IncidentDef>(Field("def")),
                RandAttrKind.Condition => RandAttribution.DefHash<GameConditionDef>(Field("def")),
                _ => 0
            };
            job = RandAttribution.DefHash<JobDef>(Field("job"));
            basePhase = Field("base").Length > 0 ? RandAttribution.RegisterPhase(Field("base")) : (ushort)0;
            anyPhase = Field("phase").Length == 0;
            anyScope = !int.TryParse(Field("scope"), out scope);
            phase = anyPhase ? (ushort)0 : RandAttribution.RegisterPhase(Field("phase"));
        }

        public static bool Matches(int s, RandAttrKind k, ushort d, RandAttrMethod m, ushort b, ushort p, ushort j)
        {
            if (!resolved)
            {
                if (DefDatabase<ThingDef>.AllDefsListForReading.Count == 0) return false;
                Resolve();
            }
            if (fields == null) return false;
            if (k != kind || d != def || m != method || b != basePhase || j != job || (!anyPhase && p != phase) || (!anyScope && s != scope)) return false;
            if (DateTime.UtcNow > expiresUtc)
            {
                Clear("it expired");
                return false;
            }

            return true;
        }

        public static string Describe() => fields == null
            ? "none"
            : $"{Field("kind")} {Field("owner")} def={Field("def")} m={Field("method")} base={Field("base")} phase={(Field("phase").Length > 0 ? Field("phase") : "any")} " +
              $"job={Field("job")} scope={Field("scope")}, {captures} of {MaxTotalCaptures} captures taken, expires {expiresUtc:u}";
    }
}
