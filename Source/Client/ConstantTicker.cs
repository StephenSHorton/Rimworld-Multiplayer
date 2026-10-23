using System;
using HarmonyLib;
using Multiplayer.Client.Desyncs;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    public static class ConstantTicker
    {
        public static bool ticking;

        public static void Tick()
        {
            ticking = true;

            try
            {
                TickShipCountdown(); // todo control the RNG seed here?
                TickNonSimulation();
            }
            finally
            {
                ticking = false;
            }
        }

        private static void TickNonSimulation()
        {
            DeferredStackTracing.ignoreTraces++;

            try
            {
                TickSyncCoordinator();
                TickAutosave();
            }
            finally
            {
                DeferredStackTracing.ignoreTraces--;
            }
        }

        private const float TicksPerMinute = GenTicks.TicksPerRealSecond * 60;
        private const float TicksPerIngameDay = GenDate.TicksPerDay;

        private static void TickAutosave()
        {
            // When connected to a remote standalone server, the client drives
            // the autosave timer using the interval received at connection time
            // (from the server's TOML settings via ServerProtocolOkPacket).
            if (Multiplayer.session?.ConnectedToStandaloneServer == true)
            {
                var session = Multiplayer.session;
                if (session.autosaveInterval <= 0)
                    return;

                if (session.autosaveUnit == AutosaveUnit.Minutes)
                {
                    session.autosaveCounter++;

                    if (session.autosaveCounter > session.autosaveInterval * TicksPerMinute)
                    {
                        session.autosaveCounter = 0;
                        Autosaving.DoAutosave();
                    }
                }
                else if (session.autosaveUnit == AutosaveUnit.Days)
                {
                    var anyMapCounterUp =
                        Multiplayer.game.mapComps
                        .Any(m => m.autosaveCounter > session.autosaveInterval * TicksPerIngameDay);

                    if (anyMapCounterUp)
                    {
                        Multiplayer.game.mapComps.Do(m => m.autosaveCounter = 0);
                        Autosaving.DoAutosave();
                    }
                }
                return;
            }

            if (Multiplayer.LocalServer is not { } server) return;

            if (server.settings.autosaveUnit == AutosaveUnit.Minutes)
            {
                var session = Multiplayer.session;
                session.autosaveCounter++;

                if (server.settings.autosaveInterval > 0 &&
                    session.autosaveCounter > server.settings.autosaveInterval * TicksPerMinute)
                {
                    session.autosaveCounter = 0;
                    Autosaving.DoAutosave();
                }
            }
            else if (server.settings.autosaveUnit == AutosaveUnit.Days && server.settings.autosaveInterval > 0)
            {
                var anyMapCounterUp =
                    Multiplayer.game.mapComps
                    .Any(m => m.autosaveCounter > server.settings.autosaveInterval * TicksPerIngameDay);

                if (anyMapCounterUp)
                {
                    Multiplayer.game.mapComps.Do(m => m.autosaveCounter = 0);
                    Autosaving.DoAutosave();
                }
            }
        }

        private static void TickSyncCoordinator()
        {
            Desyncs.LeafRequests.Tick(TickPatch.Timer);

            if (TickPatch.Timer % 30 == 0)
            {
                var sync = Multiplayer.game.sync;
                if (TickPatch.Timer % SyncLanes.CheckpointInterval == 0)
                    sync.TryAddLanes(TickPatch.Timer);

                var opinion = sync.FinishLocalOpinion();
                if (opinion == null) return;

                try
                {
                    // Every game sends its opinions: the server compares each with the reference game's and tells
                    // both players when they differ, so the host hears about a desync too. A joiner catching up
                    // sends them as well (without traces): the server keeps more of the host's windows than the
                    // joiner gets, and a desync found while catching up reached nobody but the joiner.
                    var net = opinion.ToNet();
                    // Only the clients compare trace hashes, against the reference game's
                    if (TickPatch.Simulating || Multiplayer.LocalServer == null && !Multiplayer.arbiterInstance)
                        net.traceHashes = [];
                    Multiplayer.Client.SendFragmented(new ClientSyncInfoPacket { SyncOpinion = net }.Serialize());
                }
                catch (Exception e)
                {
                    Log.Error($"Failed to send client sync info packet {e}");
                }

                sync.AddClientOpinionAndCheckDesync(opinion);
            }
        }

        // Moved from RimWorld.ShipCountdown because the original one is called from Update
        private static void TickShipCountdown()
        {
            if (ShipCountdown.timeLeft > 0f)
            {
                ShipCountdown.timeLeft -= 1f / GenTicks.TicksPerRealSecond;

                if (ShipCountdown.timeLeft <= 0f)
                    ShipCountdown.CountdownEnded();
            }
        }
    }

    [HarmonyPatch(typeof(ShipCountdown), nameof(ShipCountdown.CancelCountdown))]
    static class CancelCancelCountdown
    {
        static bool Prefix() => Multiplayer.Client == null || Current.ProgramState != ProgramState.Playing;
    }

    [HarmonyPatch(typeof(ShipCountdown), nameof(ShipCountdown.ShipCountdownUpdate))]
    static class ShipCountdownUpdatePatch
    {
        static bool Prefix() => Multiplayer.Client == null;
    }

}
