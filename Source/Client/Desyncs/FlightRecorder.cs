using System.Collections.Generic;
using System.Linq;
using System.Text;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// This game's last <see cref="Capacity"/> sync opinions (about two minutes of game time at normal speed):
    /// every random state, error and lane. Kept locally and written into the desync report, so a report shows the
    /// run-up to the desync, not just the one opinion that disagreed; with both players' reports the first tick
    /// that differs can be found.
    /// </summary>
    public class FlightRecorder
    {
        public const int Capacity = 120;

        private readonly SyncOpinion[] ring = new SyncOpinion[Capacity];
        private int next;
        private int count;

        public int Count => count;

        public void Record(ClientSyncOpinion opinion)
        {
            ring[next] = opinion.ToNet();
            next = (next + 1) % Capacity;
            if (count < Capacity) count++;
        }

        /// <summary>Oldest first.</summary>
        public IEnumerable<SyncOpinion> Opinions()
        {
            for (int i = 0; i < count; i++)
                yield return ring[(next - count + i + Capacity) % Capacity];
        }

        public SyncOpinion? Find(int startTick)
        {
            foreach (var o in Opinions())
                if (o.startTick == startTick)
                    return o;
            return null;
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Flight recorder: the last {count} opinions of this game, oldest first.");
            sb.AppendLine("Random states are seed:calls. Compare with the other player's report to find the first tick that differs.");
            foreach (var o in Opinions())
            {
                sb.AppendLine();
                sb.AppendLine($"== opinion from timer tick {o.startTick}{(o.simulating ? " (simulating)" : "")}");
                foreach (var map in o.mapRandomStates ?? [])
                    sb.AppendLine($"map {map.mapId} rng: {Format(map.randomStates)}");
                sb.AppendLine($"world rng: {Format(o.worldRandomStates)}");
                sb.AppendLine($"command rng: {Format(o.commandRandomStates)}");
                foreach (var f in o.faults ?? [])
                    sb.AppendLine($"error at {f.tick} (map {f.mapId}): {f.text}");
                foreach (var lane in o.lanes ?? [])
                    sb.AppendLine($"lane {(lane.mapId == -1 ? "world" : "map " + lane.mapId)}/{lane.lane} @ {lane.tick}: {lane.hash:X16}{(lane.hard ? " HARD" : "")}");
            }
            return sb.ToString();
        }

        /// <summary>Which lanes differ between two opinions of the same ticks, one line each.</summary>
        public static string CompareLanes(SyncOpinion local, SyncOpinion remote)
        {
            var theirs = (remote.lanes ?? []).ToDictionary(l => (l.tick, l.mapId, l.lane), l => l.hash);
            var sb = new StringBuilder();
            foreach (var lane in local.lanes ?? [])
            {
                var where = lane.mapId == -1 ? "world" : $"map {lane.mapId}";
                if (!theirs.TryGetValue((lane.tick, lane.mapId, lane.lane), out var hash))
                    sb.AppendLine($"{where}/{lane.lane} @ {lane.tick}: only here");
                else
                    sb.AppendLine($"{where}/{lane.lane} @ {lane.tick}: {(hash == lane.hash ? "same" : $"DIFFERS ({lane.hash:X16} vs {hash:X16})")}");
            }
            return sb.Length == 0 ? "No lanes in these opinions" : sb.ToString();
        }

        private static string Format(List<ulong>? states)
        {
            if (states == null || states.Count == 0) return "-";
            return string.Join(" ", states.Select(s => $"{(uint)s:X8}:{(uint)(s >> 32)}"));
        }
    }
}
