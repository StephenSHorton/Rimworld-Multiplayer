using System;
using System.Net;
using Multiplayer.Common.Networking.Packet;
using Multiplayer.Common.Util;

namespace Multiplayer.Common
{
    public class ServerSettings
    {
        public string gameName = "Multiplayer Server";
        public string lanAddress = "127.0.0.1";

        public string directAddress = $"0.0.0.0:{MultiplayerServer.DefaultPort}";
        public int maxPlayers = 8;
        public float autosaveInterval = 1f;
        public AutosaveUnit autosaveUnit;
        public bool steam;
        public bool direct;
        public bool lan = true;
        public bool arbiter;
        public bool asyncTime;
        public bool multifaction;
        public bool debugMode;
        // Off by default: on 1.7 trace hashes aren't compared (stacks differ between processes), so they only cost
        // bandwidth; the random states, faults and lanes in the opinion do the checking.
        public bool desyncTraces;
        public bool syncConfigs = true;
        // Debug: when two games first differ, hold every game at one tick and save each full game state there
        public bool desyncDebugFreeze;
        // Not on autosave: a join point freezes every game while it saves and reloads (2-3 s on a two-colony test map,
        // 8-14 s on a played colony, 2026-10-23), and at 4x speed the daily autosave made one every couple of minutes.
        // A joiner still gets a fresh one (Join), so it never catches up through more than its own wait.
        public AutoJoinPointFlags autoJoinPoint = DefaultAutoJoinPoint;
        public const AutoJoinPointFlags DefaultAutoJoinPoint = AutoJoinPointFlags.Join | AutoJoinPointFlags.Desync;
        // Experimental: a join point for a joiner (or an autosave) is saved by the host and loaded by the joiner alone;
        // the other games play on instead of saving and reloading. A player whose game desyncs gets join points that
        // reload everyone from then on (WorldData.joinersNeedingReloadAll).
        public bool joinPointReloadsOnlyJoiner;
        public DevModeScope devModeScope;
        public bool hasPassword;
        public string password = "";
        public PauseOnLetter pauseOnLetter = PauseOnLetter.AnyThreat;
        public bool pauseOnJoin = true;
        public bool pauseOnDesync = true;
        public TimeControl timeControl;

        public string? TryParseEndpoints(out IPEndPoint[] endpoints)
        {
            var split = directAddress.Split(MultiplayerServer.EndpointSeparator);
            endpoints = new IPEndPoint[split.Length];
            for (var i = 0; i < split.Length; i++)
            {
                var endpoint = split[i];
                if (Endpoints.TryParse(endpoint, MultiplayerServer.DefaultPort, out var parsed))
                    endpoints[i] = parsed;
                else return endpoint;
            }

            return null;
        }

        public void ExposeData()
        {
            // Remember to mirror the default values

            ScribeLike.Look(ref gameName!, "gameName", "Multiplayer Server");
            ScribeLike.Look(ref lanAddress!, "lanAddress", "127.0.0.1");
            ScribeLike.Look(ref directAddress!, "directAddress", $"0.0.0.0:{MultiplayerServer.DefaultPort}");
            ScribeLike.Look(ref maxPlayers, "maxPlayers", 8);
            ScribeLike.Look(ref autosaveInterval, "autosaveInterval", 1f);
            ScribeLike.Look(ref autosaveUnit, "autosaveUnit");
            ScribeLike.Look(ref steam, "steam");
            ScribeLike.Look(ref direct, "direct");
            ScribeLike.Look(ref lan, "lan", true);
            ScribeLike.Look(ref asyncTime, "asyncTime");
            ScribeLike.Look(ref multifaction, "multifaction");
            ScribeLike.Look(ref debugMode, "debugMode");
            ScribeLike.Look(ref desyncTraces, "desyncTraces");
            ScribeLike.Look(ref syncConfigs, "syncConfigs", true);
            ScribeLike.Look(ref desyncDebugFreeze, "desyncDebugFreeze");
            ScribeLike.Look(ref autoJoinPoint, "autoJoinPoint", DefaultAutoJoinPoint);
            ScribeLike.Look(ref joinPointReloadsOnlyJoiner, "joinPointReloadsOnlyJoiner");
            ScribeLike.Look(ref devModeScope, "devModeScope");
            ScribeLike.Look(ref hasPassword, "hasPassword");
            ScribeLike.Look(ref password!, "password", "");
            ScribeLike.Look(ref pauseOnLetter, "pauseOnLetter", PauseOnLetter.AnyThreat);
            ScribeLike.Look(ref pauseOnJoin, "pauseOnJoin", true);
            ScribeLike.Look(ref pauseOnDesync, "pauseOnDesync", true);
            ScribeLike.Look(ref timeControl, "timeControl");
        }

        public static readonly Binder<ServerSettings> Binder = (PacketBuffer buf, ref ServerSettings settings) =>
        {
            settings ??= new ServerSettings();

            buf.Bind(ref settings.gameName, maxLength: 256);
            buf.Bind(ref settings.directAddress, maxLength: 256);
            buf.Bind(ref settings.maxPlayers);
            buf.Bind(ref settings.autosaveInterval);
            buf.BindEnum(ref settings.autosaveUnit);
            buf.Bind(ref settings.steam);
            buf.Bind(ref settings.direct);
            buf.Bind(ref settings.lan);
            buf.Bind(ref settings.arbiter);
            buf.Bind(ref settings.asyncTime);
            buf.Bind(ref settings.multifaction);
            buf.Bind(ref settings.debugMode);
            buf.Bind(ref settings.desyncTraces);
            buf.Bind(ref settings.syncConfigs);
            buf.Bind(ref settings.desyncDebugFreeze);
            buf.BindEnum(ref settings.autoJoinPoint);
            buf.BindEnum(ref settings.devModeScope);
            buf.Bind(ref settings.hasPassword);
            buf.Bind(ref settings.password, maxLength: 256);
            buf.BindEnum(ref settings.pauseOnLetter);
            buf.Bind(ref settings.pauseOnJoin);
            buf.Bind(ref settings.pauseOnDesync);
            buf.BindEnum(ref settings.timeControl);
            buf.Bind(ref settings.joinPointReloadsOnlyJoiner);
        };
    }

    public enum AutosaveUnit
    {
        Days,
        Minutes
    }

    [Flags]
    public enum AutoJoinPointFlags
    {
        Join = 1,
        Desync = 2,
        Autosave = 4
    }

    public enum DevModeScope
    {
        HostOnly,
        Everyone
    }

    public enum PauseOnLetter
    {
        Never,
        MajorThreat,
        AnyThreat,
        AnyLetter
    }

    public enum TimeControl
    {
        EveryoneControls,
        LowestWins,
        HostOnly
    }
}
