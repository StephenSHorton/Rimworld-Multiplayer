using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Multiplayer.Common.Util;

namespace Multiplayer.Common
{
    /// <summary>
    /// Who was slow and when: each game's frame time as the server sees it (<see cref="ServerPlayer.frameTime"/>, the
    /// client's smoothed frame time, sent about 30 times a second while it ticks), sampled every net tick. Once a second
    /// a player's p50 that moved by 20% or more since the last line for that player writes a line, and every 30 s every
    /// player gets one: <c>HH:MM:SS player p50 p95 serverTimePerTick</c>, in ms, to
    /// <c>MpDesyncs/perf-timeline-&lt;session&gt;.txt</c>. A player with no frame time yet (0) is skipped.
    /// </summary>
    public class PerfTimeline
    {
        public const float ChangeThreshold = 0.2f;
        public const int HeartbeatSeconds = 30;
        public const int KeepFiles = 20;
        private const int Window = MultiplayerServer.NetTicksPerSecond;

        private class PlayerSamples
        {
            public readonly Queue<float> samples = new();
            public float lastP50 = -1;
        }

        private readonly Dictionary<string, PlayerSamples> players = new();
        private readonly string? path;
        private int seconds;
        private bool failed;

        public string? Path => path;

        /// <param name="dir">Folder for the file; null keeps the lines in memory only (<see cref="Second"/> returns them)</param>
        public PerfTimeline(string? dir, DateTime? start = null)
        {
            if (dir == null) return;
            path = System.IO.Path.Combine(dir, $"perf-timeline-{(start ?? DateTime.Now):yyyyMMdd-HHmmss}.txt");

            // One file per hosted session: keep the newest ones
            try
            {
                if (Directory.Exists(dir))
                    foreach (var old in new DirectoryInfo(dir).GetFiles("perf-timeline-*.txt")
                                 .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles - 1))
                        old.Delete();
            }
            catch (Exception e)
            {
                ServerLog.Error($"Perf timeline: can't remove old files: {e.Message}");
            }
        }

        /// <summary>Called every net tick with the players serverTimePerTick is taken from.</summary>
        public void Sample(IEnumerable<(string name, float frameTime)> current)
        {
            foreach (var (name, frameTime) in current)
            {
                if (frameTime <= 0) continue;
                if (!players.TryGetValue(name, out var p))
                    players[name] = p = new PlayerSamples();
                p.samples.Enqueue(frameTime);
                while (p.samples.Count > Window)
                    p.samples.Dequeue();
            }
        }

        /// <summary>Called once a second; returns the lines written.</summary>
        public List<string> Second(IEnumerable<string> present, float serverTimePerTick, DateTime now)
        {
            seconds++;
            var heartbeat = seconds % HeartbeatSeconds == 0;
            var lines = new List<string>();
            var here = new HashSet<string>(present);

            // A player who left starts again from nothing
            foreach (var gone in players.Keys.Where(k => !here.Contains(k)).ToList())
                players.Remove(gone);

            foreach (var (name, p) in players.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (p.samples.Count == 0) continue;
                var sorted = p.samples.OrderBy(f => f).ToArray();
                var p50 = Percentile(sorted, 0.5f);
                var p95 = Percentile(sorted, 0.95f);
                var moved = p.lastP50 <= 0 || Math.Abs(p50 - p.lastP50) >= p.lastP50 * ChangeThreshold;
                if (!moved && !heartbeat) continue;

                if (moved) p.lastP50 = p50;
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss} {1} {2:F1} {3:F1} {4:F1}",
                    now, name, p50, p95, serverTimePerTick));
            }

            if (lines.Count > 0 && path != null && !failed)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    File.AppendAllLines(path, lines);
                }
                catch (Exception e)
                {
                    failed = true;
                    ServerLog.Error($"Perf timeline: can't write {path}, stopped: {e.Message}");
                }
            }

            return lines;
        }

        private static float Percentile(float[] sorted, float q) =>
            sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
    }
}
