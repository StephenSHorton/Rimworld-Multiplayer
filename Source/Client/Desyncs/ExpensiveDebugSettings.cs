using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    /// <summary>
    /// Slow desync tracing (a managed stack for every Rand call) and the steering ledger (a stack for every pawn turn
    /// and path request) are expensive on 1.7. On 2026-10-22 they made Blake's game the host's bottleneck: 120-300 ms
    /// frames, the same simulation at 15 ms a tick there and 0.74 on the host, and nothing in the game said why.
    /// Each is named once when a session finds it on, and again whenever it is turned back on.
    /// </summary>
    public static class ExpensiveDebugSettings
    {
        private static long nextCheck;
        private static bool slowWarned;
        private static bool ledgerWarned;

        public static void Update()
        {
            long now = System.Environment.TickCount64;
            if (now < nextCheck) return;
            nextCheck = now + 2000;

            bool slow = Multiplayer.settings.desyncTracingMode == DesyncTracingMode.Slow;
            if (slow && !slowWarned)
                Warn("Slow desync tracing is on: it records a stack for every random call and can make this game far " +
                     "slower than the others. Set Mod settings > Multiplayer > Desync tracing mode to Fast for play.");
            slowWarned = slow;

            bool ledger = SteeringLedger.Enabled;
            if (ledger && !ledgerWarned)
                Warn("The steering ledger is on (" + (Prefs.DevMode ? "dev mode" : "MpSteeringLedger.flag") + "): it " +
                     "records a stack for every pawn turn and path request, which slows ticks. Turn it off for play.");
            ledgerWarned = ledger;
        }

        private static void Warn(string text)
        {
            MpLog.Warn(text);
            Messages.Message(text, MessageTypeDefOf.CautionInput, false);
        }
    }
}
