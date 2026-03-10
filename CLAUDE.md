# Rimworld Multiplayer Mod

A RimWorld mod enabling cooperative multiplayer gameplay.

## Project Structure

```
Source/
├── Multiplayer.sln          # Visual Studio solution
├── Client/                  # Main client mod (net48) → Multiplayer.dll
├── Common/                  # Shared library (net48) → MultiplayerCommon.dll
├── Server/                  # Standalone dedicated server (net8.0)
├── MultiplayerLoader/       # Mod entry point/loader (net48)
├── Tests/                   # NUnit tests (net8.0)
└── TestsOnMono/             # Mono compatibility tests (net48)
```

**How the projects relate:**
- **MultiplayerLoader** is the RimWorld mod entry point. It loads Client and Common DLLs at runtime via reflection.
- **Client** contains all gameplay patches, UI, networking, and sync logic. Depends on Common and MultiplayerLoader.
- **Common** is shared between Client and Server: networking protocol (LiteNetLib), serialization, packets.
- **Server** is a standalone .NET 8 console app for dedicated hosting. Depends on Common.

## Build

```bash
cd Source
dotnet build Multiplayer.sln
```

**Requirements:** .NET 9 SDK, .NET Framework 4.8 Developer Pack

Post-build, Client automatically copies output DLLs to `Assemblies/` and `AssembliesCustom/` at the repo root (the mod folder structure RimWorld expects).

## Key Architecture

### Harmony Patching
Extensive use of Harmony to patch RimWorld. Patches live in `Client/Patches/` organized by concern (Determinism, Alerts, Letters, etc.). Convention:
```csharp
[HarmonyPatch(typeof(ClassName), nameof(ClassName.MethodName))]
static class ClassName_MethodName_Patch
{
    static void Prefix() { }
    static void Postfix() { }
}
```

### Synchronization System
- `SyncHandler` base class for syncing fields, methods, and actions
- `SyncSerialization` / `SyncWorkerDictionary` for serializing game state
- `ReadingSyncWorker` / `WritingSyncWorker` for binary protocol

### Networking (LiteNetLib)
- `ConnectionBase` → `LiteNetConnection` abstraction
- State machine: Disconnected → Connecting → Loading → Playing
- Type-safe `IPacket` implementations in `Common/Networking/`

### Time & Desync
- `AsyncTimeComp` / `AsyncWorldTimeComp` handle time synchronization
- `SyncCoordinator` validates world state checksums across clients
- `DeferredStackTracing` for desync debugging

### Multi-faction
- `FactionContext` tracks current faction context for determinism

## Code Style

- **Indent:** 4 spaces for C#, CRLF line endings
- **Naming:** PascalCase for public members/classes/constants, camelCase for private fields
- **Language:** C# 12 features (pattern matching, using declarations, etc.)
- **Nullable:** Enabled in Common and Server projects

## Testing

```bash
cd Source
dotnet test
```

Uses NUnit 4, FluentAssertions, and Verify (snapshot testing). Tests cover serialization, packets, and server logic.

## Version

Version is defined in `Source/Common/Version.cs` (`SimpleVersion` constant). Also update `About/About.xml` for RimWorld mod metadata.

## Git Workflow

- **Branch target:** PRs go to `dev` branch
- **Branch naming:** `issue-{number}-description` (e.g. `issue-9-feature`)
- **Commit prefix:** `#{issue_number}: description`
- **Merge policy:** Squash merge, 2+ reviewer approval
- **CI:** GitHub Actions runs restore → build (Debug + Release) → test on `ubuntu-latest` with .NET 9
