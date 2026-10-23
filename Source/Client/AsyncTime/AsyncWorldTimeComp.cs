using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Comp;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Factions;
using Multiplayer.Client.Patches;
using Multiplayer.Client.Saving;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.AsyncTime;

public class AsyncWorldTimeComp : IExposable, ITickable
{
    public static bool tickingWorld;
    private TimeSpeed timeSpeedInt;

    public float TimeToTickThrough { get; set; }

    public float TickRateMultiplier(TimeSpeed speed)
    {
        if (Multiplayer.GameComp.asyncTime)
        {
            var enforcePause = Multiplayer.WorldComp.sessionManager.IsAnySessionCurrentlyPausing(null);

            if (enforcePause)
                return 0f;
        }

        return speed switch
        {
            TimeSpeed.Paused => 0f,
            TimeSpeed.Normal => 1f,
            TimeSpeed.Fast => 3f,
            TimeSpeed.Superfast => 6f,
            TimeSpeed.Ultrafast => 15f,
            _ => -1f
        };
    }

    // Run at the speed of the fastest map or at chosen speed if there are no maps
    public TimeSpeed DesiredTimeSpeed
    {
        get => !Find.Maps.Any()
            ? timeSpeedInt
            : Find.Maps.Select(m => m.AsyncTime())
                .Where(a => a.ActualRateMultiplier(a.DesiredTimeSpeed) != 0f)
                .Max(a => a?.DesiredTimeSpeed) ?? TimeSpeed.Paused;
        set => timeSpeedInt = value;
    }

    public Queue<ScheduledCommand> Cmds => cmds;
    public Queue<ScheduledCommand> cmds = new();

    public int CurrentPlayerCount { get; private set; }
    public int VTR => CurrentPlayerCount > 0 ? VTRSync.MinimumVtr : VTRSync.MaximumVtr;

    public int TickableId => -1;

    public World world;
    public ulong randState;

    // The random state at the end of the world's last recorded tick or command (Rand attribution)
    private ulong attrLastEnd;
    private bool attrLastEndKnown;

    /// <summary>
    /// The world's clock: its tick count, its game start tick and its time slower, which the tick manager holds
    /// whenever no map's context is open (a map's context switches the tick manager to the map's and back). They are
    /// saved here, and every save writes them into the tick manager's section too (<see cref="WithWorldClock"/>).
    /// </summary>
    public int worldTicks;
    public int gameStartAbsTick;
    public TimeSlower slower;

    public AsyncWorldTimeComp(World world)
    {
        this.world = world;
        gameStartAbsTick = Find.TickManager?.gameStartAbsTick ?? 0;
        slower = Find.TickManager?.slower ?? new TimeSlower();

        // Use the world's constant rand seed as our initial randState.
        // Only fill the seed part, leave the iterations out.
        randState = (uint)world.ConstantRandSeed;
    }

    public void ExposeData()
    {
        var timer = TickPatch.Timer;
        Scribe_Values.Look(ref timer, "timer");
        TickPatch.SetTimer(timer);

        Scribe_Values.Look(ref timeSpeedInt, "timeSpeed");
        Scribe_Custom.LookULong(ref randState, "randState", 2);

        // See AsyncTimeComp.ExposeData: viewers must survive a reload
        var playerCount = CurrentPlayerCount;
        Scribe_Values.Look(ref playerCount, "playerCount");
        CurrentPlayerCount = playerCount;

        // The tick manager's speed, for the record. Before 2026-10-21 it was saved under "timeSpeed" as well, and a
        // load read the first of that name, the world's; loads keep giving the tick manager the world's speed (every
        // game the same), old saves and new
        TimeSpeed timeSpeed = Find.TickManager.CurTimeSpeed;
        Scribe_Values.Look(ref timeSpeed, "tickManagerTimeSpeed", forceSave: true);
        if (Scribe.mode == LoadSaveMode.LoadingVars)
            Find.TickManager.CurTimeSpeed = timeSpeedInt;

        if (Scribe.mode == LoadSaveMode.LoadingVars)
            Multiplayer.game.worldComp = new MultiplayerWorldComp(world);

        Multiplayer.game.worldComp.ExposeData();

        // Saved since 2026-10-21; before, the world's clock was rebuilt from the tick manager's, which held a map's
        // tick count whenever a map context hadn't put it back (the world clock went back at join points). A save
        // from before keeps the date its tick manager saved, leaked or not: the world's own count was never saved
        // anywhere, and every game loads the same value, so the games stay in step
        Scribe_Values.Look(ref worldTicks, "worldTicks", -1, forceSave: true);
        Scribe_Values.Look(ref gameStartAbsTick, "worldGameStartAbsTick", -1, forceSave: true);

        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            var tm = Find.TickManager;
            if (worldTicks < 0) worldTicks = tm.TicksGame;
            if (gameStartAbsTick < 0) gameStartAbsTick = tm.gameStartAbsTick;
            // The tick manager loaded just before (Game.ExposeSmallComponents); its slower is new
            slower = tm.slower;
            tm.ticksGameInt = worldTicks;
            tm.gameStartAbsTick = gameStartAbsTick;
        }
    }

    public void Tick()
    {
        tickingWorld = true;
        PreContext();
        Desyncs.RandAttribution.Begin(global::Multiplayer.Common.Desyncs.RandAttrScope.World, Multiplayer.game.sync.WorldEntryIndex,
            Desyncs.RandAttrPhases.WorldTick, attrLastEnd, attrLastEndKnown);

        try
        {
            Find.TickManager.DoSingleTick();
            worldTicks++;
            Desyncs.RandAttribution.Phase(Desyncs.RandAttrPhases.WorldSessions);
            Multiplayer.WorldComp.TickWorldSessions();

            Desyncs.RandAttribution.Phase(Desyncs.RandAttrPhases.WorldUpdate);
            if (ModsConfig.BiotechActive)
            {
                // Vanilla puts those into a separate try/catch blocks
                try
                {
                    CompDissolutionEffect_Goodwill.WorldUpdate();
                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }
                try
                {
                    CompDissolutionEffect_Pollution.WorldUpdate();
                }
                catch (Exception e)
                {
                    Log.Error(e.ToString());
                }
            }
        }
        finally
        {
            if (Desyncs.RandAttribution.active)
            {
                attrLastEnd = Desyncs.RandAttribution.End();
                attrLastEndKnown = true;
            }
            PostContext();
            tickingWorld = false;

            Multiplayer.game.sync.TryAddWorldRandomState(randState);
        }
    }

    private static int clockMismatches;

    /// <summary>
    /// Puts the world's clock in the tick manager, logging what was there instead (rate limited). Between contexts
    /// the tick manager should hold the world's clock, but a map context that didn't restore it (one opened inside
    /// itself, 2026-10-21) left a map's, and a save stores the tick manager's.
    /// </summary>
    public void ApplyWorldClock(string where, bool speed = true, bool warn = true)
    {
        var tm = Find.TickManager;
        if (warn && (tm.ticksGameInt != worldTicks || tm.gameStartAbsTick != gameStartAbsTick || tm.slower != slower))
        {
            if (clockMismatches++ % 100 == 0)
            {
                // Name the map whose clock it is: that map's context is the one that didn't put the clock back
                var owner = Find.Maps.FirstOrDefault(m => m.AsyncTime() is { } t && t.mapTicks == tm.ticksGameInt && t.slower == tm.slower) ??
                            Find.Maps.FirstOrDefault(m => m.AsyncTime()?.mapTicks == tm.ticksGameInt);
                MpLog.Warn($"The tick manager held ticks {tm.ticksGameInt}, game start {tm.gameStartAbsTick}" +
                           $"{(tm.slower != slower ? ", another time slower" : "")} at {where}; the world's are {worldTicks}, {gameStartAbsTick}. " +
                           $"{(owner != null ? $"Those are map {owner.uniqueID}'s ticks" : "No map has those ticks")}; set back to the world's " +
                           $"({clockMismatches} times so far)");
            }
        }

        tm.ticksGameInt = worldTicks;
        tm.gameStartAbsTick = gameStartAbsTick;
        tm.slower = slower;
        if (speed) tm.CurTimeSpeed = DesiredTimeSpeed;
    }

    /// <summary>
    /// Runs a save with the world's clock in the tick manager, whose section a load turns into the world's clock,
    /// and puts back what was there. Saves run in long events outside every context (the join point, autosaves).
    /// </summary>
    public static T WithWorldClock<T>(Func<T> save)
    {
        var comp = Multiplayer.game?.asyncWorldTimeComp;
        if (comp == null || Find.TickManager == null) return save();

        var prev = TimeSnapshot.Current();
        // Not the speed: a load gives the tick manager the world's own timeSpeed
        // A save made inside a map's context (in a tick or a command) finds that map's clock there rightly
        comp.ApplyWorldClock("a save", speed: false, warn: !Multiplayer.Ticking && !Multiplayer.ExecutingCmds);
        try
        {
            return save();
        }
        finally
        {
            prev.Set();
        }
    }

    public void PreContext()
    {
        ApplyWorldClock("the world's context");
        Rand.PushState();
        Rand.StateCompressed = randState;

        if (Multiplayer.GameComp.multifaction)
        {
            FactionExtensions.PushFaction(null, Multiplayer.WorldComp.spectatorFaction, force: true);
            foreach (var map in Find.Maps)
                map.MpComp().SetFaction(Multiplayer.WorldComp.spectatorFaction);
        }
    }

    public void PostContext()
    {
        if (Multiplayer.GameComp.multifaction)
        {
            var f = FactionExtensions.PopFaction();
            foreach (var map in Find.Maps)
                map.MpComp().SetFaction(f);
        }

        randState = Rand.StateCompressed;
        Rand.PopState();
    }

    public void ExecuteCmd(ScheduledCommand cmd)
    {
        CommandType cmdType = cmd.type;
        LoggingByteReader data = new LoggingByteReader(cmd.data);
        data.Log.Node($"{cmdType} Global");

        TickPatch.currentExecutingCmdIssuedBySelf = cmd.IsIssuedBySelf() && !TickPatch.Simulating;
        TickPatch.currentExecutingCmdType = cmdType;

        PreContext();
        FactionExtensions.PushFaction(null, cmd.GetFaction());
        Desyncs.RandAttribution.Begin(global::Multiplayer.Common.Desyncs.RandAttrScope.Commands, Multiplayer.game.sync.CommandEntryIndex,
            Desyncs.RandAttrPhases.Command(cmdType), attrLastEnd, attrLastEndKnown);

        bool prevDevMode = Prefs.data.devMode;
        var prevGodMode = DebugSettings.godMode;
        Multiplayer.GameComp.playerData.GetValueOrDefault(cmd.playerId)?.SetContext();

        var randCalls1 = DeferredStackTracing.randCalls;

        try
        {
            if (cmdType == CommandType.Sync)
            {
                var handler = SyncUtil.HandleCmd(data);
                data.Log.current.text = handler.ToString();
            }

            if (cmdType == CommandType.DebugTools)
            {
                DebugSync.HandleCmd(data);
            }

            if (cmdType == CommandType.GlobalTimeSpeed)
            {
                HandleTimeSpeed(cmd, data);
            }

            if (cmdType == CommandType.TimeSpeedVote)
            {
                HandleTimeVote(cmd, data);
            }

            if (cmdType == CommandType.PauseAll)
            {
                SetTimeEverywhere(TimeSpeed.Paused);
            }

            if (cmdType == CommandType.CreateJoinPoint)
            {
                if (Multiplayer.session?.ConnectedToStandaloneServer == true && !TickPatch.currentExecutingCmdIssuedBySelf)
                    return;

                // Every game gets the command with the same tick: it names the join point for the referee
                Desyncs.JoinFingerprint.joinPointTick = cmd.ticks;
                var reload = data.Left > 0 ? (JoinPointReload)data.ReadByte() : JoinPointReload.Everyone;
                if (reload == JoinPointReload.HostOnly)
                    LongEventHandler.QueueLongEvent(CreateHostOnlyJoinPoint, "MpCreatingJoinPoint", false, null);
                else
                    LongEventHandler.QueueLongEvent(CreateJoinPointAndSendIfHost, "MpCreatingJoinPoint", false, null);
            }

            if (cmdType == CommandType.InitPlayerData)
            {
                var playerId = data.ReadInt32();
                var canUseDevMode = data.ReadBool();
                Multiplayer.GameComp.playerData[playerId] = new PlayerData { canUseDevMode = canUseDevMode };
            }

            if (cmdType == CommandType.SetPlayerFaction)
            {
                var username = data.ReadString();
                var factionId = data.ReadInt32();
                Multiplayer.GameComp.playerFactions[username] = factionId;
            }

            if (cmdType == CommandType.PlayerCount)
            {
                int previousMapId = data.ReadInt32();
                int newMapId = data.ReadInt32();
                int mapCount = Find.Maps.Count;

                var prev = -1;
                if (previousMapId >= 0)
                    prev = Find.Maps.FirstOrDefault(x => x.uniqueID == previousMapId)?.AsyncTime()?.DecreasePlayerCount() ?? -1;
                else if (previousMapId == VTRSync.WorldMapId)
                    prev = Multiplayer.AsyncWorldTime.CurrentPlayerCount -= 1;

                var curr = -1;
                if (newMapId >= 0)
                    curr = Find.Maps.FirstOrDefault(x => x.uniqueID == newMapId)?.AsyncTime()?.IncreasePlayerCount() ?? -1;
                else if (newMapId == VTRSync.WorldMapId)
                    curr = Multiplayer.AsyncWorldTime.CurrentPlayerCount += 1;

                MpLog.Debug($"[{worldTicks}|{Multiplayer.session.remoteTickUntil}] Player count change: previousMapId={previousMapId} ({prev}), newMapId={newMapId} ({curr}), mapCount={mapCount}");
            }
        }
        catch (Exception e)
        {
            Log.Error($"World cmd exception ({cmdType}): {e}");
        }
        finally
        {
            DebugSettings.godMode = prevGodMode;
            Prefs.data.devMode = prevDevMode;

            MpLog.Debug($"rand calls {DeferredStackTracing.randCalls - randCalls1}");
            MpLog.Debug("rand state " + Rand.StateCompressed);

            if (Desyncs.RandAttribution.active)
            {
                attrLastEnd = Desyncs.RandAttribution.End();
                attrLastEndKnown = true;
            }
            FactionExtensions.PopFaction();
            PostContext();
            TickPatch.currentExecutingCmdIssuedBySelf = false;
            TickPatch.currentExecutingCmdType = null;

            Multiplayer.game.sync.TryAddCommandRandomState(randState);

            if (cmdType != CommandType.GlobalTimeSpeed)
                Multiplayer.ReaderLog.AddCurrentNode(data);
        }
    }

    private static void CreateJoinPointAndSendIfHost()
    {
        // Every game freezes for the whole join point; on 2026-10-23 a two-colony test map took 8-14 s each time with
        // nothing in the log saying where it went. The steps are timed and logged in one line at the end.
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Self-check: the same lanes before saving and after reloading, or some state isn't saved
        var (lanesBefore, leavesBefore) = TickPatch.Simulating || Multiplayer.IsReplay
            ? (null, null)
            : Desyncs.JoinFingerprint.BeforeJoinPoint();
        long beforeMs = watch.ElapsedMilliseconds;

        Multiplayer.session.dataSnapshot = SaveLoad.SaveReloadAndCreateSnapshot(
            Multiplayer.GameComp.multifaction,
            ReloadOptimizationMode.ForJoinPointSnapshot
        );
        long reloadMs = watch.ElapsedMilliseconds - beforeMs;

        if (!TickPatch.Simulating && !Multiplayer.IsReplay)
            Desyncs.JoinFingerprint.AfterJoinPoint(lanesBefore, leavesBefore);
        long afterMs = watch.ElapsedMilliseconds - beforeMs - reloadMs;

        if (!TickPatch.Simulating && !Multiplayer.IsReplay)
        {
            if (Multiplayer.session?.ConnectedToStandaloneServer == true)
            {
                // Standalone: every client uploads world data + individual snapshots
                SaveLoad.SendGameData(Multiplayer.session.dataSnapshot, true);
                SaveLoad.SendStandaloneMapSnapshots(Multiplayer.session.dataSnapshot);
                SaveLoad.SendStandaloneWorldSnapshot(Multiplayer.session.dataSnapshot);
            }
            else if (Multiplayer.LocalServer != null || Multiplayer.arbiterInstance)
            {
                // Hosted: only host/arbiter uploads world data
                SaveLoad.SendGameData(Multiplayer.session.dataSnapshot, true);
            }
        }

        long total = watch.ElapsedMilliseconds;
        MpLog.Log($"Join point took {total} ms: self-check lanes before {beforeMs}, save {SaveLoad.lastSaveMs}, reload {SaveLoad.lastLoadMs}, " +
                  $"snapshot and section hashes {reloadMs - SaveLoad.lastSaveMs - SaveLoad.lastLoadMs}, self-check after {afterMs}, " +
                  $"send {total - beforeMs - reloadMs - afterMs}");
    }

    /// <summary>
    /// A join point only its joiner loads (<see cref="JoinPointReload.HostOnly"/>): the host saves the snapshot the
    /// joiner gets and uploads it, and every game plays on with nothing reloaded, so no one else freezes. No
    /// self-check or section hashes, which compare games that all saved and reloaded. A game catching up skips it.
    /// </summary>
    private static void CreateHostOnlyJoinPoint()
    {
        if (TickPatch.Simulating || Multiplayer.IsReplay) return;
        if (Multiplayer.LocalServer == null && !Multiplayer.arbiterInstance)
        {
            MpLog.Log($"Join point at timer {TickPatch.Timer}: only the joiner loads, this game plays on");
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        // As SaveLoad.SaveAndReloadCore: in multifaction the save is made as the first player faction, the one loading
        // starts as, and the host's own is put back after
        var multifaction = Multiplayer.GameComp.multifaction;
        var localFaction = Multiplayer.RealPlayerFaction;
        if (multifaction)
            Multiplayer.game.ChangeRealPlayerFaction(Find.FactionManager.AllFactions.First(f => f.IsPlayer), false);

        TempGameData data;
        try
        {
            data = SaveLoad.SaveGameData();
        }
        finally
        {
            if (multifaction)
                Multiplayer.game.ChangeRealPlayerFaction(localFaction, false);
        }

        long saveMs = watch.ElapsedMilliseconds;
        Multiplayer.session.dataSnapshot = SaveLoad.CreateGameDataSnapshot(data, multifaction, hashSections: false);
        long snapshotMs = watch.ElapsedMilliseconds - saveMs;
        SaveLoad.SendGameData(Multiplayer.session.dataSnapshot, true);
        MpLog.Log($"Join point (only the joiner loads) took {watch.ElapsedMilliseconds} ms on the host: save {saveMs}, snapshot {snapshotMs}");
    }

    public void SetTimeEverywhere(TimeSpeed speed)
    {
        foreach (var map in Find.Maps)
            map.AsyncTime().DesiredTimeSpeed = speed;
        DesiredTimeSpeed = speed;
    }

    public static float lastSpeedChange;

    private void HandleTimeSpeed(ScheduledCommand cmd, ByteReader data)
    {
        TimeSpeed speed = (TimeSpeed)data.ReadByte();
        DesiredTimeSpeed = speed;

        if (!Multiplayer.GameComp.asyncTime)
        {
            SetTimeEverywhere(speed);

            if (!cmd.IsIssuedBySelf())
                lastSpeedChange = Time.realtimeSinceStartup;
        }

        MpLog.Debug($"Set world speed {speed} {TickPatch.Timer} {Find.TickManager.TicksGame}");
    }

    private void HandleTimeVote(ScheduledCommand cmd, ByteReader data)
    {
        TimeVote vote = (TimeVote)data.ReadByte();
        int tickableId = data.ReadInt32();

        // Update the vote
        if (vote >= TimeVote.ResetTickable)
            Multiplayer.GameComp.playerData.Do(p => p.Value.SetTimeVote(tickableId, vote));
        else if (Multiplayer.GameComp.playerData.GetValueOrDefault(cmd.playerId) is { } playerData)
            playerData.SetTimeVote(tickableId, vote);

        // Update the time speed
        if (!Multiplayer.GameComp.asyncTime || vote == TimeVote.ResetGlobal)
            SetTimeEverywhere(Multiplayer.GameComp.GetLowestTimeVote(TickableId));
        else if (TickPatch.TickableById(tickableId) is { } tickable)
            tickable.DesiredTimeSpeed = Multiplayer.GameComp.GetLowestTimeVote(tickableId);
    }

    public void FinalizeInit()
    {
        Multiplayer.game.SetThingMakerSeed((int)(randState >> 32));
        Multiplayer.game.sync.OnLoaded(TickPatch.Timer);
    }
}
