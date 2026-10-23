using System.Collections.Generic;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Debug settings that make every tick slow. 2026-10-22: Blake's game ran at 15 ms a tick against the host's 0.74,
    /// with Slow desync tracing (a managed stack for every Rand call) and the steering ledger (a stack for every pawn
    /// rotation and path) on, and nobody knew. Every load into an MP game logs a warning naming them, and the first in
    /// a process also says so on screen.
    /// </summary>
    public static class DebugCostWarning
    {
        private static bool shownOnScreen;

        public static List<string> ExpensiveSettings()
        {
            var on = new List<string>();
            if (Multiplayer.settings.desyncTracingMode == DesyncTracingMode.Slow && Multiplayer.GameComp is { logDesyncTraces: true })
                on.Add("Slow desync tracing (Multiplayer settings, Desync tracing mode)");
            SteeringLedger.Refresh();
            if (SteeringLedger.Enabled)
                on.Add(SteeringLedger.FlagFile ? "the steering ledger (MpSteeringLedger.flag in the save data folder)" : "the steering ledger (dev mode)");
            return on;
        }

        /// <summary>After a load into an MP game (a join, a join point, a resync).</summary>
        public static void Check()
        {
            if (Multiplayer.Client == null || Multiplayer.IsReplay) return;

            var on = ExpensiveSettings();
            if (on.Count == 0) return;

            var text = $"Multiplayer: {string.Join(" and ", on)} {(on.Count > 1 ? "are" : "is")} on; each can make every tick " +
                       "many times slower for everyone, since the host waits for the slowest game. Turn it off unless you are hunting a desync.";
            MpLog.Warn(text);
            if (shownOnScreen) return;
            shownOnScreen = true;
            LongEventHandler.ExecuteWhenFinished(() => Messages.Message(text, MessageTypeDefOf.CautionInput, false));
        }
    }
}
