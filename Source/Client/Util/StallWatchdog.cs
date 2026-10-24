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
/// only the main thread did. Each line carries what the GC did in the gap and the memory load (<see cref="GcMark"/>).
/// Every 60 s of an MP session the main thread also logs a memory line, so heap growth shows before a gen2 freezes
/// the game: that host's freezes came at a 9 GB heap with desync traces on.
/// Waylith runs no frames while the window is unfocused unless runInBackground is on (an MP session turns it on), so
/// a frame gap the watchdog thread saw the engine idle through is labelled as such, not as a block (2026-10-23: three
/// "blocked" lines of 5-170 s at the menu were alt-tabs).
/// </summary>
public static class StallWatchdog
{
    private const int SleepMs = 100;
    private const int ProcessGapMs = 1000;
    private const int FrameGapMs = 2000;
    private const int MemoryLineMs = 60_000;

    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static Thread thread;
    // Frames the main thread has started, and when the last one started (clock ms)
    private static volatile int frames;
    private static long lastFrameMs = -1;
    private static GcMark frameMark;
    private static long lastMemoryMs;
    private static GcMark memoryMark;
    // When the watchdog thread last saw the engine idle unfocused (clock ms)
    private static long idleSeenMs = -1;

    public static void Start()
    {
        if (thread != null) return;
        frameMark = memoryMark = GcMark.Now();
        lastMemoryMs = clock.ElapsedMilliseconds;
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
            if (Interlocked.Read(ref idleSeenMs) > lastFrameMs)
                MpLog.Log($"No frames for {now - lastFrameMs} ms while the window was unfocused (runInBackground off)");
            else
                MpLog.Warn($"Main thread blocked {now - lastFrameMs} ms (the stall watchdog kept running unless it logs a gap too): " +
                           $"{GcMark.Now().Since(frameMark)}");
        }
        frameMark = GcMark.Now();
        lastFrameMs = now;

        if (now - lastMemoryMs >= MemoryLineMs)
        {
            if (Multiplayer.Client != null) LogMemory(now);
            else memoryMark = frameMark;
            lastMemoryMs = now;
        }
    }

    private static void LogMemory(long now)
    {
        var mark = GcMark.Now();
        var traces = Multiplayer.game?.sync is { } sync ? sync.TraceCensus() : "no game";
        MpLog.Log($"Memory: {GcMark.HeapText()}; desync traces {traces}; " +
                  $"the last {(now - lastMemoryMs) / 1000} s: {mark.Since(memoryMark)}");
        memoryMark = mark;
    }

    private static void Run()
    {
        var mark = GcMark.Now();
        long last = clock.ElapsedMilliseconds;
        while (true)
        {
            Thread.Sleep(SleepMs);
            long now = clock.ElapsedMilliseconds;
            // Both read plain static bools in the engine
            if (!UnityEngine.Application.isFocused && !UnityEngine.Application.runInBackground)
                Interlocked.Exchange(ref idleSeenMs, now);
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
}
