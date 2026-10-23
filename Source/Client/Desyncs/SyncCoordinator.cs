using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Multiplayer.Common.Desyncs;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    public class SyncCoordinator
    {
        public bool ShouldCollect => !Multiplayer.IsReplay;

        // Every game keys a window by the timer tick after the last window closed (they close at the same ticks),
        // not by the tick its first record came at: a window that started with a one-sided error, or with the first
        // command after a pause, was keyed differently on each game and never compared, without a word. Every load
        // makes a new coordinator, whose first window starts at the loaded timer (OnLoaded): every game reloads at a
        // join point's tick, and a joiner loads that join point's save.
        private int nextWindowStart = -1;

        private ClientSyncOpinion OpinionInBuilding =>
            currentOpinion ??= new ClientSyncOpinion(nextWindowStart >= 0 ? nextWindowStart : TickPatch.Timer)
            {
                isLocalClientsOpinion = true
            };

        /// <summary>The game finished loading at this timer tick: the first window starts there.</summary>
        public void OnLoaded(int timer)
        {
            if (currentOpinion == null) nextWindowStart = timer;
        }

        // Contains both local and remote opinions. The first opinion is the oldest one, and the last is the newest one.
        // The host player has only local opinions.
        public readonly List<ClientSyncOpinion> knownClientOpinions = [];

        private ClientSyncOpinion currentOpinion;

        public readonly FlightRecorder recorder = new();
        private readonly Dictionary<string, int> softSeen = new();

        public int lastValidTick = -1;
        public bool arbiterWasPlayingOnLastValidTick;

        private const int MaxBacklog = 30;
        // The host's opinions wait for a joiner catching up from far behind (about 5 minutes of play): with 30 the
        // oldest, the ones the joiner reaches next, were dropped first and nothing was compared until it caught up
        private const int MaxRemoteBacklog = 600;

        private int skippedPairs;

        // A new game, reload or resync: the Rand attribution window built before it belongs to no opinion of this one
        public SyncCoordinator() => RandAttribution.ResetWindow();

        public ClientSyncOpinion FinishLocalOpinion()
        {
            if (!ShouldCollect) return null;
            nextWindowStart = TickPatch.Timer + 1;
            if (currentOpinion == null) return null;
            currentOpinion.roundMode = RoundMode.GetCurrentRoundMode();
            currentOpinion.randAttr = RandAttribution.TakeWindowHashes(currentOpinion.startTick);
            var opinion = currentOpinion;
            currentOpinion = null;
            recorder.Record(opinion);
            return opinion;
        }

        /// <summary>The start of the opinion being built (Rand attribution keys its ticks by it).</summary>
        public int CurrentOpinionStart => ShouldCollect ? OpinionInBuilding.startTick : -1;

        /// <summary>The index the next random state of this list gets (a Rand attribution tick's entry).</summary>
        public int MapEntryIndex(int mapId) => ShouldCollect ? OpinionInBuilding.GetRandomStatesForMap(mapId).Count : -1;
        public int WorldEntryIndex => ShouldCollect ? OpinionInBuilding.worldRandomStates.Count : -1;
        public int CommandEntryIndex => ShouldCollect ? OpinionInBuilding.commandRandomStates.Count : -1;

        /// <summary>Adds every state lane at this tick to the opinion being built (see <see cref="SyncLanes"/>).</summary>
        public void TryAddLanes(int tick)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            SyncLanes.Compute(tick, OpinionInBuilding.lanes);
            SyncLanes.DumpLeavesIfFlagged(tick);
        }

        /// <summary>
        /// Adds a client opinion to the <see cref="knownClientOpinions"/> list and checks that it matches the most recent currently in there. If not, a desync event is fired.
        /// </summary>
        /// <param name="newOpinion">The <see cref="ClientSyncOpinion"/> to add and check.</param>
        public void AddClientOpinionAndCheckDesync(ClientSyncOpinion newOpinion)
        {
            //If we've already desynced, don't even bother
            if (Multiplayer.session.desynced) return;

            //If this is the first client opinion we have nothing to compare it with, so just add it
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions[0].isLocalClientsOpinion == newOpinion.isLocalClientsOpinion)
            {
                // In window order, whatever order they came in (a joiner gets the host's latest opinions with the
                // game and the live ones after, and adds its own while it catches up)
                int at = knownClientOpinions.FindLastIndex(o => o.startTick <= newOpinion.startTick) + 1;
                if (at > 0 && knownClientOpinions[at - 1].startTick == newOpinion.startTick)
                {
                    newOpinion.Clear();
                    return;
                }
                knownClientOpinions.Insert(at, newOpinion);
                var cap = newOpinion.isLocalClientsOpinion ? MaxBacklog : MaxRemoteBacklog;
                // The host has only its own opinions: those go quietly
                while (knownClientOpinions.Count > cap)
                    RemoveAndClearFirst();
                // Past the old backlog, the host's trace hashes go (with desync traces on they can be large; on
                // CoreCLR they only find the first differing trace, a soft difference)
                if (!newOpinion.isLocalClientsOpinion)
                    for (int i = 0; i < knownClientOpinions.Count - MaxBacklog; i++)
                        if (knownClientOpinions[i].desyncStackTraceHashes.Count > 0)
                            knownClientOpinions[i].desyncStackTraceHashes = new List<int>();
                return;
            }

            // Remove all opinions that started before this one, as it's the most up-to-date one
            while (knownClientOpinions.Count > 0 && knownClientOpinions[0].startTick < newOpinion.startTick)
                LogSkipped(knownClientOpinions[0], $"the other side's next is from tick {newOpinion.startTick}");

            // If there are none left, we don't need to compare this new one
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions.First().startTick != newOpinion.startTick)
            {
                // Ignore this opinion
                var why = $"the other side's oldest kept is from tick {knownClientOpinions[0].startTick}";
                knownClientOpinions.Insert(0, newOpinion);
                LogSkipped(newOpinion, why);
                return;
            }

            // If these two contain the same tick range - i.e., they start at the same time, because they should
            // continue to the current tick, then do a comparison.
            var oldOpinion = knownClientOpinions.RemoveFirst();

            // Actually do the comparison to find any desync
            var desyncMessage = oldOpinion.CheckForDesync(newOpinion);

            if (desyncMessage != null)
            {
                MpLog.Log($"Desynced after last valid tick {lastValidTick}: {desyncMessage}");
                Multiplayer.session.desynced = true;
                // Keep this window's Rand attribution before the ring moves on or a resync reloads
                RandAttribution.Freeze(oldOpinion.startTick);
                TickPatch.ClearSimulating();
                OnMainThread.Enqueue(() => HandleDesync(oldOpinion, newOpinion, desyncMessage));
            }
            else
            {
                foreach (var soft in oldOpinion.softDifferences.Concat(newOpinion.softDifferences).Distinct())
                {
                    var key = FaultSignature.Normalize(soft);
                    softSeen.TryGetValue(key, out var seen);
                    softSeen[key] = seen + 1;
                    IdLedger.OnLaneDiffers(soft);
                    if (seen % 50 == 0)
                        MpLog.Warn($"Sync check at tick {oldOpinion.startTick} (play continues): {soft}" + (seen > 0 ? $" (seen {seen + 1} times)" : ""));
                }

                // Update fields
                lastValidTick = oldOpinion.startTick;
                arbiterWasPlayingOnLastValidTick = Multiplayer.session.ArbiterPlaying;

                // Return inner data to the pool
                oldOpinion.Clear();
            }
        }

        private void RemoveAndClearFirst()
        {
            var opinion = knownClientOpinions.RemoveFirst();
            opinion.Clear();
        }

        /// <summary>Drops the first opinion, which no opinion of the other side will pair with, and says so (rate limited).</summary>
        private void LogSkipped(ClientSyncOpinion opinion, string why)
        {
            RemoveAndClearFirst();
            if (skippedPairs++ < 5 || skippedPairs % 100 == 0)
                MpLog.Log($"Sync check skipped for the {(opinion.isLocalClientsOpinion ? "local" : "host's")} opinion of the window from tick {opinion.startTick}: " +
                          $"no opinion of the other side for it ({why}; {skippedPairs} skipped so far, last valid tick {lastValidTick})");
        }

        /// <summary>
        /// Called by <see cref="AddClientOpinionAndCheckDesync"/> if the newly added opinion doesn't match with what other ones.
        /// </summary>
        /// <param name="oldOpinion">The first up-to-date client opinion present in <see cref="knownClientOpinions"/>, that disagreed with the new one</param>
        /// <param name="newOpinion">The opinion passed to <see cref="AddClientOpinionAndCheckDesync"/> that disagreed with the currently known opinions.</param>
        /// <param name="desyncMessage">The error message that explains exactly what desynced.</param>
        private void HandleDesync(ClientSyncOpinion oldOpinion, ClientSyncOpinion newOpinion, string desyncMessage)
        {
            // Identify which of the two sync infos is local and which is the remote.
            var local = oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;
            var remote = !oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;

            var diffAt = FindTraceHashesDiffTick(local, remote, out var found);
            var verdict = oldOpinion.verdict;
            Multiplayer.Client.Send(DesyncedPacket(local.startTick, diffAt, desyncMessage, verdict));

            // A clock or a command count that differs comes from the commands: say which ran here
            bool timeLane = verdict?.hardLane == "time";
            bool commandCount = verdict != null && verdict.rngDiffs.Any(d => d.scope == RandAttrScope.Commands);
            if (timeLane || commandCount)
                MpLog.Log(SaveableDesyncInfo.CommandReport(local.startTick, lastValidTick));

            DesyncRecovery.OnDesync(desyncMessage, new SaveableDesyncInfo(this, local, remote, diffAt, found, desyncMessage));
        }

        /// <summary>The verdict for the server: the host logs it and the referee asks both games for what differs.</summary>
        public static ClientDesyncedPacket DesyncedPacket(int tick, int diffAt, string message, OpinionVerdict verdict) =>
            new(tick, diffAt)
            {
                message = SyncReferee.TruncateUtf8(message ?? "", ClientDesyncedPacket.MaxMessageBytes),
                lane = verdict?.hardLane ?? "",
                mapId = verdict?.hardMapId ?? -1,
                rngScope = verdict is { rngDiffs.Count: > 0 } ? verdict.rngDiffs[0].scope : ClientDesyncedPacket.NoRng,
                rngEntry = verdict is { rngDiffs.Count: > 0 } ? verdict.rngDiffs[0].entry : -1,
                laneDiffs = verdict?.laneDiffs.Take(64).Select(d => $"{d.mapId}|{d.lane}").ToArray() ?? [],
                extra = verdict?.extra.Take(ClientDesyncedPacket.MaxExtra).Select(e => SyncReferee.TruncateUtf8(e, 1000)).ToArray() ?? []
            };

        private static int FindTraceHashesDiffTick(ClientSyncOpinion local, ClientSyncOpinion remote, out bool found)
        {
            found = true;
            //Find the length of whichever stack trace is shorter.
            var localCount = local.desyncStackTraceHashes.Count;
            var remoteCount = remote.desyncStackTraceHashes.Count;
            int count = Math.Min(localCount, remoteCount);

            //Find the point at which the hashes differ - this is where the desync occurred.
            for (int i = 0; i < count; i++)
                if (local.desyncStackTraceHashes[i] != remote.desyncStackTraceHashes[i])
                    return i;

            found = false;
            if (localCount != remoteCount)
                return count - 1;

            return -1;
        }

        /// <summary>
        /// Adds a random state to the commandRandomStates list
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddCommandRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.commandRandomStates.Add(state);
        }

        /// <summary>
        /// Adds a random state to the worldRandomStates list
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddWorldRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.worldRandomStates.Add(state);
        }

        /// <summary>
        /// Adds a random state to the list of the map random state handler for the map with the given id
        /// </summary>
        /// <param name="map">The map id to add the state to</param>
        /// <param name="state">The state to add</param>
        public void TryAddMapRandomState(int map, ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.GetRandomStatesForMap(map).Add(state);
        }

        public const int MaxFaultsPerOpinion = 32;

        /// <summary>
        /// Records an error logged while simulating. Both games should log the same errors on the same ticks;
        /// one that only one game logged means that game ran a code path the other didn't.
        /// </summary>
        public void TryAddFault(int mapId, string message)
        {
            if (!ShouldCollect) return;

            var opinion = OpinionInBuilding;
            opinion.TryMarkSimulating();
            if (opinion.faults.Count >= MaxFaultsPerOpinion) return;

            var text = FaultSignature.Normalize(message);
            opinion.faults.Add(new FaultEvent
            {
                tick = TickPatch.Timer,
                mapId = mapId,
                signature = FaultSignature.Hash(text),
                text = text.Length > 300 ? text.Substring(0, 300) : text
            });
        }

        /// <summary>
        /// Logs an item to aid in desync debugging.
        /// </summary>
        /// <param name="info1">Information to be logged</param>
        /// <param name="info2">Information to be logged</param>
        public void TryAddInfoForDesyncLog(string info1, string info2)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            int hash = Gen.HashCombineInt(info1.GetHashCode(), info2.GetHashCode());

            OpinionInBuilding.desyncStackTraces.Add(new StackTraceLogItemObj {
                tick = TickPatch.Timer,
                hash = hash,
                info1 = info1,
                info2 = info2,
            });

            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        public void TryAddStackTraceForDesyncLogRaw(StackTraceLogItemRaw item, int depth, int hashIn, string moreInfo = null, bool hashStack = true, string kind = null)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            item.depth = depth;
            item.ticksGame = Find.TickManager.ticksGameInt;
            item.rngState = Rand.StateCompressed;
            item.tick = TickPatch.Timer;
            item.factionName = Faction.OfPlayer?.Name ?? string.Empty;
            item.moreInfo = moreInfo;
            item.kind = kind;

            var thing = ThingContext.Current;
            if (thing != null)
            {
                item.thingDef = thing.def;
                item.thingId = thing.thingIDNumber;
            }

            // A stack that isn't stable between processes (CoreCLR's managed traces) stays out of the hash, so
            // only the random state is compared; the stack is still kept for the report.
            var hash = hashStack
                ? Gen.HashCombineInt(hashIn, depth, (int)(item.rngState >> 32), (int)item.rngState)
                : Gen.HashCombineInt(item.ticksGame, 0, (int)(item.rngState >> 32), (int)item.rngState);
            item.hash = hash;

            OpinionInBuilding.desyncStackTraces.Add(item);

            // Track & network trace hash, for comparison with other opinions.
            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        public static string MethodNameWithIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000]
            // rawName = rawName.Substring(0, rawName.LastIndexOf(']') + 1);

            return rawName;
        }

        public static string MethodNameWithoutIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil ()
            // rawName = rawName.Substring(0, rawName.LastIndexOf('['));

            return rawName;
        }
    }
}
