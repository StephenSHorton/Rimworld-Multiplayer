# Rimworld Multiplayer Mod

A RimWorld mod enabling cooperative multiplayer gameplay.

## Project Structure

```
Source/
├── Multiplayer.sln          # Visual Studio solution
├── Client/                  # Main client mod (net10.0) → Multiplayer.dll
├── Common/                  # Shared library (net10.0) → MultiplayerCommon.dll
├── Server/                  # Standalone dedicated server (net10.0)
├── MultiplayerLoader/       # Mod entry point/loader (net10.0)
├── Tests/                   # NUnit tests (net10.0)
└── TestsOnMono/             # Mono-only tests; out of the solution on rw-1.7
```

**How the projects relate:**
- **MultiplayerLoader** is the RimWorld mod entry point. It loads Client and Common DLLs at runtime via reflection.
- **Client** contains all gameplay patches, UI, networking, and sync logic. Depends on Common and MultiplayerLoader.
- **Common** is shared between Client and Server: networking protocol (LiteNetLib), serialization, packets.
- **Server** is a standalone .NET console app for dedicated hosting. Depends on Common.

## Build

```bash
# once, from a RimWorld 1.7 install (copies Assembly-CSharp, UnityStubs and Steamworks into gitignored Refs/RimWorld)
RIMWORLD_DIR="/path/to/RimWorld" ./scripts/fetch-rimworld-refs.sh    # or scripts\fetch-rimworld-refs.ps1
cd Source
dotnet build Multiplayer.sln -c Release
```

**Requirements:** .NET 10 SDK. SourceGen needs Roslyn 5.3; if the SDK's compiler is older, add a gitignored `Source/Client/PersonalDefinitions.targets` that references `Microsoft.Net.Compilers.Toolset` 5.3.0.

Post-build, Client automatically copies output DLLs to `Assemblies/` and `AssembliesCustom/` at the repo root (the mod folder structure RimWorld expects). On `rw-1.7` those folders are committed: rebuild and commit them with every source change, because the repo itself is what players install. Never commit anything from `Refs/`; those are the game's files.

## RimWorld 1.7 (`rw-1.7` branch)

RimWorld 1.7 runs on the Waylith engine: CoreCLR (.NET 10), Vulkan, and `UnityStubs.dll` in place of UnityEngine. Mods get Harmony 3. This branch is the 1.7 port; `master` follows rwmt for 1.6.

- **No Prepatcher.** Prepatcher cannot run on CoreCLR. Its one use, syncing ritual role assignment, is now a Harmony prefix in `MultiplayerLoader/SyncRituals.cs`.
- **Shared generic bodies.** CoreCLR compiles one body for every reference-type instantiation of a generic method, so a patch on `Foo<A>.M` also runs for `Foo<B>`, while Harmony's replacement is built for `A`. `SyncRituals` shows the pattern: a prefix that takes the generic arguments as `object`, always returns false, and runs an unpatched `DynamicMethodDefinition` copy for the instance's own instantiation.
- **Strict IL.** Mono tolerated IL that CoreCLR rejects with `InvalidProgramException`: `ret` with an empty stack for a non-void method, and branches out of a `try` block. Emitted and transpiled IL must be valid by the ECMA rules.
- **Strict patch parameters.** Harmony 3 checks `__instance` and argument types. A static target takes no `__instance`.
- **No Mono internals.** `Native` (the Mono embedding API, LMF stack walking, debug options) only runs when `Native.IsMono`. `MpUtil.MarkNoInlining` is a no-op on CoreCLR; Harmony already disables inlining of the methods it patches. `JittedMethods` (Mono profiler) is off.
- **Desync stack traces.** Fast mode reads Mono's LMF, so on 1.7 it records no traces. Slow mode records managed traces (`DeferredStackTracing.ManagedTrace`).
- **Early static constructors.** Don't call `Verse.Widgets` helpers (or anything that touches textures and shaders) from patch setup; it runs the class constructor before the graphics device is ready.

Check a change against the real game DLLs before shipping it. `rimworld-mods/tools/RwCompat` lists members a DLL references that 1.7 no longer has, and `rimworld-mods/tools/RwPatchHarness --patch --mp-init` applies every Harmony patch and runs Multiplayer's sync setup under .NET 10 without starting the game.

### Installing this build

1. Unsubscribe the Workshop Multiplayer mod (2606448745); this build uses the same package id, `rwmt.Multiplayer`.
2. `git clone --recurse-submodules -b rw-1.7 https://github.com/StephenSHorton/Rimworld-Multiplayer.git`, then junction or copy the clone into RimWorld's `Mods` folder.
3. Every player needs RimWorld 1.7 and the same commit of this branch. Multiplayer compares mod files when a player joins.
4. Pull with RimWorld closed.

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
- `SyncCoordinator` builds each game's sync opinion every 30 ticks: the full `Rand` state (seed and call count) after every map tick, world tick and command; the errors logged while simulating (the fault lane); and state checksums (lanes). `OpinionComparer` (Common) is the one comparison. A difference in random state or a HARD lane is a desync; a one-sided error or a SOFT lane is a warning and play continues.
- Every game sends its opinions. The clients compare against the host's (or the arbiter's), and the server's `SyncReferee` compares every player's against it too and sends a `ServerSyncVerdictPacket` to that player and the host, so the host hears about a desync as well.
- Sync handlers are numbered once at startup (`Sync.PostInitHandlers`); registering one later throws.
- `StableDefHash` (an early patch) makes `Def.GetHashCode` the same on every machine; CoreCLR randomizes `string.GetHashCode` per process, and vanilla seeds random numbers from def hashes. A def built in code must call `ResolveDefNameHash` after its `defName` is set; startup logs an error for any two defs of a type that share a hash.
- `DeferredStackTracing` for desync debugging; trace hashes are a desync only on Mono; on CoreCLR a trace mismatch is a SOFT warning. `desyncTraces` is off by default.
- **State lanes** (`SyncLanes`): checksums of each map's terrain, roof, fog, snow, gas, things, plants, pawns, zones, designations and time, and of the world's factions, quests, world objects, world pawns and time, every 60 timer ticks (grid lanes every fifth checkpoint, staggered). Order-independent sums of mixed ids and field bits; never runtime `GetHashCode`. All lanes are SOFT. Mods add lanes with `SyncLanes.RegisterMapLane` / `RegisterWorldLane` (Rimkit adds `rimkit.simsettings`). The log prints average and slowest lane times every 200 checkpoints.
- **Entity diffs**: when a lane differs, the server's referee asks both games to list that lane's entities (`SyncLanes.Leaves`) at the same upcoming timer tick and sends both players the field-level diff (`LeafDiff`): "Human538 needs.Beauty: 0.105 (host) vs 0.129 (Blake)". Diffs go to the log, `MpDesyncs/leafdiff-*.txt` and the next desync report. For local hunts, a file `MpLaneDump.flag` (containing an interval in timer ticks) in the save data folder makes each game dump its leaves to `MpDesyncs/lanes-<player>-<tick>.txt`.
- **Flight recorder**: the last 120 opinions of a game go in its desync report (`local_flight_recorder.txt`), with `lanes.txt` and `entity_diffs.txt`.
- **Debug freeze** (host setting, dev mode): on the first difference the server holds its timer a little ahead and every game saves its full state at that tick (`MpDesyncs/state-*.xml.gz`). A join point lifts the hold.
- **Interface reads must not fill simulation caches.** The UI runs on each player's own frame schedule, so any cache it fills that the simulation later reads is a desync. Found with the lanes: `Need_Beauty`/`Need_Mood` instant levels, `Room` open-roof count, exposure and stats, situational mood thoughts (`Patches/NeedCaches.cs`, `Patches/RoomCaches.cs`). Use the same pattern (compute fresh, restore the cache) for new cases.
- **Join fingerprint** (`JoinFingerprint`): rows the simulation depends on but no save carries (sync handler table, Harmony patches, market values, the generators' price caches, runtime, language, and mod rows via `JoinFingerprint.Register`). A differing blocking row refuses the join with the rows named; warn rows (values the host's save can change) are compared after every join point, where the referee reports any difference to both players. Each game also checks at every join point that saving and reloading changed none of its lanes (plant growth rounding by the save is expected).
- **Commands**: a string or command too big for the reader throws when written. A command over 60,000 bytes goes compressed in `CommandType.Chunk` parts (`CommandChunks`) and runs once, when its last part comes up. Sync handler ids come from the sorted handler key, not registration order.
- **Desync recovery** (`DesyncRecovery`): the first desync shows a non-blocking overlay, writes the report and re-syncs by itself; the same within 15 minutes restarts RimWorld in a fresh process that rejoins by itself; a third shows the old blocking window (as does debug freeze). Mods clear caches on `DesyncRecovery.BeforeResync`. The host puts back the speeds the desync pause replaced once the player plays again. A joiner's game loads on the main thread like every join point (`JoinLoadOnMainThread`).
- **Host-led clock**: the server's timer never runs more than `HostMaxLead` ticks ahead of the host's game (`MultiplayerServer.hostTimer`), the host keeps a 0–3 tick buffer instead of 3–7, and queued work (a host command) wakes the server thread at once.

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
- **CI:** GitHub Actions runs restore → build (Debug + Release) → test on `ubuntu-latest` with .NET 9 (upstream; `rw-1.7` needs the game refs and is not built in CI)
