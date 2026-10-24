using System;

namespace Multiplayer.Common
{
    /// <summary>
    /// The GC's counters at one moment, so a log line can say what the GC did between two of them (StallWatchdog's
    /// stall lines and its memory line). Taking one reads four counters and allocates nothing.
    /// </summary>
    public readonly struct GcMark(TimeSpan pause, int gen0, int gen1, int gen2)
    {
        private readonly TimeSpan pause = pause;
        private readonly int gen0 = gen0, gen1 = gen1, gen2 = gen2;

        public static GcMark Now() =>
            new(GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        public string Since(GcMark before)
        {
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            var lastPause = info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0;
            return $"GC paused {(pause - before.pause).TotalMilliseconds:F0} ms in it (collections gen0 +{gen0 - before.gen0}, " +
                   $"gen1 +{gen1 - before.gen1}, gen2 +{gen2 - before.gen2}); last GC #{info.Index} gen{info.Generation} " +
                   $"{(info.Compacted ? "compacting" : "not compacting")}{(info.Concurrent ? " background" : "")} paused {lastPause:F0} ms; " +
                   $"heap {info.HeapSizeBytes >> 20} MB, working set {Environment.WorkingSet >> 20} MB, " +
                   $"memory load {info.MemoryLoadBytes >> 20} of {info.TotalAvailableMemoryBytes >> 20} MB";
        }

        /// <summary>
        /// The heap past what <see cref="Since"/> says: its fragmentation and commit after the last GC, and the last
        /// blocking and background gen2 (a blocking one stops every thread; 2026-10-23 they took 1.9-3.8 s at a
        /// 9 GB heap).
        /// </summary>
        public static string HeapText()
        {
            var any = GC.GetGCMemoryInfo(GCKind.Any);
            var blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var background = GC.GetGCMemoryInfo(GCKind.Background);
            return $"fragmented {any.FragmentedBytes >> 20} MB, committed {any.TotalCommittedBytes >> 20} MB, " +
                   $"promoted {any.PromotedBytes >> 20} MB; gen2 collections {GC.CollectionCount(2)}, " +
                   $"last blocking gen2 {Gen2Text(blocking)}, last background gen2 {Gen2Text(background)}";
        }

        private static string Gen2Text(GCMemoryInfo info)
        {
            if (info.Index == 0) return "none";
            var pause = info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0;
            return $"#{info.Index} {(info.Compacted ? "compacting" : "not compacting")} paused {pause:F0} ms, heap after {info.HeapSizeBytes >> 20} MB";
        }
    }
}
