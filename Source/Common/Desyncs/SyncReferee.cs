using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common;

/// <summary>
/// Compares every player's sync opinions with the reference game's (the arbiter's when one plays, else the
/// host's) on the server, and tells both the reference player and the other player when they differ. Before this
/// only the clients compared, so the host never heard which part of its own game disagreed, and an opinion that
/// found no partner was dropped without a word.
/// </summary>
public class SyncReferee(MultiplayerServer server)
{
    private const int MaxKept = 256;
    private const int SoftRepeatEvery = 50;

    private readonly Dictionary<int, SyncOpinion> reference = new();
    private readonly Queue<int> referenceOrder = new();
    private readonly Dictionary<int, Dictionary<int, SyncOpinion>> pending = new();
    private readonly Dictionary<string, int> softSeen = new();
    private readonly HashSet<int> hardReported = new();
    private int unpaired;

    public int PairsCompared { get; private set; }
    public int Differences { get; private set; }

    public void OnOpinion(ServerPlayer player, byte[] raw, bool isReference)
    {
        SyncOpinion opinion;
        try
        {
            opinion = BinderOf.Identity<SyncOpinion>().Deserialize(raw);
        }
        catch (System.Exception e)
        {
            ServerLog.Error($"Sync referee: unreadable opinion from {player.Username}: {e.Message}");
            return;
        }

        if (isReference)
        {
            if (reference.ContainsKey(opinion.startTick)) return;
            reference[opinion.startTick] = opinion;
            referenceOrder.Enqueue(opinion.startTick);
            while (referenceOrder.Count > MaxKept)
                reference.Remove(referenceOrder.Dequeue());

            foreach (var (playerId, opinions) in pending)
            {
                if (!opinions.Remove(opinion.startTick, out var waiting)) continue;
                var p = server.GetPlayer(playerId);
                if (p != null) Judge(p, opinion, waiting);
            }

            return;
        }

        if (reference.TryGetValue(opinion.startTick, out var refOpinion))
        {
            Judge(player, refOpinion, opinion);
            return;
        }

        // The reference game hasn't reached this tick yet (or never will, if the opinions started at different
        // ticks); keep it for a while.
        if (!pending.TryGetValue(player.id, out var mine))
            pending[player.id] = mine = new Dictionary<int, SyncOpinion>();
        mine[opinion.startTick] = opinion;

        if (mine.Count > MaxKept)
        {
            var oldest = mine.Keys.Min();
            mine.Remove(oldest);
            if (unpaired++ % 100 == 0)
                ServerLog.Log($"Sync referee: no reference opinion for {player.Username}'s tick {oldest} ({unpaired} unpaired so far)");
        }
    }

    public void OnPlayerReset(ServerPlayer player)
    {
        pending.Remove(player.id);
        hardReported.Remove(player.id);
    }

    private void Judge(ServerPlayer player, SyncOpinion refOpinion, SyncOpinion opinion)
    {
        PairsCompared++;
        var verdict = OpinionComparer.Compare(refOpinion, opinion);
        if (!verdict.Clean) Differences++;
        if (PairsCompared % 1000 == 0)
            ServerLog.Log($"Sync referee: {PairsCompared} opinion pairs compared, {Differences} with differences, {unpaired} unpaired");
        if (verdict.Clean) return;

        if (verdict.hard != null && hardReported.Add(player.id))
        {
            ServerLog.Log($"Sync referee: {player.Username}'s game differs from the reference at tick {opinion.startTick}: {verdict.hard}");
            Send(player, new ServerSyncVerdictPacket
            {
                playerId = player.id,
                username = player.Username,
                tick = opinion.startTick,
                hard = true,
                lane = verdict.hardLane ?? "",
                mapId = verdict.hardMapId,
                message = verdict.hard
            });
        }

        foreach (var soft in verdict.soft)
        {
            softSeen.TryGetValue(soft, out var count);
            softSeen[soft] = count + 1;
            if (count % SoftRepeatEvery != 0) continue;

            ServerLog.Log($"Sync referee (soft): {player.Username} at tick {opinion.startTick}: {soft}" +
                          (count > 0 ? $" (seen {count + 1} times)" : ""));
            Send(player, new ServerSyncVerdictPacket
            {
                playerId = player.id,
                username = player.Username,
                tick = opinion.startTick,
                hard = false,
                lane = "",
                mapId = -1,
                message = count > 0 ? $"{soft} (seen {count + 1} times)" : soft
            });
        }
    }

    private void Send(ServerPlayer player, ServerSyncVerdictPacket packet)
    {
        player.SendPacket(packet);
        foreach (var other in server.PlayingPlayers)
            if (other != player && other.IsHost)
                other.SendPacket(packet);
    }
}
