using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Desyncs;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Always-on Rand attribution: which owner (thing, map component, lord, quest, incident, command...), in which
    /// tick method and phase, made how many Rand calls in every map tick, world tick and command.
    ///
    /// Rand calls themselves cost nothing extra: the call count is read from Rand's state at every context boundary
    /// (a thing's tick starting or ending, a map system's phase, ...) and the difference since the last boundary is
    /// charged to the frame that was on top. Only boundaries at the scope's base Rand depth count, so calls inside a
    /// nested PushState block (which the outer state never sees) are left out, exactly as the compared random state
    /// leaves them out. The deltas of a tick add up to the change in that scope's random state by construction.
    ///
    /// Each opinion carries one hash per scope over the records (merged to tier 1 keys, so tier 2 armed on one game
    /// a moment before the other changes nothing). On a desync the referee asks both games for their tables of the
    /// window and names the first record that differs; that also arms a watch which captures managed stacks for the
    /// matching frame, so the next occurrence names the exact call.
    /// </summary>
    public static class RandAttribution
    {
        // --- Gates (read by every patch; plain statics) ---

        /// <summary>Inside a map tick, world tick or command on the main thread with attribution on.</summary>
        public static bool active;
        /// <summary>A watch is armed: the Rand postfix checks the top frame and captures a stack on a match.</summary>
        public static bool watchArmed;
        /// <summary>Tier 2 phases (pawn trackers, job drivers, comps) are recorded.</summary>
        public static bool tier2;
        /// <summary>Kill switch: a file MpRandAttributionOff.flag in the save data folder.</summary>
        public static bool disabled;
        public static int mainThreadId = -1;

        public const int MaxDepth = 128;
        public const int RecordCapacity = 1 << 18;
        public const int HeaderCapacity = 4096;
        public const int MaxRecordsPerTick = 16384;

        // --- Frames ---

        private struct Frame
        {
            public int owner;
            public ushort def;
            public ushort basePhase;
            public ushort phase;
            public ushort job;
            public sbyte toil;
            public RandAttrKind kind;
            public RandAttrMethod method;

            public bool SameRecord(in Frame o) =>
                owner == o.owner && def == o.def && basePhase == o.basePhase && phase == o.phase && job == o.job &&
                toil == o.toil && kind == o.kind && method == o.method;

            public bool SameUnit(in Frame o) =>
                owner == o.owner && def == o.def && basePhase == o.basePhase && job == o.job && toil == o.toil &&
                kind == o.kind && method == o.method;
        }

        private struct Record
        {
            public Frame frame;
            public int calls;
        }

        private struct TickHeader
        {
            public int scope;
            public int opinionStart;
            public int entry;
            public int ticksGame;
            public int timer;
            public ulong stateBefore, stateAfter;
            public long firstRecord;
            public int count;
            public bool complete;
        }

        private static readonly Frame[] stack = new Frame[MaxDepth];
        private static int depth;
        private static int baseDepth;
        private static ulong last;

        private static readonly Record[] records = new Record[RecordCapacity];
        private static long recordSeq;
        private static readonly TickHeader[] headers = new TickHeader[HeaderCapacity];
        private static long headerSeq;
        private static int curHeader = -1;
        private static int recordsThisTick;
        private static long lastRecordSeq = -1;
        private static bool overflowed;
        private static bool reseeded;

        // The current hash unit (records merged by tier 1 key)
        private static bool unitOpen;
        private static Frame unitFrame;
        private static int unitCalls;
        private static int unitOrdinal;

        // This window's hash of the scope being recorded, and every scope's
        private static ulong curHash;
        private static int curUnits;
        private static readonly Dictionary<int, (ulong hash, int records)> windowHashes = new();

        // --- Phase registry ---

        private static readonly List<string> phaseNames = ["-"];
        private static readonly List<int> phaseHashes = [0];
        private static readonly Dictionary<string, ushort> phaseIds = new() { ["-"] = 0 };
        private static readonly Dictionary<Type, ushort> typePhases = new();

        /// <summary>A phase name's id (registered on first use). Mods mark their own ticks with it.</summary>
        public static ushort RegisterPhase(string name)
        {
            name = RandAttrLine.Token(name);
            if (phaseIds.TryGetValue(name, out var id)) return id;
            if (phaseNames.Count >= ushort.MaxValue) return 0;
            id = (ushort)phaseNames.Count;
            phaseNames.Add(name);
            phaseHashes.Add(GenText.StableStringHash(name));
            phaseIds[name] = id;
            return id;
        }

        public static string PhaseName(ushort id) => id < phaseNames.Count ? phaseNames[id] : "?";

        /// <summary>The phase named after a type (with a prefix), cached per type.</summary>
        public static ushort PhaseOf(Type type, string prefix = "")
        {
            if (typePhases.TryGetValue(type, out var id)) return id;
            return typePhases[type] = RegisterPhase(prefix + type.Name);
        }

        // --- Stats ---

        private static long ticksRecorded, recordsTotal, boundariesTotal, timedTicks, timedTicksTotal;
        private static long timedBoundaryTicks;
        private static int boundariesThisTick;
        private static bool timing;
        public static int offThreadCalls;
        private static int offThreadLogged;
        private static int selfCheckErrors;
        private static long framesRepaired;
        private static int nestedBegins;
        private static readonly ConcurrentQueue<(bool error, string text)> pendingLogs = new();
        private static readonly List<string> recentSelfCheck = new();

        // --- Lifecycle ---

        public static void Init()
        {
            mainThreadId = Environment.CurrentManagedThreadId;
            try
            {
                disabled = File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath, "MpRandAttributionOff.flag"));
                tier2 = File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath, "MpRandAttributionTier2.flag"));
            }
            catch (Exception e)
            {
                Log.Warning($"Rand attribution: couldn't read the flag files: {e.Message}");
            }

            if (disabled)
                Log.Message("Multiplayer: Rand attribution is off (MpRandAttributionOff.flag)");

            RandWatch.Load();
        }

        private static bool ShouldRecord =>
            !disabled && Multiplayer.Client != null && Multiplayer.game != null && Multiplayer.game.sync.ShouldCollect &&
            Environment.CurrentManagedThreadId == mainThreadId;

        // --- Scopes ---

        /// <summary>
        /// Starts recording a map tick (scope = map id), the world's tick (-1) or a command (-2). Called right after
        /// the scope's context put its Rand state in place. <paramref name="lastEnd"/> is this Rand state's value at
        /// the end of its last tick or command, if known: any change since is recorded as calls between its ticks.
        /// </summary>
        public static void Begin(int scope, int entry, ushort rootPhase, ulong lastEnd, bool lastEndKnown)
        {
            if (!ShouldRecord) return;
            var sync = Multiplayer.game.sync;
            BeginCore(scope, entry, rootPhase, lastEnd, lastEndKnown, sync.CurrentOpinionStart, Find.TickManager?.ticksGameInt ?? 0, TickPatch.Timer);
        }

        // Begin without the game: the harness's smoke test drives this directly
        private static void BeginCore(int scope, int entry, ushort rootPhase, ulong lastEnd, bool lastEndKnown, int opinionStart, int ticksGame, int timer)
        {
            if (active)
            {
                if (nestedBegins++ < 20) Enqueue($"Rand attribution: scope {RandAttrScope.Name(scope)} began inside {RandAttrScope.Name(headers[curHeader].scope)}; closing that one");
                End();
            }

            baseDepth = Rand.stateStack.Count;
            last = Rand.StateCompressed;
            timing = (ticksRecorded & 63) == 0;
            long t0 = timing ? Stopwatch.GetTimestamp() : 0;

            curHeader = (int)(headerSeq++ % HeaderCapacity);
            headers[curHeader] = new TickHeader
            {
                scope = scope,
                opinionStart = opinionStart,
                entry = entry,
                ticksGame = ticksGame,
                timer = timer,
                stateBefore = last,
                firstRecord = recordSeq
            };

            recordsThisTick = 0;
            lastRecordSeq = -1;
            overflowed = false;
            reseeded = false;
            unitOpen = false;
            boundariesThisTick = 0;

            windowHashes.TryGetValue(scope, out var acc);
            curHash = acc.hash == 0 && acc.records == 0 ? 14695981039346656037UL : acc.hash;
            curUnits = acc.records;

            depth = 1;
            stack[0] = new Frame { kind = RandAttrKind.Root, basePhase = rootPhase, toil = -1 };

            if (lastEndKnown && lastEnd != last)
            {
                uint calls = (uint)lastEnd == (uint)last && (uint)(last >> 32) >= (uint)(lastEnd >> 32)
                    ? (uint)(last >> 32) - (uint)(lastEnd >> 32)
                    : (uint)(last >> 32);
                Append(new Frame { kind = RandAttrKind.BetweenTicks, toil = -1 }, (int)calls);
            }

            active = true;
            if (timing) timedBoundaryTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>Ends the scope begun last; returns the Rand state at its end (the scope's next lastEnd).</summary>
        public static ulong End()
        {
            if (!active) return Rand.StateCompressed;
            long t0 = timing ? Stopwatch.GetTimestamp() : 0;

            Sample();
            FoldUnit();
            active = false;

            ref var h = ref headers[curHeader];
            h.stateAfter = Rand.stateStack.Count == baseDepth ? Rand.StateCompressed : last;
            h.count = recordsThisTick;
            h.complete = true;
            windowHashes[h.scope] = (curHash, curUnits);

            ticksRecorded++;
            recordsTotal += recordsThisTick;
            boundariesTotal += boundariesThisTick;

            // Self-check: the calls charged add up to the change in the random state
            if (!overflowed && !reseeded && (uint)h.stateAfter == (uint)h.stateBefore)
            {
                long sum = 0;
                for (long s = h.firstRecord; s < h.firstRecord + h.count; s++)
                {
                    ref var r = ref records[s % RecordCapacity];
                    if (r.frame.kind != RandAttrKind.BetweenTicks) sum += r.calls;
                }
                long expected = (long)(uint)(h.stateAfter >> 32) - (uint)(h.stateBefore >> 32);
                if (sum != expected && selfCheckErrors++ < 20)
                {
                    var msg = $"Rand attribution lost {expected - sum} calls in {RandAttrScope.Name(h.scope)} at tick {h.ticksGame} " +
                              $"(phase {PhaseName(stack[0].basePhase)}): an unbalanced Rand.PushState/PopState or a Rand state set at the base level";
                    Enqueue(msg);
                    lock (recentSelfCheck) { recentSelfCheck.Add(msg); if (recentSelfCheck.Count > 20) recentSelfCheck.RemoveAt(0); }
                }
            }

            if (timing)
            {
                timedBoundaryTicks += Stopwatch.GetTimestamp() - t0;
                timedTicks++;
            }

            return h.stateAfter;
        }

        /// <summary>Charges the calls made since the last boundary to the top frame.</summary>
        private static void Sample()
        {
            boundariesThisTick++;
            if (Rand.stateStack.Count != baseDepth) return;

            ulong s = Rand.StateCompressed;
            if (s == last) return;

            uint iterNow = (uint)(s >> 32), iterLast = (uint)(last >> 32);
            if ((uint)s != (uint)last || iterNow < iterLast)
            {
                reseeded = true;
                var f = stack[depth - 1];
                f.kind = RandAttrKind.Reseed;
                Append(f, (int)iterNow, mergeable: false);
            }
            else
            {
                Add((int)(iterNow - iterLast));
            }

            last = s;
        }

        private static void Add(int delta)
        {
            ref var top = ref stack[depth - 1];
            if (lastRecordSeq >= 0 && records[lastRecordSeq % RecordCapacity].frame.SameRecord(top))
            {
                records[lastRecordSeq % RecordCapacity].calls += delta;
                unitCalls += delta;
                return;
            }

            Append(top, delta);
        }

        private static void Append(in Frame frame, int calls, bool mergeable = true)
        {
            if (recordsThisTick >= MaxRecordsPerTick)
            {
                if (!overflowed)
                {
                    overflowed = true;
                    FoldUnit();
                    var f = new Frame { kind = RandAttrKind.Overflow, toil = -1 };
                    lastRecordSeq = recordSeq;
                    records[recordSeq++ % RecordCapacity] = new Record { frame = f, calls = calls };
                    recordsThisTick++;
                    unitOpen = true;
                    unitFrame = f;
                    unitCalls = calls;
                    unitOrdinal = recordsThisTick - 1;
                }
                else
                {
                    records[lastRecordSeq % RecordCapacity].calls += calls;
                    unitCalls += calls;
                }
                return;
            }

            if (!unitOpen || !mergeable || !unitFrame.SameUnit(frame) || unitFrame.kind == RandAttrKind.Reseed)
            {
                FoldUnit();
                unitOpen = true;
                unitFrame = frame;
                unitCalls = 0;
                unitOrdinal = recordsThisTick;
            }

            unitCalls += calls;
            lastRecordSeq = recordSeq;
            records[recordSeq++ % RecordCapacity] = new Record { frame = frame, calls = calls };
            recordsThisTick++;
            if (!mergeable) lastRecordSeq = -1;
        }

        private static void FoldUnit()
        {
            if (!unitOpen) return;
            unitOpen = false;
            ref var f = ref unitFrame;
            ulong h = curHash;
            h = Mix(h, headers[curHeader].entry);
            h = Mix(h, (int)f.kind);
            h = Mix(h, f.owner);
            h = Mix(h, f.def);
            h = Mix(h, f.basePhase < phaseHashes.Count ? phaseHashes[f.basePhase] : 0);
            h = Mix(h, (int)f.method);
            h = Mix(h, f.job);
            h = Mix(h, f.toil);
            h = Mix(h, unitCalls);
            curHash = h;
            curUnits++;
        }

        private static ulong Mix(ulong h, int v)
        {
            for (int i = 0; i < 4; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
            return h;
        }

        // --- Frame pushes (pop-to-depth: each push returns its depth for the matching pop) ---

        public static int PushThing(Thing thing, RandAttrMethod method)
        {
            if (!active) return -1;
            long t0 = timing ? Stopwatch.GetTimestamp() : 0;
            Sample();
            int d = depth;
            if (d < MaxDepth)
            {
                ref var f = ref stack[d];
                f.kind = RandAttrKind.Thing;
                f.owner = thing.thingIDNumber;
                f.def = thing.def.shortHash;
                f.method = method;
                f.basePhase = 0;
                f.phase = 0;
                f.job = 0;
                f.toil = -1;
                if (thing is Pawn { jobs: { curJob: { } job } jobs })
                {
                    f.job = job.def.shortHash;
                    var toil = jobs.curDriver?.CurToilIndex ?? -1;
                    f.toil = (sbyte)(toil > 127 ? 127 : toil);
                }
                depth = d + 1;
            }
            if (timing) timedBoundaryTicks += Stopwatch.GetTimestamp() - t0;
            return d;
        }

        public static int PushOwner(RandAttrKind kind, int owner, ushort def, ushort basePhase, RandAttrMethod method = RandAttrMethod.None)
        {
            if (!active) return -1;
            long t0 = timing ? Stopwatch.GetTimestamp() : 0;
            Sample();
            int d = depth;
            if (d < MaxDepth)
            {
                stack[d] = new Frame { kind = kind, owner = owner, def = def, basePhase = basePhase, method = method, toil = -1 };
                depth = d + 1;
            }
            if (timing) timedBoundaryTicks += Stopwatch.GetTimestamp() - t0;
            return d;
        }

        /// <summary>A phase inside the current owner: a map system (tier 1) or a tracker, driver or comp (tier 2).</summary>
        public static int PushPhase(ushort phase, bool isTier2)
        {
            if (!active || (isTier2 && !tier2)) return -1;
            long t0 = timing ? Stopwatch.GetTimestamp() : 0;
            Sample();
            int d = depth;
            if (d < MaxDepth)
            {
                stack[d] = stack[d - 1];
                if (isTier2) stack[d].phase = phase;
                else
                {
                    stack[d].basePhase = phase;
                    stack[d].phase = 0;
                }
                depth = d + 1;
            }
            if (timing) timedBoundaryTicks += Stopwatch.GetTimestamp() - t0;
            return d;
        }

        public static void PopTo(int d)
        {
            if (d < 1 || !active) return;
            long t0 = timing ? Stopwatch.GetTimestamp() : 0;
            Sample();
            if (d < depth) depth = d;
            if (timing) timedBoundaryTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>Moves the scope's root to the next step of the tick.</summary>
        public static void Phase(ushort phase)
        {
            if (!active) return;
            Sample();
            stack[0].basePhase = phase;
            if (depth > 1)
            {
                // A frame whose postfix an exception skipped (vanilla catches inside MapPreTick/MapPostTick):
                // repaired here by design, counted rather than logged every tick
                framesRepaired++;
                depth = 1;
            }
        }

        /// <summary>Names the command's handler once it is known (sync handlers).</summary>
        public static void CommandPhase(ushort phase)
        {
            if (!active || headers[curHeader].scope != RandAttrScope.Commands) return;
            Sample();
            stack[0].basePhase = phase;
        }

        // --- Off the main thread ---

        /// <summary>A Rand call off the main thread while a scope is recorded: a desync of its own.</summary>
        public static void OffThread()
        {
            var n = Interlocked.Increment(ref offThreadCalls);
            if (Interlocked.Increment(ref offThreadLogged) <= 5 || n % 10000 == 0)
            {
                var scope = curHeader >= 0 ? RandAttrScope.Name(headers[curHeader].scope) : "?";
                pendingLogs.Enqueue((true, $"Rand called off the main thread during {scope}'s tick ({n} times so far): " +
                                    IdLedger.Describe(new StackTrace(2, false), 16)));
            }
        }

        private static void Enqueue(string message) => pendingLogs.Enqueue((true, message));

        /// <summary>A warning logged after the tick (see <see cref="Update"/>).</summary>
        public static void Note(string message) => pendingLogs.Enqueue((false, message));

        /// <summary>Every frame, outside the simulation: logs what the simulation queued (logging inside it would land in the fault lane).</summary>
        public static void Update()
        {
            while (pendingLogs.TryDequeue(out var msg))
            {
                if (msg.error) MpLog.Error(msg.text);
                else MpLog.Warn(msg.text);
            }
            RandInjectPatch.Poll();
        }

        public static string StatsLine()
        {
            if (ticksRecorded == 0) return disabled ? "rand attribution: off" : "rand attribution: nothing recorded yet";
            double us = timedTicks == 0 ? 0 : timedBoundaryTicks * 1_000_000.0 / Stopwatch.Frequency / timedTicks;
            return $"rand attribution: avg {recordsTotal / (double)ticksRecorded:F1} records/tick, {boundariesTotal / (double)ticksRecorded:F1} " +
                   $"boundaries/tick, {us:F1} us/tick (timed, upper bound), tier2={(tier2 ? "on" : "off")}, off-thread calls {offThreadCalls}, " +
                   $"self-check errors {selfCheckErrors}, frames left open by exceptions {framesRepaired}";
        }

        public static string StatsReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine(StatsLine());
            sb.AppendLine($"ticks recorded {ticksRecorded}, records {recordsTotal}, boundaries {boundariesTotal}, timed ticks {timedTicks}");
            sb.AppendLine($"disabled={disabled} tier2={tier2} watch={RandWatch.Describe()}");
            lock (recentSelfCheck)
            {
                sb.AppendLine(recentSelfCheck.Count == 0 ? "No self-check errors" : "Self-check errors:");
                foreach (var e in recentSelfCheck) sb.AppendLine("  " + e);
            }
            return sb.ToString();
        }

        /// <summary>This window's hash per scope, for the opinion being finished; starts the next window.</summary>
        public static List<RandAttrHash> TakeWindowHashes()
        {
            if (windowHashes.Count == 0) return [];
            var list = windowHashes.OrderBy(kv => kv.Key).Select(kv => new RandAttrHash { scope = kv.Key, hash = kv.Value.hash, records = kv.Value.records }).ToList();
            windowHashes.Clear();
            return list;
        }

        // --- Watch (targeted stack capture) ---

        private struct CaptureEntry
        {
            public int scope, opinionStart, entry, unit, call;
            public uint iteration;
            public string[] frames;
        }

        private const int CaptureCapacity = 4096;
        private const int MaxCapturesPerTick = 256;
        private static readonly CaptureEntry[] captures = new CaptureEntry[CaptureCapacity];
        private static long captureSeq;
        private static long captureTickHeader = -1;
        private static int capturesThisTick;
        private static readonly Dictionary<MethodBase, string> frameNames = new();

        /// <summary>From the Rand postfix, after the call: captures this call's stack if the top frame matches the watch.</summary>
        public static void OnWatchedCall()
        {
            if (!active || Environment.CurrentManagedThreadId != mainThreadId || Rand.stateStack.Count != baseDepth) return;
            ref var f = ref stack[depth - 1];
            if (!RandWatch.Matches(f.kind, f.def, f.method, f.basePhase, f.phase, f.job)) return;

            if (captureTickHeader != headerSeq)
            {
                captureTickHeader = headerSeq;
                capturesThisTick = 0;
            }
            if (capturesThisTick >= MaxCapturesPerTick) return;

            ulong s = Rand.StateCompressed;
            if ((uint)s != (uint)last) return;
            int segment = (int)((uint)(s >> 32) - (uint)(last >> 32));

            int unit, call;
            if (unitOpen && unitFrame.SameUnit(f))
            {
                unit = unitOrdinal;
                call = unitCalls + segment;
            }
            else
            {
                unit = recordsThisTick;
                call = segment;
            }

            capturesThisTick++;
            var trace = new StackTrace(2, false).GetFrames();
            var frames = new List<string>(24);
            foreach (var frame in trace)
            {
                if (frames.Count == 24) break;
                var method = Harmony.GetMethodFromStackframe(frame);
                if (method == null) continue;
                if (!frameNames.TryGetValue(method, out var name))
                {
                    bool patched;
                    try { patched = Harmony.GetPatchInfo(method) != null; }
                    catch { patched = false; }
                    frameNames[method] = name = (patched ? " *" : "  ") + method.FullDescription();
                }
                frames.Add(name);
            }

            ref var h = ref headers[curHeader];
            captures[captureSeq++ % CaptureCapacity] = new CaptureEntry
            {
                scope = h.scope, opinionStart = h.opinionStart, entry = h.entry, unit = unit, call = call,
                iteration = (uint)(s >> 32) - 1, frames = frames.ToArray()
            };
            RandWatch.captures++;
        }

        private static IEnumerable<CaptureEntry> Captures(int scope, int opinionStart, int entryFrom, int entryTo)
        {
            for (long s = Math.Max(0, captureSeq - CaptureCapacity); s < captureSeq; s++)
            {
                var c = captures[s % CaptureCapacity];
                if (c.frames != null && c.scope == scope && c.opinionStart == opinionStart && c.entry >= entryFrom && c.entry <= entryTo)
                    yield return c;
            }
        }

        private static string FormatCaptures(IEnumerable<CaptureEntry> list)
        {
            var sb = new StringBuilder();
            foreach (var c in list)
            {
                sb.AppendLine(RandAttrDiff.Capture.Header(c.entry, c.unit, c.call, c.iteration));
                foreach (var f in c.frames) sb.AppendLine(f);
            }
            return sb.ToString();
        }

        // --- Snapshots and answers ---

        private class Snapshot
        {
            public int opinionStart;
            public readonly List<(TickHeader header, Record[] records)> ticks = new();
            public readonly List<CaptureEntry> captures = new();
        }

        private static readonly List<Snapshot> snapshots = new();

        /// <summary>
        /// Copies every tick of a window out of the ring (when a desync is noticed), so a later request can still be
        /// answered after the ring moved on or the game reloaded.
        /// </summary>
        public static void Freeze(int opinionStart)
        {
            if (disabled || snapshots.Any(s => s.opinionStart == opinionStart)) return;
            var snap = new Snapshot { opinionStart = opinionStart };
            foreach (var (h, recs) in Ticks(opinionStart))
                snap.ticks.Add((h, recs.ToArray()));
            for (long s = Math.Max(0, captureSeq - CaptureCapacity); s < captureSeq; s++)
                if (captures[s % CaptureCapacity] is { frames: not null } c && c.opinionStart == opinionStart)
                    snap.captures.Add(c);
            snapshots.Add(snap);
            if (snapshots.Count > 4) snapshots.RemoveAt(0);
        }

        private static IEnumerable<(TickHeader, IEnumerable<Record>)> Ticks(int opinionStart)
        {
            for (long hs = Math.Max(0, headerSeq - HeaderCapacity); hs < headerSeq; hs++)
            {
                var h = headers[hs % HeaderCapacity];
                if (!h.complete || h.opinionStart != opinionStart) continue;
                if (h.firstRecord < recordSeq - RecordCapacity) continue; // overwritten
                yield return (h, RecordsOf(h));
            }
        }

        private static IEnumerable<Record> RecordsOf(TickHeader h)
        {
            for (long s = h.firstRecord; s < h.firstRecord + h.count; s++)
                yield return records[s % RecordCapacity];
        }

        /// <summary>The table of a window (one scope, or every scope when null) and its captures, names resolved here.</summary>
        public static bool Table(int opinionStart, int? scope, int entryFrom, int entryTo, out string table, out string stacks)
        {
            var snap = snapshots.LastOrDefault(s => s.opinionStart == opinionStart);
            IEnumerable<(TickHeader h, IEnumerable<Record> recs)> ticks = snap != null
                ? snap.ticks.Select(t => (t.header, (IEnumerable<Record>)t.records))
                : Ticks(opinionStart).ToList();

            var names = new Names();
            var sb = new StringBuilder();
            int found = 0;
            foreach (var (h, recs) in ticks.OrderBy(t => t.h.scope).ThenBy(t => t.h.entry))
            {
                if (scope != null && h.scope != scope) continue;
                if (h.entry < entryFrom || h.entry > entryTo) continue;
                found++;
                sb.AppendLine(new RandAttrTickLine
                {
                    scope = h.scope, window = h.opinionStart, entry = h.entry, ticksGame = h.ticksGame, timer = h.timer,
                    before = h.stateBefore, after = h.stateAfter, records = h.count
                }.Format());
                int ordinal = 0;
                foreach (var r in recs)
                    sb.AppendLine(names.Line(h.entry, ordinal++, r).Format());
            }
            sb.AppendLine($"offthread={offThreadCalls}");
            table = sb.ToString();

            var caps = snap != null
                ? snap.captures.Where(c => (scope == null || c.scope == scope) && c.entry >= entryFrom && c.entry <= entryTo)
                : scope != null ? Captures(scope.Value, opinionStart, entryFrom, entryTo) : Enumerable.Empty<CaptureEntry>();
            stacks = FormatCaptures(caps);
            return found > 0;
        }

        public static void Answer(ServerRandAttrRequestPacket request)
        {
            string table = "", stacks = "";
            bool ok = false;
            try
            {
                ok = !disabled && Table(request.opinionStart, request.scope, request.entryFrom, request.entryTo, out table, out stacks);
            }
            catch (Exception e)
            {
                MpLog.Error($"Rand attribution: couldn't build the table for window {request.opinionStart}: {e}");
            }

            var packet = new ClientRandAttrPacket
            {
                requestId = request.requestId,
                missed = !ok,
                gzTable = LeafDiff.Gzip(ok ? table : ""),
                gzStacks = LeafDiff.Gzip(ok ? stacks : ""),
                offThreadCalls = offThreadCalls
            };
            Multiplayer.Client?.SendFragmented(packet.Serialize());
        }

        /// <summary>Resolves ids to names for the table lines (defs by short hash, things by id).</summary>
        private class Names
        {
            private Dictionary<int, Thing> things;
            private readonly Dictionary<(Type, ushort), string> defs = new();

            private Thing FindThing(int id)
            {
                if (things == null)
                {
                    things = new Dictionary<int, Thing>();
                    foreach (var map in Find.Maps)
                        foreach (var t in map.listerThings.AllThings)
                            things.TryAdd(t.thingIDNumber, t);
                    foreach (var p in Find.WorldPawns?.AllPawnsAliveOrDead ?? Enumerable.Empty<Pawn>())
                        things.TryAdd(p.thingIDNumber, p);
                }
                return things.GetValueOrDefault(id);
            }

            private string Def<T>(ushort hash) where T : Def
            {
                if (hash == 0) return "-";
                var key = (typeof(T), hash);
                if (defs.TryGetValue(key, out var name)) return name;
                name = DefDatabase<T>.AllDefsListForReading.FirstOrDefault(d => d.shortHash == hash)?.defName ?? $"#{hash}";
                return defs[key] = RandAttrLine.Token(name);
            }

            public RandAttrLine Line(int entry, int ordinal, Record r)
            {
                var f = r.frame;
                var line = new RandAttrLine
                {
                    entry = entry, ordinal = ordinal, kind = f.kind.ToString(), calls = r.calls, toil = f.toil,
                    method = f.method == RandAttrMethod.None ? "-" : f.method.ToString(),
                    basePhase = PhaseName(f.basePhase), phase = PhaseName(f.phase),
                    job = Def<JobDef>(f.job)
                };

                switch (f.kind)
                {
                    case RandAttrKind.Thing:
                    {
                        line.def = Def<ThingDef>(f.def);
                        line.owner = (line.def == "-" ? "Thing" : line.def) + f.owner;
                        var thing = FindThing(f.owner);
                        line.cls = RandAttrLine.Token(thing?.GetType().Name ?? DefDatabase<ThingDef>.GetNamedSilentFail(line.def)?.thingClass?.Name);
                        if (thing is Pawn pawn) line.name = pawn.LabelShort ?? "";
                        break;
                    }
                    case RandAttrKind.WorldObject:
                        line.def = Def<WorldObjectDef>(f.def);
                        line.owner = (line.def == "-" ? "WorldObject" : line.def) + f.owner;
                        break;
                    case RandAttrKind.Incident:
                        line.def = Def<IncidentDef>(f.def);
                        break;
                    case RandAttrKind.Condition:
                        line.def = Def<GameConditionDef>(f.def);
                        break;
                    case RandAttrKind.Lord:
                        line.owner = "Lord" + f.owner;
                        break;
                    case RandAttrKind.Quest:
                        line.owner = "Quest" + f.owner;
                        break;
                }

                return line;
            }
        }

        /// <summary>The last recorded tick's records, ids only (the harness's smoke test reads it).</summary>
        internal static string RawLastTick()
        {
            if (curHeader < 0) return "";
            var h = headers[curHeader];
            var sb = new StringBuilder($"scope={h.scope} entry={h.entry} before={RandAttrTickLine.State(h.stateBefore)} after={RandAttrTickLine.State(h.stateAfter)} records={h.count} hash={curHash:X16}\n");
            foreach (var r in RecordsOf(h))
                sb.Append($"{r.frame.kind} owner={r.frame.owner} m={r.frame.method} base={PhaseName(r.frame.basePhase)} phase={PhaseName(r.frame.phase)} calls={r.calls}\n");
            return sb.ToString();
        }

        /// <summary>For tests and debug actions: the whole window, every scope.</summary>
        public static string WindowTable(int opinionStart)
        {
            Table(opinionStart, null, 0, int.MaxValue, out var table, out var stacks);
            return table + (stacks.Length > 0 ? "\n" + stacks : "");
        }

        internal static ushort DefHash<T>(string name) where T : Def =>
            name.NullOrEmpty() || name == "-" ? (ushort)0 : DefDatabase<T>.GetNamedSilentFail(name)?.shortHash ?? (ushort)0;
    }
}
