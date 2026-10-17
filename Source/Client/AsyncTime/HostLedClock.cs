using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// The host's side of the host-led clock (see MultiplayerServer.hostTimer for the rules that make it safe). The
    /// host's own commands run at its next tick with no round trip through the server, so the host plays like
    /// single player; it reports its timer after every tick, and that report is the limit everyone else runs to.
    /// </summary>
    public static class HostLedClock
    {
        /// <summary>This game hosts a host-led session.</summary>
        public static bool IsHost => Multiplayer.LocalServer is { HostLed: true } && Multiplayer.Client != null;

        private static int lastReported = -1;

        public static void ReportTimer()
        {
            if (!IsHost || TickPatch.Timer == lastReported) return;
            lastReported = TickPatch.Timer;
            Multiplayer.Client.Send(new ClientHostTimerPacket(TickPatch.Timer));
        }

        /// <summary>
        /// The tick to run the host's own command at, or 0 to let the server stamp it (the old way). It's past every
        /// command the host has already received and no further than the host's limit, so it can't share a tick with
        /// any command the server stamps.
        /// </summary>
        public static int TicksForOwnCommand(ConnectionBase conn)
        {
            if (!IsHost || conn != Multiplayer.Client || Multiplayer.game == null) return 0;
            if (TickPatch.Simulating || Multiplayer.IsReplay || Current.ProgramState != ProgramState.Playing) return 0;
            // Multifaction: the faction the server gives a command can change under it; keep the round trip
            if (Multiplayer.GameComp.multifaction) return 0;

            int ticks = TickPatch.Timer + 1;
            foreach (var tickable in TickPatch.AllTickables)
                foreach (var cmd in tickable.Cmds)
                    if (cmd.ticks >= ticks)
                        ticks = cmd.ticks + 1;

            return ticks <= TickPatch.tickUntil ? ticks : 0;
        }

        public static void ScheduleOwnCommand(CommandType type, int mapId, byte[] data, int ticks)
        {
            var session = Multiplayer.session;
            session.ScheduleCommand(new ScheduledCommand(type, ticks, session.myFactionId, mapId, session.playerId, data));
            // The server counts it as sent to everyone; the host has it already
            session.receivedCmds++;
        }

        /// <summary>Inserts after every command with the same or an earlier tick, so equal ticks keep their order.</summary>
        public static void InsertInTickOrder(Queue<ScheduledCommand> queue, ScheduledCommand cmd)
        {
            if (queue.Count == 0 || queue.Last().ticks <= cmd.ticks)
            {
                queue.Enqueue(cmd);
                return;
            }

            var all = queue.ToList();
            int at = all.FindLastIndex(c => c.ticks <= cmd.ticks) + 1;
            all.Insert(at, cmd);
            queue.Clear();
            foreach (var c in all)
                queue.Enqueue(c);
        }

        /// <summary>A stable sort by tick, for queues rebuilt from a snapshot's command lists.</summary>
        public static IEnumerable<ScheduledCommand> InTickOrder(IEnumerable<ScheduledCommand> cmds) =>
            cmds.OrderBy(c => c.ticks);
    }
}
