using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client
{

    public class ClientSyncOpinion(int startTick)
    {
        public bool isLocalClientsOpinion;

        public int startTick = startTick;
        public List<ulong> commandRandomStates = new();
        public List<ulong> worldRandomStates = new();
        public List<MapRandomStateData> mapStates = new();
        public List<FaultEvent> faults = new();
        public List<LaneHash> lanes = new();
        public List<RandAttrHash> randAttr = new();

        // Serialized only after a desync to reduce bandwidth usage in regular gameplay (only the hashes are used) and
        // help with debugging in case something goes wrong.
        public List<StackTraceLogItem> desyncStackTraces = new();
        public List<int> desyncStackTraceHashes = new();
        public bool simulating;
        public RoundModeEnum roundMode;

        // What the last CheckForDesync found that doesn't stop play.
        public List<string> softDifferences = new();
        // The last CheckForDesync's whole verdict (the hard difference's lane, map and random state list)
        public OpinionVerdict verdict;

        public string CheckForDesync(ClientSyncOpinion other)
        {
            var reference = isLocalClientsOpinion ? other : this;
            var checkedOne = isLocalClientsOpinion ? this : other;
            var verdict = OpinionComparer.Compare(reference.ToNet(), checkedOne.ToNet());
            this.verdict = verdict;
            softDifferences = verdict.soft;

            if (verdict.hard != null)
                return verdict.hard;

            // On CoreCLR (RimWorld 1.7) the stacks in trace hashes differ between processes, so there a trace mismatch
            // alone is not a desync; the traces are still collected for the report.
            if (!simulating && !other.simulating && desyncStackTraceHashes.Any() && other.desyncStackTraceHashes.Any())
            {
                if (Native.IsMono)
                {
                    if (!desyncStackTraceHashes.SequenceEqual(other.desyncStackTraceHashes))
                        return "Trace hashes don't match";
                }
                else
                {
                    // CoreCLR traces hash only the tick and the random state, which is stable between processes but
                    // also sees Rand calls that leave the state alone (ValueSeeded): one side running a seeded roll
                    // the other didn't (Desync-06, hours before Desync-07). Reported, not a desync. The host sends
                    // its traces in blocks (TraceBlocks), so this names the block they part in.
                    var at = TraceBlocks.FirstDifference(desyncStackTraceHashes, other.desyncStackTraceHashes, out _);
                    if (at >= 0)
                        softDifferences.Add($"trace lane differs at trace {at} ({TraceBlocks.Count(desyncStackTraceHashes)} vs " +
                                            $"{TraceBlocks.Count(other.desyncStackTraceHashes)} traces)");
                }
            }

            return null;
        }

        public List<ulong> GetRandomStatesForMap(int mapId)
        {
            var result = mapStates.Find(m => m.mapId == mapId);
            if (result != null) return result.randomStates;
            mapStates.Add(result = new MapRandomStateData(mapId));
            return result.randomStates;
        }

        private static readonly Binder<SyncOpinion> Binder = BinderOf.Identity<SyncOpinion>();

        public byte[] Serialize() => Binder.Serialize(ToNet());

        public static ClientSyncOpinion FromNet(SyncOpinion sync) => new(sync.startTick)
        {
            commandRandomStates = sync.commandRandomStates ?? [],
            worldRandomStates = sync.worldRandomStates ?? [],
            mapStates = (sync.mapRandomStates ?? []).Select(state => new MapRandomStateData(state.mapId)
                { randomStates = state.randomStates ?? [] }).ToList(),
            desyncStackTraceHashes = sync.traceHashes ?? [],
            simulating = sync.simulating,
            roundMode = sync.roundMode,
            faults = sync.faults ?? [],
            lanes = sync.lanes ?? [],
            randAttr = sync.randAttr ?? []
        };

        public SyncOpinion ToNet() => new()
        {
            startTick = startTick,
            commandRandomStates = commandRandomStates,
            worldRandomStates = worldRandomStates,
            mapRandomStates = mapStates.Select(state => new MapRandomState
                { mapId = state.mapId, randomStates = state.randomStates }).ToList(),
            traceHashes = desyncStackTraceHashes,
            simulating = simulating,
            roundMode = roundMode,
            faults = faults,
            lanes = lanes,
            randAttr = randAttr
        };

        public static ClientSyncOpinion Deserialize(ByteReader data) =>
            FromNet(Binder.Deserialize(data.ReadRaw(data.Left)));

        public void TryMarkSimulating()
        {
            if (TickPatch.Simulating)
                simulating = true;
        }

        /// <param name="span">How many traces from diffAt the difference can be in: off Mono, diffAt is the first trace of a
        /// block of <see cref="TraceBlocks.Size"/> (the host sends its traces in blocks), so the range covers the block</param>
        public string GetFormattedStackTracesForRange(int diffAt, int span = 0)
        {
            var start = Math.Max(0, diffAt - Multiplayer.settings.desyncTracesRadius);
            var end = diffAt + span + Multiplayer.settings.desyncTracesRadius;
            var traceId = start;

            return
                $"Trace count: {desyncStackTraces.Count}\nTrace of first desynced map random state:\n{diffAt} {desyncStackTraces.ElementAtOrDefault(diffAt)}" +
                "\n\nContext traces:\n" +
                desyncStackTraces
                .Skip(start)
                .Take(end - start)
                .Join(a => traceId++ + " " + a, "\n\n");
        }

        public void Clear()
        {
            for (int i = 0; i < desyncStackTraces.Count; i++)
                desyncStackTraces[i].Dispose();
        }
    }
}
