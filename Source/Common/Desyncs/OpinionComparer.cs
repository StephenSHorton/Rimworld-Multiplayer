using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common.Desyncs;

/// <summary>
/// What two games' opinions of the same ticks disagree on. <see cref="hard"/> is a desync: the games no longer
/// match. <see cref="soft"/> lists differences that are reported but don't stop play (one-sided errors, SOFT lanes).
/// </summary>
public class OpinionVerdict
{
    public string? hard;
    public string? hardLane;
    public int hardMapId = -1;
    public readonly List<string> soft = [];
    // Every lane that differs, hard or soft
    public readonly List<(int mapId, string lane)> laneDiffs = [];
    // Every random state list that differs (scope: map id, -1 world, -2 commands) and its first differing entry
    public readonly List<(int scope, int entry)> rngDiffs = [];
    // Scopes whose Rand attribution hash differs while their random states are equal
    public readonly List<int> randAttrDiffs = [];
    // With a hard difference: everything else found (other random states, lanes, errors)
    public readonly List<string> extra = [];

    public bool Clean => hard == null && soft.Count == 0;
}

/// <summary>
/// The one comparison of sync opinions, used by both the clients and the server. The first opinion is the
/// reference (the host's or the arbiter's), the second the game checked against it.
/// </summary>
public static class OpinionComparer
{
    public static OpinionVerdict Compare(SyncOpinion reference, SyncOpinion other)
    {
        var verdict = new OpinionVerdict();

        if (reference.roundMode != other.roundMode)
        {
            verdict.hard = $"FP round mode doesn't match: {reference.roundMode} != {other.roundMode}";
            return verdict;
        }

        var refMaps = reference.mapRandomStates ?? [];
        var otherMaps = other.mapRandomStates ?? [];
        if (!refMaps.Select(m => m.mapId).SequenceEqual(otherMaps.Select(m => m.mapId)))
        {
            verdict.hard = $"Map instances don't match: [{string.Join(", ", refMaps.Select(m => m.mapId))}] vs [{string.Join(", ", otherMaps.Select(m => m.mapId))}]";
            return verdict;
        }

        // Every random state that differs, not only the first: a lane or a second scope that also differs says
        // whether the state or the code path split first
        for (int i = 0; i < refMaps.Count; i++)
        {
            var diff = DescribeRandomDiff(refMaps[i].randomStates, otherMaps[i].randomStates, out var index);
            if (diff != null)
                AddRngDiff(verdict, refMaps[i].mapId, index, $"Wrong random state on map {refMaps[i].mapId}: {diff}");
        }

        var worldDiff = DescribeRandomDiff(reference.worldRandomStates, other.worldRandomStates, out var worldIndex);
        if (worldDiff != null)
            AddRngDiff(verdict, RandAttrScope.World, worldIndex, $"Wrong random state for the world: {worldDiff}");

        var cmdDiff = DescribeRandomDiff(reference.commandRandomStates, other.commandRandomStates, out var cmdIndex);
        if (cmdDiff != null)
            AddRngDiff(verdict, RandAttrScope.Commands, cmdIndex, $"Random state from commands doesn't match: {cmdDiff}");

        int softBefore = verdict.soft.Count;
        CompareLanes(reference.lanes ?? [], other.lanes ?? [], verdict);
        CompareFaults(reference.faults ?? [], other.faults ?? [], verdict);
        CompareRandAttr(reference.randAttr ?? [], other.randAttr ?? [], verdict);

        if (verdict.rngDiffs.Count > 0)
        {
            // With a random state difference, the rest is context for the hard message, not a warning of its own
            verdict.extra.AddRange(verdict.soft.Skip(softBefore));
            if (verdict.extra.Count > 0)
            {
                var also = string.Join("; ", verdict.extra.Take(3));
                if (also.Length > 600) also = also.Substring(0, 600) + "...";
                verdict.hard += $"; also: {also}" + (verdict.extra.Count > 3 ? $" (and {verdict.extra.Count - 3} more)" : "");
            }
        }

        return verdict;
    }

    private static void AddRngDiff(OpinionVerdict verdict, int scope, int entry, string message)
    {
        verdict.rngDiffs.Add((scope, entry));
        if (verdict.hard == null)
        {
            verdict.hard = message;
            verdict.hardLane = "rng";
            verdict.hardMapId = scope >= 0 ? scope : -1;
        }
        else
        {
            verdict.extra.Add(message);
        }
    }

    /// <summary>
    /// Equal random states but a different attribution hash: the same number of calls made by different code (one
    /// more on one pawn and one less on another). Soft. Only scopes both games hashed count, and a random state
    /// difference makes this redundant.
    /// </summary>
    private static void CompareRandAttr(List<RandAttrHash> reference, List<RandAttrHash> other, OpinionVerdict verdict)
    {
        if (verdict.rngDiffs.Count > 0) return;
        foreach (var mine in reference)
        {
            int at = other.FindIndex(o => o.scope == mine.scope);
            if (at < 0) continue;
            var theirs = other[at];
            if (theirs.hash == mine.hash) continue;
            verdict.randAttrDiffs.Add(mine.scope);
            verdict.soft.Add($"{RandAttrScope.Name(mine.scope)} Rand attribution differs (same random state; {mine.records} vs {theirs.records} records)");
        }
    }

    private static void CompareLanes(List<LaneHash> reference, List<LaneHash> other, OpinionVerdict verdict)
    {
        var otherByKey = new Dictionary<(int, int, string), LaneHash>();
        foreach (var lane in other)
            otherByKey[(lane.tick, lane.mapId, lane.lane)] = lane;

        foreach (var lane in reference)
        {
            if (!otherByKey.TryGetValue((lane.tick, lane.mapId, lane.lane), out var theirs))
                continue; // A lane only one build knows; the join fingerprint covers build differences

            if (theirs.hash == lane.hash) continue;

            verdict.laneDiffs.Add((lane.mapId, lane.lane));
            var where = lane.mapId == -1 ? "world" : $"map {lane.mapId}";
            var msg = $"{where} / {lane.lane} lane differs at tick {lane.tick}";
            // A lane of a few values (time) carries them: both sides' say which input moved
            if (!string.IsNullOrEmpty(lane.detail) || !string.IsNullOrEmpty(theirs.detail))
                msg += $" (reference: {lane.detail}; other: {theirs.detail})";
            if (lane.hard && theirs.hard && verdict.hard == null)
            {
                verdict.hard = msg;
                verdict.hardLane = lane.lane;
                verdict.hardMapId = lane.mapId;
            }
            else
            {
                verdict.soft.Add(msg);
            }
        }
    }

    private static void CompareFaults(List<FaultEvent> reference, List<FaultEvent> other, OpinionVerdict verdict)
    {
        if (reference.Count == other.Count &&
            reference.Select(f => (f.tick, f.signature)).SequenceEqual(other.Select(f => (f.tick, f.signature))))
            return;

        var theirs = other.Select(f => (f.tick, f.signature)).ToList();
        var ours = reference.Select(f => (f.tick, f.signature)).ToList();

        foreach (var f in reference)
            if (!theirs.Remove((f.tick, f.signature)))
                verdict.soft.Add($"Error only in the reference game at tick {f.tick}: {f.text}");

        foreach (var f in other)
            if (!ours.Remove((f.tick, f.signature)))
                verdict.soft.Add($"Error only in the other game at tick {f.tick}: {f.text}");

        if (verdict.soft.Count == 0)
            verdict.soft.Add("The same errors were logged in a different order");
    }

    /// <summary>Null if equal; otherwise where and how two lists of Rand states first differ.</summary>
    public static string? DescribeRandomDiff(List<ulong>? a, List<ulong>? b) => DescribeRandomDiff(a, b, out _);

    /// <summary>As above; <paramref name="index"/> is the first entry that differs (or the shorter count).</summary>
    public static string? DescribeRandomDiff(List<ulong>? a, List<ulong>? b, out int index)
    {
        index = -1;
        a ??= [];
        b ??= [];
        int n = System.Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            if (a[i] == b[i]) continue;
            index = i;
            uint seedA = (uint)a[i], seedB = (uint)b[i];
            uint callsA = (uint)(a[i] >> 32), callsB = (uint)(b[i] >> 32);
            return seedA != seedB
                ? $"entry {i}: seed {seedA:X8} vs {seedB:X8}, calls {callsA} vs {callsB}"
                : $"entry {i}: {callsA} vs {callsB} random calls";
        }

        if (a.Count == b.Count) return null;
        index = n;
        return $"{a.Count} vs {b.Count} entries";
    }
}
