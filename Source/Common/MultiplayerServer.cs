using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Multiplayer.Common.ChatCommands;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class MultiplayerServer
    {
        static MultiplayerServer()
        {
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerSteam, typeof(ServerSteamState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerJoining, typeof(ServerJoiningState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerBootstrap, typeof(ServerBootstrapState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerLoading, typeof(ServerLoadingState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ServerPlaying, typeof(ServerPlayingState));
        }

        public static MultiplayerServer? instance;

        public const int DefaultPort = 30502;
        public const int LanBroadcastPort = 5100;
        public const string LanBroadcastName = "mp-server";
        public const int MaxUsernameLength = 15;
        public const int MinUsernameLength = 3;
        public const char EndpointSeparator = '&';
        public const int NetTicksPerSecond = 30; // Not an exact amount. The net loop isn't particularly precise.

        public static readonly Regex UsernamePattern = new(@"^[a-zA-Z0-9_]+$");

        public WorldData worldData;
        public FreezeManager freezeManager;
        public CommandHandler commands;
        public ChatCommandManager chatCmdManager;
        public PlayerManager playerManager;
        public SyncReferee referee;
        public List<INetManager> netManagers = [];
        public IEnumerable<ServerPlayer> JoinedPlayers => playerManager.JoinedPlayers;
        public IEnumerable<ServerPlayer> PlayingPlayers => playerManager.PlayingPlayers;
        public IEnumerable<ServerPlayer> PlayingIngamePlayers => playerManager.PlayingPlayers.Where(p => p.status == PlayerStatus.Playing);

        public string? hostUsername;
        public int gameTimer;
        // The timer stops here (debug freeze, see SyncReferee); int.MaxValue when not held
        public int holdTimerAt = int.MaxValue;

        /// <summary>
        /// Host-led clock (a hosted game, not a standalone server). The host's game sets the pace: it reports its
        /// timer through the same ordered channel as its commands, and <see cref="gameTimer"/>, which every other game
        /// runs up to, is the last timer it reported. The host runs up to <see cref="hostLimit"/>, a few ticks ahead.
        ///
        /// Why it's safe:
        /// - The host schedules its own commands at its next tick and runs them without waiting for the server; the
        ///   server only forwards them. No other game can have passed that tick, because the host reports reaching
        ///   it only after the command, in order.
        /// - Everyone else's commands (and the server's) are stamped one past the highest limit the host was given,
        ///   so the host, which can't run past its limit, gets them before it reaches their tick.
        /// - The host picks its tick past any command it has already received, so no two commands share a tick
        ///   unless they came the same way, and every game puts commands in tick order.
        /// Null hostTimer: the old server-paced clock (standalone servers).
        /// </summary>
        public Func<int>? hostTimer;
        public bool HostLed => hostTimer != null;
        public const int HostLimitLead = 6;
        public int hostLimit;
        private int lastSentHostLimit;

        /// <summary>The tick a command the server schedules now runs at.</summary>
        public int StampTick => HostLed ? Math.Max(lastSentHostLimit, gameTimer) + 1 : gameTimer;

        public void OnHostTimer(int timer)
        {
            if (HostLed && timer > gameTimer)
                gameTimer = timer;
        }

        // Set when something is queued for the server thread, so a host command is stamped and sent right away
        private readonly AutoResetEvent queued = new(false);
        public int startingTimer;
        public int workTicks;
        public ActionQueue queue = new();
        public ServerSettings settings;

        public ServerInitData? InitData => initDataSource.Task.ResultNowOrNull();
        private TaskCompletionSource<ServerInitData?> initDataSource = new();
        public InitDataState InitDataState =>
            InitData != null ? InitDataState.Complete :
            // started init data must've completed with null, meaning the client disconnected while waiting for the data,
            // so we are waiting again
            initDataSource.Task.IsCompleted ? InitDataState.Waiting :
            InitDataState.Requested;

        public volatile bool running;

        public bool ArbiterPlaying => PlayingPlayers.Any(p => p.IsArbiter && p.status == PlayerStatus.Playing);
        public ServerPlayer HostPlayer => PlayingPlayers.First(p => p.IsHost);

        public bool FullyStarted => running && worldData.savedGame != null;

        public const float StandardTimePerTick = 1000.0f / 60.0f;

        public float serverTimePerTick = StandardTimePerTick;

        // Each game's frame time once a second (MpDesyncs/perf-timeline-*.txt); null when nothing set it up
        public PerfTimeline? perfTimeline;
        public int sentCmdsSnapshot;

        public int NetTimer { get; private set; }

        public readonly ConnectionWatch connectionWatch = new();
        private const int TrafficLogEverySeconds = 30;

        public bool IsStandaloneServer { get; set; }
        public StandalonePersistence? persistence;

        public MultiplayerServer(ServerSettings settings)
        {
            this.settings = settings;

            worldData = new WorldData(this);
            freezeManager = new FreezeManager(this);
            commands = new CommandHandler(this);
            chatCmdManager = new ChatCommandManager(this);
            playerManager = new PlayerManager(this);
            referee = new SyncReferee(this);

            ChatCommandRegistry.Register(chatCmdManager, this);

            initDataSource.SetResult(null);
        }

        public void Run()
        {
            ServerLog.Detail("Server started");

            Stopwatch time = Stopwatch.StartNew();
            Stopwatch tickTime = Stopwatch.StartNew();
            double realTime = 0;

            List<ServerPlayer> playersBehind = [];
            while (running)
            {
                try
                {
                    double elapsed = time.ElapsedMillisDouble();
                    time.Restart();
                    realTime += elapsed;

                    tickTime.Restart();

                    freezeManager.Tick();
                    queue.RunQueue(ServerLog.Error);
                    netManagers.ForEach(manager => manager.Tick());
                    TickNet();

                    if (HostLed)
                    {
                        TickHostLed();
                        realTime = 0;
                    }

                    int ticked = 0;
                    while (!HostLed && realTime > 0 && ticked < 2)
                    {
                        playersBehind.Clear();
                        playersBehind.AddRange(PlayingIngamePlayers.Where(p => p.ExtrapolatedTicksBehind > 90 || connectionWatch.IsStale(p.id)));
                        if (!freezeManager.Frozen &&
                            gameTimer < holdTimerAt &&
                            PlayingPlayers.Any(p => p.ExtrapolatedTicksBehind < 40) &&
                            !playersBehind.Any())
                        {
                            gameTimer++;
                            sentCmdsSnapshot = commands.SentCmds;
                        }
                        else if (playersBehind.Any())
                        {
                            var text = playersBehind.Join(p => $"{p.Username}");
                            ServerLog.Log($"Simulation paused because some players are too far behind: {text}");
                        }

                        // Run up to three times slower depending on max ticksBehind
                        var slowdown = Math.Min(
                            PlayingIngamePlayers.MaxOrZero(p => p.ticksBehind) / 60f,
                            2f
                        );
                        realTime -= serverTimePerTick * (1f + slowdown);

                        ticked++;
                    }

                    if (realTime > 0)
                        realTime = 0f;

                    if (MpVersion.IsDebug && tickTime.ElapsedMillisDouble() > 15f)
                        ServerLog.Log($"Server tick took {tickTime.ElapsedMillisDouble()}ms");

                    // On Windows, the clock ticks 64 times a second and sleep durations too close to a multiple of 15.625ms
                    // tend to be rounded up, so we sleep for a bit less
                    int sleepFor = (int)Math.Floor((1000d / NetTicksPerSecond - tickTime.ElapsedMillisDouble()) * 0.9f);

                    // Wake for queued work (a host command) without running a whole net tick early
                    while (sleepFor > 0)
                    {
                        if (queued.WaitOne(sleepFor))
                            queue.RunQueue(ServerLog.Error);
                        sleepFor = (int)Math.Floor((1000d / NetTicksPerSecond - tickTime.ElapsedMillisDouble()) * 0.9f);
                    }
                }
                catch (Exception e)
                {
                    ServerLog.Log($"Exception ticking the server: {e}");
                }
            }

            try
            {
                TryStop();
            }
            catch (Exception e)
            {
                ServerLog.Log($"Exception stopping the server: {e}");
            }
        }

        // Host-led: the host may run a few ticks past where it is, unless someone is too far behind, the game is
        // frozen or held (debug freeze); the pace slows the way the server clock used to when players fall behind
        private void TickHostLed()
        {
            var behind = PlayingIngamePlayers.Where(p => !p.IsHost && (p.ExtrapolatedTicksBehind > 90 || connectionWatch.IsStale(p.id))).ToList();
            if (behind.Any() && NetTimer % NetTicksPerSecond == 0)
                ServerLog.Log($"Simulation paused because some players are too far behind: " +
                              behind.Join(p => connectionWatch.IsStale(p.id) ? $"{p.Username} (no packet for {p.conn.LastReceivedAgeMs} ms)" : p.Username));

            if (!freezeManager.Frozen && !behind.Any())
            {
                var limit = Math.Min(gameTimer + HostLimitLead, holdTimerAt);
                if (limit > hostLimit) hostLimit = limit;
            }

            sentCmdsSnapshot = commands.SentCmds;
        }

        private void TickNet()
        {
            NetTimer++;

            if (NetTimer % NetTicksPerSecond == 0)
            {
                playerManager.SendLatencies();
                CheckConnections();
            }


            if (NetTimer % (NetTicksPerSecond / 5) == 0)
            {
                foreach (var player in JoinedPlayers) {
                    player.SendKeepAlivePacket();
                }
            }

            // Send to simulating players as well to update the simulation window for them and actually update further
            // during the same simulation.
            if (HostLed)
            {
                // The count must be taken now, with the timers it goes out with: gameTimer may already have moved
                // past a host command processed since the last count, and a game that sees the new limit with the
                // old count would run past that command's tick before receiving it (Desync-10, "Random state from
                // commands doesn't match: 1 vs 0 entries")
                sentCmdsSnapshot = commands.SentCmds;

                // The host runs to its limit, everyone else to where the host is; a slow player slows the host down
                var slowdown = Math.Min(PlayingIngamePlayers.Where(p => !p.IsHost).MaxOrZero(p => p.ticksBehind) / 60f, 2f);
                foreach (var player in PlayingPlayers)
                {
                    var tickUntil = player.IsHost ? hostLimit : gameTimer;
                    player.SendPacket(new ServerTimeControlPacket(tickUntil, sentCmdsSnapshot,
                        player.IsHost ? serverTimePerTick * (1f + slowdown) : serverTimePerTick), false);
                }
                if (hostLimit > lastSentHostLimit) lastSentHostLimit = hostLimit;
            }
            else
            {
                SendToPlaying(new ServerTimeControlPacket(gameTimer, sentCmdsSnapshot, serverTimePerTick), false);
            }

            serverTimePerTick = PlayingIngamePlayers.MaxOrZero(p => p.frameTime);

            if (serverTimePerTick < StandardTimePerTick)
                serverTimePerTick = StandardTimePerTick;

            if (serverTimePerTick > StandardTimePerTick * 4f)
                serverTimePerTick = StandardTimePerTick * 4f;

            if (perfTimeline != null)
            {
                perfTimeline.Sample(PlayingIngamePlayers.Select(p => (p.Username, p.frameTime)));
                if (NetTimer % NetTicksPerSecond == 0)
                    perfTimeline.Second(PlayingIngamePlayers.Select(p => p.Username), serverTimePerTick, DateTime.Now,
                        PlayingIngamePlayers.Where(p => connectionWatch.IsStale(p.id))
                            .Select(p => (p.Username, p.conn.LastReceivedAgeMs / 1000.0)).ToList());
            }
        }

        /// <summary>Once a second: a playing game that went quiet pauses everyone (see <see cref="ConnectionWatch"/>).</summary>
        private void CheckConnections()
        {
            var watched = PlayingIngamePlayers.Where(p => !p.IsHost).ToList();
            connectionWatch.Keep(watched.Select(p => p.id).ToHashSet());

            bool grace = worldData.CreatingJoinPoint ||
                         Environment.TickCount64 - worldData.joinPointEndedAtMs < ConnectionWatch.JoinPointGraceMs;
            bool logTraffic = NetTimer % (NetTicksPerSecond * TrafficLogEverySeconds) == 0;

            foreach (var player in watched)
            {
                var age = player.conn.LastReceivedAgeMs;
                switch (connectionWatch.Check(player.id, age, grace))
                {
                    case ConnectionWatch.Change.Lost:
                        ServerLog.Log($"{player.Username}: connection lost, waiting ({player.conn.TrafficText})");
                        SendNotification("MpPlayerConnectionLost", player.Username, $"{age / 1000}");
                        break;
                    case ConnectionWatch.Change.Back:
                        ServerLog.Log($"{player.Username}: connection back ({player.conn.TrafficText})");
                        SendNotification("MpPlayerConnectionBack", player.Username);
                        break;
                    case ConnectionWatch.Change.Drop:
                        ServerLog.Log($"{player.Username}: no packet for {age / 1000} s, dropping them so they can rejoin ({player.conn.TrafficText})");
                        SendNotification("MpPlayerConnectionDropped", player.Username, $"{age / 1000}");
                        connectionWatch.Forget(player.id);
                        playerManager.SetDisconnected(player.conn, MpDisconnectReason.NetFailed);
                        continue;
                }

                if (logTraffic)
                    ServerLog.Log($"Connection {player.Username}: {player.conn.TrafficText}, {player.ExtrapolatedTicksBehind} ticks behind");
            }
        }

        public void TryStop()
        {
            ServerLog.Detail("Server shutting down...");

            playerManager.OnServerStop();
            netManagers.ForEach(manager => manager.Stop());

            instance = null;
        }

        public void Enqueue(Action action)
        {
            queue.Enqueue(action);
            queued.Set();
        }

        public void SendToPlaying<T>(T packet, bool reliable = true, ServerPlayer? excluding = null) where T : IPacket
        {
            var serialized = packet.Serialize();
            foreach (ServerPlayer player in PlayingPlayers)
                if (player != excluding)
                    player.conn.Send(serialized, reliable);
        }

        public void SendToIngame<T>(T packet, bool reliable = true, ServerPlayer? excluding = null) where T : IPacket
        {
            var serialized = packet.Serialize();
            foreach (ServerPlayer player in PlayingIngamePlayers)
                if (player != excluding)
                    player.conn.Send(serialized, reliable);
        }

        public bool CanUseStandaloneMapStreaming(int mapId) => false;

        public void SendMapResponse(ServerPlayer player, int mapId)
        {
            if (!CanUseStandaloneMapStreaming(mapId))
                return;

            ByteWriter writer = new ByteWriter();
            writer.WriteInt32(mapId);

            var mapCmds = worldData.mapCmds.GetValueSafe(mapId) ?? [];
            writer.WriteInt32(mapCmds.Count);
            foreach (var cmd in mapCmds)
                writer.WritePrefixedBytes(cmd);

            writer.WritePrefixedBytes(worldData.mapData[mapId]);
            player.conn.SendFragmented(Packets.Server_MapResponse, writer.ToArray());
        }

        public ServerPlayer? GetPlayer(string username)
        {
            return playerManager.GetPlayer(username);
        }

        public ServerPlayer? GetPlayer(int id)
        {
            return playerManager.GetPlayer(id);
        }

        public void SendChat(string msg)
        {
            ServerLog.Detail($"[Chat] {msg}");
            SendToPlaying(ServerChatPacket.Create(msg));
        }

        public void SendNotification(string key, params string[] args) =>
            SendToPlaying(new ServerNotificationPacket(key) { args = args });

        public void RegisterChatCommand(string commandName, IChatCommand command) =>
            chatCmdManager.AddCommand(commandName, command);

        public void RegisterChatCommand(string[] commandNames, IChatCommand command, string description = "", string usage = "", bool requiresHost = false) =>
            chatCmdManager.AddCommands(commandNames, command, description, usage, requiresHost);

        [Obsolete("Use RegisterChatCommand instead.")]
        public void RegisterChatCmd(string cmdName, ChatCmdHandler handler) =>
            RegisterChatCommand(cmdName, handler);

        public void HandleChatCommand(IChatSource source, string command) => chatCmdManager.Handle(source, command);

        [Obsolete("Use HandleChatCommand instead.")]
        public void HandleChatCmd(IChatSource source, string cmd) => HandleChatCommand(source, cmd);

        public Task<ServerInitData?> InitDataTask() => initDataSource.Task;

        public bool BootstrapMode { get; set; }

        /// Can only start one init data at a time. A StartInitData is considered complete once
        /// TaskCompletionResult.SetResult is called. Until that time no new calls to StartInitData will succeed.
        public TaskCompletionSource<ServerInitData?> StartInitData()
        {
            if (InitDataState != InitDataState.Waiting)
                throw new InvalidOperationException($"Can't start init data in state {InitDataState}");
            return initDataSource = new TaskCompletionSource<ServerInitData?>();
        }
    }

    public enum InitDataState
    {
        Waiting,
        Requested,
        Complete
    }
}
