using System.Collections.Generic;
using System.Diagnostics;
using LudeonTK;
using Multiplayer.Client.Networking;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Steamworks;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client
{
    public class MultiplayerSession : IConnectionStatusListener
    {
        public string gameName;
        public int playerId;

        public int receivedCmds;
        public int remoteTickUntil;
        public int remoteSentCmds;

        public ConnectionBase client;
        public PacketLogWindow writerLog = new(true);
        public PacketLogWindow readerLog = new(false);
        public int myFactionId;
        public List<PlayerInfo> players = new();
        public GameDataSnapshot dataSnapshot;
        public LocationPings locationPings = new();
        public PlayerCursors playerCursors = new();
        public int autosaveCounter;
        public float? lastSaveAt;

        public bool replay;
        public int replayTimerStart = -1;
        public int replayTimerEnd = -1;
        public bool showTimeline;

        public bool desynced;

        public List<CSteamID> pendingSteam = new();
        public List<CSteamID> knownUsers = new();

        public const int MaxMessages = 200;
        public List<ChatMsg> messages = new();
        public bool hasUnread;
        public bool ghostModeCheckbox;

        public Process arbiter;
        public bool ArbiterPlaying => players.Any(p => p.type == PlayerType.Arbiter && p.status == PlayerStatus.Playing);

        public IConnector connector;
        public bool isStandaloneServer;
        public float autosaveInterval;
        public AutosaveUnit autosaveUnit;
        public bool ConnectedToStandaloneServer => client != null && isStandaloneServer;

        public void Stop()
        {
            if (client != null)
            {
                client.Close(MpDisconnectReason.Internal);
                client.ChangeState(ConnectionStateEnum.Disconnected);
            }

            if (arbiter != null)
            {
                arbiter.TryKill();
                arbiter = null;
            }
        }

        public PlayerInfo GetPlayerInfo(int id) => players.FirstOrDefault(p => p.id == id);

        public void AddMsg(string msg, bool notify = true, bool rawMessage = false)
        {
            AddMsg(new ChatMsg_Text(msg, rawMessage), notify);
        }

        public void AddMsg(ChatMsg msg, bool notify = true)
        {
            var window = ChatWindow.Opened;

            if (window != null)
                window.OnChatReceived();
            else if (notify)
                NotifyChat();

            messages.Add(msg);

            if (messages.Count > MaxMessages)
                messages.RemoveAt(0);
        }

        public void NotifyChat()
        {
            hasUnread = true;
            SoundDefOf.PageChange.PlayOneShotOnCamera();
        }

        public void Reconnect(string username)
        {
            Multiplayer.username = username;
            ClientUtil.TryConnectWithWindow(connector);
        }

        public void Connected()
        {
        }

        public void Disconnected(SessionDisconnectInfo info)
        {
            MpUI.ClearWindowStack();

            Find.WindowStack.Add(new DisconnectedWindow(info)
            {
                returnToServerBrowser = Multiplayer.Client?.State != ConnectionStateEnum.ClientPlaying
            });
        }

        public void ReapplyPrefs()
        {
            Application.runInBackground = true;
        }

        public void ProcessTimeControl()
        {
            if (receivedCmds >= remoteSentCmds)
                TickPatch.tickUntil = remoteTickUntil;
        }

        [TweakValue("Multiplayer")] public static bool consistentCommandOrder = true;
        public void ScheduleCommand(ScheduledCommand cmd)
        {
            MpLog.Debug(cmd.ToString());
            dataSnapshot.MapCmds.GetOrAddNew(cmd.mapId).Add(cmd);

            if (Current.ProgramState != ProgramState.Playing) return;

            // Minimal code impact fix for #733. Having all the commands be added to a single queue gets rid of the
            // out-of-order execution problem.
            // In tick order: with the host-led clock the host's own command can arrive after a later-stamped one
            if (cmd.mapId == ScheduledCommand.Global || consistentCommandOrder)
                HostLedClock.InsertInTickOrder(Multiplayer.AsyncWorldTime.Cmds, cmd);
            else if (cmd.GetMap() is { } map)
                HostLedClock.InsertInTickOrder(map.AsyncTime().Cmds, cmd);
            else
                // A map this game hasn't made yet (a caravan's or a quest's map the host made a few ticks ahead):
                // maps no longer take their snapshot list when they are made, and the tick loop runs a world queue
                // command on the map it names (TickPatch.RunCmds), which exists by the command's tick
                HostLedClock.InsertInTickOrder(Multiplayer.AsyncWorldTime.Cmds, cmd);
        }

        public void Update()
        {
            locationPings.UpdatePing();
        }

        private readonly Stopwatch sinceTrafficLog = Stopwatch.StartNew();
        private readonly Stopwatch sinceLastWatch = Stopwatch.StartNew();
        private bool quietWarned;
        private readonly Stopwatch waitingForCmds = new();
        private bool waitingWarned;

        /// <summary>
        /// This game's side of a connection that goes quiet (see ConnectionWatch on the server): a warning when the
        /// server sends nothing for 5 s or the commands the server counted don't arrive for 5 s (this game stops at
        /// its limit then), and the traffic counters every 30 s. Every frame from OnMainThread.Update after the
        /// connection has read its packets, so a frame after a long block (a join point's save and reload, a map
        /// being made) sees the packets that queued up during it; a frame that itself came more than 5 s after the
        /// last one is not judged at all, since this game, not the server, was the one that stopped.
        /// </summary>
        public void WatchConnection()
        {
            var conn = Multiplayer.Client;
            // The host's own connection is in-process
            if (conn == null || Multiplayer.LocalServer != null) return;

            var frameGap = sinceLastWatch.ElapsedMilliseconds;
            sinceLastWatch.Restart();
            if (frameGap > ConnectionWatch.StaleMs)
            {
                if (waitingForCmds.IsRunning) waitingForCmds.Restart();
                return;
            }

            var age = conn.LastReceivedAgeMs;
            if (age > ConnectionWatch.StaleMs && !quietWarned)
            {
                quietWarned = true;
                MpLog.Warn($"Connection: nothing from the server for {age} ms ({conn.TrafficText}); timer {TickPatch.Timer}, limit {TickPatch.tickUntil}");
            }
            else if (age <= ConnectionWatch.StaleMs && quietWarned)
            {
                quietWarned = false;
                MpLog.Log($"Connection: the server is sending again ({conn.TrafficText})");
            }

            if (receivedCmds < remoteSentCmds)
            {
                if (!waitingForCmds.IsRunning) waitingForCmds.Restart();
                if (!waitingWarned && waitingForCmds.ElapsedMilliseconds > ConnectionWatch.StaleMs)
                {
                    waitingWarned = true;
                    MpLog.Warn($"Connection: waiting {waitingForCmds.ElapsedMilliseconds} ms for {remoteSentCmds - receivedCmds} commands the server counted; " +
                               $"this game stays at timer {TickPatch.Timer}, limit {TickPatch.tickUntil} (the server's {remoteTickUntil}); {conn.TrafficText}");
                }
            }
            else if (waitingForCmds.IsRunning)
            {
                if (waitingWarned)
                    MpLog.Log($"Connection: the commands arrived after {waitingForCmds.ElapsedMilliseconds} ms");
                waitingForCmds.Reset();
                waitingWarned = false;
            }

            if (sinceTrafficLog.ElapsedMilliseconds > 30_000)
            {
                sinceTrafficLog.Restart();
                MpLog.Log($"Connection: {conn.TrafficText}; commands {receivedCmds} of {remoteSentCmds}, timer {TickPatch.Timer}, " +
                          $"limit {TickPatch.tickUntil} (the server's {remoteTickUntil})");
            }
        }
    }
}
