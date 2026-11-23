# GraveBuilder

An in-game hex-map editor and sandbox-map unlock mod for
[Graveyard Orbit](https://store.steampowered.com/app/4654120) (dev AnchorheadGames), built on
[BepInEx](https://github.com/BepInEx/BepInEx)/[Harmony](https://github.com/pardeike/Harmony).

Paint a mining-field layout visually, instead of hand-authoring cell JSON, then either
playtest it instantly or publish it as a real contract other players can see in the Contracts
list.

Requires [GraveFramework](https://github.com/Luminaex/GraveFramework) — install that first.

## What it does

- **Map builder UI** — open it with `Ctrl+F7` in-game, or the new "MAP BUILDER" button on the
  title screen. Paint/erase/fill/line/circle tools, mirror painting, undo/redo, a clipboard for
  copy-pasting sections between maps, and a library view for loading/exporting saved work.
- **Instant playtest** — "Save Override" writes the map to this plugin's own
  `sandbox_map.json`; the next mining sortie you start loads it instead of the default
  structure. No restart needed.
- **Publish as a contract** — "Export Structure" writes the map into
  `content/structures/` in the framework's format, so it shows up as a real selectable
  contract. Exported maps are grouped into their own **CUSTOM MAPS** section in the Contracts
  list. Press `Ctrl+F6` afterwards (the framework's hot-reload) to pick it up without
  restarting.
- **Parts library** — save/load a clipboard selection as a reusable named piece
  (`content/parts/<name>.json`) to build up a kit of reusable map fragments.
- **Other unlocks bundled in**: new campaign chapter content, new ship parts/weapon
  mechanics, refinery tier-cap removal, ore recolouring, and mod-supported localisation — see
  the comments at the top of each `*Patch.cs` file in this repo for what each one does.

## Installing (for players)

1. Install [BepInEx 5.4.x (Mono)](https://github.com/BepInEx/BepInEx/releases) into your
   Graveyard Orbit install, if you haven't already — extract its zip into the game's install
   folder and run the game once so it finishes setting itself up.
2. Install [GraveFramework](https://github.com/Luminaex/GraveFramework) first (same steps,
   its own release).
3. Download the latest release zip from this repo's
   [Releases](https://github.com/Luminaex/GraveBuilder/releases) page and extract it into
   `BepInEx/plugins/`, so you end up with `BepInEx/plugins/GraveBuilder/GraveBuilder.dll` plus
   its bundled content folders.

## Building from source

Requires the .NET SDK, a local install of Graveyard Orbit, **and a clone of
[GraveFramework](https://github.com/Luminaex/GraveFramework) as a sibling folder named
`GraveFramework`** (the default name `git clone` gives it) — this project references it via a
relative `ProjectReference`:

```
some-folder/
├── GraveFramework/        (clone of the framework repo)
└── GraveBuilder/          (this repo)
```

Edit the `$(GameDir)` property at the top of `GraveBuilder.csproj` to point at your own
Graveyard Orbit install, then `dotnet build`.

## Known gap

Exported structures write every band field to JSON, but the reader currently only uses
`layer`/`outer_frac`/`hp_sealed` — the rest are reserved for a future release, not a bug.

## Status

Early v1. The sandbox-override unlock, new contracts/mods/boosters/anomalies/quotas, refinery
tier cap, recolouring and localisation have all been verified live in-game. The map builder UI
itself is new in this release and has been reviewed but not yet smoke-tested end-to-end in a
live session — try it and open an issue if something doesn't work. Not affiliated with
AnchorheadGames.
