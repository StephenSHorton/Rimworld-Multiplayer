using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using Multiplayer.Client.Util;
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

        RebuildCmdQueues();
    }

    /// <summary>
    /// Builds every command queue from the snapshot, the one list that has every command since the join point.
    /// A joiner's load takes several frames: a command that arrived after a map's <c>FinalizeInit</c> copied its
    /// list but before the game was Playing only reached the snapshot (<c>ScheduleCommand</c> returns early then),
    /// and one that arrived after that went into the world queue, which this replaced with the snapshot's global
    /// commands. Both were lost (2026-10-22 Desync-01: map 0's speed changes never ran and its clock stopped;
    /// Desync-02: "Init map with cmds 123" with 138 in the snapshot). Queues are laid out the way
    /// <c>ScheduleCommand</c> lays them out in play.
    /// </summary>
    private static void RebuildCmdQueues()
    {
        var snapshotCmds = Multiplayer.session.dataSnapshot.MapCmds;
        var world = new List<ScheduledCommand>();
        var counts = new List<string>();
        int late = 0;

        foreach (Map map in Find.Maps)
            map.AsyncTime().cmds = new Queue<ScheduledCommand>();

        // Global first, then maps by id, so commands on the same tick keep one order on every game
        foreach (var (mapId, cmds) in snapshotCmds.OrderBy(kv => kv.Key))
        {
            counts.Add($"{(mapId == ScheduledCommand.Global ? "global" : $"map {mapId}")} {cmds.Count}");
            late += cmds.Count(c => c.ticks < TickPatch.Timer);

            if (mapId == ScheduledCommand.Global || MultiplayerSession.consistentCommandOrder)
                world.AddRange(cmds);
            else if (Find.Maps.FirstOrDefault(m => m.uniqueID == mapId) is { } map)
                map.AsyncTime().cmds = new Queue<ScheduledCommand>(HostLedClock.InTickOrder(cmds));
            else
                MpLog.Error($"{cmds.Count} commands for map {mapId}, which this game didn't load, are dropped");
        }

        Multiplayer.AsyncWorldTime.cmds = new Queue<ScheduledCommand>(HostLedClock.InTickOrder(world));

        Log.Message($"Command queues from the snapshot at timer {TickPatch.Timer}: {counts.Join(", ")}" +
                    (MultiplayerSession.consistentCommandOrder ? " (all in the world queue)" : ""));
        if (late > 0)
            MpLog.Warn($"{late} snapshot commands are for ticks before timer {TickPatch.Timer}; they run at once");
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
