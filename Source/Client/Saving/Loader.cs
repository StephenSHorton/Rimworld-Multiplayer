using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using Multiplayer.Common;
using Verse;
using Verse.Profile;

namespace Multiplayer.Client.Saving;

public static class Loader
{
    public static void ReloadGame(List<int> mapsToLoad, bool changeScene, bool forceAsyncTime) => ReloadGame(mapsToLoad,
        changeScene, () =>
        {
            if (forceAsyncTime) Multiplayer.game.gameComp.asyncTime = true;
        });

    public static void ReloadGame(List<int> mapsToLoad, bool changeScene, Action customPostLoadAction)
    {
        var gameDoc = DataSnapshotToXml(Multiplayer.session.dataSnapshot, mapsToLoad);

        LoadPatch.gameToLoad = new TempGameData(gameDoc, Multiplayer.session.dataSnapshot.SessionData);
        TickPatch.replayTimeSpeed = TimeSpeed.Paused;

        if (changeScene)
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                MemoryUtility.ClearAllMapsAndWorld();
                Current.Game = new Game
                {
                    InitData = new GameInitData
                    {
                        gameToLoad = "server"
                    }
                };

                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    LongEventHandler.QueueLongEvent(() =>
                    {
                        PostLoad();
                        customPostLoadAction();
                    }, "MpSimulating", false, null);
                });
            }, "Play", "MpLoading", true, null);
        }
        else
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                SaveLoad.LoadInMainThread(LoadPatch.gameToLoad);
                PostLoad();
                customPostLoadAction();
            }, "MpLoading", false, null);
        }
    }

    private static void PostLoad()
    {
        // If the client gets disconnected during loading
        if (Multiplayer.Client == null) return;

        Multiplayer.session.dataSnapshot = Multiplayer.session.dataSnapshot with
        {
            CachedAtTime = TickPatch.Timer
        };

        Multiplayer.session.replayTimerStart = TickPatch.Timer;

        Multiplayer.game.ChangeRealPlayerFaction(
            Find.FactionManager.GetById(Multiplayer.session.myFactionId) ?? Multiplayer.WorldComp.spectatorFaction
        );
        Multiplayer.game.myFactionLoading = null;

        RebuildCommandQueues();
        Desyncs.DebugCostWarning.Check();
    }

    /// <summary>
    /// Every queue from the snapshot's lists, placed as <see cref="MultiplayerSession.ScheduleCommand"/> places a
    /// command that arrives in play. The lists hold every command since the join point, including those that came
    /// while the game loaded; the queues are rebuilt here, on the main thread, after the load, so none is missed or
    /// queued twice.
    /// </summary>
    private static void RebuildCommandQueues()
    {
        var snapshot = Multiplayer.session.dataSnapshot.MapCmds;
        // The tick loop's order (TickPatch.AllTickables): the world, then the maps from the last
        var loadedMaps = Enumerable.Range(0, Find.Maps.Count).Reverse().Select(i => Find.Maps[i].uniqueID).ToList();
        var plan = CommandQueuePlan.Build(snapshot, loadedMaps, TickPatch.Timer, MultiplayerSession.consistentCommandOrder);

        Multiplayer.AsyncWorldTime.cmds = new Queue<ScheduledCommand>(plan.world);
        foreach (var map in Find.Maps)
            map.AsyncTime().cmds = new Queue<ScheduledCommand>(plan.maps.GetValueSafe(map.uniqueID) ?? []);

        Log.Message($"Command queues at timer {TickPatch.Timer}: {plan.Describe()}");
        CheckAllQueued("after the load");
    }

    /// <summary>Logs an error for every snapshot command still ahead that no queue holds: this game would never run it.</summary>
    public static void CheckAllQueued(string when)
    {
        if (Multiplayer.session?.dataSnapshot == null || Multiplayer.game == null) return;

        var loaded = Find.Maps.Select(m => m.uniqueID).ToHashSet();
        var missing = CommandQueuePlan.Missing(Multiplayer.session.dataSnapshot.MapCmds,
            TickPatch.AllTickables.Select(t => (IEnumerable<ScheduledCommand>)t.Cmds), loaded, TickPatch.Timer);
        if (missing.Count == 0) return;

        Log.Error($"{missing.Count} commands {when} are in the snapshot but in no queue at timer {TickPatch.Timer}; " +
                  $"this game won't run them: {string.Join("; ", missing.Take(8))}{(missing.Count > 8 ? "; ..." : "")}");
    }

    private static XmlDocument DataSnapshotToXml(GameDataSnapshot dataSnapshot, List<int> mapsToLoad)
    {
        XmlDocument gameDoc = ScribeUtil.LoadDocument(dataSnapshot.GameData);
        XmlNode gameNode = gameDoc.DocumentElement["game"];

        foreach (int map in mapsToLoad)
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(dataSnapshot.MapData[map]));
            XmlNode mapNode = gameDoc.ReadNode(reader);
            gameNode["maps"].AppendChild(mapNode);
        }

        return gameDoc;
    }
}
