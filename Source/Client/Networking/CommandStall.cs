using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Networking
{
    /// <summary>
    /// A game only runs to the host's limit once it has every command the server says it sent
    /// (<see cref="MultiplayerSession.ProcessTimeControl"/>). On 2026-10-22 at 21:15 a command packet on the reliable
    /// stream never arrived on Blake's game, with no Steam error and no disconnect: its limit froze, every click
    /// waited behind it, and its keepalives (unreliable, still getting through) said it was 0 ticks behind its frozen
    /// limit, so the host never waited and ran 8,600 ticks ahead. While commands are missing for more than
    /// <see cref="ReportAfterMs"/>, the keepalive measures from the limit the host sent instead, so the host pauses
    /// for this player the way it does for one that is too far behind; after <see cref="WarnAfterMs"/> the player is
    /// told and the counters are logged.
    /// </summary>
    public static class CommandStall
    {
        public const int ReportAfterMs = 2000;
        public const int WarnAfterMs = 5000;

        private static long waitingSince;
        private static long nextLog;
        private static bool warned;

        public static bool Stalled => waitingSince != 0 && System.Environment.TickCount64 - waitingSince >= ReportAfterMs;

        /// <summary>Ticks behind for the keepalive: from the host's limit while commands are missing.</summary>
        public static int TicksBehind()
        {
            int behind = TickPatch.tickUntil - TickPatch.Timer;
            return Stalled ? System.Math.Max(behind, Multiplayer.session.remoteTickUntil - TickPatch.Timer) : behind;
        }

        public static void Update()
        {
            var session = Multiplayer.session;
            if (session == null || Multiplayer.Client?.State != ConnectionStateEnum.ClientPlaying ||
                session.receivedCmds >= session.remoteSentCmds)
            {
                if (warned)
                    MpLog.Log($"Commands from the host arrive again after {(System.Environment.TickCount64 - waitingSince) / 1000} s ({Counters()})");
                waitingSince = 0;
                warned = false;
                return;
            }

            long now = System.Environment.TickCount64;
            if (waitingSince == 0) waitingSince = now;
            long waited = now - waitingSince;

            if (!warned && waited >= WarnAfterMs)
            {
                warned = true;
                nextLog = now + 10000;
                MpLog.Warn($"No commands from the host for {waited / 1000} s although it sent them ({Counters()}); the " +
                           "connection may have stalled, the host is told this game is behind");
                Messages.Message($"Commands from the host stopped arriving {waited / 1000} s ago. If the game stays frozen, " +
                                 "leave to the main menu and rejoin.", MessageTypeDefOf.CautionInput, false);
            }
            else if (warned && now >= nextLog)
            {
                nextLog = now + 10000;
                MpLog.Warn($"Still no commands from the host after {waited / 1000} s ({Counters()})");
            }
        }

        private static string Counters()
        {
            var s = Multiplayer.session;
            return $"received {s.receivedCmds} of {s.remoteSentCmds} commands, limit {TickPatch.tickUntil}, host's limit " +
                   $"{s.remoteTickUntil}, timer {TickPatch.Timer}";
        }
    }
}
