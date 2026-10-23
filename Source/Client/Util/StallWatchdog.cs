using System;
using System.Diagnostics;
using System.Threading;
using Multiplayer.Common;
using Verse;

namespace Multiplayer.Client.Util;

/// <summary>
/// Tells a stall of the whole process from a block of the main thread. 2026-10-23: a host froze 8.7 s right after a
/// rejoin's world was sent and both peers timed out on LiteNet's 5 s timeout; LiteNet answers pings on its own logic
/// thread, so a main thread block alone shouldn't do that. A background thread sleeps 100 ms in a loop: a gap over a
/// second there means every thread stopped (a blocking GC, paging); a frame over two seconds with no such gap means
/// only the main thread did. Each line carries what the GC did in the gap and the memory load.
/// </summary>
public static class StallWatchdog
{
    private const int SleepMs = 100;
    private const int ProcessGapMs = 1000;
    private const int FrameGapMs = 2000;

    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static Thread thread;
    // Frames the main thread has started, and when the last one started (clock ms)
    private static volatile int frames;
    private static long lastFrameMs = -1;
    private static GcMark frameMark;

    public static void Start()
    {
        if (thread != null) return;
        frameMark = GcMark.Now();
        thread = new Thread(Run) { IsBackground = true, Name = "MP stall watchdog", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Called at the start of every main thread frame (<see cref="OnMainThread.Update"/>).</summary>
    public static void OnFrame()
    {
        long now = clock.ElapsedMilliseconds;
        frames++;
        if (lastFrameMs >= 0 && now - lastFrameMs > FrameGapMs)
        {
            var mark = GcMark.Now();
            MpLog.Warn($"Main thread blocked {now - lastFrameMs} ms (the stall watchdog kept running unless it logs a gap too): " +
                       $"{mark.Since(frameMark)}");
        }
        frameMark = GcMark.Now();
        lastFrameMs = now;
    }

    private static void Run()
    {
        var mark = GcMark.Now();
        long last = clock.ElapsedMilliseconds;
        while (true)
        {
            Thread.Sleep(SleepMs);
            long now = clock.ElapsedMilliseconds;
            var next = GcMark.Now();
            long gap = now - last - SleepMs;
            if (gap > ProcessGapMs)
            {
                int framesNow = frames;
                var line = $"Whole process stalled {gap} ms (the stall watchdog's 100 ms sleep took {now - last} ms; main thread at frame {framesNow}): {next.Since(mark)}";
                // Verse's log isn't for other threads; it shows when the main thread runs again
                OnMainThread.Enqueue(() => MpLog.Warn(line));
            }
            mark = next;
            last = now;
        }
    }

    private readonly struct GcMark(TimeSpan pause, int gen0, int gen1, int gen2)
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
    }
}
