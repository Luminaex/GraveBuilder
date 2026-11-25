# Changelog

## v1.1.0

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
