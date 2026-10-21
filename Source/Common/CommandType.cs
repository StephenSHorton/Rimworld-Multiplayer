namespace Multiplayer.Common;

public enum CommandType : byte
{
    // Global scope
    GlobalTimeSpeed,
    TimeSpeedVote,
    PauseAll,
    CreateJoinPoint,
    InitPlayerData,

    // Mixed scope
    Sync,
    DebugTools,

    // Map scope
    MapTimeSpeed,
    Designator,
    PlayerCount,

    // Any scope: one part of a command too big for one packet (see CommandChunks)
    Chunk,

    // Global scope, server only: records which faction a username plays (saved in MultiplayerGameComp.playerFactions)
    SetPlayerFaction,
}
