![banner](https://user-images.githubusercontent.com/49448379/134965756-2a30ffd9-2f6c-43d6-a2a4-584252fc2e4b.png)



The RimWorld Multiplayer mod allows users to play full games of Rimworld cooperatively.

## RimWorld 1.7 (this fork's `rw-1.7` branch)

The `rw-1.7` branch is a port to RimWorld 1.7, which runs on .NET 10 instead of Mono. It does not need Prepatcher. The built DLLs are committed, so the repository itself is the mod:

1. Unsubscribe the Workshop Multiplayer mod. This build uses the same package id.
2. `git clone --recurse-submodules -b rw-1.7 https://github.com/StephenSHorton/Rimworld-Multiplayer.git` and junction or copy it into RimWorld's `Mods` folder.
3. Every player needs RimWorld 1.7 and the same commit of this branch.

Build notes are in `CLAUDE.md`.

## Links
[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=2606448745) |
[Website](https://rimworldmultiplayer.com) |
[Discord](https://discord.gg/S4bxXpv) |
[Documentation](https://hackmd.io/@rimworldmultiplayer/docs/)

## Development
[Git Releases](https://github.com/rwmt/Multiplayer/releases) |
[Installation](https://hackmd.io/@rimworldmultiplayer/docs/https%3A%2F%2Fhackmd.io%2F%40rimworldmultiplayer%2Fplay%23Installation) |
[Hosting](https://hackmd.io/@rimworldmultiplayer/docs/https%3A%2F%2Fhackmd.io%2F%40rimworldmultiplayer%2Fplay%23Installation#Hosting-and-Joining) |
[FAQ](https://hackmd.io/@rimworldmultiplayer/docs/https%3A%2F%2Fhackmd.io%2F%40rimworldmultiplayer%2Ffaq%23Common-Questions#FAQ) |
[Contributing](https://github.com/rwmt/Multiplayer/blob/master/CONTRIBUTORS.md) |
[DEV Wiki](https://hackmd.io/@rimworldmultiplayer/docs/https%3A%2F%2Fhackmd.io%2F%40rimworldmultiplayer%2Fdev-home)

Please do all pull requests to the [dev](https://github.com/rwmt/Multiplayer/tree/dev) branch.

## Donations

If you’re feeling generous these are people who have contributed greatly to the mod’s development and upkeep.

**[Zetrith](https://patreon.com/zetrith)** - Creator, Core, Support\
**[NotFood](https://ko-fi.com/notfood)** - Core, Mod Compatiblity, Compatibility Commissions\
**[Sokyran](https://ko-fi.com/sokyran)** - Core, Mod Compatiblity, Compatibility Commissions\
[Nebual](https://ko-fi.com/Nebual) - Core\
[Thomas107500](https://ko-fi.com/thomas107500) - Mod Compatiblity\
[Luz](https://ko-fi.com/llavorre) - Support, Admin\
[Mistress Mia](https://ko-fi.com/miaamakiir) - Support, Admin\
[Swept](https://ko-fi.com/swept) - Support, Admin, Website

## Notes
Thanks to Pardeike for making [Harmony](https://github.com/pardeike/Harmony) and RevenantX for creating [LiteNetLib](https://github.com/RevenantX/LiteNetLib)

