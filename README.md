# GVK Zebedee

The **Inside Planet Teleporter** for the **GV: Deserts of Kharak (GVK)** server. It rescues grids and players that have fallen through the planet's terrain, and removes fallen floating items to reduce server load. Workshop item: [3430753515](https://steamcommunity.com/sharedfiles/filedetails/?id=3430753515).

## Philosophy
- **A fall through the world should not end a game.** The rescue is automatic, with no admin ticket.
- **Cheap while nothing is wrong.** It scans on a slow interval and ignores tiny debris.

## Requirements
Server-side. Loaded with the rest of the server mods ([Steam collection](https://steamcommunity.com/sharedfiles/filedetails/?id=2650582206)). It stays a standalone mod; it is not part of GVK_Settings.

## What it does
`ModScripts/GVK_PlanetFailsafe.cs` scans the core regions of the planets every 5 seconds. It moves dynamic grids of at least 5 blocks and player characters that have clipped or fallen through voxel terrain back out, gives a rescued grid 3 seconds of collision immunity, and removes fallen floating items.

## Folder map
| Path | What is in it |
| --- | --- |
| `ModScripts/` | The failsafe session component |

## Build
An MDK2 mod project. Ctrl+Shift+B in VS Code builds Release and deploys through MDK2 to your local Mods folder.

## Quick links
- Hub board and issues: [GV-Server-Mods/GVK-Settings](https://github.com/GV-Server-Mods/GVK-Settings)
- Workshop: [3430753515](https://steamcommunity.com/sharedfiles/filedetails/?id=3430753515)
- Steam guide: [players' rules and gameplay](https://steamcommunity.com/sharedfiles/filedetails/?id=2781522559); mod collection: [2650582206](https://steamcommunity.com/sharedfiles/filedetails/?id=2650582206)
- Skills: [se-dev-kharak](https://github.com/GV-Server-Mods/se-dev-kharak)
