using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common.Desyncs;

/// <summary>What a Rand attribution record belongs to.</summary>
public enum RandAttrKind : byte
{
    Root, Thing, MapComp, WorldComp, WorldObject, Lord, Quest, Incident, Condition, Command, BetweenTicks, Reseed, Overflow
}

/// <summary>The thing method a Thing record's calls were made in.</summary>
public enum RandAttrMethod : byte
{
    None, Tick, TickRare, TickLong, TickInterval, TakeDamage, Kill, SpawnSetup, CheckForJobOverride
}

/// <summary>
/// One scope's Rand attribution hash for one sync opinion window: every record (who made how many Rand calls, in
/// which tick method and phase) folded in order. Equal random states with different hashes mean the same number of
/// calls was made by different code (one call more on one pawn, one less on another).
/// </summary>
public record struct RandAttrHash : IPacketBufferable
{
    public int scope;
    public ulong hash;
    public int records;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref scope);
        buf.Bind(ref hash);
        buf.Bind(ref records);
    }
}

public static class RandAttrScope
{
    public const int World = -1;
    public const int Commands = -2;

    public static string Name(int scope) => scope switch
    {
        World => "world",
        Commands => "commands",
        _ => $"map {scope}"
    };
}

/// <summary>
/// One record of a Rand attribution table, one text line:
/// <c>e8 #57 Thing Human1234 def=Human cls=Pawn m=TickInterval base=- phase=JobDriver_Wait job=Wait_Combat toil=1 calls=3 name="Tynan"</c>.
/// Ids and def names only; the name is for people and stays out of every comparison.
/// </summary>
public class RandAttrLine
{
    public int entry;
    public int ordinal;
    public string kind = "Root";
    public string owner = "-";
    public string def = "-";
    public string cls = "-";
    public string method = "-";
    // The tier 1 phase (map tick step, map system, component type, command handler)
    public string basePhase = "-";
    // The tier 2 phase (pawn tracker, job driver, comp), "-" when tier 2 was off or none was open
    public string phase = "-";
    public string job = "-";
    public int toil = -1;
    public int calls;
    public string name = "";

    /// <summary>Everything but the tier 2 phase: two games that armed tier 2 at different times still agree on this.</summary>
    public string UnitKey => $"{kind}|{owner}|{def}|{method}|{basePhase}|{job}|{toil}";

    public string Key => UnitKey + "|" + phase;

    public static string Token(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        var sb = new StringBuilder(s!.Length);
        foreach (var c in s)
            sb.Append(c is ' ' or '\t' or '=' or '"' or '\n' or '\r' ? '_' : c);
        return sb.ToString();
    }

    public string Format()
    {
        var sb = new StringBuilder(96);
        sb.Append('e').Append(entry).Append(" #").Append(ordinal).Append(' ').Append(kind).Append(' ').Append(owner)
            .Append(" def=").Append(def).Append(" cls=").Append(cls).Append(" m=").Append(method)
            .Append(" base=").Append(basePhase).Append(" phase=").Append(phase).Append(" job=").Append(job)
            .Append(" toil=").Append(toil).Append(" calls=").Append(calls);
        if (name.Length > 0)
            sb.Append(" name=\"").Append(name.Replace("\"", "'")).Append('"');
        return sb.ToString();
    }

    public static RandAttrLine? Parse(string line)
    {
        if (line.Length < 4 || line[0] != 'e' || !char.IsDigit(line[1])) return null;

        var result = new RandAttrLine();
        var nameAt = line.IndexOf(" name=\"", StringComparison.Ordinal);
        var body = nameAt >= 0 ? line.Substring(0, nameAt) : line;
        if (nameAt >= 0)
        {
            var start = nameAt + 7;
            var end = line.LastIndexOf('"');
            result.name = end > start ? line.Substring(start, end - start) : "";
        }

        var parts = body.Split(' ');
        if (parts.Length < 4 || parts[1].Length < 2 || parts[1][0] != '#') return null;
        if (!int.TryParse(parts[0].Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out result.entry)) return null;
        if (!int.TryParse(parts[1].Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out result.ordinal)) return null;
        result.kind = parts[2];
        result.owner = parts[3];

        for (int i = 4; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            var key = parts[i].Substring(0, eq);
            var value = parts[i].Substring(eq + 1);
            switch (key)
            {
                case "def": result.def = value; break;
                case "cls": result.cls = value; break;
                case "m": result.method = value; break;
                case "base": result.basePhase = value; break;
                case "phase": result.phase = value; break;
                case "job": result.job = value; break;
                case "toil": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.toil); break;
                case "calls": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.calls); break;
            }
        }

        return result;
    }

    /// <summary>"Human1234 "Tynan" (Pawn, Human) TickInterval > JobDriver_Wait job=Wait_Combat toil 1"</summary>
    public string Describe(bool withPhase = true)
    {
        var sb = new StringBuilder();
        switch (kind)
        {
            case nameof(RandAttrKind.Root):
                sb.Append("the tick itself");
                break;
            case nameof(RandAttrKind.BetweenTicks):
                sb.Append("calls on this Rand state between its ticks (outside every tick and command)");
                break;
            case nameof(RandAttrKind.Reseed):
                sb.Append("a Rand reseed at the base level");
                break;
            case nameof(RandAttrKind.Overflow):
                sb.Append("the records past the per-tick limit");
                break;
            default:
                sb.Append(owner != "-" ? owner : kind);
                break;
        }

        if (name.Length > 0) sb.Append(" \"").Append(name).Append('"');

        var what = new List<string>();
        if (kind != nameof(RandAttrKind.Root) && owner != "-") what.Add(kind == nameof(RandAttrKind.Thing) && cls != "-" ? cls : kind);
        if (def != "-") what.Add(def);
        if (what.Count > 0) sb.Append(" (").Append(string.Join(", ", what)).Append(')');

        if (method != "-") sb.Append(' ').Append(method);
        if (basePhase != "-") sb.Append(method != "-" || what.Count > 0 ? " > " : " ").Append(basePhase);
        if (withPhase && phase != "-") sb.Append(" > ").Append(phase);
        if (job != "-") sb.Append(" job=").Append(job);
        if (toil >= 0) sb.Append(" toil ").Append(toil);
        return sb.ToString();
    }
}

/// <summary>The header line of one tick in a table: <c>tick scope=1 window=366901 entry=8 ticksGame=366909 timer=366909 before=1A2B3C4D:120 after=1A2B3C4D:155 records=212</c>.</summary>
public class RandAttrTickLine
{
    public int scope;
    public int window;
    public int entry;
    public int ticksGame;
    public int timer;
    public ulong before;
    public ulong after;
    public int records;

    public static string State(ulong s) => $"{(uint)s:X8}:{(uint)(s >> 32)}";

    public string Format() =>
        $"tick scope={scope} window={window} entry={entry} ticksGame={ticksGame} timer={timer} before={State(before)} after={State(after)} records={records}";

    public static RandAttrTickLine? Parse(string line)
    {
        if (!line.StartsWith("tick ", StringComparison.Ordinal)) return null;
        var r = new RandAttrTickLine();
        foreach (var part in line.Split(' '))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var value = part.Substring(eq + 1);
            switch (part.Substring(0, eq))
            {
                case "scope": int.TryParse(value, out r.scope); break;
                case "window": int.TryParse(value, out r.window); break;
                case "entry": int.TryParse(value, out r.entry); break;
                case "ticksGame": int.TryParse(value, out r.ticksGame); break;
                case "timer": int.TryParse(value, out r.timer); break;
                case "records": int.TryParse(value, out r.records); break;
                case "before": r.before = ParseState(value); break;
                case "after": r.after = ParseState(value); break;
            }
        }
        return r;
    }

    private static ulong ParseState(string s)
    {
        var colon = s.IndexOf(':');
        if (colon < 0) return 0;
        uint.TryParse(s.Substring(0, colon), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seed);
        uint.TryParse(s.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iter);
        return seed | ((ulong)iter << 32);
    }
}

public class RandAttrTable
{
    public readonly List<RandAttrTickLine> ticks = new();
    public readonly List<RandAttrLine> lines = new();
    public int offThread;

    public static RandAttrTable Parse(string? text)
    {
        var table = new RandAttrTable();
        if (string.IsNullOrEmpty(text)) return table;
        foreach (var raw in text!.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("tick ", StringComparison.Ordinal))
            {
                if (RandAttrTickLine.Parse(line) is { } t) table.ticks.Add(t);
            }
            else if (line.StartsWith("offthread=", StringComparison.Ordinal))
            {
                int.TryParse(line.Substring("offthread=".Length), out table.offThread);
            }
            else if (RandAttrLine.Parse(line) is { } l)
            {
                table.lines.Add(l);
            }
        }
        return table;
    }
}

/// <summary>A run of records with the same <see cref="RandAttrLine.UnitKey"/>: what the hash compares.</summary>
public class RandAttrUnit
{
    public readonly List<RandAttrLine> records = new();
    public string Key => records[0].UnitKey;
    public int Calls => records.Sum(r => r.calls);
    public int Ordinal => records[0].ordinal;
    public RandAttrLine First => records[0];

    public static List<RandAttrUnit> Of(IEnumerable<RandAttrLine> lines)
    {
        var units = new List<RandAttrUnit>();
        foreach (var line in lines)
        {
            if (units.Count == 0 || units[^1].Key != line.UnitKey)
                units.Add(new RandAttrUnit());
            units[^1].records.Add(line);
        }
        return units;
    }
}

public class RandAttrDiffResult
{
    public bool differs;
    public string summary = "";
    public string text = "";
    public int entry = -1;
    public int ticksGame;
    // The record the watch is armed from, and whether its tier 2 phase counts
    public RandAttrLine? watchLine;
    public bool watchPhase;
    // Which unit on each side, for lining up the stacks (-1: none on that side)
    public int refUnitOrdinal = -1;
    public int otherUnitOrdinal = -1;
}

/// <summary>
/// Compares two games' Rand attribution tables of the same window and names the first record that differs: who
/// made how many Rand calls in which tick method and phase on each game.
/// </summary>
public static class RandAttrDiff
{
    private const int LookAhead = 64;

    public static RandAttrDiffResult Diff(string refText, string otherText, string refName, string otherName, int scope, int window)
    {
        var refT = RandAttrTable.Parse(refText);
        var otherT = RandAttrTable.Parse(otherText);
        var result = new RandAttrDiffResult();
        var sb = new StringBuilder();
        var where = RandAttrScope.Name(scope);

        var entries = refT.lines.Select(l => l.entry).Concat(otherT.lines.Select(l => l.entry))
            .Concat(refT.ticks.Select(t => t.entry)).Concat(otherT.ticks.Select(t => t.entry))
            .Distinct().OrderBy(e => e).ToList();

        foreach (var e in entries)
        {
            var a = refT.lines.Where(l => l.entry == e).ToList();
            var b = otherT.lines.Where(l => l.entry == e).ToList();
            var first = FirstDifference(RandAttrUnit.Of(a), RandAttrUnit.Of(b), refName, otherName);
            if (first == null) continue;

            var tick = refT.ticks.FirstOrDefault(t => t.entry == e) ?? otherT.ticks.FirstOrDefault(t => t.entry == e);
            result.differs = true;
            result.entry = e;
            result.ticksGame = tick?.ticksGame ?? 0;
            result.watchLine = first.Value.watch;
            result.watchPhase = first.Value.watchPhase;
            result.refUnitOrdinal = first.Value.refOrdinal;
            result.otherUnitOrdinal = first.Value.otherOrdinal;
            var count = Math.Max(a.Count, b.Count);
            result.summary = $"{where} tick {result.ticksGame} (window {window} entry {e}): {first.Value.what}; record #{first.Value.ordinal} of {count}";
            sb.AppendLine(result.summary);
            sb.AppendLine();
            sb.AppendLine($"Entry {e}, {refName}'s records then {otherName}'s, from the first difference:");
            AppendAround(sb, a, first.Value.ordinal, refName);
            AppendAround(sb, b, first.Value.ordinal, otherName);
            break;
        }

        if (!result.differs)
        {
            result.summary = refT.lines.Count == 0 && otherT.lines.Count == 0
                ? $"{where} window {window}: no Rand attribution records on either game"
                : $"{where} window {window}: the Rand attribution tables are the same ({refT.lines.Count} records)";
            sb.AppendLine(result.summary);
        }

        // Per-owner totals over the whole table
        var totals = new Dictionary<string, (int a, int b, RandAttrLine line)>();
        foreach (var l in refT.lines)
        {
            var k = OwnerKey(l);
            totals.TryGetValue(k, out var t);
            totals[k] = (t.a + l.calls, t.b, t.line ?? l);
        }
        foreach (var l in otherT.lines)
        {
            var k = OwnerKey(l);
            totals.TryGetValue(k, out var t);
            totals[k] = (t.a, t.b + l.calls, t.line ?? l);
        }

        var differing = totals.Where(kv => kv.Value.a != kv.Value.b).OrderByDescending(kv => Math.Abs(kv.Value.a - kv.Value.b)).Take(10).ToList();
        sb.AppendLine();
        sb.AppendLine(differing.Count == 0 ? "Every owner made the same number of calls in total." : "Owners whose totals differ (top 10):");
        foreach (var kv in differing)
            sb.AppendLine($"  {kv.Value.line.Describe(false)}: {kv.Value.a} ({refName}) vs {kv.Value.b} ({otherName})");

        sb.AppendLine();
        sb.AppendLine($"Reseeds: {Count(refT, RandAttrKind.Reseed)} ({refName}) vs {Count(otherT, RandAttrKind.Reseed)} ({otherName}); " +
                      $"between-tick calls: {Sum(refT, RandAttrKind.BetweenTicks)} vs {Sum(otherT, RandAttrKind.BetweenTicks)}; " +
                      $"overflow records: {Count(refT, RandAttrKind.Overflow)} vs {Count(otherT, RandAttrKind.Overflow)}; " +
                      $"Rand calls off the main thread so far: {refT.offThread} vs {otherT.offThread}");

        result.text = sb.ToString();
        return result;

        static string OwnerKey(RandAttrLine l) => $"{l.kind}|{l.owner}|{l.def}|{(l.owner == "-" ? l.basePhase : "")}";
        static int Count(RandAttrTable t, RandAttrKind k) => t.lines.Count(l => l.kind == k.ToString());
        static int Sum(RandAttrTable t, RandAttrKind k) => t.lines.Where(l => l.kind == k.ToString()).Sum(l => l.calls);
    }

    private static void AppendAround(StringBuilder sb, List<RandAttrLine> lines, int ordinal, string who)
    {
        var at = lines.FindIndex(l => l.ordinal >= ordinal);
        if (at < 0) at = lines.Count;
        sb.AppendLine($"-- {who}");
        for (int i = Math.Max(0, at - 3); i < Math.Min(lines.Count, at + 6); i++)
            sb.AppendLine((i == at ? "> " : "  ") + lines[i].Format());
        if (lines.Count == 0) sb.AppendLine("  (no records)");
    }

    private struct First
    {
        public string what;
        public int ordinal;
        public RandAttrLine watch;
        public bool watchPhase;
        public int refOrdinal, otherOrdinal;
    }

    private static First? FirstDifference(List<RandAttrUnit> a, List<RandAttrUnit> b, string refName, string otherName)
    {
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            if (a[i].Key == b[j].Key)
            {
                if (a[i].Calls != b[j].Calls)
                    return CountDiff(a[i], b[j], refName, otherName);
                i++;
                j++;
                continue;
            }

            // Realign: a unit only one game has
            for (int k = 1; k <= LookAhead; k++)
            {
                if (j + k < b.Count && b[j + k].Key == a[i].Key)
                    return Extra(b[j], otherName, refName, refOrdinal: -1, otherOrdinal: b[j].Ordinal);
                if (i + k < a.Count && a[i + k].Key == b[j].Key)
                    return Extra(a[i], refName, otherName, refOrdinal: a[i].Ordinal, otherOrdinal: -1);
            }

            return new First
            {
                what = $"{a[i].First.Describe()} made {a[i].Calls} Rand calls on {refName}, where {otherName}'s game had " +
                       $"{b[j].First.Describe()} with {b[j].Calls}",
                ordinal = Math.Min(a[i].Ordinal, b[j].Ordinal),
                watch = b[j].First,
                refOrdinal = a[i].Ordinal,
                otherOrdinal = b[j].Ordinal
            };
        }

        if (i < a.Count) return Extra(a[i], refName, otherName, a[i].Ordinal, -1);
        if (j < b.Count) return Extra(b[j], otherName, refName, -1, b[j].Ordinal);
        return null;
    }

    private static First CountDiff(RandAttrUnit a, RandAttrUnit b, string refName, string otherName)
    {
        int ca = a.Calls, cb = b.Calls;
        var more = cb > ca ? otherName : refName;
        var line = a.First;
        var watchPhase = false;

        // With tier 2 on both games, name the phase inside the unit that differs
        if (a.records.Count > 1 || b.records.Count > 1 || a.First.phase != "-" || b.First.phase != "-")
        {
            int n = Math.Min(a.records.Count, b.records.Count);
            for (int k = 0; k < n; k++)
            {
                if (a.records[k].phase == b.records[k].phase && a.records[k].calls == b.records[k].calls) continue;
                if (a.records[k].phase == b.records[k].phase)
                {
                    line = a.records[k];
                    watchPhase = line.phase != "-";
                }
                break;
            }
        }

        return new First
        {
            what = $"{line.Describe()} made {ca} Rand calls on {refName}, {cb} on {otherName} (+{Math.Abs(cb - ca)} on {more})",
            ordinal = line.ordinal,
            watch = line,
            watchPhase = watchPhase,
            refOrdinal = a.Ordinal,
            otherOrdinal = b.Ordinal
        };
    }

    private static First Extra(RandAttrUnit unit, string has, string lacks, int refOrdinal, int otherOrdinal) => new()
    {
        what = $"{unit.First.Describe()} made {unit.Calls} Rand calls on {has} and none on {lacks} (a record only {has}'s game has)",
        ordinal = unit.Ordinal,
        watch = unit.First,
        watchPhase = unit.First.phase != "-",
        refOrdinal = refOrdinal,
        otherOrdinal = otherOrdinal
    };

    // --- Targeted stack captures ---

    /// <summary>
    /// One captured Rand call: <c>cap e8 u57 c3 it=12345</c> followed by its frames, one per line, each starting with
    /// a space; a frame of a method Harmony patched (never inlined) starts with " *", any other with two spaces.
    /// </summary>
    public class Capture
    {
        public int entry;
        public int unit;
        public int call;
        public uint iteration;
        public readonly List<string> frames = new();

        public IEnumerable<string> Significant(int top = 8) => frames
            .Select(f => f.Trim())
            .Where(f => !f.Contains("Verse.Rand::") && !f.Contains("Verse.Rand.") && !f.Contains("DeferredStackTracing") && !f.Contains("RandAttribution"))
            .Take(top);

        public static string Header(int entry, int unit, int call, uint iteration) => $"cap e{entry} u{unit} c{call} it={iteration}";
    }

    public static List<Capture> ParseCaptures(string? text)
    {
        var list = new List<Capture>();
        if (string.IsNullOrEmpty(text)) return list;
        Capture? current = null;
        foreach (var raw in text!.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("cap ", StringComparison.Ordinal))
            {
                current = new Capture();
                foreach (var part in line.Split(' '))
                {
                    if (part.Length < 2) continue;
                    if (part[0] == 'e' && int.TryParse(part.Substring(1), out var e)) current.entry = e;
                    else if (part[0] == 'u' && int.TryParse(part.Substring(1), out var u)) current.unit = u;
                    else if (part[0] == 'c' && int.TryParse(part.Substring(1), out var c)) current.call = c;
                    else if (part.StartsWith("it=", StringComparison.Ordinal)) uint.TryParse(part.Substring(3), out current.iteration);
                }
                list.Add(current);
            }
            else if (current != null && line.StartsWith(" ", StringComparison.Ordinal))
            {
                current.frames.Add(line);
            }
        }
        return list;
    }

    private static bool SameCall(Capture a, Capture b)
    {
        var fa = a.Significant().ToList();
        var fb = b.Significant().ToList();
        // CoreCLR's tiered JIT inlines differently in each process; Harmony-patched methods are never inlined, so
        // when both stacks show some, those decide
        var pa = fa.Where(f => f.StartsWith("*")).ToList();
        var pb = fb.Where(f => f.StartsWith("*")).ToList();
        if (pa.Count > 0 || pb.Count > 0)
            return pa.SequenceEqual(pb) && (fa.Count == 0 || fb.Count == 0 || fa[0] == fb[0] || !fa[0].StartsWith("*") || !fb[0].StartsWith("*"));
        return fa.SequenceEqual(fb);
    }

    private static string Chain(Capture c) => string.Join(" < ", c.Significant(4).Select(f => f.TrimStart('*')));

    /// <summary>
    /// Lines up both games' captured stacks of the differing unit by call number and names the first call whose
    /// stacks differ or, if every shared call matches, the first call past the shorter side (the extra call). Null
    /// when neither side captured anything for it.
    /// </summary>
    public static string? FirstDifferingCall(string? refStacks, string? otherStacks, int entry, int refUnit, int otherUnit,
        string refName, string otherName, out string detail)
    {
        var a = ParseCaptures(refStacks).Where(c => c.entry == entry && c.unit == refUnit && refUnit >= 0).OrderBy(c => c.call).ToList();
        var b = ParseCaptures(otherStacks).Where(c => c.entry == entry && c.unit == otherUnit && otherUnit >= 0).OrderBy(c => c.call).ToList();
        var sb = new StringBuilder();

        if (a.Count == 0 && b.Count == 0)
        {
            detail = "No captured stacks for this record on either game (no watch was armed for it yet, or none matched)";
            return null;
        }

        var byCallA = a.GroupBy(c => c.call).ToDictionary(g => g.Key, g => g.First());
        var byCallB = b.GroupBy(c => c.call).ToDictionary(g => g.Key, g => g.First());
        string? result = null;

        foreach (var call in byCallA.Keys.Intersect(byCallB.Keys).OrderBy(c => c))
        {
            if (SameCall(byCallA[call], byCallB[call])) continue;
            result = $"call {call} differs: {Chain(byCallA[call])} ({refName}) vs {Chain(byCallB[call])} ({otherName})";
            break;
        }

        if (result == null)
        {
            int maxA = byCallA.Count == 0 ? 0 : byCallA.Keys.Max();
            int maxB = byCallB.Count == 0 ? 0 : byCallB.Keys.Max();
            if (maxA != maxB)
            {
                var (longer, who, from) = maxB > maxA ? (byCallB, otherName, maxA) : (byCallA, refName, maxB);
                var extra = longer.Keys.Where(k => k > from).OrderBy(k => k).First();
                result = $"first extra call (call {extra} on {who}): {Chain(longer[extra])}";
            }
        }

        sb.AppendLine($"Captured stacks for entry {entry}: {a.Count} on {refName} (unit #{refUnit}), {b.Count} on {otherName} (unit #{otherUnit})");
        sb.AppendLine(result ?? "Every captured call has the same stack on both games");
        foreach (var (who, list) in new[] { (refName, a), (otherName, b) })
        {
            sb.AppendLine();
            sb.AppendLine($"== {who}");
            foreach (var c in list)
            {
                sb.AppendLine(Capture.Header(c.entry, c.unit, c.call, c.iteration));
                foreach (var f in c.frames) sb.AppendLine(f);
            }
        }

        detail = sb.ToString();
        return result;
    }
}
