using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Client.Comp;
using Multiplayer.Client.Networking;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public static class HostUtil
    {
        // Raised when a single-player game (not a replay) is about to be hosted, before anything is changed; the
        // argument is the multifaction setting. A handler may change the game (Rimkit splits planned colonies into
        // player factions here). A throw cancels hosting and leaves the single-player game running
        public static event Action<bool> BeforeHostFromSingleplayer;

        // Raised while a single-player game is set up for hosting, after every player faction has its world data
        // (factionData, ReassignIds) and before the maps are set up. A throw is logged and hosting continues
        public static event Action AfterFactionDataCreated;

        // Host entry points:
        // - singleplayer save, server browser
        // - singleplayer save, ingame
        // - replay, server browser
        // - replay, ingame
        public static async ClientTask HostServer(ServerSettings settings, bool fromReplay)
        {
            Log.Message("Starting the server");

            if (!fromReplay && BeforeHostFromSingleplayer != null)
            {
                try
                {
                    BeforeHostFromSingleplayer(settings.multifaction);
                }
                catch (Exception e)
                {
                    // This runs before the first await, so hosting stops here and the caller sees no session
                    Log.Error($"MP: hosting cancelled by a BeforeHostFromSingleplayer handler: {e}");
                    Messages.Message($"Hosting cancelled: {e.Message}", MessageTypeDefOf.RejectInput, false);
                    Multiplayer.StopMultiplayer();
                    return;
                }
            }

            CreateSession(settings);

            if (!fromReplay)
                SetupGameFromSingleplayer(settings.multifaction);

            // Server already pre-inited in HostWindow
            PrepareLocalServer(settings, fromReplay);

            var serverConn = CreateLocalClient(settings);
            PrepareGame();
            SetGameState(settings);

            Multiplayer.session.dataSnapshot = await CreateGameData();

            MakeHostOnServer(serverConn);

            // todo handle sending cmds for hosting from loaded replay?
            SaveLoad.SendGameData(Multiplayer.session.dataSnapshot, false);

            StartLocalServer();
        }

        private static void CreateSession(ServerSettings settings) =>
            Multiplayer.session = new MultiplayerSession
            {
                myFactionId = Faction.OfPlayer.loadID,
                gameName = settings.gameName,
                dataSnapshot = Multiplayer.session?.dataSnapshot // This is the case when hosting from a replay
            };

        private static void PrepareLocalServer(ServerSettings settings, bool fromReplay)
        {
            var localServer = Multiplayer.LocalServer;
            MultiplayerServer.instance = Multiplayer.LocalServer;

            localServer.hostUsername = Multiplayer.username;
            localServer.worldData.hostFactionId = Faction.OfPlayer.loadID;
            localServer.worldData.spectatorFactionId = Multiplayer.WorldComp.spectatorFaction.loadID;

            // Faction reservations kept in the save (always empty without multifaction)
            foreach (var (username, factionId) in Multiplayer.GameComp.playerFactions)
                localServer.worldData.playerFactions[username] = factionId;

            if (fromReplay)
            {
                localServer.gameTimer = TickPatch.Timer;
                localServer.startingTimer = TickPatch.Timer;
            }

            var initData = ClientJoiningState.CreateInitDataPacket(settings.syncConfigs);
            localServer.StartInitData().SetResult(ServerInitData.FromNet(initData));
        }

        private static void PrepareGame()
        {
            foreach (var tickable in TickPatch.AllTickables)
                tickable.Cmds.Clear();

            Multiplayer.game.ChangeRealPlayerFaction(Faction.OfPlayer);
            Multiplayer.session.ReapplyPrefs();

            Find.MainTabsRoot.EscapeCurrentTab(false);

            Multiplayer.session.AddMsg("If you are having any issues with the mod and would like some help resolving them, then please reach out to us on our Discord server:", false);
            Multiplayer.session.AddMsg(new ChatMsg_Url(Multiplayer.DiscordLink), false);
        }

        private static void SetGameState(ServerSettings settings)
        {
            Multiplayer.AsyncWorldTime.DesiredTimeSpeed = TimeSpeed.Paused;
            foreach (var map in Find.Maps)
                map.AsyncTime().DesiredTimeSpeed = TimeSpeed.Paused;

            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            Multiplayer.GameComp.asyncTime = settings.asyncTime;
            Multiplayer.GameComp.multifaction = settings.multifaction;
            Multiplayer.GameComp.debugMode = settings.debugMode;
            Multiplayer.GameComp.logDesyncTraces = settings.desyncTraces;
            Multiplayer.GameComp.desyncDebugFreeze = settings.desyncDebugFreeze;
            Multiplayer.GameComp.pauseOnLetter = settings.pauseOnLetter;
            Multiplayer.GameComp.timeControl = settings.timeControl;
        }

        private static async Task<GameDataSnapshot> CreateGameData()
        {
            await LongEventTask.ContinueInLongEvent("MpSaving", false);
            return SaveLoad.CreateGameDataSnapshot(SaveLoad.SaveAndReload(), Multiplayer.GameComp.multifaction);
        }

        private static void SetupGameFromSingleplayer(bool multifaction)
        {
            var worldComp = new MultiplayerWorldComp(Find.World);

            Multiplayer.game = new MultiplayerGame
            {
                gameComp = new MultiplayerGameComp(),
                asyncWorldTimeComp = new AsyncWorldTimeComp(Find.World) { worldTicks = Find.TickManager.TicksGame },
                worldComp = worldComp
            };

            // With multifaction, a faction name the player chose is kept
            if (!multifaction || !Faction.OfPlayer.HasName)
                Faction.OfPlayer.Name = $"{Multiplayer.username}'s faction";

            var spectator = AddNewFaction("Spectator", FactionDefOf.PlayerColony);
            spectator.hidden = true;
            spectator.SetRelation(new FactionRelation(Faction.OfPlayer, FactionRelationKind.Neutral));
            worldComp.spectatorFaction = spectator;

            var playerFactionData = FactionWorldData.FromCurrent(Faction.OfPlayer.loadID);
            worldComp.factionData[Faction.OfPlayer.loadID] = playerFactionData;

            foreach (var faction in Find.FactionManager.AllFactions.Where(f => f.IsPlayer))
                if (faction != Faction.OfPlayer)
                {
                    var factionData = FactionWorldData.New(faction.loadID);
                    worldComp.factionData[faction.loadID] = factionData;

                    factionData.researchManager.progress = new(playerFactionData.researchManager.progress);
                    factionData.researchManager.techprints = new(playerFactionData.researchManager.techprints);
                    factionData.researchManager.anomalyKnowledge = new(playerFactionData.researchManager.anomalyKnowledge);
                    factionData.researchManager.currentAnomalyKnowledgeProjects = new(playerFactionData.researchManager.currentAnomalyKnowledgeProjects ?? Enumerable.Empty<ResearchManager.KnowledgeCategoryProject>());
                    factionData.researchManager.tabInfoVisibility = new();
                    if (playerFactionData.researchManager.tabInfoVisibility != null)
                    {
                        foreach (var (def, value) in playerFactionData.researchManager.tabInfoVisibility)
                            factionData.researchManager.tabInfoVisibility[def] = value;
                    }
                }

            foreach (FactionWorldData data in worldComp.factionData.Values)
                data.ReassignIds();

            if (AfterFactionDataCreated != null)
            {
                try
                {
                    AfterFactionDataCreated();
                }
                catch (Exception e)
                {
                    Log.Error($"MP: AfterFactionDataCreated handler failed: {e}");
                }
            }

            foreach (Map map in Find.Maps)
            {
                MapSetup.SetupMap(map, true, multifaction ? MapSetup.OwnerAtHostTime(map) : null);
            }
        }

        // Records that a username plays a faction, so that player joins straight into it (multifaction); saved with
        // the game. For AfterFactionDataCreated handlers (before that there is no game comp to write to)
        public static void ReservePlayerFaction(string username, int factionId)
        {
            var gameComp = Multiplayer.game?.gameComp;
            if (username.NullOrEmpty() || gameComp == null)
                return;
            gameComp.playerFactions[username] = factionId;
        }

        // The default apparel, drug and food policies of a player faction's own databases (its multifaction world
        // data). For AfterFactionDataCreated handlers
        public static bool TryGetFactionDefaultPolicies(int factionId, out ApparelPolicy apparel, out DrugPolicy drug, out FoodPolicy food)
        {
            apparel = null;
            drug = null;
            food = null;

            var worldComp = Multiplayer.game?.worldComp;
            if (worldComp == null || !worldComp.factionData.TryGetValue(factionId, out var data))
                return false;

            apparel = data.outfitDatabase?.DefaultOutfit();
            drug = data.drugPolicyDatabase?.DefaultDrugPolicy();
            food = data.foodRestrictionDatabase?.DefaultFoodRestriction();
            return apparel != null && drug != null && food != null;
        }

        public static Faction AddNewFaction(string name, FactionDef def)
        {
            Faction faction = new Faction { loadID = Find.UniqueIDsManager.GetNextFactionID(), def = def };

            faction.ideos = new FactionIdeosTracker(faction);
            faction.ideos.ChooseOrGenerateIdeo(new IdeoGenerationParms());

            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
                faction.TryMakeInitialRelationsWith(other);

            faction.Name = name;
            faction.def = def;

            Find.FactionManager.Add(faction);

            return faction;
        }

        private static LocalConnection CreateLocalClient(ServerSettings settings)
        {
            if (settings.arbiter)
                StartArbiter();

            var (client, server) = LocalConnection.Paired(Multiplayer.username);
            client.ChangeState(ConnectionStateEnum.ClientPlaying);
            Multiplayer.session.client = client;
            return server;
        }

        private static void MakeHostOnServer(LocalConnection serverConnection)
        {
            var server = Multiplayer.LocalServer;
            var player = server.playerManager.OnConnected(serverConnection);
            server.playerManager.MakeHost(player);
            // Normally set during ClientJoiningState, but when hosting we skip straight through to the playing state.
            Multiplayer.session.playerId = player.id;
        }

        private static void StartLocalServer()
        {
            Multiplayer.LocalServer.running = true;

            Multiplayer.localServerThread = new Thread(Multiplayer.LocalServer.Run)
            {
                Name = "Local server thread"
            };
            Multiplayer.localServerThread.Start();

            const string text = "Server started.";
            Messages.Message(text, MessageTypeDefOf.SilentInput, false);
            Log.Message(text);
        }

        private static void StartArbiter()
        {
            Multiplayer.session.AddMsg("The Arbiter instance is starting...", false);

            var arbiterNet = LiteNetArbiterManager.Create(Multiplayer.LocalServer);
            if (arbiterNet == null) throw new Exception("Failed to setup Arbiter network.");
            Multiplayer.LocalServer.netManagers.Add(arbiterNet);

            string args = $"-batchmode -nographics -arbiter -logfile arbiter_log.txt -connect=127.0.0.1:{arbiterNet.Port}";

            if (GenCommandLine.TryGetCommandLineArg("savedatafolder", out string saveDataFolder))
                args += $" \"-savedatafolder={saveDataFolder}\"";

            string arbiterInstancePath;
            if (Application.platform == RuntimePlatform.OSXPlayer)
            {
                arbiterInstancePath = Application.dataPath + "/MacOS/" + Process.GetCurrentProcess().MainModule!.ModuleName;
            }
            else
            {
                arbiterInstancePath = Process.GetCurrentProcess().MainModule!.FileName;
            }

            try
            {
                Multiplayer.session.arbiter = Process.Start(
                    arbiterInstancePath,
                    args
                );
            }
            catch (Exception ex)
            {
                Multiplayer.session.AddMsg("Arbiter failed to start.", false);
                Log.Error("Arbiter failed to start.");
                Log.Error(ex.ToString());
                if (ex.InnerException is Win32Exception)
                {
                    Log.Error("Win32 Error Code: " + ((Win32Exception)ex).NativeErrorCode);
                }
            }
        }
    }
}
