using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using HarmonyLib;
using Multiplayer.Client.Util;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Every thing (but motes) destroyed or despawned while a join point reloads, from Map.FinalizeLoading to the
    /// end of the first frame after, with a short stack. The 2026-10-21 join point saves were missing conduit
    /// blueprints and filth the live games had; the list goes in the join point's
    /// MpDesyncs/selfcheck-&lt;player&gt;-&lt;tick&gt;.txt to name what drops them. Off outside a join point.
    /// </summary>
    public static class JoinPointDrops
    {
        private const int Kept = 300;
        private const int Frames = 8;

        private class Entry
        {
            public string kinds;
            public string phase;
            public string thing;
            public string where;
            public int tick;
            public StackTrace stack;
        }

        private static bool armed;
        private static int armedFrame;
        private static bool recording;
        private static string phase;
        private static readonly List<Entry> entries = new();
        private static readonly Dictionary<int, Entry> byThing = new();
        private static int total;

        /// <summary>Before a join point's save; recording starts when its reload finalizes a map.</summary>
        public static void Arm()
        {
            if (Multiplayer.Client == null) return;
            armed = true;
            armedFrame = UnityEngine.Time.frameCount;
            recording = false;
            entries.Clear();
            byThing.Clear();
            total = 0;
        }

        /// <summary>After the join point's reload: keep recording through the next frame, then write the list.</summary>
        public static void StopAfterNextFrame(string path)
        {
            if (!armed) return;
            phase = "first frame";
            // Run at the next frame's queue, then the one after: the whole next frame is recorded
            OnMainThread.Enqueue(() => OnMainThread.Enqueue(() => Finish(path)));
        }

        private static void Finish(string path)
        {
            armed = false;
            recording = false;
            if (total == 0) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append($"\n=== Destroyed or despawned while the join point reloaded and in the first frame after: {total} things" +
                          (total > entries.Count ? $", the first {entries.Count} listed" : "") + "\n");
                foreach (var e in entries)
                    sb.Append($"{e.kinds} {e.thing} {e.where} tick={e.tick} ({e.phase}) {IdLedger.Describe(e.stack, Frames)}\n");

                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                File.AppendAllText(path, sb.ToString());
                MpLog.Warn($"The join point reload destroyed or despawned {total} things; they're listed in {path}");
            }
            catch (System.Exception e)
            {
                MpLog.Error($"Couldn't write the things the join point reload dropped: {e}");
            }
            entries.Clear();
            byThing.Clear();
        }

        private static void Record(Thing t, char kind)
        {
            if (Multiplayer.Client == null)
            {
                armed = recording = false;
                return;
            }
            if (t is Mote || t.def?.category == ThingCategory.Mote) return;

            // Destroy despawns too: one line per thing
            if (byThing.TryGetValue(t.thingIDNumber, out var seen))
            {
                if (seen.kinds.IndexOf(kind) < 0) seen.kinds += kind;
                return;
            }

            total++;
            if (entries.Count >= Kept) return;
            var entry = new Entry
            {
                kinds = kind.ToString(),
                phase = phase,
                thing = t.ThingID,
                where = $"map={t.MapHeld?.uniqueID.ToString() ?? "-"} pos={t.PositionHeld}",
                tick = Find.TickManager?.TicksGame ?? -1,
                stack = new StackTrace(3, false)
            };
            entries.Add(entry);
            byThing[t.thingIDNumber] = entry;
        }

        [HarmonyPatch(typeof(Map), nameof(Map.FinalizeLoading))]
        static class StartOnFinalizeLoading
        {
            static void Prefix()
            {
                // The join point saves and reloads in one frame; a later reload (after one that failed) isn't it
                if (!armed || !Multiplayer.reloading || recording || UnityEngine.Time.frameCount != armedFrame) return;
                recording = true;
                phase = "loading";
            }
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
        static class DestroyPatch
        {
            static void Prefix(Thing __instance)
            {
                if (recording) Record(__instance, 'D');
            }
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
        static class DeSpawnPatch
        {
            static void Prefix(Thing __instance)
            {
                if (recording) Record(__instance, 'S');
            }
        }
    }
}
