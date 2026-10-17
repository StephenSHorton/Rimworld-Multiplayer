using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Multiplayer.Common;
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

        // Host: the speeds from before the desync pause, put back once the player has re-synced
        // By name: a player whose game restarts to re-sync comes back with a new id
        private static string waitingFor;
        private static bool sawThemAway;
        private static readonly Stopwatch waitingSince = new();
        private const long GiveUpAfterMs = 5 * 60_000;
        private static TimeSpeed worldSpeed;
        private static readonly Dictionary<int, TimeSpeed> mapSpeeds = new();

        private static void RememberSpeedsUntilBack(int playerId)
        {
            if (Multiplayer.LocalServer == null || waitingFor != null || Multiplayer.game == null) return;

            waitingFor = Multiplayer.session.players.FirstOrDefault(p => p.id == playerId)?.username;
            sawThemAway = false;
            waitingSince.Restart();
            worldSpeed = Multiplayer.AsyncWorldTime.DesiredTimeSpeed;
            mapSpeeds.Clear();
            foreach (var map in Find.Maps)
                mapSpeeds[map.uniqueID] = map.AsyncTime().DesiredTimeSpeed;
        }

        /// <summary>Every frame: once the re-syncing player plays again, unpause what the desync paused.</summary>
        public static void Update()
        {
            if (waitingFor == null) return;
            if (Multiplayer.Client == null || Multiplayer.game == null || waitingSince.ElapsedMilliseconds > GiveUpAfterMs)
            {
                waitingFor = null;
                return;
            }

            var player = Multiplayer.session.players.FirstOrDefault(p => p.username == waitingFor);
            if (player == null || player.status != PlayerStatus.Playing)
            {
                sawThemAway = true;
                return;
            }

            if (!sawThemAway) return;
            waitingFor = null;

            if (worldSpeed != TimeSpeed.Paused && Multiplayer.AsyncWorldTime.DesiredTimeSpeed == TimeSpeed.Paused)
                AsyncTime.MpTimeControls.SendTimeChange(Multiplayer.AsyncWorldTime, worldSpeed);
            foreach (var map in Find.Maps)
                if (mapSpeeds.TryGetValue(map.uniqueID, out var speed) && speed != TimeSpeed.Paused && map.AsyncTime().DesiredTimeSpeed == TimeSpeed.Paused)
                    AsyncTime.MpTimeControls.SendTimeChange(map.AsyncTime(), speed);

            Messages.Message($"{player.username} is back in sync", MessageTypeDefOf.PositiveEvent, false);
        }

        public static void Show(ServerSyncVerdictPacket packet)
        {
            bool aboutMe = packet.playerId == Multiplayer.session?.playerId;
            var who = aboutMe ? "Your game" : $"{packet.username}'s game";

            if (packet.hard)
            {
                MpLog.Warn($"Sync referee: {who} differs from the host's at tick {packet.tick}: {packet.message}");
                if (!aboutMe)
                {
                    Messages.Message($"{who} no longer matches yours (tick {packet.tick}): {packet.message}. They re-sync on their own.",
                        MessageTypeDefOf.NegativeEvent, false);
                    RememberSpeedsUntilBack(packet.playerId);
                }
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
