using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
public static class MapSetup
{
    public static bool SetupNextMapFromTickZero;

    static void Prefix(ref Action<Map> extraInitBeforeContentGen)
    {
        if (Multiplayer.Client == null) return;
        extraInitBeforeContentGen += map => SetupMap(map);
    }

    // owner: the faction that gets the map's current managers (zones, areas, designations, unforbidden things);
    // null means Faction.OfPlayer, as for every map generated in a session
    public static void SetupMap(Map map, bool usingMapTimeFromSingleplayer = false, Faction owner = null)
    {
        owner ??= Faction.OfPlayer;
        Log.Message("MP: Setting up map " + map.uniqueID + (owner != Faction.OfPlayer ? $" for faction {owner.loadID}" : ""));

        // Initialize and store Multiplayer

        var mapComp = new MultiplayerMapComp(map);
        Multiplayer.game.mapComps.Add(mapComp);

        var async = CreateAsyncTimeCompForMap(map, usingMapTimeFromSingleplayer);
        Multiplayer.game.asyncTimeComps.Add(async);

        // Store all current managers for the owner
        InitFactionDataFromMap(map, owner);

        // Add all other player factions to the map
        foreach (var faction in Find.FactionManager.AllFactions.Where(f => f.IsPlayer))
            if (faction != owner)
                InitNewFactionData(map, faction);
    }

    // Who owns a single-player map when a game is hosted with multifaction: the map's settlement faction when that
    // is a player faction other than the spectator (a colony split off at host time), else the host's faction
    public static Faction OwnerAtHostTime(Map map)
    {
        var parentFaction = map.ParentFaction;
        if (parentFaction != null && parentFaction.IsPlayer && parentFaction != Multiplayer.WorldComp.spectatorFaction)
            return parentFaction;
        return Faction.OfPlayer;
    }

    private static AsyncTimeComp CreateAsyncTimeCompForMap(Map map, bool usingMapTimeFromSingleplayer)
    {
        int startingMapTicks;
        int gameStartAbsTick;
        TimeSpeed startingTimeSpeed;

        bool startingMapTimeFromBeginning =
            Multiplayer.GameComp.multifaction &&
            Multiplayer.GameComp.asyncTime &&
            SetupNextMapFromTickZero;

        if (usingMapTimeFromSingleplayer)
        {
            startingMapTicks = Find.TickManager.TicksGame;
            gameStartAbsTick = Find.TickManager.gameStartAbsTick;
            startingTimeSpeed = Find.TickManager.CurTimeSpeed;
        }
        else if (startingMapTimeFromBeginning)
        {
            startingMapTicks = 0;
            gameStartAbsTick = GenTicks.ConfiguredTicksAbsAtGameStart;
            startingTimeSpeed = TimeSpeed.Paused;
        }
        else
        {
            startingMapTicks = Find.Maps.Where(m => m != map).Select(m => m.AsyncTime()?.mapTicks).Max() ?? Find.TickManager.TicksGame;
            gameStartAbsTick = Find.TickManager.gameStartAbsTick;
            startingTimeSpeed = TimeSpeed.Paused;
        }

        if (!Multiplayer.GameComp.asyncTime)
            startingTimeSpeed = Find.TickManager.CurTimeSpeed;

        var asyncTimeCompForMap = new AsyncTimeComp(map, gameStartAbsTick)
        {
            mapTicks = startingMapTicks,
            DesiredTimeSpeed = startingTimeSpeed
        };

        SetupNextMapFromTickZero = false;

        return asyncTimeCompForMap;
    }

    private static void InitFactionDataFromMap(Map map, Faction f)
    {
        var mapComp = map.MpComp();
        mapComp.factionData[f.loadID] = FactionMapData.NewFromMap(map, f.loadID);

        var customData = mapComp.customFactionData[f.loadID] = CustomFactionMapData.New(f.loadID, map);

        foreach (var t in map.listerThings.AllThings)
            if (t is ThingWithComps tc &&
                tc.GetComp<CompForbiddable>() is { forbiddenInt: false })
                customData.unforbidden.Add(t);
    }

    public static void InitNewFactionData(Map map, Faction f)
    {
        var mapComp = map.MpComp();

        mapComp.factionData[f.loadID] = FactionMapData.New(f.loadID, map);
        mapComp.factionData[f.loadID].areaManager.AddStartingAreas();

        mapComp.customFactionData[f.loadID] = CustomFactionMapData.New(f.loadID, map);
    }
}
