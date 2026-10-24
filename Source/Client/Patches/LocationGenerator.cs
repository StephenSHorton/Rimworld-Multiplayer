using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Common.Util;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client.Patches
{
    // Vanilla's WorldComponentTick, with two changes. Each layer's check counted the layer's tiles with a generated
    // location by comparing every tile with every world object, a 450-560 ms hitch every 90,000 ticks on a 100%
    // coverage planet with 1355 world objects (2026-10-23). Only tiles whose id some generated location is on are
    // compared now, with vanilla's own AnyGeneratedWorldLocationAt, so the count is the same (PlanetTile equality
    // needs equal tile ids). And vanilla's interval offset is the layer's GetHashCode, which PlanetLayer doesn't
    // override: an identity hash, different in every process, so each game generated asteroids (Rand calls, ids,
    // world objects) at its own tick. In MP the offset comes from the saved layer id instead. Generation itself is
    // vanilla's GenerateUntilTarget, untouched.
    [HarmonyPatch(typeof(WorldComponent_LocationGenerator), nameof(WorldComponent_LocationGenerator.WorldComponentTick))]
    static class LocationGeneratorTick
    {
        private static readonly HashSet<int> locationTileIds = new();

        static bool Prefix(WorldComponent_LocationGenerator __instance)
        {
            foreach (var (_, layer) in Find.WorldGrid.PlanetLayers)
            {
                if (!GenTicks.IsTickInterval(IntervalOffset(layer), 90000))
                    continue;

                int target = Mathf.RoundToInt(layer.Def.generatedLocationFactor * __instance.worldLocationsTarget);
                if (CountTilesWithGeneratedLocation(__instance.world, layer) < target)
                    __instance.GenerateUntilTarget(layer);
            }

            return false;
        }

        static int IntervalOffset(PlanetLayer layer)
        {
            if (Multiplayer.Client == null)
                return layer.GetHashCode().HashOffset();

            return layer.LayerID.HashOffset();
        }

        static int CountTilesWithGeneratedLocation(World world, PlanetLayer layer)
        {
            var holder = world.worldObjects;
            var objects = holder.worldObjects;

            locationTileIds.Clear();
            for (int i = 0; i < objects.Count; i++)
                if (objects[i].isGeneratedLocation)
                    locationTileIds.Add(objects[i].Tile.tileId);

            int count = PrefilteredCount.Count(
                layer.Tiles,
                tile => tile.tile.tileId,
                locationTileIds,
                tile => holder.AnyGeneratedWorldLocationAt(tile.tile));

            locationTileIds.Clear();
            return count;
        }
    }
}
