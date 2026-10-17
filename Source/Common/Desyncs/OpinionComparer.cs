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

        for (int i = 0; i < refMaps.Count; i++)
        {
            var diff = DescribeRandomDiff(refMaps[i].randomStates, otherMaps[i].randomStates);
            if (diff != null)
            {
                verdict.hard = $"Wrong random state on map {refMaps[i].mapId}: {diff}";
                verdict.hardLane = "rng";
                verdict.hardMapId = refMaps[i].mapId;
                return verdict;
            }
        }

        var worldDiff = DescribeRandomDiff(reference.worldRandomStates, other.worldRandomStates);
        if (worldDiff != null)
        {
            verdict.hard = $"Wrong random state for the world: {worldDiff}";
            verdict.hardLane = "rng";
            return verdict;
        }

        var cmdDiff = DescribeRandomDiff(reference.commandRandomStates, other.commandRandomStates);
        if (cmdDiff != null)
        {
            verdict.hard = $"Random state from commands doesn't match: {cmdDiff}";
            verdict.hardLane = "rng";
            return verdict;
        }

        CompareLanes(reference.lanes ?? [], other.lanes ?? [], verdict);
        CompareFaults(reference.faults ?? [], other.faults ?? [], verdict);

        return verdict;
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
    public static string? DescribeRandomDiff(List<ulong>? a, List<ulong>? b)
    {
        a ??= [];
        b ??= [];
        int n = System.Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            if (a[i] == b[i]) continue;
            uint seedA = (uint)a[i], seedB = (uint)b[i];
            uint callsA = (uint)(a[i] >> 32), callsB = (uint)(b[i] >> 32);
            return seedA != seedB
                ? $"entry {i}: seed {seedA:X8} vs {seedB:X8}, calls {callsA} vs {callsB}"
                : $"entry {i}: {callsA} vs {callsB} random calls";
        }

        return a.Count != b.Count ? $"{a.Count} vs {b.Count} entries" : null;
    }
}
