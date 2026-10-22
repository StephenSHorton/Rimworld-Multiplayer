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
- **Map contexts nest** (`AsyncTimeComp.PreContext`/`PostContext`, `Common/Util/ContextStack`): opening the map context that is already innermost only counts, and nothing switches back until the outermost closes; a different map's context inside switches and restores. With one saved time per map, a quest made in its own map's tick (QuestManager.Add sorts by `TicksSinceAppeared`, which opens the quest's map) left the map's tick count in the world's clock, and the next save turned it into the world's time (2026-10-21: the world clock went back about 1800 ticks at a join point). The quest and incident context patches skip a context that is already current and close in finalizers, and so do the interface's map time switches (`SetMapTime.cs`, `TileTemperatures.cs`); a map context opened inside another logs its stack (rate limited). A map tick or command that finds a context still open logs an error and clears them.
- **The world's clock** (`AsyncWorldTimeComp`: `worldTicks`, `gameStartAbsTick`, `slower`) is what the tick manager holds whenever no map context is open. It is saved in the world comp (`worldTicks`, `worldGameStartAbsTick`), and every save in an MP game (`SaveLoad.SaveGameToDoc`, so join points and autosaves) runs with the world's clock in the tick manager (`WithWorldClock`) and puts back what was there. Converting to single player saves after the MP game is gone, so `ConvertToSp.PrepareLoading` puts the world's clock in the tick manager before stopping multiplayer. The tick manager's speed is saved as `tickManagerTimeSpeed` (it shared the world's `timeSpeed` name before); loads still give the tick manager the world's speed. The world's context puts the whole clock back on entry and logs (rate limited) what it found and which map's ticks those are. A save from before keeps the date its tick manager saved, leaked or not: the world's own count was never saved, and every game loads the same value.
- `SyncCoordinator` builds each game's sync opinion every 30 ticks: the full `Rand` state (seed and call count) after every map tick, world tick and command; the errors logged while simulating (the fault lane); and state checksums (lanes). `OpinionComparer` (Common) is the one comparison. A difference in random state or a HARD lane is a desync; a one-sided error or a SOFT lane is a warning and play continues.
- Every game sends its opinions. The clients compare against the host's (or the arbiter's), and the server's `SyncReferee` compares every player's against it too and sends a `ServerSyncVerdictPacket` to that player and the host, so the host hears about a desync as well.
- Sync handlers are numbered once at startup (`Sync.PostInitHandlers`); registering one later throws.
- `StableDefHash` (an early patch) makes `Def.GetHashCode` the same on every machine; CoreCLR randomizes `string.GetHashCode` per process, and vanilla seeds random numbers from def hashes. A def built in code must call `ResolveDefNameHash` after its `defName` is set; startup logs an error for any two defs of a type that share a hash.
- `DeferredStackTracing` for desync debugging; trace hashes are a desync only on Mono; on CoreCLR a trace mismatch is a SOFT warning. `desyncTraces` is off by default.
- **State lanes** (`SyncLanes`): checksums of each map's terrain, roof, fog, snow, gas, things, plants, pawns, zones, designations and time, and of the world's factions, quests, world objects, world pawns, time and id counters (`ids`), every 60 timer ticks (grid lanes every fifth checkpoint, staggered). Order-independent sums of mixed ids and field bits; never runtime `GetHashCode`. The map and world time lanes are HARD (a difference is a desync); the rest are SOFT. A 1x1 door's rotation is left out (it follows the camera), and so are the hit points of things that have none (`def.useHitPoints` false: 100 live, -1 after a load). The pawns lane also covers what steers a pawn (`SteeringHash`): rotation, stance (type, and for a busy stance its focus target, ticks left and verb), path (moving, next cell, destination, whether it has a path), lord duty and its focus, enemy target and the job's targets A and B; the pawn leaf lists each as its own field (`rot=`, `stance=`, `stanceFocus=`, `next=`, `dest=`, `duty=`, `enemy=`, `tA=`, ...; targets as `Human12` or `(x,z)`, never labels), so a leaf diff names the first steering input that differs. Mods add lanes with `SyncLanes.RegisterMapLane` / `RegisterWorldLane` (Rimkit adds `rimkit.simsettings`). The log prints average and slowest lane times every 200 checkpoints.
- **Entity diffs**: when a lane differs, the server's referee asks both games to list that lane's entities (`SyncLanes.Leaves`) at the same upcoming timer tick and sends both players the field-level diff (`LeafDiff`): "Human538 needs.Beauty: 0.105 (host) vs 0.129 (Blake)". Diffs go to the log, `MpDesyncs/leafdiff-*.txt` and the next desync report. **Steering ledger** (`SteeringLedger`, dev mode or a file `MpSteeringLedger.flag` in the save data folder): every pawn rotation change (`Thing.Rotation` setter) and path request (`Pawn_PathFollower.StartPath`) with the game tick, timer, values and a short stack; when a leaf diff of a map's pawns or things lane arrives, both games write the entries of the pawns it names to `MpDesyncs/steering-<player>-<tick>-<lane>.txt` (newest 30 kept); a game with it off logs that it wrote none, so a one-sided dump is visible. The patches are applied on every game and return at once when it's off, so the Harmony patches row stays the same. For local hunts, a file `MpLaneDump.flag` (containing an interval in timer ticks) in the save data folder makes each game dump its leaves to `MpDesyncs/lanes-<player>-<tick>.txt`.
- **Flight recorder**: the last 120 opinions of a game go in its desync report (`local_flight_recorder.txt`), with `lanes.txt` and `entity_diffs.txt`. The report also takes the newest `<SaveData>/Rimkit_Perf/*.json` capture, if any (`local_rimkit_perf_<name>.json`).
- **Perf timeline** (`Common/PerfTimeline.cs`): the server samples each playing game's frame time (`ServerPlayer.frameTime`, the client's smoothed frame time from `ClientFrameTimePacket`; it only changes while that game ticks) every net tick, and once a second writes a line for a player whose 1 s p50 moved by 20% or more since its last line, and every 30 s one for every player: `HH:MM:SS player p50 p95 serverTimePerTick` (ms). Hosted games write `MpDesyncs/perf-timeline-<yyyyMMdd-HHmmss>.txt` on the host (newest 20 kept), standalone servers `MpDesyncs/` next to the server. No protocol change.
- **Referee reset**: when a player rejoins (`SyncReferee.OnPlayerReset`), the referee forgets that player's pending opinions, fingerprint, hard report, soft repeat counts and leaf request cooldowns, and drops leaf requests still waiting on it, so a difference that comes back after a resync is reported and diffed again at once.
- **Debug freeze** (host setting, dev mode): on the first difference the server holds its timer a little ahead and every game saves its full state at that tick (`MpDesyncs/state-*.xml.gz`). A join point lifts the hold.
- **`Multiplayer.InInterface`** is false while ticking, running a command, reloading and running a long event, and true in the one drawn frame a synchronous long event with the standard window waits before it runs (`NewLongEvent.pendingWithInterface`): the interface is drawn and takes input then, and every interface guard was off in it (2026-10-21: the melee attack gizmo stored a melee verb on one game's drafted pawns in a join point's frame; a click in it would have run unsynced). Mods reading it through the API see the same. `Multiplayer.OutsideSimulation` (not ticking, no command) is the stricter test for keeping interface reads out of simulation state, since long events and loads that run on one game only are outside the simulation too.
- **Interface reads must not fill simulation caches.** The UI runs on each player's own frame schedule, so any cache it fills that the simulation later reads is a desync. Found with the lanes: `Need_Beauty`/`Need_Mood` instant levels, `Room` and `District` open-roof counts (a district counts lazily with a kept enumerator, so interface reads count afresh), cell counts, exposure and stats, situational mood thoughts (`Patches/NeedCaches.cs`, `Patches/RoomCaches.cs`), and a pawn's apparel, drug, food and reading policy, which the getters fill with the default on first read and the save keeps (`Patches/PolicyDefaults.cs`: outside ticking and commands the getter returns the default and leaves the field null), the melee verb `TryGetMeleeVerb` stores (`Determinism.cs`: outside the simulation it returns the first usable verb and stores nothing), and the bossgroup call count `NumTimesCalledBossgroup`, which adds a 0 entry on a read (the debug menu's bossgroup submenu, the call gizmo's warning) that the save keeps and the next call counts from (`Patches/BossgroupCount.cs`: outside the simulation a missing entry reads 0 and nothing is stored). Use the same pattern (compute fresh, restore the cache) for new cases.
- **Motes stay out of the shared thing lists** (`Patches/MoteListing.cs`): which text motes spawn follows each player's camera, and `ListerThings` removes by swap, so a listed mote reordered one game's `AllThings` (Desync-17/19). A mote is listed only under its def and in the GUI overlay group.
- **Life stage on interface reads** (`Patches/LifeStageCache.cs`): a pawn's life stage is computed on first read after a load, and that first computation counts as a change (flag, renames, dynamic comps). In play, an interface read gets the index without caching it; loading still computes it, since the life stage workers skip their effects then.
- **Beard rolls** (`Desyncs/StyleRolls.cs`): each `RandomBeardFor` in a session logs `[beard] roll` with the random state, every candidate's weight with its factors (gender x identity), where the identity factor comes from (kind tags, culture, ideo), the kind's style tags, NoBeard's tags and the ideo's NoBeard frequency and gender, all computed before the roll. The 2026-10-19 join point saves had different beards on the same caravan pawns with equal random states, and on 2026-10-21 NoBeard weighed 1200 on the host and 600 on the joiner for mercenary, town and villager kinds; diff both players' lines for the pawn. The fingerprint's `Style tags` warn rows (every pawn kind's style item tags and every hair, beard and tattoo def's tags, duplicates kept) name a def that differs.
- **Spawning while a map loads** (`Patches/LoadingWipes.cs`, `MapFinalizeLoading.Running`): everything `Map.FinalizeLoading` spawns was together when saved, so `GenSpawn.SpawningWipes` wipes nothing then inside the wipe paths (`NoWipesWhileLoading`, last priority, gated on `InWipe`: `WipeExistingThings`, `WipeAndRefundExistingThings`, `SpawnBuildingAsPossible`; placement queries such as `CanSpawnAt` keep vanilla's answer; vanilla wiped filth under a chunk or furniture that spawned after it at every load, and on 2026-10-22 a Replace Stuff wall frame sharing a cell with MineableSteel destroyed the rock at a reload; either spawn order wipes one of the two), and a pawn respawning after the load keeps its saved rotation (`KeepSavedRotationWhileLoading` skips `Pawn_RotationTracker.Notify_Spawned` when `Pawn.SpawnSetup` got `respawningAfterLoad`, `PawnRespawningAfterLoad`; vanilla's re-face from a half restored job and path made the self-check flag rotation changes; a pawn a mod spawns fresh then is faced as usual). `SpawningWipesBlueprintPatch` (`Factions/Blueprints.cs`) only wipes blueprints in play (on load it wiped every blueprint over natural rock, since buildings spawn after blueprints). Both apply in single player too.
- **Save compression** (`Saving/SaveCompression.cs`): MP writes rocks, some wild plants and rock rubble in binary streams, one per cell and kind, and `Map.ExposeData` leaves out of the things list everything `IsSaveCompressible` is true for. So that is true only for the thing `Save` finds first in its cell (`SaveCompression.IsCompressed`), with vanilla's guards: never in a debug save, for items on a cell that holds several (shelves, Adaptive Storage crates) or for buildings that keep their terrain (2026-10-21: every chunk after the first on a crate's cell was written nowhere, up to 283 at one save). References are checked on every map, like vanilla.
- **Save-only tidies**: a save can run on one game alone, so a tidy that vanilla did by changing live state at save time goes only into the save and the live value is put back. A pawn's active battle (`RecordsTrackerExposePatch`: followed to the end of its absorbed chain, null if the battle log no longer has it or its exit tick has already passed on the pawn's own map or world clock, that is exit tick < clock; "Could not resolve Battle_N" on loads), and a condition causer's list of caused conditions (`CausedConditionsSaveOnlyActive`: leaves out those their map no longer has, and entries for removed maps, so vanilla's save-time removal of those changes only the saved copy).
- **Conditions from another map's causer** (`ConditionFromOtherMapCauserStays`): a causer refreshes a condition on another map in that map's clock, but only when its own map ticks; with that map paused or slower the condition ran out, the causer still listed it, saves held a dangling reference ("GameCondition_SmokeSpewer_N" unresolved) and it was made again. A condition doesn't expire while its causer is spawned on another map, active and in range.
- **Pawn timestamps across clocks** (`TimestampFixer`): a pawn moving between a map and the world gets `canSleepTick`, `canLovinTick`, `ticksWhenAllowedToEscapeAgain` and `lastPrisonBreakTicks` shifted by the clock difference, its own and those of the pawns it holds, directly (it used a debug save of the whole pawn, which warned for dead pawns).
- **UI writes that change nothing**: `Gizmo_SetFuelLevel` writes the target fuel level every draw, and the setter stores it in `configuredTargetFuelLevel` (-1 until set), which the property watch can't see; outside the simulation a write that leaves `TargetFuelLevel` the same stores nothing (`CompRefuelableSkipUnchangedTarget`).
- **Id ledger** (`IdLedger`): every real job and thing id is recorded with a short stack; one taken outside ticking, commands and loading is logged at once, and when the `ids` lane first differs each game writes `MpDesyncs/ids-<player>-<tick>.txt`.
- **Join fingerprint** (`JoinFingerprint`): rows the simulation depends on but no save carries (sync handler table, Harmony patches, market values, the generators' price caches, runtime, language, and mod rows via `JoinFingerprint.Register`). The Harmony patches row leaves out the owners in `JoinFingerprint.DiagnosticHarmonyIds` (Rimkit's `horton.rimkit.core.perf` and `.hitch` profilers, the test bridge's `horton.rimkit.testbridge.inject`) and any method only they patch or that no one patches any more (Harmony keeps listing a method after its last patch is removed), so a client-only profiler armed or disarmed on one game raises no warn row; mods add their diagnostic ids to the set. A differing blocking row refuses the join with the rows named; warn rows (values the host's save can change) are compared after every join point, where the referee reports any difference to both players. The Harmony patches row is the exception: mods patch interface code lazily, so after a join point the referee only logs a warning (`SyncReferee.HarmonyRowWarnings`), naming the methods only one game patches when both values list them (`count methods, hash: m1;m2`); in dev mode `MpDesyncs/harmony-<player>.txt` lists each game's methods. Each game also checks at every join point that saving and reloading changed none of its lanes (plant growth rounding by the save, and cold plants made leafless again at the saved map tick by `Plant.PostMapInit`, are expected). The rows also carry a hash of every section of the game's join point save (minus per-player parts: play time, camera, letters, message start times, the history's letter and message archive; the rest of `game/history` is hashed child by child), so the referee names a section that differs and both games write it to `MpDesyncs/joinsave-<player>-<tick>.xml.gz`: the verdict packet lists every differing section (`ServerSyncVerdictPacket.saveSections`; the message names six), and every dump starts with the world clock, tick manager and map ticks before saving and after reloading, and the `game/tickManager` section. The rows carry the join point command's tick, and the referee holds a player's rows until the host's from the same join point arrive. At the door the save section rows (`Fingerprint.JoinSaveRowPrefix`) are left out of the comparison (`Fingerprint.WithoutJoinSave`): a player joining from the menu has no join point save yet (2026-10-21: about 295 "only host's game has it" lines). Section hashes leave out save noise the simulation never reads (`SaveNoise`: default reading, food, apparel and drug policies one game's interface filled in, inventory stock entries equal to the group's default, never-used verb lists, 1x1 door rotation, non-player factions' mech colors, battle names, 0 entries in the bossgroup call counts (saves from before `BossgroupCount`), the play settings' view toggles, and the contents of any component whose class has an attribute named `ClientOnlyAttribute`); the world's components are hashed one by one. A self-check that finds changes writes every lane's full diff to `MpDesyncs/selfcheck-<player>-<tick>.txt` (for `world / ids` every id counter, for `world / time` the world's and the tick manager's clock, before and after); every non-mote thing destroyed or despawned from `Map.FinalizeLoading` to the end of the first frame after goes in the same file (`JoinPointDrops`).
- **Commands**: a string or command too big for the reader throws when written. A command over 60,000 bytes goes compressed in `CommandType.Chunk` parts (`CommandChunks`) and runs once, when its last part comes up. Sync handler ids come from the sorted handler key, not registration order.
- **Desync recovery** (`DesyncRecovery`): the first desync shows a non-blocking overlay, writes the report and re-syncs by itself; the same within 15 minutes restarts RimWorld in a fresh process that rejoins by itself; a third shows the old blocking window (as does debug freeze). Mods clear caches on `DesyncRecovery.BeforeResync`. The host puts back the speeds the desync pause replaced once the player plays again. A joiner's game loads on the main thread like every join point (`JoinLoadOnMainThread`).
- **Host-led clock** (hosted games; standalone servers keep the old server-paced clock): the host's game sets the pace. It reports its timer after every tick through the same ordered channel as its commands, and that timer is the limit every other game runs to (`MultiplayerServer.gameTimer`); the host runs up to `hostLimit`, `HostLimitLead` ticks ahead. The host's own commands run at its next tick with no round trip (`HostLedClock`), and the server only forwards them; everyone else's are stamped one past the highest limit the host was given (`StampTick`). Command queues are kept in tick order. The comment on `MultiplayerServer.hostTimer` explains why no game can miss a command. A command that still arrives after its tick runs at once with an error in the log rather than blocking the queue. For local tests, `MP_SIM_RELIABLE_DELAY=<ms>` in a client's environment holds its reliable packets (commands) back while unreliable ones (time control) go through, the reordering a real connection can produce and 127.0.0.1 never does.

### Persistent dialogs
- `PersistentDialogs.cs` keeps a `Dialog_NodeTree` opened in a map context (an incident asking the player) in the map's MP comp, synced by option clicks, and saves each option's delegates with the values their closures captured (`FieldSave`). An `IncidentWorker` value is saved as its def's name and loads as `def.Worker` when it is that def's own worker (Hospitality's visitor group dialog captures its worker); any other instance logs a warning when saved (once per worker type). A dialog with a part that can't be loaded again is not built: it logs one line and the map comp drops it after loading, the same on every game (2026-10-21: the visitor dialog loaded half built from a join point, every click threw on both games and the visitors never came).

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
