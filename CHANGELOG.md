# Changelog

## v1.2.0

- The PALETTE's special tiles are now read from the game's live tile table each time the
  panel is built, so a tile another mod adds (e.g. an "ambusher" encounter) gets its own
  button, a readable name, a stable marker colour and a count in the PARTS detail pane, and
  exports as its own tile rather than plain substrate.

## v1.1.0

- Added a second mirror, MIRROR (L-R), independent of the existing MIRROR (180°): a left-right
  reflection across the map's centre column. Turning both on paints up to 4 positions per edit
  (the two mirrors plus their composition, a top-bottom reflection).
- The clipboard can now be rotated (60° per click) or flipped left-right before pasting
  (ROTATE 60°/FLIP, next to COPY/CUT/PASTE), so a saved part isn't stuck in the orientation it
  was copied in.
- Saved parts and your own exported structures can now be renamed in their detail pane
  (RENAME, next to LOAD/DELETE) without deleting and re-saving.
- FILL MODE + SELECT together now flood-selects: a click adds (or, right-click, removes) every
  cell connected to it in the same exact state, instead of a brush-radius select-drag.
- The PARTS tab and the live clipboard (SELECTION & CLIPBOARD panel, SAVE CLIPBOARD AS PART)
  now show a rendered preview of their cells, the same silhouette style the STRUCTURES tab
  already used, instead of name/count text alone.
- Added SHRINK TO RADIUS next to GROW TO RADIUS: trims the map down to the radius field,
  deleting any painted cells outside the new boundary. Asks first when that would delete
  anything, and the whole change (including the radius itself) is a single undo step.
- The STRUCTURES and PARTS tabs' detail pane is now laid out like the game's own Contracts
  screen: a centred hero (preview/title/description/actions) beside a fixed-width stats
  column holding bordered STRUCTURE DATA and COMPOSITION NOTES cards (tile count, either the
  game's own band/hostile/rich-deposit totals or your export/part's own per-layer and
  per-special-tile breakdown), instead of one long left-aligned stack.
- The map builder's shortcuts (Ctrl+F7 to open/close, Ctrl+Z/Y/C/X/V) can now be rebound in
  Settings > Controls, in a new MAP BUILDER SHORTCUTS section. Saved to `shortcuts.json`.
- The TOOLS panel's Undo/Redo/Copy/Cut/Paste tooltips now show the live bound key instead of a
  fixed "Ctrl+Z" etc., so they stay correct after a rebind.

- Replaced the Simple/Advanced mode toggle with a single unified map/palette/tools view.
- Reorganized the library panel into tabs (STRUCTURES/PARTS/IMAGE/EXPORT), each with its own
  detail or preview pane, replacing the old combined "Structures & Parts" view.
- Fixed the library's top title pill: it now shows the active tab's name (was always stuck on
  the old combined label) and sits correctly centred in its box.
- Brush size now reads 1-11 instead of 0-10, since "brush size 1" better reads as one cell than
  "brush size 0" did.

## v1.0.0

Initial release.

- In-game map builder UI (`Ctrl+F7` or the title-screen "MAP BUILDER" button): paint/erase/fill/
  line/circle tools, mirror painting, undo/redo, clipboard, and a library view.
- Instant playtest via Save Override (`sandbox_map.json`), no restart needed.
- Publish a painted map as a real contract via Export Structure, grouped into its own
  **CUSTOM MAPS** section in the Contracts list.
- Parts library for saving/loading reusable map fragments.
- Load Image: turn a PNG/JPEG into a map matched to the five ore colours or by brightness.
- Requires [GraveFramework](https://github.com/Luminaex/GraveFramework).
