using System.Diagnostics;
using Multiplayer.Client.Util;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Shows what the server's sync referee found. A hard verdict means a player's game no longer matches the
    /// reference game; the player's own client also catches it and opens the desync window, so here it is a
    /// message for everyone else (the host). Soft verdicts never stop play: they are logged and, a few at a time,
    /// shown as messages.
    /// </summary>
    public static class SyncVerdicts
    {
        private static readonly Stopwatch sinceLastSoftMessage = Stopwatch.StartNew();
        private const long SoftMessageEveryMs = 30_000;

        public static int SoftCount { get; private set; }

        public static void Show(ServerSyncVerdictPacket packet)
        {
            bool aboutMe = packet.playerId == Multiplayer.session?.playerId;
            var who = aboutMe ? "Your game" : $"{packet.username}'s game";

            if (packet.hard)
            {
                MpLog.Error($"Sync referee: {who} differs from the host's at tick {packet.tick}: {packet.message}");
                if (!aboutMe)
                    Messages.Message($"{who} no longer matches yours (tick {packet.tick}): {packet.message}",
                        MessageTypeDefOf.NegativeEvent, false);
                return;
            }

            SoftCount++;
            MpLog.Warn($"Sync referee (play continues): {who} at tick {packet.tick}: {packet.message}");
            if (sinceLastSoftMessage.ElapsedMilliseconds < SoftMessageEveryMs) return;

            sinceLastSoftMessage.Restart();
            Messages.Message($"Sync warning ({who}): {packet.message}", MessageTypeDefOf.SilentInput, false);
        }
    }
}
