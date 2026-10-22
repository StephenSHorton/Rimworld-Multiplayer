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
public class SyncReferee
{
    private readonly MultiplayerServer server;

    public SyncReferee(MultiplayerServer server)
    {
        this.server = server;
        fingerprints = new FingerprintPairing(JudgeFingerprint,
            (playerId, joinPoint, why) => ServerLog.Log(
                $"Sync referee: not comparing {server.GetPlayer(playerId)?.Username ?? "a player"}'s fingerprint from the join point at tick {joinPoint}: {why}"));
    }

    private const int MaxKept = 256;
    private const int SoftRepeatEvery = 50;

    private readonly Dictionary<int, SyncOpinion> reference = new();
    private readonly Queue<int> referenceOrder = new();
    private readonly Dictionary<int, Dictionary<int, SyncOpinion>> pending = new();
    private readonly Dictionary<string, int> softSeen = new();
    private readonly HashSet<int> hardReported = new();
    private int unpaired;

    public int PairsCompared { get; private set; }

    /// <summary>The reference game's latest fingerprint (from its last join point), or null before the first.</summary>
    public FingerprintRow[]? ReferenceFingerprint => fingerprints.Latest;

    private readonly FingerprintPairing fingerprints;

    /// <summary>
    /// After a join point every game sends its fingerprint again; a difference from the reference game's at the
    /// same join point is reported to both players, since their games will now diverge.
    /// </summary>
    public void OnFingerprint(ServerPlayer player, int joinPoint, FingerprintRow[] rows, bool isReference)
    {
        if (isReference)
            fingerprints.OnReference(joinPoint, rows);
        else
            fingerprints.OnPlayer(player.id, joinPoint, rows);
    }

    private void JudgeFingerprint(int playerId, FingerprintRow[] referenceRows, FingerprintRow[] rows)
    {
        var player = server.GetPlayer(playerId);
        if (player == null) return;

        // After a join point both games have the same save loaded, so every row counts, warn rows too
        var (blocking, warnings) = Fingerprint.Compare(referenceRows, rows, "host", player.Username);
        // Not the Harmony row: mods patch interface and drawing code lazily (Map Preview, Show Me Your Hands), so
        // two games in sync differ there; in dev mode MpDesyncs/harmony-<player>.txt lists them for diffing
        blocking.AddRange(warnings.Where(w => !w.StartsWith("Runtime") && !w.StartsWith("Language") && !w.StartsWith("Harmony patches")));
        if (blocking.Count == 0) return;

        var text = Fingerprint.Describe(blocking, 6);
        // The message names six rows; the games dump every differing save section (2026-10-21: the fifth join
        // point's dumps held only the six named)
        const string savePrefix = Fingerprint.JoinSaveRowPrefix;
        var sections = Fingerprint.DifferingNames(referenceRows, rows)
            .Where(n => n.StartsWith(savePrefix))
            .Select(n => n.Substring(savePrefix.Length))
            .Take(ServerSyncVerdictPacket.MaxSaveSections)
            .ToArray();
        ServerLog.Log($"Sync referee: {player.Username}'s fingerprint differs from the host's after the join point:\n{text}");
        Send(player, new ServerSyncVerdictPacket
        {
            playerId = player.id,
            username = player.Username,
            tick = server.gameTimer,
            hard = false,
            lane = "fingerprint",
            mapId = -1,
            message = $"after the join point, {blocking.Count} fingerprint rows differ: {text.Replace("\n", "; ")}",
            saveSections = sections
        });
    }
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
        fingerprints.Remove(player.id);
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

        if (server.settings.desyncDebugFreeze && (verdict.hard != null || verdict.laneDiffs.Count > 0))
            HoldForDebug();

        foreach (var (mapId, lane) in verdict.laneDiffs)
            RequestLeaves(player, mapId, lane);

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
            // The same difference at later ticks is one problem, not many
            var key = $"{player.id}|{FaultSignature.Normalize(soft)}";
            softSeen.TryGetValue(key, out var count);
            softSeen[key] = count + 1;
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

    // --- Leaves: which entity differs ---

    private const int LeafLead = 30;
    private const int LeafCooldown = 1800;

    private class LeafRequest
    {
        public int id;
        public int tick;
        public int mapId;
        public string lane = "";
        public int refPlayer;
        public int otherPlayer;
        public string? refText;
        public string? otherText;
        public bool refMissed, otherMissed;
    }

    private readonly Dictionary<int, LeafRequest> leafRequests = new();
    private readonly Dictionary<(int, int, string), int> lastLeafRequest = new();
    private int nextLeafRequest;

    /// <summary>
    /// Asks the reference game and the player's to list a lane's entities at the same timer tick, a little ahead
    /// of the server's clock so neither game has passed it yet.
    /// </summary>
    private void RequestLeaves(ServerPlayer player, int mapId, string lane)
    {
        var key = (player.id, mapId, lane);
        if (lastLeafRequest.TryGetValue(key, out var last) && server.gameTimer - last < LeafCooldown) return;

        var reference = server.PlayingPlayers.FirstOrDefault(p => server.ArbiterPlaying ? p.IsArbiter : p.IsHost);
        if (reference == null || reference == player) return;

        lastLeafRequest[key] = server.gameTimer;
        var request = new LeafRequest
        {
            id = nextLeafRequest++,
            tick = server.holdTimerAt != int.MaxValue ? server.holdTimerAt : server.StampTick + LeafLead,
            mapId = mapId,
            lane = lane,
            refPlayer = reference.id,
            otherPlayer = player.id
        };
        leafRequests[request.id] = request;

        var packet = new ServerLeafRequestPacket { requestId = request.id, tick = request.tick, mapId = mapId, lane = lane };
        reference.SendPacket(packet);
        player.SendPacket(packet);

        // Drop requests nobody answered
        foreach (var stale in leafRequests.Values.Where(r => server.gameTimer - r.tick > LeafCooldown).Select(r => r.id).ToList())
            leafRequests.Remove(stale);
    }

    /// <summary>
    /// Debug freeze: stop the server's timer a little ahead, where every game still running stops too, and have
    /// each save its full game state at that tick. The two saves are the same tick of two games that differ.
    /// A join point lifts the hold.
    /// </summary>
    private void HoldForDebug()
    {
        if (server.holdTimerAt != int.MaxValue) return;

        server.holdTimerAt = server.StampTick + LeafLead;
        ServerLog.Log($"Sync referee: debug freeze, holding every game at timer tick {server.holdTimerAt} and saving their states");
        var packet = new ServerLeafRequestPacket { requestId = -1, tick = server.holdTimerAt, mapId = -1, lane = "save" };
        foreach (var p in server.PlayingPlayers)
            p.SendPacket(packet);
    }

    public void OnLeaves(ServerPlayer player, ClientLeavesPacket packet)
    {
        if (!leafRequests.TryGetValue(packet.requestId, out var request)) return;

        var text = packet.missed ? null : LeafDiff.Gunzip(packet.gzippedLines);
        if (player.id == request.refPlayer) { request.refText = text; request.refMissed = packet.missed; }
        else if (player.id == request.otherPlayer) { request.otherText = text; request.otherMissed = packet.missed; }
        else return;

        bool refDone = request.refText != null || request.refMissed;
        bool otherDone = request.otherText != null || request.otherMissed;
        if (!refDone || !otherDone) return;
        leafRequests.Remove(request.id);

        var refName = server.GetPlayer(request.refPlayer)?.Username ?? "host";
        var other = server.GetPlayer(request.otherPlayer);
        var otherName = other?.Username ?? "player";

        var diff = request.refMissed || request.otherMissed
            ? $"Could not compare: {(request.refMissed ? refName : otherName)}'s game had already passed tick {request.tick}"
            : LeafDiff.Diff(request.refText!, request.otherText!, refName, otherName);

        var where = request.mapId == -1 ? "world" : $"map {request.mapId}";
        ServerLog.Log($"Sync referee: {where} / {request.lane} at tick {request.tick}, {refName} vs {otherName}: " +
                      string.Join(" | ", diff.Split('\n').Take(6)));

        var result = new ServerLeafDiffPacket
        {
            playerId = request.otherPlayer,
            username = otherName,
            tick = request.tick,
            mapId = request.mapId,
            lane = request.lane,
            gzippedText = LeafDiff.Gzip(diff)
        };
        foreach (var p in new[] { server.GetPlayer(request.refPlayer), other })
            p?.conn.SendFragmented(result.Serialize());
    }

    private void Send(ServerPlayer player, ServerSyncVerdictPacket packet)
    {
        player.SendPacket(packet);
        foreach (var other in server.PlayingPlayers)
            if (other != player && other.IsHost)
                other.SendPacket(packet);
    }
}

/// <summary>
/// Pairs each game's fingerprint with the reference game's from the same join point. A game that reloads before the
/// reference game sends its rows first, and judging them against the reference game's rows from the join point
/// before reported every save section that changed in between (2026-10-21, the fifth join point: 63 rows). So the
/// rows wait for the reference game's rows with the same stamp; rows whose join point the reference game has moved
/// past are dropped, never judged against another join point's rows.
/// </summary>
public class FingerprintPairing(
    System.Action<int, FingerprintRow[], FingerprintRow[]> judge,
    System.Action<int, int, string> dropped)
{
    private const int KeptJoinPoints = 4;

    private readonly Dictionary<int, FingerprintRow[]> reference = new();
    private readonly Queue<int> referenceOrder = new();
    private readonly Dictionary<int, (int joinPoint, FingerprintRow[] rows)> pending = new();
    private int latestJoinPoint = int.MinValue;

    /// <summary>The reference game's rows from its latest join point, or null before the first.</summary>
    public FingerprintRow[]? Latest { get; private set; }

    public void OnReference(int joinPoint, FingerprintRow[] rows)
    {
        if (joinPoint >= latestJoinPoint)
        {
            latestJoinPoint = joinPoint;
            Latest = rows;
        }

        if (!reference.ContainsKey(joinPoint))
        {
            referenceOrder.Enqueue(joinPoint);
            while (referenceOrder.Count > KeptJoinPoints)
                reference.Remove(referenceOrder.Dequeue());
        }
        reference[joinPoint] = rows;

        foreach (var (playerId, waiting) in pending.ToList())
        {
            if (waiting.joinPoint == joinPoint)
                judge(playerId, rows, waiting.rows);
            else if (waiting.joinPoint < joinPoint)
                dropped(playerId, waiting.joinPoint, $"the host sent no rows for it before the join point at tick {joinPoint}");
            else
                continue;
            pending.Remove(playerId);
        }
    }

    public void OnPlayer(int playerId, int joinPoint, FingerprintRow[] rows)
    {
        if (reference.TryGetValue(joinPoint, out var refRows))
        {
            pending.Remove(playerId);
            judge(playerId, refRows, rows);
            return;
        }

        if (joinPoint < latestJoinPoint)
        {
            dropped(playerId, joinPoint, $"the host's rows for it are gone (its latest are from tick {latestJoinPoint})");
            return;
        }

        // The reference game hasn't finished this join point's reload yet
        if (pending.TryGetValue(playerId, out var older) && older.joinPoint != joinPoint)
            dropped(playerId, older.joinPoint, $"a newer join point (tick {joinPoint}) came first");
        pending[playerId] = (joinPoint, rows);
    }

    public void Remove(int playerId) => pending.Remove(playerId);
}
