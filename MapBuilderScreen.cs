using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GraveyardOrbit;
using GraveyardOrbit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace GraveBuilder
{
    // In-game map builder (Ctrl+F7, or MAP BUILDER on the title screen): paint a SandboxMap on
    // a 2D hex grid, then either save it as the sandbox override (the next real mining sortie
    // loads it, see Patches.cs) or export it as a content/structures/ contract (the export
    // runs the framework's reload, RELOAD MODS's path, so it's live at once). Existing structures (our
    // own exports and the game's catalogue) can be loaded back in to edit, and our own
    // exports deleted (with a confirm). Opening and closing run behind the game's hex wipe
    // (WipeSwap); Ctrl+Z/Y/C/X/V work in the painting view (OnShortcutKey).
    // Button explanations are hover tooltips (AttachTooltip), not static paragraphs.
    //
    // Styled with the game's own UI toolkits (TerminalUi.Frame shell, MetaUi buttons, heads,
    // table rows and callouts) so it reads like one of the game's menus; the accent family is
    // MetaUi.Orange, the Contracts list's own (this builds contracts, and its rows copy
    // ModeSelectController.AddRow). It was Teal as a separate identity until the user asked
    // for it to follow the game's theme (2026-11-23). Owns its own UIDocument like
    // BoostersScreen.Create. Destroyed on close and rebuilt on open (MainMenuBoot's
    // convention); the map, the tool, the undo history and the form values are static, so
    // closing and reopening keeps the work.
    //
    // Layout: two views. The painting view: the HexCanvas fills the whole frame body, a row of
    // toggle keys sits over its top edge (PALETTE, TOOLS, MAP on the right), and each key
    // shows/hides its own floating panel dropped down under it; any number can be open at
    // once. These are the "ongoing tool" panels you keep open while painting. The library
    // (STRUCTURES & PARTS in the foot band): a modal overlay (dim backdrop + a second
    // TerminalUi.Frame at Frame()'s own near-fullscreen size) over the still-live painting
    // view, laid out like the game's Contracts screen. A tab row across the top picks one of
    // four pages: STRUCTURES and PARTS are Contracts-style splits (slab rows on the left,
    // click to select, double-click to load; a detail pane on the right with the selected
    // entry's description and actions), IMAGE (a PNG/JPEG painted in the substrate colours)
    // and EXPORT (contract details + band editor) are plain scrolling forms. All four pages
    // are built once per open and only shown/hidden, so a form's half-typed values and the
    // lists stay live behind an inactive tab; the current tab is remembered like the floating
    // panels (_libraryTab). Controls are compact (paired fields, chip-sized toggles, short
    // rows) with detail in tooltips. BACK TO MAP just removes the overlay (instant, no
    // rebuild, no wipe); the painting view under it was never torn down.
    //
    // One panel set (PALETTE/TOOLS/MAP) with everything: substrate layers + special tiles,
    // fill/mirror, line/circle drag tools, select (with auto-fill loops) + copy/cut/paste, a
    // brush-footprint preview on the canvas, radius/grow, the live stats readout in the foot
    // band, and the library view. There used to be a Simple/Advanced mode split, stripping
    // the above down to just colours/erase/brush/undo/reset/sandbox-override for a first-run
    // user; removed (2026-11-24) once the UX no longer needed it. Saved parts are the
    // clipboard written to parts/<name>.json next to the DLL.
    internal sealed class MapBuilderScreen : MonoBehaviour, IHexCanvasHost
    {
        // Same stock PanelSettings GameRoot.TryBuildBoostersScreen hands BoostersScreen.Create.
        private const string PanelSettingsPath = "Assets/UI/Settings/CassettePanelSettings.asset";
        // Same font MainMenuBoot.ConfirmWipeProfile uses; a root fallback for anything the
        // toolkit helpers don't style themselves.
        private const string FontPath = "Assets/UI/Fonts/SpaceGrotesk-Bold UITK.asset";
        // Our own copy of the PanelSettings (Announce.Display's pattern) so we draw above the
        // game's screens; below Announce's 1000 so its modal cards still win.
        private const float PanelSortingOrder = 900f;
        private const int DefaultRadius = 24;
        private const int MinRadius = 1;
        private const int MaxRadius = 300;
        private const int MaxBrush = 10;
        // LOAD IMAGE's longest grid side, in cells. Default matches tools/image_to_structure.py.
        // The cap keeps the result inside MaxRadius: a centred W x H offset rectangle reaches
        // about (W + H/2)/2 from its middle, so the worst case (W = H = size) is 0.75 * size.
        private const int DefaultImageSize = 140;
        private const int MaxImageSize = MaxRadius * 4 / 3;
        // Below this alpha (of 255) a pixel is a hole: no cell at all. image_to_structure.py's default.
        private const int DefaultImageAlpha = 16;
        private const int DefaultStartingMoney = 500;
        private const string DefaultId = "builder_map";
        private const string DefaultName = "Builder Map";

        // Undo history bounds: entries, and total recorded cells across both stacks (a New on
        // a radius-300 map records ~270k cells, so the entry cap alone isn't a memory bound).
        private const int MaxHistory = 50;
        private const int MaxHistoryCells = 2000000;

        private const string Substrate = "substrate";
        private const string Erase = "erase";

        // Every special marker tile SandboxMap.AltToTile knows, in its declaration order: the
        // first BehaviorTileCount are "behavior" tiles (paint a hidden enemy encounter) — their
        // labels match gamedata_tiles.json's own "label" field (Title Case, no underscores),
        // the game's real name for them, confirmed 2026-11-22. The rest are "material" tiles —
        // further confirmed 2026-11-22 to be the game's "shard" currency (the handbook's
        // "Shards"/"Mods" entries): ColourTokens.TokenKeyOfTile maps special_1/2/3/meta_1 to
        // tok_gold/pink/blue/purple, and ColourTokens.Family names those Laser/Breacher/Dynamo/
        // Pulse — so "Laser Shard" etc. below is the real player-facing name, not the raw
        // gamedata "Special 1"/"Meta 1" label (which is accurate but meaningless to a player).
        private const int BehaviorTileCount = 4;

        private static readonly string[] SpecialTiles =
        {
            "explosive", "lightning", "void", "swarm", "special_1", "special_2", "special_3", "meta_1"
        };

        // special_1/2/3 and meta_1 are the game's own "shard" currency used to research Mods
        // (see the handbook's "Shards"/"Mods" entries) — ColourTokens.TokenKeyOfTile maps
        // special_1->tok_gold, special_2->tok_pink, special_3->tok_blue, meta_1->tok_purple,
        // and ColourTokens.Family names those Laser/Breacher/Dynamo/Pulse respectively
        // (verified 2026-11-22). "Special 1"/"Meta 1" were the raw gamedata_tiles.json label
        // text, which is accurate but not the name a player would recognise; this is.
        private static readonly Dictionary<string, string> SpecialTileLabel = new Dictionary<string, string>
        {
            ["explosive"] = Loc.T("gravebuilder.tiles.explosive", "Explosive"),
            ["lightning"] = Loc.T("gravebuilder.tiles.lightning", "Lightning"),
            ["void"] = Loc.T("gravebuilder.tiles.void", "Void"),
            ["swarm"] = Loc.T("gravebuilder.tiles.swarm", "Swarm"),
            ["special_1"] = Loc.T("gravebuilder.tiles.laser_shard", "Laser Shard"),
            ["special_2"] = Loc.T("gravebuilder.tiles.breacher_shard", "Breacher Shard"),
            ["special_3"] = Loc.T("gravebuilder.tiles.dynamo_shard", "Dynamo Shard"),
            ["meta_1"] = Loc.T("gravebuilder.tiles.pulse_shard", "Pulse Shard"),
        };

        private static readonly Regex IdPattern = new Regex("^[a-z0-9_]+$");

        // Saved parts (the library view's PARTS tab): one JSON file per part in a parts/ folder next to
        // this plugin's DLL. The file name is the part name squeezed down to IdPattern's
        // character set (runs of anything else become one '_'), capped in length.
        private const string PartsDirName = "parts";
        private const int MaxPartFileNameLength = 48;
        private static readonly Regex PartNameInvalid = new Regex("[^a-z0-9_]+");

        // Panel accent family (see class comment).
        private static readonly Color Accent = MetaUi.Orange;
        private static readonly Color AccentLt = MetaUi.OrangeLt;
        private static readonly Color BodyText = MetaUi.Fade(MetaUi.Ink, 0.82f);
        private static readonly Color DimText = MetaUi.Fade(MetaUi.Ink, 0.6f);
        // Family for "off" toggles and game-catalogue list entries.
        private static readonly Color Neutral = new Color(0.55f, 0.58f, 0.62f);
        private static readonly Color SelectedEdge = Color.white;
        // SegmentSwitch: SettingsModal's own control fill (its Control) and a near-black ink
        // for the word on the filled half (its OnOrange; same accent family now).
        private static readonly Color SwitchControl = new Color32(18, 18, 26, 255);
        private static readonly Color SwitchInk = new Color32(18, 10, 0, 255);

        // ---- band editor field table -----------------------------------------------------
        // One row per BandDef field, in BandDef's order. Key = the struct-def JSON key (the
        // first three are the keys StructuresInjectPatch.BuildFromJson reads; the rest follow
        // the same snake_case convention, see the export comment).
        // Label is plain sentence case (shown upper-cased on the field, used as-is in error
        // messages); Tooltip is the field's hover text (AttachTooltip).
        private sealed class BandField
        {
            public string Key;
            public string Label;
            public string Default;
            public bool IsFloat;
            public float Min;
            public float Max;
            public string Tooltip;
        }

        // What each field means, confirmed 2026-11-22: a band's tiles are every cell painted
        // its colour (StructureDef.BandIndexOfLayer, first matching band wins) and bands
        // unlock in bands[] order; hpSealed is each tile's HP while its band is sealed;
        // special1-3/meta1 and explosive/lightning/voidTiles/swarm are shard-pickup and
        // hidden-enemy counts seeded into the band's tiles; outerFrac and unsealedLook are
        // cosmetic (see their rows). The framework's StructuresInjectPatch.BuildFromJson
        // currently reads only layer, outer_frac and hp_sealed, so the seed counts and the
        // core look are written to the export but not applied in game yet; the BANDS intro
        // says so once rather than on every field.
        private static readonly BandField[] BandFields =
        {
            new BandField { Key = "layer", Label = Loc.T("gravebuilder.bands.layer_label", "Substrate colour"), Default = "1", Min = 1, Max = 5,
                Tooltip = Loc.T("gravebuilder.bands.layer_tooltip", "Which painted colour this stage is made of: every cell painted this colour belongs to this stage. If two stages pick the same colour, the first one gets all of its tiles.") },
            // Cosmetic only (verified 2026-11-22): the only reader of outerFrac anywhere in the
            // game is StructureHexElement, the little hex icon in the Contracts menu — it draws
            // this band's ring at this fraction of that icon's radius. No sortie/mining code
            // reads it; a band's actual tile membership is purely by colour match
            // (StructureDef.BandIndexOfLayer), and unlock order is purely the bands[] array
            // order. Label reflects that it's just the preview icon, not a real boundary.
            new BandField { Key = "outer_frac", Label = Loc.T("gravebuilder.bands.outer_frac_label", "Preview icon ring size"), Default = "1", IsFloat = true, Min = 0, Max = 1,
                Tooltip = Loc.T("gravebuilder.bands.outer_frac_tooltip", "Cosmetic only. How far out this stage's ring is drawn on the little hex icon in the Contracts list, as a fraction of the icon's radius (0 to 1). It doesn't change which tiles are in the stage.") },
            new BandField { Key = "hp_sealed", Label = Loc.T("gravebuilder.bands.hp_sealed_label", "While sealed"), Default = "75", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.hp_sealed_tooltip", "Hit points each of this stage's tiles has while the stage is still sealed (locked). Higher = slower to mine through. Default 75.") },
            new BandField { Key = "special_1", Label = Loc.T("gravebuilder.bands.special_1_label", "Laser shards"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.special_1_tooltip", "How many Laser Shard pickups (the shard currency used to research Mods) are seeded into this stage's tiles.") },
            new BandField { Key = "special_2", Label = Loc.T("gravebuilder.bands.special_2_label", "Breacher shards"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.special_2_tooltip", "How many Breacher Shard pickups (the shard currency used to research Mods) are seeded into this stage's tiles.") },
            new BandField { Key = "special_3", Label = Loc.T("gravebuilder.bands.special_3_label", "Dynamo shards"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.special_3_tooltip", "How many Dynamo Shard pickups (the shard currency used to research Mods) are seeded into this stage's tiles.") },
            new BandField { Key = "meta_1", Label = Loc.T("gravebuilder.bands.meta_1_label", "Pulse shards"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.meta_1_tooltip", "How many Pulse Shard pickups (the shard currency used to research Mods) are seeded into this stage's tiles.") },
            new BandField { Key = "explosive", Label = Loc.T("gravebuilder.bands.explosive_label", "Explosive"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.explosive_tooltip", "How many hidden Explosive enemy encounters are seeded into this stage's tiles. Destroying such a tile (or mining close to it) wakes the enemy.") },
            new BandField { Key = "lightning", Label = Loc.T("gravebuilder.bands.lightning_label", "Lightning"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.lightning_tooltip", "How many hidden Lightning enemy encounters are seeded into this stage's tiles. Destroying such a tile (or mining close to it) wakes the enemy.") },
            new BandField { Key = "void", Label = Loc.T("gravebuilder.bands.void_label", "Void"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.void_tooltip", "How many hidden Void enemy encounters are seeded into this stage's tiles. Destroying such a tile (or mining close to it) wakes the enemy.") },
            new BandField { Key = "swarm", Label = Loc.T("gravebuilder.bands.swarm_label", "Swarm"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.swarm_tooltip", "How many hidden Swarm enemy encounters are seeded into this stage's tiles. Destroying such a tile (or mining close to it) wakes the enemy.") },
            // TileField.LookLayer: once the band is unsealed its tiles are drawn with the look
            // of layer unsealedLook instead of their painted layer (0 or less = painted look).
            new BandField { Key = "unsealed_look", Label = Loc.T("gravebuilder.bands.unsealed_look_label", "Core look after unlocking"), Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = Loc.T("gravebuilder.bands.unsealed_look_tooltip", "Cosmetic only, no gameplay effect. Once this stage unlocks, its tiles are drawn with the look of another colour: 1 Grey, 2 Blue, 3 Pink, 4 Gold, 5 Purple. 0 keeps the colour you painted.") },
        };

        // BandFields indices the UI still shows (BandRow). The rest of BandFields (outer_frac,
        // the shard/enemy counts, unsealed_look) stays in the array — BandsToJson still needs
        // to export them so re-exporting an existing structure round-trips its values unchanged
        // — but has no editor UI any more (removed 2026-11-24: the framework doesn't read any
        // of them, so showing controls for them was pure clutter; see BandRow's comment).
        private const int BandLayer = 0;
        private const int BandHpSealed = 2;

        // ---- floating panels ---------------------------------------------------------------
        // One toggle key + one floating panel each (PALETTE, TOOLS, MAP; the painting view
        // only). Every panel of the current mode is built up front (hidden ones are
        // display:none) so the Refresh* helpers always have their element references,
        // whatever is open.

        private sealed class PanelDef
        {
            public string Id;
            public string Label;
            public float Width;
            // MAP: key pushed to the right end of the bar, panel right-aligned under it.
            public bool AnchorRight;
            public Action<VisualElement> Build;
        }

        private const string PanelPalette = "palette";
        private const string PanelTools = "tools";
        private const string PanelMap = "map";

        // Library view: the form pages' (IMAGE, EXPORT) column width cap, since a form
        // stretched across the near-fullscreen frame reads as a few fields lost in empty
        // space; and the list column's width on the list+detail pages (STRUCTURES, PARTS).
        private const float LibraryMaxWidth = 1200f;
        private const float LibraryListWidth = 640f;
        // Warnings in the export form (a valid value with a consequence worth knowing). Was
        // TerminalUi.Orange, which vanished into the orange accent; the terminal's warm red
        // still stands apart from it and from the pinker MetaUi.Crimson used for errors.
        private static readonly Color WarnText = TerminalUi.Red;

        // Inset of the key bar and panels from the canvas edges, gap between them.
        private const float PanelEdge = 14f;
        private const float PanelGap = 10f;

        // ---- undo history ----------------------------------------------------------------

        private sealed class Edit
        {
            public string Label;
            public List<(Vector2Int pos, SandboxMap.Cell? before, SandboxMap.Cell? after)> Cells;
            public int RadiusBefore;
            public int RadiusAfter;
        }

        // ---- load list -------------------------------------------------------------------

        private sealed class LoadEntry
        {
            public string Label;
            public bool Local;
            public string Id;           // The underlying struct-def/StructureDef id, for disambiguation
            public string DefPath;      // Local: the struct-def JSON
            public StructureDef Def;    // Catalogue: the game's StructureDef
        }

        // ---- library tabs ------------------------------------------------------------------

        private enum LibraryTab
        {
            Structures,
            Parts,
            Image,
            Export
        }

        // ---- saved parts list --------------------------------------------------------------

        private sealed class PartEntry
        {
            public string Name;
            public string Path;
            public int CellCount;
            public int EmptyCount;
        }

        // ---- static state (survives close/reopen) ----------------------------------------

        private static MapBuilderScreen _instance;
        private static SandboxMap _map;
        private static string _tool = Substrate;
        private static int _layer = 1;
        private static int _brush;
        private static bool _fillMode;
        private static bool _mirror;
        // LINE / CIRCLE drag tools (see "line & circle tools" below). Exclusive with each
        // other and with fill mode and SELECT.
        private static bool _lineMode;
        private static bool _circleMode;

        // Selection + clipboard (see "selection & clipboard" below). The selection is a set of
        // axial positions, independent of what's painted there; the clipboard is the last
        // Copy/Cut, as offsets from its anchor.
        private static bool _selectMode;
        private static bool _pasteMode;
        // AUTO-FILL LOOPS: after each SELECT add-drag, cells the selection fully encloses are
        // selected too (FillEnclosedSelection). Only affects SELECT.
        private static bool _autoFillLoops;
        private static readonly HashSet<Vector2Int> _selection = new HashSet<Vector2Int>();
        private static readonly List<ClipCell> _clipboard = new List<ClipCell>();

        // Which floating panels are open; kept across close/reopen. PALETTE starts open so
        // there's something to paint with straight away.
        private static readonly Dictionary<string, bool> _panelOpen = new Dictionary<string, bool> { [PanelPalette] = true };
        // Which library tab is showing; same lifetime as _panelOpen.
        private static LibraryTab _libraryTab = LibraryTab.Structures;

        private static readonly List<Edit> _undo = new List<Edit>();
        private static readonly List<Edit> _redo = new List<Edit>();

        // Form values, kept as the raw text so a half-typed value survives a rebuild.
        private static string _radiusText = DefaultRadius.ToString();
        // Displayed 1-based (see BuildBrushControl's BrushDisplayOffset) even though the
        // underlying _brush is the 0-based radius used everywhere else in this file.
        private static string _brushText = "1";
        private static string _idText = DefaultId;
        private static string _nameText = DefaultName;
        private static string _moneyText = DefaultStartingMoney.ToString();
        private static string _partNameText = "";
        private static string _imagePathText = "";
        private static string _imageSizeText = DefaultImageSize.ToString();
        private static string _imageAlphaText = DefaultImageAlpha.ToString();
        // LOAD IMAGE's options, image_to_structure.py's --color-mode / --invert / --no-dither,
        // exposed so the look can be tuned live. Defaults match the script's.
        private static bool _imageLuminance;
        private static bool _imageInvert;
        private static bool _imageDither = true;
        private static readonly List<string[]> _bands = new List<string[]> { DefaultBand() };

        private static string _lastStatus;
        private static bool _lastStatusError;

        // ---- per-instance UI -------------------------------------------------------------

        private UIDocument _doc;
        private PanelSettings _panelCopy;
        private HexCanvas _canvas;
        // Whether the library overlay (STRUCTURES & PARTS) is up over the painting view. Per
        // instance, so reopening the builder always starts on the canvas.
        private bool _libraryOpen;
        // The library overlay's root (dim backdrop + its frame), a later sibling of the
        // painting frame under root; null while closed.
        private VisualElement _libraryOverlay;
        // Fills the painting frame's body: the canvas plus everything floating over it. Always
        // the painting view's, overlay or not (LayoutPanels positions against it).
        private VisualElement _stage;
        // Where the tooltip box lives and is positioned: _stage normally, the library page's
        // own stage while the overlay is up. The painting view's tooltip and status host are
        // parked in _paint* meanwhile and put back on close.
        private VisualElement _tipStage;
        private VisualElement _paintTooltip;
        private Label _paintTooltipText;
        private VisualElement _paintStatusHost;
        private List<PanelDef> _panelDefs = new List<PanelDef>();
        private readonly Dictionary<string, VisualElement> _panels = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, VisualElement> _panelKeyHosts = new Dictionary<string, VisualElement>();
        private Label _hoverLabel;
        private VisualElement _statusHost;
        private VisualElement _historyRow;
        private VisualElement _toggleRow;
        private VisualElement _selectRow;
        private VisualElement _loopRow;
        private VisualElement _clipRow;
        private VisualElement _bandHost;
        private VisualElement _loadListHost;
        // The detail pane beside the list (Contracts-screen style): preview, name, description
        // and the LOAD SELECTED/DELETE actions for whichever row is selected. Rebuilt wholesale
        // on every selection change rather than patched in place — selection is infrequent and a
        // full rebuild is far simpler than tracking each piece.
        private VisualElement _loadDetailHost;
        // The library's tab row (rebuilt on a switch, like RefreshPanelKey) and each tab's
        // page, shown or hidden by ShowLibraryTab. _libraryCentreTitle is the ring's top-centre
        // label (set once per the comment where it's built) kept in sync with the active tab by
        // ShowLibraryTab, same as the tab row itself.
        private VisualElement _libraryTabRow;
        private Label _libraryCentreTitle;
        private readonly Dictionary<LibraryTab, VisualElement> _libraryTabPages = new Dictionary<LibraryTab, VisualElement>();
        private VisualElement _partSaveHost;
        private VisualElement _partsListHost;
        // The PARTS tab's detail pane, as _loadDetailHost.
        private VisualElement _partDetailHost;
        private TextField _partNameField;
        private TextField _radiusField;
        private TextField _imagePathField;
        private TextField _imageSizeField;
        private TextField _imageAlphaField;
        private VisualElement _imageOptionsHost;
        // The IMAGE tab's preview pane and the texture it's currently showing (RefreshImagePreview).
        private VisualElement _imagePreviewHost;
        private Texture2D _imagePreviewTex;
        // One-line counts over the STRUCTURES / PARTS list columns, kept current by the list
        // rebuilds.
        private Label _loadSummary;
        private Label _partsSummary;
        private SliderInt _radiusSlider;
        private TextField _brushField;
        private SliderInt _brushSlider;
        private TextField _idField;
        private TextField _nameField;
        private TextField _moneyField;
        // Inline feedback lines under the export form's ID / name / money fields.
        private Label _idHint;
        private Label _nameHint;
        private Label _moneyHint;
        private readonly Dictionary<string, VisualElement> _toolHolders = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, Label> _statValues = new Dictionary<string, Label>();
        private bool _statsDirty;
        private bool _statsTickScheduled;
        private bool _shortcutsWired;

        private readonly List<LoadEntry> _loadEntries = new List<LoadEntry>();
        private readonly List<VisualElement> _loadHolders = new List<VisualElement>();
        // Per load-list row (same index as _loadEntries/_loadHolders): its slab, name label
        // and colour family, repainted for hover/selection (PaintLoadRows).
        private readonly List<(SlabFrame frame, Label name, Color family)> _loadRowPaint = new List<(SlabFrame, Label, Color)>();
        private int _loadSelected = -1;
        private int _loadHover = -1;

        private readonly List<PartEntry> _partEntries = new List<PartEntry>();
        private int _partsSkipped;
        // As _loadRowPaint / _loadSelected / _loadHover, for the parts list (PaintPartRows).
        private readonly List<(SlabFrame frame, Label name, Color family)> _partRowPaint = new List<(SlabFrame, Label, Color)>();
        private int _partSelected = -1;
        private int _partHover = -1;

        // Hover tooltip (see "hover tooltips" below): one shared box on the stage, rebuilt
        // with it, and the element whose text it is showing right now.
        private VisualElement _tooltip;
        private Label _tooltipText;
        private VisualElement _tooltipOwner;

        // Catalogue entries for our own exports deleted this session (DeleteLocalStructure):
        // the framework injected them into StructureCatalog at load, and they stay there until
        // the next RELOAD MODS, so without this they'd reappear in the load list as grey game
        // structures the moment their local file is gone. Exact references, so a re-export +
        // RELOAD MODS (a new StructureDef object) shows normally. Static: survives close/reopen.
        private static readonly HashSet<StructureDef> _deletedInjected = new HashSet<StructureDef>();

        // Open action (stroke / fill / new / load / grow): prior state of every touched cell.
        private Dictionary<Vector2Int, SandboxMap.Cell?> _pending;
        private string _pendingLabel;
        private int _pendingRadius;

        public static bool IsOpen => _instance != null;

        // Open/Close/Toggle (the title button, Ctrl+F7, the CLOSE key) run behind the game's
        // own hex wipe, as MainMenuBoot's screen swaps do, instead of popping in and out.
        // FlowManager's overlay canvas sorts at 32000, above our PanelSortingOrder.
        // _swapPending drops repeat requests while a wipe is still covering: WipeSwap only
        // queues one callback, and a second Toggle in that window would act on the state from
        // before the first had happened.
        private static bool _swapPending;
        private static float _swapPendingSince;

        private static void WipeSwap(Action swap)
        {
            // A wipe takes ~1.2 s (two 0.55 s unscaled halves); one that's never called back
            // (FlowManager torn down mid-wipe) mustn't lock the builder out for the session.
            if (_swapPending && Time.realtimeSinceStartup - _swapPendingSince < 3f)
            {
                return;
            }
            _swapPending = true;
            _swapPendingSince = Time.realtimeSinceStartup;
            void Run()
            {
                _swapPending = false;
                swap();
            }
            try
            {
                FlowManager.I.WipeSwap(Run);
            }
            catch (Exception e)
            {
                // No transition is better than no builder.
                Plugin.Log.LogWarning($"MapBuilder: wipe transition unavailable ({e.Message}); swapping instantly.");
                if (_swapPending)
                {
                    Run();
                }
            }
        }

        public static void Toggle()
        {
            WipeSwap(() =>
            {
                if (IsOpen)
                {
                    CloseNow();
                }
                else
                {
                    OpenNow();
                }
            });
        }

        public static void Open()
        {
            if (!IsOpen)
            {
                WipeSwap(OpenNow);
            }
        }

        public static void Close()
        {
            if (IsOpen)
            {
                WipeSwap(CloseNow);
            }
        }

        private static void OpenNow()
        {
            if (IsOpen)
            {
                return;
            }
            PanelSettings stock = AssetCatalog.Load<PanelSettings>(PanelSettingsPath);
            if (stock == null)
            {
                Plugin.Log.LogError($"MapBuilder: PanelSettings '{PanelSettingsPath}' not found, cannot open the map builder.");
                return;
            }
            if (_map == null)
            {
                _map = SandboxMap.NewFilled(DefaultRadius);
            }

            GameObject go = new GameObject("MapBuilderScreen");
            MapBuilderScreen screen = go.AddComponent<MapBuilderScreen>();
            screen._panelCopy = UnityEngine.Object.Instantiate(stock);
            screen._panelCopy.name = "MapBuilderPanel";
            screen._panelCopy.sortingOrder = PanelSortingOrder;
            screen._doc = go.AddComponent<UIDocument>();
            screen._doc.panelSettings = screen._panelCopy;
            _instance = screen;

            if (screen._doc.rootVisualElement == null)
            {
                Plugin.Log.LogError("MapBuilder: UIDocument has no root visual element, closing.");
                CloseNow();
                return;
            }
            screen.Build(screen._doc.rootVisualElement);
            Plugin.Log.LogInfo("MapBuilder: opened.");
        }

        private static void CloseNow()
        {
            if (_instance != null)
            {
                _instance.CommitAction();
                UnityEngine.Object.Destroy(_instance.gameObject);
                Plugin.Log.LogInfo("MapBuilder: closed.");
            }
            _instance = null;
        }

        private void OnDestroy()
        {
            // A stroke still open when the object goes (e.g. scene change) is kept in history.
            CommitAction();
            if (_panelCopy != null)
            {
                UnityEngine.Object.Destroy(_panelCopy);
                _panelCopy = null;
            }
            if (_instance == this)
            {
                _instance = null;
            }
        }

        // ---- UI construction ----------------------------------------------------------

        // Rebuilds the whole screen from the static state: on open. The library is not a view
        // this switches between: it's an overlay added on top of the painting view (ShowLibrary
        // / CloseLibrary) without a rebuild.
        private void Build(VisualElement root)
        {
            root.Clear();
            // Element references from a previous build point at detached elements; drop them so
            // the Refresh* helpers no-op for sections the current view doesn't show.
            _stage = null;
            _tipStage = null;
            _libraryOverlay = null;
            _paintTooltip = null;
            _paintTooltipText = null;
            _paintStatusHost = null;
            _tooltip = null;
            _tooltipText = null;
            _tooltipOwner = null;
            _hoverLabel = null;
            _statusHost = null;
            _panels.Clear();
            _panelKeyHosts.Clear();
            _panelDefs = new List<PanelDef>();
            _toolHolders.Clear();
            _statValues.Clear();
            _idHint = null;
            _nameHint = null;
            _moneyHint = null;
            _loadRowPaint.Clear();
            _loadHover = -1;
            _toggleRow = null;
            _selectRow = null;
            _loopRow = null;
            _clipRow = null;
            _historyRow = null;
            _bandHost = null;
            _loadListHost = null;
            _loadDetailHost = null;
            _loadHolders.Clear();
            _libraryTabRow = null;
            _libraryTabPages.Clear();
            _partRowPaint.Clear();
            _partHover = -1;
            _partDetailHost = null;
            _partSaveHost = null;
            _partsListHost = null;
            _partNameField = null;
            _radiusField = null;
            _imagePathField = null;
            _imageSizeField = null;
            _imageAlphaField = null;
            _imageOptionsHost = null;
            _imagePreviewHost = null;
            _imagePreviewTex = null;
            _loadSummary = null;
            _partsSummary = null;
            _radiusSlider = null;
            _brushField = null;
            _brushSlider = null;
            _idField = null;
            _nameField = null;
            _moneyField = null;
            root.style.position = Position.Absolute;
            root.style.left = 0f;
            root.style.top = 0f;
            root.style.right = 0f;
            root.style.bottom = 0f;
            root.style.backgroundColor = new Color(0f, 0f, 0f, 0.8f);
            TryApplyFont(root);

            // Shell: the game's terminal frame (chamfered glass panel, header band, foot band).
            // Always the painting view; the library is an overlay on top of it (ShowLibrary).
            TerminalUi.Frame(root, Accent, Loc.T("gravebuilder.screen.frame_title", "MAP BUILDER"), out VisualElement body, out VisualElement foot, "map-builder-frame");

            // A hair of inset so the canvas doesn't paint over the frame's own edge stroke.
            body.style.flexDirection = FlexDirection.Column;
            body.style.paddingLeft = 3f;
            body.style.paddingRight = 3f;
            body.style.paddingTop = 0f;
            body.style.paddingBottom = 0f;

            BuildPaintingView(body, foot);
            // A rebuild with the overlay up (none reachable today, since the overlay covers the
            // MODE key, but cheap to honour) puts it back over the new painting view.
            if (_libraryOpen)
            {
                ShowLibrary(root);
            }

            UpdateStatsNow();
            if (_lastStatus != null)
            {
                SetStatus(_lastStatus, _lastStatusError, log: false);
            }
            else
            {
                SetStatus(Loc.T("gravebuilder.status.builder_ready", "Map builder ready."), log: false);
            }

            // Stats are recomputed at most every 100 ms while painting, not per pointer sample.
            // root.Clear() doesn't cancel items scheduled on root itself, so only schedule once
            // per instance (a mode-toggle rebuild reuses the same root).
            if (!_statsTickScheduled)
            {
                _statsTickScheduled = true;
                root.schedule.Execute(() =>
                {
                    if (_statsDirty)
                    {
                        UpdateStatsNow();
                    }
                }).Every(100L);
            }

            // The game's click/hover sounds for every button in this panel (UiSfx.Wire is
            // idempotent per root).
            UiSfx.Wire(root);

            // Editor shortcuts. On the panel's own top element, not root: with nothing focused
            // (the usual case while painting, the canvas doesn't take focus) a key event targets
            // that top element, which root is only a child of, so root would never see it.
            // Focused elements' events bubble up to it too. Registered once per instance: the
            // panel (our own PanelSettings copy) outlives every rebuild.
            if (!_shortcutsWired)
            {
                VisualElement keyHost = root.panel?.visualTree ?? root;
                keyHost.RegisterCallback<KeyDownEvent>(OnShortcutKey);
                _shortcutsWired = true;
            }
        }

        // Ctrl+Z undo; Ctrl+Y or Ctrl+Shift+Z redo; Ctrl+C / Ctrl+X / Ctrl+V copy, cut, paste
        // (the default keys; the player can rebind them, see ShortcutBindings).
        // The painting view only: the library has no selection or clipboard actions. Never
        // while a text field has focus, so Ctrl+V/C/X/Z keep editing the text in Image path,
        // Part name and the rest.
        private void OnShortcutKey(KeyDownEvent e)
        {
            if (!e.ctrlKey || _libraryOpen || _instance != this || _doc == null)
            {
                return;
            }
            if (IsTextInput(e.target as VisualElement) || IsTextInput(_doc.rootVisualElement?.panel?.focusController?.focusedElement as VisualElement))
            {
                return;
            }
            // Keys from ShortcutBindings (rebindable in Settings > Controls); an unbound
            // (KeyCode.None) action never matches. Shift + Undo's key is always Redo too.
            bool handled = true;
            if (IsShortcut(e.keyCode, ShortcutBindings.Undo))
            {
                if (e.shiftKey)
                {
                    Redo();
                }
                else
                {
                    Undo();
                }
            }
            else if (IsShortcut(e.keyCode, ShortcutBindings.Redo))
            {
                Redo();
            }
            else if (IsShortcut(e.keyCode, ShortcutBindings.Copy))
            {
                Copy();
            }
            else if (IsShortcut(e.keyCode, ShortcutBindings.Cut))
            {
                Cut();
            }
            else if (IsShortcut(e.keyCode, ShortcutBindings.Paste))
            {
                // Starts placement, like PASTE; unlike the button it never cancels it, since
                // a second Ctrl+V reads as "paste again", which placement already does.
                if (!PasteModeActive)
                {
                    TogglePaste();
                }
            }
            else
            {
                handled = false;
            }
            if (handled)
            {
                e.StopPropagation();
            }
        }

        private static bool IsShortcut(KeyCode pressed, string actionId)
        {
            KeyCode bound = ShortcutBindings.Get(actionId);
            return bound != KeyCode.None && pressed == bound;
        }

        // The element or any ancestor is a text input (a TextField's inner editor is what
        // actually holds focus).
        private static bool IsTextInput(VisualElement ve)
        {
            for (; ve != null; ve = ve.parent)
            {
                if (ve is TextField)
                {
                    return true;
                }
            }
            return false;
        }

        // The painting view: canvas, key bar, floating panels; foot band with the hovered-cell
        // readout, the stats readout and the screen keys.
        private void BuildPaintingView(VisualElement body, VisualElement foot)
        {
            // Foot: hovered-cell readout on the left; on the right, the stats readout then the
            // keys (library, CLOSE). The right group is its own row so the readout sits beside
            // the keys rather than mid-band; FootKeys is given that row, so the foot's right
            // padding is cleared here as FootKeys would.
            _hoverLabel = MetaUi.Text(Loc.T("gravebuilder.screen.hover_none", "CELL  -"), MetaUi.Face.BodyBold, 18f, DimText, 2f);
            _hoverLabel.style.flexShrink = 1f;
            foot.Add(_hoverLabel);
            foot.style.paddingRight = 0f;
            VisualElement right = new VisualElement { pickingMode = PickingMode.Ignore };
            right.style.flexDirection = FlexDirection.Row;
            right.style.alignSelf = Align.Stretch;
            right.style.alignItems = Align.Center;
            right.style.flexShrink = 0.2f;
            right.style.minWidth = 0f;
            foot.Add(right);
            right.Add(BuildStatsReadout());
            VisualElement keys = TerminalUi.FootKeys(right, 18f);
            keys.style.flexShrink = 0f;
            keys.Add(TerminalUi.FootKey("map-builder-library", Loc.T("gravebuilder.screen.foot_library", "STRUCTURES & PARTS"), Accent, OpenLibrary, primary: false, 440f));
            keys.Add(TerminalUi.FootKey("map-builder-close", Loc.T("gravebuilder.screen.foot_close", "CLOSE"), Accent, Close, primary: true, 280f));

            // Stage: fills the body. Children in paint/pick order (later = on top): canvas,
            // status callout, key bar, panels. No padding, so its local space is the space the
            // absolute overlays are positioned in.
            _stage = new VisualElement { name = "map-builder-stage" };
            _stage.style.flexGrow = 1f;
            _stage.style.flexDirection = FlexDirection.Column;
            _stage.style.overflow = Overflow.Hidden;
            body.Add(_stage);
            _tipStage = _stage;

            // flexGrow 1 (its own constructor) fills the stage; it fits the grid to whatever
            // rect it gets on every draw.
            if (_canvas == null)
            {
                _canvas = new HexCanvas(this);
            }
            _stage.Add(_canvas);
            _canvas.Refresh();

            // Status callout floats at the bottom centre, under the panels (an open panel may
            // cover it). Never pickable (SetStatus re-applies that to each new callout), so
            // painting works straight through it.
            _statusHost = new VisualElement { pickingMode = PickingMode.Ignore };
            _statusHost.style.position = Position.Absolute;
            _statusHost.style.left = Length.Percent(24f);
            _statusHost.style.right = Length.Percent(24f);
            _statusHost.style.bottom = PanelEdge;
            _stage.Add(_statusHost);

            _panelDefs = Panels();

            // Key bar across the top of the canvas. Ignore-picking (as are the key hosts and the
            // spacer) so only the keys themselves take clicks; the gaps between them still
            // reach the canvas.
            VisualElement bar = new VisualElement { name = "map-builder-panel-keys", pickingMode = PickingMode.Ignore };
            bar.style.position = Position.Absolute;
            bar.style.left = PanelEdge;
            bar.style.right = PanelEdge;
            bar.style.top = PanelEdge;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.FlexStart;
            _stage.Add(bar);

            // Before the panels are built: their buttons attach to it as they're made.
            BuildTooltip();
            bool spacerAdded = false;
            foreach (PanelDef def in _panelDefs)
            {
                if (def.AnchorRight && !spacerAdded)
                {
                    VisualElement spacer = new VisualElement { pickingMode = PickingMode.Ignore };
                    spacer.style.flexGrow = 1f;
                    bar.Add(spacer);
                    spacerAdded = true;
                }
                VisualElement host = new VisualElement { pickingMode = PickingMode.Ignore };
                host.style.flexShrink = 0f;
                host.style.marginRight = def.AnchorRight ? 0f : PanelGap;
                host.RegisterCallback<GeometryChangedEvent>(_ => LayoutPanels());
                bar.Add(host);
                _panelKeyHosts[def.Id] = host;
            }

            foreach (PanelDef def in _panelDefs)
            {
                VisualElement panel = BuildPanel(def);
                _stage.Add(panel);
                _panels[def.Id] = panel;
                RefreshPanelKey(def.Id);
            }
            _stage.RegisterCallback<GeometryChangedEvent>(_ => LayoutPanels());

            RefreshToolButtons();
            RefreshToggles();
            RefreshClipboardButtons();
            RefreshHistoryButtons();
        }

        // Live map stats (painting view): a compact two-line block in the foot band
        // beside the keys, in place of the old STATS panel. Line 1: total cells, radius,
        // special tiles; line 2: per-layer counts, each behind a chip of that layer's exact
        // colour (StructureSilhouette.LayerColour, as the swatches/canvas use). The value
        // labels are registered in _statValues under the old STATS ids, so UpdateStatsNow and
        // the 100 ms dirty tick drive it unchanged.
        private VisualElement BuildStatsReadout()
        {
            VisualElement box = new VisualElement { name = "map-builder-stats" };
            box.style.flexDirection = FlexDirection.Column;
            box.style.justifyContent = Justify.Center;
            // The hover readout gives way first if the foot band runs short (both shrink).
            box.style.flexShrink = 0.2f;
            box.style.overflow = Overflow.Hidden;
            box.style.marginRight = 28f;

            VisualElement totals = Row();
            totals.style.alignItems = Align.Center;
            totals.style.justifyContent = Justify.FlexEnd;
            totals.style.marginBottom = 4f;
            box.Add(totals);
            ReadoutItem(totals, "cells", Loc.T("gravebuilder.stats.cells", "CELLS"), null);
            ReadoutItem(totals, "radius", Loc.T("gravebuilder.stats.radius", "RADIUS"), null);
            ReadoutItem(totals, "specials", Loc.T("gravebuilder.stats.special", "SPECIAL"), null);

            VisualElement layers = Row();
            layers.style.alignItems = Align.Center;
            layers.style.justifyContent = Justify.FlexEnd;
            box.Add(layers);
            for (int l = 1; l <= 5; l++)
            {
                ReadoutItem(layers, "L" + l, null, StructureSilhouette.LayerColour(l));
            }

            return AttachTooltip(box, Loc.T("gravebuilder.stats.tooltip", "Live map stats. Top line: painted cells, map radius, and special tiles (enemy markers and shards). Bottom line: painted cells of each substrate colour (Grey, Blue, Pink, Gold, Purple)."));
        }

        // One readout entry: an optional dim caption or a colour chip, then the value label
        // (registered in _statValues[id]).
        private void ReadoutItem(VisualElement host, string id, string caption, Color? chip)
        {
            VisualElement item = Row();
            item.style.alignItems = Align.Center;
            item.style.marginLeft = 18f;
            item.pickingMode = PickingMode.Ignore;
            if (caption != null)
            {
                Label cap = MetaUi.Text(caption, MetaUi.Face.Body, 14f, DimText, 2f);
                cap.style.marginRight = 6f;
                item.Add(cap);
            }
            if (chip.HasValue)
            {
                VisualElement sw = new VisualElement { pickingMode = PickingMode.Ignore };
                sw.style.width = 12f;
                sw.style.height = 12f;
                sw.style.marginRight = 6f;
                sw.style.backgroundColor = chip.Value;
                TerminalUi.Border(sw, 1f, MetaUi.Fade(MetaUi.Ink, 0.35f));
                item.Add(sw);
            }
            Label value = MetaUi.Text("-", MetaUi.Face.BodyBold, 16f, MetaUi.Ink, 1f);
            item.Add(value);
            host.Add(item);
            _statValues[id] = value;
        }

        // ---- library console ring ------------------------------------------------------
        // A from-scratch copy of the game's own CassetteConsoleChrome.ArtFrame (private there,
        // so not callable directly): the same textured picture-frame ring with chamfered
        // corners — four quads cut from the art texture's border band plus four rounded-corner
        // wedges filled with its ring colour — drawn with the game's own real border art
        // (Assets/UI/Textures/UI_BG1.png), not CassetteConsoleChrome.Attach's full screen
        // takeover (tried 2026-11-24: forces its own fixed 2560x1440 canvas and assumes it owns
        // the whole root, which broke badly nested inside this overlay — see the ShowLibrary/
        // BuildLibraryView git history for what that looked like). Proportional to its own
        // contentRect, so it works at whatever size it's given, not just 2560x1440.
        private const float ArtFrameHoleX0 = 0.023828125f;
        private const float ArtFrameHoleY0 = 0.10972222f;
        private const float ArtFrameHoleX1 = 0.9753906f;
        private const float ArtFrameHoleY1 = 0.8715278f;
        private const float ArtFrameCornerR = 1f / 64f;
        private static readonly Color ArtFrameRingColor = new Color(29f / 85f, 0.35686275f, 0.41960785f);

        // The ring art's own centre-title slot (shows "HOME"/"CAMPAIGN"/etc. on the real
        // mode-select screen, in the same CassetteConsoleChrome ring this overlay reuses).
        // Measured directly off the real con-artbox--c element's worldBound at runtime (a
        // temporary Harmony postfix on CassetteConsoleChrome.AttachCore, logged against the
        // 2560x1440 design canvas, then removed) rather than guessed — same fraction-of-own-rect
        // approach as ArtFrameHoleX0 etc. above, confirmed transferable since this overlay and
        // the real chrome's canvas both render at the same 16:9 ratio.
        private const float CentreTitleBoxX0 = 0.43906f;
        private const float CentreTitleBoxX1 = 0.55898f;
        private const float CentreTitleBoxY0 = 0.03403f;
        private const float CentreTitleBoxY1 = 0.06458f;

        private sealed class LibraryArtFrame : VisualElement
        {
            private readonly Texture2D _tex;

            public LibraryArtFrame(Texture2D tex)
            {
                _tex = tex;
                pickingMode = PickingMode.Ignore;
                generateVisualContent += Gen;
            }

            private void Gen(MeshGenerationContext mgc)
            {
                if (_tex == null)
                {
                    return;
                }
                Rect r = contentRect;
                if (r.width < 1f || r.height < 1f)
                {
                    return;
                }
                float x0 = r.width * ArtFrameHoleX0;
                float y0 = r.height * ArtFrameHoleY0;
                float x1 = r.width * ArtFrameHoleX1;
                float y1 = r.height * ArtFrameHoleY1;
                float cr = r.width * ArtFrameCornerR;
                MeshWriteData mwd = mgc.Allocate(16, 24, _tex);
                // Deprecated in favour of automatic UV remapping, but this is a straight copy of
                // the game's own (still-shipping) ArtFrame.Gen, which targets the older API.
#pragma warning disable CS0618
                Rect uvR = mwd.uvRegion;
#pragma warning restore CS0618
                int vi = 0;
                ushort[] idx = new ushort[24];
                int ii = 0;
                Quad(0f, 0f, r.width, y0);
                Quad(0f, y1, r.width, r.height);
                Quad(0f, y0, x0, y1);
                Quad(x1, y0, r.width, y1);
                for (int i = 0; i < idx.Length; i++)
                {
                    mwd.SetNextIndex(idx[i]);
                }
                Painter2D p = mgc.painter2D;
                p.fillColor = ArtFrameRingColor;
                Corner(x0 + cr, y0 + cr, x0, y0, x0 + cr, y0, 270f, 180f);
                Corner(x1 - cr, y0 + cr, x1, y0, x1, y0 + cr, 0f, -90f);
                Corner(x1 - cr, y1 - cr, x1, y1, x1 - cr, y1, 90f, 0f);
                Corner(x0 + cr, y1 - cr, x0, y1, x0, y1 - cr, 180f, 90f);

                void Corner(float cx, float cy, float ax, float ay, float bx, float by, float a0, float a1)
                {
                    p.BeginPath();
                    p.MoveTo(new Vector2(ax, ay));
                    p.LineTo(new Vector2(bx, by));
                    p.Arc(new Vector2(cx, cy), cr, a0, a1, ArcDirection.CounterClockwise);
                    p.ClosePath();
                    p.Fill();
                }
                void Quad(float ax, float ay, float bx, float by)
                {
                    V(ax, ay);
                    V(bx, ay);
                    V(bx, by);
                    V(ax, by);
                    idx[ii++] = (ushort)vi;
                    idx[ii++] = (ushort)(vi + 1);
                    idx[ii++] = (ushort)(vi + 2);
                    idx[ii++] = (ushort)(vi + 2);
                    idx[ii++] = (ushort)(vi + 3);
                    idx[ii++] = (ushort)vi;
                    vi += 4;
                }
                void V(float px, float py)
                {
                    float u = px / r.width;
                    float v = 1f - py / r.height;
                    mwd.SetNextVertex(new Vertex
                    {
                        position = new Vector3(px, py, Vertex.nearZ),
                        tint = Color.white,
                        uv = new Vector2(uvR.xMin + u * uvR.width, uvR.yMin + v * uvR.height)
                    });
                }
            }
        }

        // ---- library overlay ---------------------------------------------------------
        // STRUCTURES & PARTS: a modal over the still-live painting view, the
        // way the game's SETTINGS sits over whatever opened it: a full-screen dim backdrop and
        // a TerminalUi.Frame at its own default size. Laid out like the game's Contracts screen
        // (ModeSelectController.FillOvertimeList / AddRow): a tab per page, eyebrow sub-heads,
        // slab rows that light up on hover and selection beside a detail pane for the selected
        // one. It only reads and writes the load
        // list, the parts list and the export form, never the map/selection/clipboard/undo
        // (except through the LOAD actions).
        //
        // Opening and closing are a local Add / RemoveFromHierarchy on that overlay: no
        // rebuild of the painting view and no FlowManager wipe. A wipe here was tried and is
        // wrong: FlowManager is the game's own screen navigation, which doesn't know about
        // this screen, and routing an in-screen panel change through it sent the player to
        // the main menu on close (seen live, repeatedly).

        private void OpenLibrary()
        {
            if (_libraryOpen || _doc == null || _doc.rootVisualElement == null)
            {
                return;
            }
            CommitAction();
            _libraryOpen = true;
            ShowLibrary(_doc.rootVisualElement);
        }

        // Back to the painting view, which was never torn down: the same panels, zoom, pan
        // and tool are all still there.
        private void CloseLibrary()
        {
            if (!_libraryOpen)
            {
                return;
            }
            _libraryOpen = false;
            HideTooltip(_tooltipOwner);
            _libraryOverlay?.RemoveFromHierarchy();
            _libraryOverlay = null;

            // Hand the tooltip and status line back to the painting view.
            _tipStage = _stage;
            _tooltip = _paintTooltip;
            _tooltipText = _paintTooltipText;
            _tooltipOwner = null;
            _statusHost = _paintStatusHost;
            _paintTooltip = null;
            _paintTooltipText = null;
            _paintStatusHost = null;

            // Library-only references now point at detached elements; drop them so the
            // Refresh* helpers no-op until the overlay is built again.
            _idHint = null;
            _nameHint = null;
            _moneyHint = null;
            _loadRowPaint.Clear();
            _loadHover = -1;
            _bandHost = null;
            _loadListHost = null;
            _loadDetailHost = null;
            _loadHolders.Clear();
            _libraryTabRow = null;
            _libraryTabPages.Clear();
            _partRowPaint.Clear();
            _partHover = -1;
            _partDetailHost = null;
            _partSaveHost = null;
            _partsListHost = null;
            _partNameField = null;
            _imagePathField = null;
            _imageSizeField = null;
            _imageAlphaField = null;
            _imageOptionsHost = null;
            _imagePreviewHost = null;
            if (_imagePreviewTex != null)
            {
                UnityEngine.Object.Destroy(_imagePreviewTex);
                _imagePreviewTex = null;
            }
            _loadSummary = null;
            _partsSummary = null;
            _idField = null;
            _nameField = null;
            _moneyField = null;

            // Whatever the library last reported (a load, an export) shows on the map too.
            if (_lastStatus != null)
            {
                SetStatus(_lastStatus, _lastStatusError, log: false);
            }
            // The panels kept their places, but re-run in case the window changed meanwhile.
            LayoutPanels();
        }

        // Builds the overlay onto root, above the painting frame (a later sibling, so it's
        // drawn and picked first).
        private void ShowLibrary(VisualElement root)
        {
            HideTooltip(_tooltipOwner);
            _paintTooltip = _tooltip;
            _paintTooltipText = _tooltipText;
            _paintStatusHost = _statusHost;

            // Dim backdrop over the whole screen, behind the ring below (a fallback if its
            // texture fails to load). Position picking: it takes every pointer and wheel event
            // outside the card, and the canvas is not its ancestor, so nothing propagates to
            // the painting view underneath (modal, like ModalKit's dim).
            _libraryOverlay = new VisualElement { name = "map-builder-library-overlay", pickingMode = PickingMode.Position };
            _libraryOverlay.style.position = Position.Absolute;
            _libraryOverlay.style.left = 0f;
            _libraryOverlay.style.top = 0f;
            _libraryOverlay.style.right = 0f;
            _libraryOverlay.style.bottom = 0f;
            _libraryOverlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.7f);
            root.Add(_libraryOverlay);

            // The console ring (LibraryArtFrame, above), filling the whole overlay. "UI_BG1.png"
            // is CassetteConsoleChrome's own choice for a plain (not department-centred) title,
            // matching the Contracts page.
            LibraryArtFrame ring = new LibraryArtFrame(AssetCatalog.Load<Texture2D>("Assets/UI/Textures/UI_BG1.png"));
            ring.style.position = Position.Absolute;
            ring.style.left = 0f;
            ring.style.top = 0f;
            ring.style.right = 0f;
            ring.style.bottom = 0f;
            _libraryOverlay.Add(ring);

            // The top-centre title slot the ring's art leaves empty (it shows "OVERTIME"/
            // "CAMPAIGN"/etc. on the real mode-select screen, set by that screen's own code —
            // not baked into the texture, confirmed live 2026-11-24 the slot renders blank
            // without this). Positioned to CentreTitleBoxX0/X1/Y0/Y1 (measured above) and
            // flex-centred inside, the same mechanism CassetteConsoleChrome itself uses for this
            // slot — a box with its content centred by justify/align, not a manually translated
            // label — instead of guessing a top offset against the overlay's raw rect.
            var centreTitleBox = new VisualElement { pickingMode = PickingMode.Ignore };
            centreTitleBox.style.position = Position.Absolute;
            centreTitleBox.style.left = Length.Percent(CentreTitleBoxX0 * 100f);
            centreTitleBox.style.right = Length.Percent((1f - CentreTitleBoxX1) * 100f);
            centreTitleBox.style.top = Length.Percent(CentreTitleBoxY0 * 100f);
            centreTitleBox.style.bottom = Length.Percent((1f - CentreTitleBoxY1) * 100f);
            centreTitleBox.style.flexDirection = FlexDirection.Row;
            centreTitleBox.style.justifyContent = Justify.Center;
            centreTitleBox.style.alignItems = Align.Center;
            _libraryOverlay.Add(centreTitleBox);

            // MetaUi.Text's own tracking (letter-spacing) compensation pads the RIGHT side only
            // (so a tracked label's visual right edge lines up where normal text would end) —
            // correct for left-aligned usage, but it leaves tracked text looking off-centre once
            // the label itself is centred, like this one. MetaUi.Center halves that one-sided
            // padding onto both sides and switches to MiddleCenter alignment; every other
            // centred+tracked label in the game's own UI code goes through this same pairing
            // (ModTreeScreen, SettingsModal, ModalKit, CargoGrid).
            _libraryCentreTitle = MetaUi.Center(MetaUi.Text(LibraryTabTitle(_libraryTab), MetaUi.Face.DisplayBold, 20f, Accent, 2f));
            centreTitleBox.Add(_libraryCentreTitle);

            // The game's terminal frame, inset to land inside the ring's own hole (above, same
            // ArtFrameHoleX0/Y0/X1/Y1 proportions) rather than Frame()'s own near-fullscreen
            // 22-26px inset, so its glass panel doesn't just cover the ring's art. Its own title
            // stays a fixed page name (unlike the ring's centre title) since Frame() bakes the
            // string in once and doesn't hand back a label to update later.
            VisualElement frame = TerminalUi.Frame(_libraryOverlay, Accent, Loc.T("gravebuilder.library.frame_title", "MAP BUILDER · LIBRARY"), out VisualElement body, out VisualElement foot, "map-builder-library-frame");
            frame.style.left = Length.Percent(ArtFrameHoleX0 * 100f);
            frame.style.top = Length.Percent(ArtFrameHoleY0 * 100f);
            frame.style.right = Length.Percent((1f - ArtFrameHoleX1) * 100f);
            frame.style.bottom = Length.Percent((1f - ArtFrameHoleY1) * 100f);
            body.style.flexDirection = FlexDirection.Column;
            body.style.paddingLeft = 3f;
            body.style.paddingRight = 3f;
            body.style.paddingTop = 0f;
            body.style.paddingBottom = 0f;

            // The game's own ore-colour stripe (TerminalUi.SpectrumBar: Gold, Blue, Purple, Pink,
            // green, white) across the top of the frame, as a bit of the console look the
            // Contracts screen's bezel has.
            VisualElement stripe = TerminalUi.SpectrumBar(4f);
            stripe.style.marginBottom = 6f;
            body.Add(stripe);

            BuildLibraryView(body, foot);

            if (_lastStatus != null)
            {
                SetStatus(_lastStatus, _lastStatusError, log: false);
            }
            // No UiSfx.Wire here: Build's UiSfx.Wire(root) listens on root by event delegation,
            // so the overlay's buttons already click and hover; wiring the overlay too would
            // play every sound twice.
        }

        private void BuildLibraryView(VisualElement body, VisualElement foot)
        {
            // Foot: what this page is on the left; BACK TO MAP on the right. No CLOSE here: it
            // used to sit beside BACK TO MAP looking the same while exiting the whole builder,
            // and the painting view's own CLOSE is one click away once this is dismissed.
            foot.Add(MetaUi.Text(Loc.T("gravebuilder.library.foot_hint", "LOAD A STRUCTURE, AN IMAGE OR A SAVED PART, OR EXPORT THIS MAP"), MetaUi.Face.BodyBold, 18f, DimText, 2f));
            VisualElement keys = TerminalUi.FootKeys(foot, 18f);
            keys.Add(TerminalUi.FootKey("map-builder-back", Loc.T("gravebuilder.library.foot_back", "BACK TO MAP"), Accent, CloseLibrary, primary: true, 380f));

            // Page stage: the tab row, the current tab's page, then the status line under it
            // (in flow, so it never covers a row), then the tooltip box on top.
            VisualElement pageStage = new VisualElement { name = "map-builder-library" };
            pageStage.style.flexGrow = 1f;
            pageStage.style.flexDirection = FlexDirection.Column;
            pageStage.style.overflow = Overflow.Hidden;
            body.Add(pageStage);
            _tipStage = pageStage;

            _libraryTabRow = Row();
            _libraryTabRow.style.flexShrink = 0f;
            _libraryTabRow.style.flexWrap = Wrap.Wrap;
            _libraryTabRow.style.alignItems = Align.Center;
            _libraryTabRow.style.paddingLeft = 40f;
            _libraryTabRow.style.paddingRight = 40f;
            _libraryTabRow.style.paddingTop = 14f;
            // The head rule's hairline (SettingsModal.Section) under the keys, so the row reads
            // as a bar over the page rather than loose buttons.
            _libraryTabRow.style.borderBottomWidth = 1f;
            _libraryTabRow.style.borderBottomColor = MetaUi.Fade(Accent, 0.25f);
            pageStage.Add(_libraryTabRow);

            VisualElement pages = new VisualElement { name = "map-builder-library-pages" };
            pages.style.flexGrow = 1f;
            pages.style.flexShrink = 1f;
            pages.style.minHeight = 0f;
            pageStage.Add(pages);

            _statusHost = new VisualElement { pickingMode = PickingMode.Ignore };
            _statusHost.style.flexShrink = 0f;
            _statusHost.style.marginLeft = Length.Percent(24f);
            _statusHost.style.marginRight = Length.Percent(24f);
            _statusHost.style.marginTop = 8f;
            _statusHost.style.marginBottom = PanelEdge;
            pageStage.Add(_statusHost);

            BuildTooltip();

            BuildLoadStructureSection(LibraryTabPage(pages, LibraryTab.Structures, form: false));
            BuildLoadPartSection(LibraryTabPage(pages, LibraryTab.Parts, form: false));
            BuildLoadImageSection(LibraryTabPage(pages, LibraryTab.Image, form: false));
            BuildExportSection(LibraryTabPage(pages, LibraryTab.Export, form: true));
            ShowLibraryTab(_libraryTab);

            RefreshLoadList();
            RefreshPartsList();
            RefreshPartSaveButton();
            RebuildBands();
            RefreshExportHints();
        }

        private static readonly (LibraryTab tab, string label)[] LibraryTabKeys =
        {
            (LibraryTab.Structures, Loc.T("gravebuilder.library.tab_structures", "STRUCTURES")),
            (LibraryTab.Parts, Loc.T("gravebuilder.library.tab_parts", "PARTS")),
            (LibraryTab.Image, Loc.T("gravebuilder.library.tab_image", "IMAGE")),
            (LibraryTab.Export, Loc.T("gravebuilder.library.tab_export", "EXPORT"))
        };

        // One tab's page under the tab row, filling the rest of the height. A form page
        // (IMAGE, EXPORT) scrolls as one column capped at LibraryMaxWidth; a list page
        // (STRUCTURES, PARTS) doesn't scroll as a whole: its list scrolls on its own
        // (ListDetailSplit) beside a detail pane that stays put. Returns what to build into.
        private VisualElement LibraryTabPage(VisualElement host, LibraryTab tab, bool form)
        {
            VisualElement page = new VisualElement { name = "map-builder-library-tab" };
            page.style.flexGrow = 1f;
            page.style.flexShrink = 1f;
            page.style.minHeight = 0f;
            page.style.flexDirection = FlexDirection.Column;
            host.Add(page);
            _libraryTabPages[tab] = page;
            if (!form)
            {
                page.style.paddingLeft = 40f;
                page.style.paddingRight = 40f;
                page.style.paddingTop = 16f;
                return page;
            }

            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.flexShrink = 1f;
            scroll.style.minHeight = 0f;
            page.Add(scroll);
            MetaUi.AccentScroll(scroll, new Color(1f, 1f, 1f, 0.06f), MetaUi.Fade(Accent, 0.45f), MetaUi.Fade(Accent, 0.75f));

            VisualElement col = new VisualElement { name = "map-builder-library-column" };
            col.style.alignSelf = Align.Center;
            col.style.width = Length.Percent(100f);
            col.style.maxWidth = LibraryMaxWidth;
            col.style.paddingLeft = 40f;
            col.style.paddingRight = 40f;
            col.style.paddingTop = 16f;
            col.style.paddingBottom = 48f;
            scroll.contentContainer.Add(col);
            return col;
        }

        // Shows one tab's page and hides the rest: hidden, not rebuilt, so every page's fields,
        // hints and lists stay live whichever tab is up (the floating panels' TogglePanel
        // convention).
        private void ShowLibraryTab(LibraryTab tab)
        {
            _libraryTab = tab;
            HideTooltip(_tooltipOwner);
            foreach (KeyValuePair<LibraryTab, VisualElement> page in _libraryTabPages)
            {
                page.Value.style.display = page.Key == tab ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (_libraryCentreTitle != null)
            {
                _libraryCentreTitle.text = LibraryTabTitle(tab);
            }
            RefreshLibraryTabs();
        }

        private static string LibraryTabTitle(LibraryTab tab)
        {
            foreach ((LibraryTab t, string label) in LibraryTabKeys)
            {
                if (t == tab)
                {
                    return label;
                }
            }
            return Loc.T("gravebuilder.library.title_fallback", "LIBRARY");
        }

        // The tab keys in the panel keys' look (RefreshPanelKey): accent for the page showing,
        // neutral grey for the rest. No pulse: on the painting view that means a tool is
        // active. MetaUi.Primary bakes its colours in, so a switch rebuilds the row.
        private void RefreshLibraryTabs()
        {
            if (_libraryTabRow == null)
            {
                return;
            }
            _libraryTabRow.Clear();
            foreach ((LibraryTab tab, string label) in LibraryTabKeys)
            {
                LibraryTab t = tab;
                VisualElement key = CompactActionBtn(label, () => ShowLibraryTab(t), family: t == _libraryTab ? Accent : Neutral, size: 17f, padX: 36f);
                key.style.marginRight = 10f;
                key.style.marginBottom = 12f;
                _libraryTabRow.Add(key);
            }
        }

        // The Contracts-style split both list tabs share: a fixed-width list column on the
        // left (a dim count, the list's own scroll, then listFoot for whatever goes under the
        // list) and the detail pane filling the rest on the right. Returns the list scroll.
        private static ScrollView ListDetailSplit(VisualElement page, out VisualElement listFoot, out VisualElement detailHost, out Label summary)
        {
            VisualElement split = Row();
            split.style.flexGrow = 1f;
            split.style.flexShrink = 1f;
            split.style.minHeight = 0f;
            page.Add(split);

            VisualElement listCol = new VisualElement();
            listCol.style.width = LibraryListWidth;
            listCol.style.flexShrink = 0f;
            listCol.style.marginRight = 28f;
            split.Add(listCol);

            summary = MetaUi.Text("", MetaUi.Face.Body, 15f, DimText, 1f);
            summary.pickingMode = PickingMode.Ignore;
            summary.style.flexShrink = 0f;
            summary.style.alignSelf = Align.FlexEnd;
            // In line with the rows' right edge (ListScroll's content padding).
            summary.style.marginRight = 14f;
            listCol.Add(summary);

            ScrollView scroll = ListScroll();
            listCol.Add(scroll);

            // Never squeezed: the list scroll gives up the height instead.
            listFoot = new VisualElement();
            listFoot.style.flexShrink = 0f;
            listCol.Add(listFoot);

            detailHost = new VisualElement();
            detailHost.style.flexGrow = 1f;
            detailHost.style.minWidth = 0f;
            split.Add(detailHost);
            return scroll;
        }

        // Settings-row variant of CompactActionBtn: smaller, and spaced to sit several to a
        // wrapping Row (see ChipRow).
        private static VisualElement ChipBtn(string text, Action onClick, bool enabled = true, Color? family = null, bool pulse = false)
        {
            VisualElement b = MetaUi.Primary(text, onClick, enabled, 14f, pulse, family ?? Accent, 20f);
            b.style.alignSelf = Align.FlexStart;
            b.style.marginBottom = 8f;
            return b;
        }

        private static VisualElement ChipRow(params VisualElement[] items)
        {
            VisualElement row = Row();
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            foreach (VisualElement e in items)
            {
                e.style.marginRight = 10f;
                row.Add(e);
            }
            return row;
        }

        // Form fields side by side, each column sharing the width equally. A column can be a
        // bare Field or a Field with its hint under it.
        private static VisualElement FieldRow(params VisualElement[] columns)
        {
            VisualElement row = Row();
            row.style.alignItems = Align.FlexStart;
            for (int i = 0; i < columns.Length; i++)
            {
                columns[i].style.flexGrow = 1f;
                columns[i].style.flexBasis = 0f;
                columns[i].style.minWidth = 0f;
                if (i < columns.Length - 1)
                {
                    columns[i].style.marginRight = 24f;
                }
                row.Add(columns[i]);
            }
            return row;
        }

        // Sub-heading inside a library tab, as the Contracts list's ListHeader
        // (MetaUi.Eyebrow) in this screen's accent.
        private static Label ListHeader(string text, bool first = false)
        {
            Label label = MetaUi.Eyebrow(text);
            label.style.color = AccentLt;
            label.style.marginTop = first ? 6f : 16f;
            label.style.marginBottom = 6f;
            return label;
        }

        // An inner list with its own scroll, filling whatever height its column leaves it.
        private static ScrollView ListScroll()
        {
            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.flexShrink = 1f;
            scroll.style.minHeight = 0f;
            scroll.style.marginBottom = 8f;
            MetaUi.AccentScroll(scroll, new Color(1f, 1f, 1f, 0.06f), MetaUi.Fade(Accent, 0.45f), MetaUi.Fade(Accent, 0.75f));
            scroll.contentContainer.style.paddingRight = 14f;
            return scroll;
        }

        // STRUCTURES: the merged local-exports / game-catalogue list (was the STRUCTURES
        // panel), as a Contracts-style split: the list on the left, a detail pane for whichever
        // row is selected on the right (preview, description, LOAD SELECTED / DELETE).
        private void BuildLoadStructureSection(VisualElement page)
        {
            page.Add(BodyText_(Loc.T("gravebuilder.structures.intro", "Click a structure to preview it on the right; double-click loads it straight away, replacing the current map (can be undone). Your own exports (orange) also fill in the EXPORT tab's form; game structures (grey) reset it.")));

            ScrollView loadScroll = ListDetailSplit(page, out VisualElement listFoot, out _loadDetailHost, out _loadSummary);
            _loadListHost = loadScroll.contentContainer;
            listFoot.Add(CompactActionBtn(Loc.T("gravebuilder.structures.refresh_list", "REFRESH LIST"), () =>
            {
                RefreshLoadList();
                SetStatus(Loc.F("gravebuilder.structures.status_found", "Found {n} loadable structures.", ("n", _loadEntries.Count)));
            }));
            RefreshLoadDetail();
        }

        // The right-hand pane: a big preview, the name, a short note on what loading it does,
        // and the actions (LOAD SELECTED always; DELETE only for your own exports) — same
        // information the Contracts screen gives for its own selected entry. Nothing selected
        // yet (fresh open, or the list was just refreshed) shows a plain placeholder instead.
        private void RefreshLoadDetail()
        {
            if (_loadDetailHost == null)
            {
                return;
            }
            _loadDetailHost.Clear();
            if (_loadSelected < 0 || _loadSelected >= _loadEntries.Count)
            {
                VisualElement empty = MetaUi.Panel(Neutral, 15f, dim: true, 12f);
                empty.style.flexGrow = 1f;
                empty.style.justifyContent = Justify.Center;
                empty.style.alignItems = Align.Center;
                empty.Add(BodyText_(Loc.T("gravebuilder.structures.detail_empty", "Select a structure from the list to preview it here.")));
                _loadDetailHost.Add(empty);
                return;
            }

            LoadEntry entry = _loadEntries[_loadSelected];
            Color family = entry.Local ? Accent : Neutral;
            // Fills the whole detail column height (not just its own content's) and centres
            // that content in it, rather than a small card pinned to the top with empty space
            // under it — confirmed live 2026-11-24 that was reading as wasted space.
            VisualElement panel = MetaUi.Panel(family, 15f, dim: true, 14f);
            panel.style.flexGrow = 1f;
            panel.style.justifyContent = Justify.Center;
            _loadDetailHost.Add(panel);

            VisualElement previewRow = Row();
            previewRow.style.justifyContent = Justify.Center;
            previewRow.style.marginBottom = 14f;
            VisualElement preview = EntryPreview(entry, 380f, out int cellCount);
            previewRow.Add(preview);
            panel.Add(previewRow);

            panel.Add(MetaUi.Text(entry.Label.ToUpperInvariant(), MetaUi.Face.DisplayBold, 22f, MetaUi.TextTone(family), 2f));

            string tag = entry.Local ? Loc.T("gravebuilder.structures.detail_tag_yours", "YOUR EXPORT") : Loc.T("gravebuilder.structures.detail_tag_game", "GAME STRUCTURE");
            Label sub = MetaUi.Text(cellCount > 0 ? Loc.F("gravebuilder.structures.detail_tag_cells", "{tag} · {n} cells", ("tag", tag), ("n", cellCount)) : tag, MetaUi.Face.Body, 14f, DimText, 1.5f);
            sub.style.marginTop = 4f;
            sub.style.marginBottom = 14f;
            panel.Add(sub);

            string desc = entry.Local
                ? Loc.T("gravebuilder.structures.detail_desc_local", "Your own export. Loading it replaces the map on screen (undoable) and fills in the EXPORT tab's form, ready to re-export.")
                : Loc.T("gravebuilder.structures.detail_desc_game", "One of the game's own structures. Loading it replaces the map on screen (undoable) and resets the EXPORT tab's form.");
            Label descLabel = MetaUi.Text(desc, MetaUi.Face.Body, 15f, BodyText, 0.5f, wrap: true);
            descLabel.style.marginBottom = 18f;
            panel.Add(descLabel);

            VisualElement actions = Row();
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.alignItems = Align.Center;
            VisualElement loadBtn = CompactActionBtn(Loc.T("gravebuilder.structures.load_selected", "LOAD SELECTED"), LoadSelected, family: Accent, size: 17f, padX: 36f);
            loadBtn.style.marginRight = 10f;
            actions.Add(loadBtn);
            if (entry.Local)
            {
                actions.Add(AttachTooltip(CompactActionBtn(Loc.T("gravebuilder.structures.delete_button", "DELETE"), () => DeleteLocalStructure(entry), family: MetaUi.Crimson, size: 17f, padX: 36f),
                    Loc.T("gravebuilder.structures.delete_tooltip", "Delete this export's files (asks first).")));
            }
            panel.Add(actions);
        }

        // IMAGE: paint a PNG/JPEG onto the map in the five substrate colours. A typed path
        // like every other input here; the builder has no OS file picker anywhere. A
        // Contracts-style split, like STRUCTURES/PARTS: the form on the left (same column
        // width as their lists, LibraryListWidth), a live preview of the chosen file filling
        // the right — added 2026-11-24, the form alone left most of the tab empty.
        private void BuildLoadImageSection(VisualElement page)
        {
            VisualElement split = Row();
            split.style.flexGrow = 1f;
            split.style.flexShrink = 1f;
            split.style.minHeight = 0f;
            page.Add(split);

            VisualElement body = new VisualElement { name = "map-builder-image-form" };
            body.style.width = LibraryListWidth;
            body.style.flexShrink = 0f;
            body.style.marginRight = 28f;
            split.Add(body);

            body.Add(BodyText_(Loc.T("gravebuilder.image.intro", "Turns a PNG or JPEG into a map in the substrate colours: one cell per sampled pixel, transparent pixels become gaps. PALETTE matches each pixel to the nearest substrate colour; LUMINANCE picks it by brightness alone. Loading replaces the map and can be undone, so try the options and compare.")));

            VisualElement imagePathWrap = Field(Loc.T("gravebuilder.image.path_label", "Image path"), _imagePathText, v =>
            {
                _imagePathText = v;
                RefreshImagePreview();
            }, out _imagePathField);
            AttachTooltip(imagePathWrap, Loc.T("gravebuilder.image.path_tooltip", "The full path to a .png or .jpg file, e.g. C:\\Pictures\\logo.png. Surrounding quotes (from Copy as path) are fine."));
            body.Add(imagePathWrap);
            body.Add(AttachTooltip(ChipBtn(Loc.T("gravebuilder.image.browse_button", "BROWSE\u2026"), BrowseImage), Loc.T("gravebuilder.image.browse_tooltip", "Opens a file picker to choose a PNG or JPEG.")));

            VisualElement imageSizeWrap = Field(Loc.T("gravebuilder.image.size_label", "Max grid size"), _imageSizeText, v =>
            {
                _imageSizeText = v;
                RefreshImagePreview();
            }, out _imageSizeField);
            AttachTooltip(imageSizeWrap, Loc.F("gravebuilder.image.size_tooltip", "How many cells the image's longer side spans, {min} to {max}. The other side follows the image's own shape. Bigger keeps more detail but makes a larger map.", ("min", MinRadius), ("max", MaxImageSize)));
            VisualElement imageAlphaWrap = Field(Loc.T("gravebuilder.image.alpha_label", "Alpha threshold"), _imageAlphaText, v =>
            {
                _imageAlphaText = v;
                RefreshImagePreview();
            }, out _imageAlphaField);
            AttachTooltip(imageAlphaWrap, Loc.T("gravebuilder.image.alpha_tooltip", "0 to 255. Pixels less opaque than this become gaps. 0 keeps every pixel, even fully transparent ones; raise it to trim a soft-edged cutout."));
            body.Add(FieldRow(imageSizeWrap, imageAlphaWrap));

            _imageOptionsHost = new VisualElement();
            body.Add(_imageOptionsHost);
            RefreshImageOptions();

            body.Add(AttachTooltip(CompactActionBtn(Loc.T("gravebuilder.image.load_button", "LOAD IMAGE"), LoadImage),
                Loc.T("gravebuilder.image.load_tooltip", "Replaces the map with the image and takes you back to it. Can be undone.")));

            _imagePreviewHost = new VisualElement { name = "map-builder-image-preview" };
            _imagePreviewHost.style.flexGrow = 1f;
            _imagePreviewHost.style.minWidth = 0f;
            split.Add(_imagePreviewHost);
            RefreshImagePreview();
        }

        // The IMAGE tab's live preview: not the source file's own pixels, but the actual
        // converted result — the same ImageToMap() + substrate-colour rendering LoadImage()
        // and the structure thumbnails use — so changing PALETTE/LUMINANCE/DITHER/INVERT, the
        // grid size or the alpha threshold shows what you'd actually get, not just the source
        // picture. Added 2026-11-24: a raw-image preview didn't tell you anything about the
        // options you're actually choosing between.
        // _imagePreviewTex is a loose Texture2D (not pooled, unlike _localThumbs below) —
        // always destroyed before being replaced or when the library closes (CloseLibrary),
        // since nothing else ever cleans it up.
        private void RefreshImagePreview()
        {
            if (_imagePreviewHost == null)
            {
                return;
            }
            _imagePreviewHost.Clear();
            if (_imagePreviewTex != null)
            {
                UnityEngine.Object.Destroy(_imagePreviewTex);
                _imagePreviewTex = null;
            }

            VisualElement panel = MetaUi.Panel(Neutral, 15f, dim: true, 14f);
            panel.style.flexGrow = 1f;
            panel.style.justifyContent = Justify.Center;
            panel.style.alignItems = Align.Center;
            _imagePreviewHost.Add(panel);

            // Explorer's Copy as path wraps the path in quotes, as LoadImage() trims it.
            string path = (_imagePathText ?? "").Trim().Trim('"').Trim();
            if (path.Length == 0)
            {
                panel.Add(BodyText_(Loc.T("gravebuilder.image.preview_prompt", "Pick a PNG or JPEG to preview the converted map here.")));
                return;
            }
            if (!File.Exists(path))
            {
                panel.Add(BodyText_(Loc.F("gravebuilder.image.preview_no_file", "No file at '{path}'.", ("path", path))));
                return;
            }
            // Lenient: a half-typed field (e.g. "" or "1" while backspacing "140") just skips
            // the preview rather than flashing a validation error on every keystroke — LOAD
            // IMAGE's own ReadClampedInt is the one that actually enforces this.
            if (!int.TryParse((_imageSizeText ?? "").Trim(), out int size) || !int.TryParse((_imageAlphaText ?? "").Trim(), out int alphaThreshold))
            {
                panel.Add(BodyText_(Loc.T("gravebuilder.image.preview_bad_number", "Enter a whole number for Max grid size and Alpha threshold to preview the converted map.")));
                return;
            }
            size = Mathf.Clamp(size, MinRadius, MaxImageSize);
            alphaThreshold = Mathf.Clamp(alphaThreshold, 0, 255);

            Texture2D srcTex = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                srcTex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(srcTex, bytes))
                {
                    panel.Add(BodyText_(Loc.F("gravebuilder.image.preview_unreadable", "'{file}' couldn't be read as an image. PNG and JPEG are supported.", ("file", Path.GetFileName(path)))));
                    return;
                }
                SandboxMap previewMap = ImageToMap(srcTex, size, alphaThreshold, _imageLuminance, _imageInvert, _imageDither);
                if (previewMap == null)
                {
                    panel.Add(BodyText_(Loc.F("gravebuilder.image.preview_no_cells", "'{file}' produced no cells: every pixel is below the alpha threshold ({threshold}). Lower it to keep more.", ("file", Path.GetFileName(path)), ("threshold", alphaThreshold))));
                    return;
                }
                Texture2D tex = RenderMapPreview(previewMap);
                if (tex == null)
                {
                    panel.Add(BodyText_(Loc.T("gravebuilder.image.preview_render_failed", "Couldn't render a preview.")));
                    return;
                }
                _imagePreviewTex = tex;
                VisualElement image = new VisualElement { pickingMode = PickingMode.Ignore };
                image.style.width = Length.Percent(88f);
                image.style.height = Length.Percent(80f);
                image.style.backgroundImage = new StyleBackground(tex);
                image.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                image.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                image.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                image.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                panel.Add(image);
                Label caption = MetaUi.Text(Loc.F("gravebuilder.image.preview_caption", "{n} cells \u00b7 {file}", ("n", previewMap.cells.Count), ("file", Path.GetFileName(path))), MetaUi.Face.Body, 14f, DimText, 1f);
                caption.style.marginTop = 14f;
                panel.Add(caption);
            }
            catch (Exception e)
            {
                panel.Add(BodyText_(Loc.F("gravebuilder.image.preview_read_failed", "Couldn't read '{file}': {error}", ("file", Path.GetFileName(path)), ("error", e.Message))));
            }
            finally
            {
                if (srcTex != null)
                {
                    UnityEngine.Object.Destroy(srcTex);
                }
            }
        }

        // RenderSilhouette's exact rendering (axial-to-pixel math, 2x2 blocks, y-flip to match
        // the game's own orientation), but reading an in-memory SandboxMap.cells (the IMAGE
        // tab's live preview) instead of a parsed MegastructureData (the load-list thumbnails).
        private static Texture2D RenderMapPreview(SandboxMap map)
        {
            if (map == null || map.cells.Count == 0)
            {
                return null;
            }
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            foreach (Vector2Int axial in map.cells.Keys)
            {
                float x = Mathf.Sqrt(3f) * (axial.x + axial.y / 2f);
                float y = 1.5f * axial.y;
                xMin = Mathf.Min(xMin, x);
                xMax = Mathf.Max(xMax, x);
                yMin = Mathf.Min(yMin, y);
                yMax = Mathf.Max(yMax, y);
            }
            float s = 2f / Mathf.Sqrt(3f);
            int w = Mathf.CeilToInt((xMax - xMin) * s) + 4;
            int h = Mathf.CeilToInt((yMax - yMin) * s) + 4;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            Color32[] px = new Color32[w * h];
            foreach (KeyValuePair<Vector2Int, SandboxMap.Cell> kvp in map.cells)
            {
                Vector2Int axial = kvp.Key;
                Color32 c = StructureSilhouette.LayerColour(kvp.Value.layer);
                float x = Mathf.Sqrt(3f) * (axial.x + axial.y / 2f);
                int bx = Mathf.RoundToInt((x - xMin) * s) + 1;
                int by = h - 2 - Mathf.RoundToInt((1.5f * axial.y - yMin) * s);
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int tx = bx + dx;
                        int ty = by - dy;
                        if (tx >= 0 && tx < w && ty >= 0 && ty < h)
                        {
                            px[ty * w + tx] = c;
                        }
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return tex;
        }

        // The three settings as the game's own two-way switches (SettingsModal.Switch / OnOff:
        // a caption, then one control split into two halves with the chosen half filled),
        // rather than pulsing buttons: pulse means "a tool is active" on the painting view,
        // not "this option is set". Rebuilt on change. Each on/off only applies in one colour
        // mode, so the other mode greys it out rather than hiding it, keeping the row still.
        private void RefreshImageOptions()
        {
            if (_imageOptionsHost == null)
            {
                return;
            }
            _imageOptionsHost.Clear();
            VisualElement row = Row();
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6f;
            row.Add(SegmentSwitch(Loc.T("gravebuilder.image.colours_caption", "COLOURS"), Loc.T("gravebuilder.image.colours_palette", "PALETTE"), Loc.T("gravebuilder.image.colours_luminance", "LUMINANCE"), _imageLuminance, SetImageLuminance, enabled: true, 320f,
                Loc.T("gravebuilder.image.colours_tooltip", "PALETTE: each pixel becomes the substrate colour nearest its own (blue, pink, gold, purple); dark or greyish pixels become grey. LUMINANCE: hue is ignored; the image's brightness is stretched to its full range and split into five steps, darkest = layer 5 (Purple), lightest = layer 1 (Grey).")));
            row.Add(SegmentSwitch(Loc.T("gravebuilder.image.dither_caption", "DITHER"), Loc.T("gravebuilder.image.dither_off", "OFF"), Loc.T("gravebuilder.image.dither_on", "ON"), _imageDither, v => { _imageDither = v; RefreshImageOptions(); RefreshImagePreview(); }, enabled: !_imageLuminance, 200f,
                Loc.T("gravebuilder.image.dither_tooltip", "PALETTE only. When on, each pixel's colour error is spread to its neighbours, so gradients and photos read as a mix of colours instead of flat bands. Off matches each pixel on its own.")));
            row.Add(SegmentSwitch(Loc.T("gravebuilder.image.invert_caption", "INVERT"), Loc.T("gravebuilder.image.invert_off", "OFF"), Loc.T("gravebuilder.image.invert_on", "ON"), _imageInvert, v => { _imageInvert = v; RefreshImageOptions(); RefreshImagePreview(); }, enabled: _imageLuminance, 200f,
                Loc.T("gravebuilder.image.invert_tooltip", "LUMINANCE only. Flips the brightness steps, so the lightest pixels get the highest layer instead of the darkest.")));
            _imageOptionsHost.Add(row);
        }

        // SettingsModal.Switch in this screen's accent: caption, then a slab split into two
        // clickable halves, the chosen one filled. value false = left half, true = right.
        private VisualElement SegmentSwitch(string caption, string left, string right, bool value, Action<bool> set, bool enabled, float width, string tooltip)
        {
            VisualElement group = Row();
            group.style.alignItems = Align.Center;
            group.style.marginRight = 34f;
            group.style.marginBottom = 8f;
            if (!enabled)
            {
                group.style.opacity = 0.4f;
            }
            Label cap = MetaUi.Text(caption, MetaUi.Face.Display, 15f, BodyText, 3f);
            cap.pickingMode = PickingMode.Ignore;
            cap.style.marginRight = 14f;
            group.Add(cap);

            VisualElement box = Row();
            box.style.width = width;
            box.style.height = 42f;
            box.style.flexShrink = 0f;
            box.style.paddingLeft = 5f;
            box.style.paddingRight = 5f;
            box.style.paddingTop = 5f;
            box.style.paddingBottom = 5f;
            SlabFrame outline = new SlabFrame { Notch = 10f, Stroke = 2f, StrokeColor = MetaUi.Fade(Accent, 0.45f), FillColor = SwitchControl }.Behind(box);
            box.Add(Half(left, false));
            box.Add(Half(right, true));
            if (enabled)
            {
                box.RegisterCallback<PointerEnterEvent>(_ => outline.StrokeColor = Accent);
                box.RegisterCallback<PointerLeaveEvent>(_ => outline.StrokeColor = MetaUi.Fade(Accent, 0.45f));
            }
            group.Add(box);
            return AttachTooltip(group, tooltip);

            VisualElement Half(string text, bool side)
            {
                bool chosen = value == side;
                Button b = new Button { text = "" };
                b.style.flexGrow = 1f;
                b.style.flexBasis = 0f;
                b.style.marginLeft = 0f;
                b.style.marginRight = 0f;
                b.style.marginTop = 0f;
                b.style.marginBottom = 0f;
                b.style.paddingLeft = 0f;
                b.style.paddingRight = 0f;
                b.style.paddingTop = 0f;
                b.style.paddingBottom = 0f;
                b.style.borderLeftWidth = 0f;
                b.style.borderRightWidth = 0f;
                b.style.borderTopWidth = 0f;
                b.style.borderBottomWidth = 0f;
                b.style.backgroundColor = Color.clear;
                b.style.alignItems = Align.Center;
                b.style.justifyContent = Justify.Center;
                b.SetEnabled(enabled);
                SlabFrame fill = new SlabFrame { Notch = 8f, Stroke = 0f, StrokeColor = Color.clear, FillColor = chosen ? Accent : Color.clear }.Behind(b);
                if (side)
                {
                    fill.SquareBottomLeft = true;
                }
                else
                {
                    fill.SquareTopRight = true;
                }
                Label word = MetaUi.Text(text, MetaUi.Face.Display, 15f, chosen ? SwitchInk : MetaUi.Fade(Color.white, 0.45f), 3f);
                word.pickingMode = PickingMode.Ignore;
                b.Add(word);
                if (enabled && !chosen)
                {
                    b.clicked += () => set(side);
                }
                return b;
            }
        }

        private void SetImageLuminance(bool luminance)
        {
            _imageLuminance = luminance;
            RefreshImageOptions();
            RefreshImagePreview();
        }

        // Native Win32 "Open" dialog (see Win32OpenFileDialog) instead of a typed path — this
        // blocks the game's main thread while it's open, same as any other native modal dialog.
        private void BrowseImage()
        {
            string current = (_imagePathText ?? "").Trim().Trim('"').Trim();
            string initialDir = current.Length > 0 && File.Exists(current) ? Path.GetDirectoryName(current) : null;
            string picked = Win32OpenFileDialog.PickImage(initialDir);
            if (picked == null)
            {
                return;
            }
            _imagePathText = picked;
            _imagePathField?.SetValueWithoutNotify(picked);
            RefreshImagePreview();
        }

        // PARTS: the saved-parts list (was the PARTS panel) as STRUCTURES' split (list left,
        // detail pane for the selected part right), and saving the clipboard as a new part
        // under the list.
        private void BuildLoadPartSection(VisualElement page)
        {
            page.Add(BodyText_(Loc.T("gravebuilder.parts.intro", "Saved clipboards (made with COPY/CUT in TOOLS), kept between sessions. Click a part to see it on the right; double-click puts it in the clipboard and takes you back to the map, ready to PASTE. Save the current clipboard as a new part under the list.")));

            ScrollView partsScroll = ListDetailSplit(page, out VisualElement listFoot, out _partDetailHost, out _partsSummary);
            _partsListHost = partsScroll.contentContainer;
            listFoot.Add(CompactActionBtn(Loc.T("gravebuilder.parts.refresh_list", "REFRESH LIST"), () =>
            {
                RefreshPartsList();
                string skipped = (_partsSkipped > 0 ? (_partsSkipped == 1 ? Loc.F("gravebuilder.parts.status_found_skipped_one", " ({n} unreadable file skipped, see the log)", ("n", _partsSkipped)) : Loc.F("gravebuilder.parts.status_found_skipped_other", " ({n} unreadable files skipped, see the log)", ("n", _partsSkipped))) : "");
                SetStatus(_partEntries.Count == 1
                    ? Loc.F("gravebuilder.parts.status_found_one", "Found {n} saved part{skipped}.", ("n", _partEntries.Count), ("skipped", skipped))
                    : Loc.F("gravebuilder.parts.status_found_other", "Found {n} saved parts{skipped}.", ("n", _partEntries.Count), ("skipped", skipped)));
            }));

            listFoot.Add(ListHeader(Loc.T("gravebuilder.parts.save_header", "SAVE THE CLIPBOARD AS A PART")));
            // Name over its save button: the list column is too narrow for both on one line.
            VisualElement partNameWrap = Field(Loc.T("gravebuilder.parts.name_label", "Part name"), _partNameText, v => _partNameText = v, out _partNameField);
            AttachTooltip(partNameWrap, Loc.T("gravebuilder.parts.name_tooltip", "Also becomes the file name (lowercased, other characters turned into _). Saving under a name that already exists overwrites that part."));
            listFoot.Add(partNameWrap);
            _partSaveHost = new VisualElement();
            listFoot.Add(_partSaveHost);
            RefreshPartDetail();
        }

        // EXPORT: contract details (with inline validation) + the band editor (was the EXPORT
        // panel).
        private void BuildExportSection(VisualElement body)
        {
            body.Add(BodyText_(Loc.T("gravebuilder.export.intro", "Saves this map as a contract in content/structures/, live in the game's Contracts list straight away (no RELOAD MODS click needed) — unlike the quick, private test on the MAP panel's SAVE AS SANDBOX OVERRIDE, this publishes a real, selectable contract.")));

            body.Add(ListHeader(Loc.T("gravebuilder.export.details_header", "CONTRACT DETAILS"), first: true));
            // ID and name side by side, each with its hint under it; money on its own line.
            VisualElement idWrap = Field(Loc.T("gravebuilder.export.id_label", "Contract ID"), _idText, v =>
            {
                _idText = v;
                RefreshExportHints();
            }, out _idField);
            AttachTooltip(idWrap, Loc.T("gravebuilder.export.id_tooltip", "The contract's internal name, and its file name. Lowercase letters, digits and _ only. Exporting with one of your own export's IDs overwrites it."));
            _idHint = FieldHint();
            VisualElement idCol = new VisualElement();
            idCol.Add(idWrap);
            idCol.Add(_idHint);

            VisualElement nameWrap = Field(Loc.T("gravebuilder.export.name_label", "Display name"), _nameText, v =>
            {
                _nameText = v;
                RefreshExportHints();
            }, out _nameField);
            AttachTooltip(nameWrap, Loc.T("gravebuilder.export.name_tooltip", "The name shown in the game's Contracts list. Leave it blank to use the ID."));
            _nameHint = FieldHint();
            VisualElement nameCol = new VisualElement();
            nameCol.Add(nameWrap);
            nameCol.Add(_nameHint);
            body.Add(FieldRow(idCol, nameCol));

            VisualElement moneyWrap = Field(Loc.T("gravebuilder.export.money_label", "Starting money"), _moneyText, v =>
            {
                _moneyText = v;
                RefreshExportHints();
            }, out _moneyField);
            moneyWrap.style.maxWidth = 560f;
            AttachTooltip(moneyWrap, Loc.T("gravebuilder.export.money_tooltip", "How much money the player starts this contract with. A whole number, 0 or more."));
            body.Add(moneyWrap);
            _moneyHint = FieldHint();
            body.Add(_moneyHint);

            body.Add(ListHeader(Loc.T("gravebuilder.export.stages_header", "COLOUR PROGRESSION")));
            body.Add(BodyText_(Loc.T("gravebuilder.export.stages_intro", "One stage per substrate colour — these are the colours you already painted in PALETTE. Each stage's tiles are every cell painted that colour; a colour with nothing painted yet will say so below. Stage 1 unlocks first; each later stage stays sealed until the one before it is cleared. Hover a field for what it does.")));
            _bandHost = new VisualElement();
            body.Add(_bandHost);
            body.Add(AttachTooltip(CompactActionBtn(Loc.T("gravebuilder.export.add_stage", "ADD STAGE"), () =>
            {
                _bands.Add(DefaultBand());
                RebuildBands();
            }), Loc.T("gravebuilder.export.add_stage_tooltip", "Adds another stage at the end (it unlocks after all the others).")));
            // Its own line, not beside ADD BAND: it acts on the whole form, not the band list.
            body.Add(CompactActionBtn(Loc.T("gravebuilder.export.export_button", "EXPORT AS STRUCTURE"), ExportStructure, size: 18f, padX: 44f));
        }

        // Feedback line under a form field, lined up with the field's input (Field's label
        // column is 230 wide).
        private static Label FieldHint()
        {
            Label hint = MetaUi.Text("", MetaUi.Face.Body, 15f, DimText, 0.5f, wrap: true);
            hint.style.marginLeft = 230f;
            hint.style.marginTop = -4f;
            hint.style.marginBottom = 6f;
            PadWrappedTracking(hint, 0.5f);
            return hint;
        }

        private static void SetHint(Label hint, string text, Color colour)
        {
            if (hint == null)
            {
                return;
            }
            hint.text = text;
            hint.style.color = colour;
        }

        // Inline checks for the export form, run on every keystroke. Same rules as
        // ExportStructure (which still re-checks and refuses on export), plus two warnings it
        // doesn't block on: overwriting your own export, and an ID a game structure already
        // owns (the framework skips such a file: ContentInjector's "already exists").
        private void RefreshExportHints()
        {
            string raw = (_idText ?? "").Trim();
            string id = raw.ToLowerInvariant();
            if (raw.Length == 0)
            {
                SetHint(_idHint, Loc.T("gravebuilder.export.id_hint_required", "Required: lowercase letters, digits and _ only (e.g. my_map_1)."), MetaUi.Crimson);
            }
            else if (!IdPattern.IsMatch(id))
            {
                SetHint(_idHint, Loc.T("gravebuilder.export.id_hint_invalid", "Only letters a-z, digits 0-9 and _ are allowed: no spaces, dashes or other symbols."), MetaUi.Crimson);
            }
            else
            {
                string lower = raw != id ? Loc.F("gravebuilder.export.id_hint_lowercase", "Saved in lowercase as '{id}'. ", ("id", id)) : "";
                LoadEntry game = _loadEntries.FirstOrDefault(e => !e.Local && e.Id == id);
                bool exists = false;
                try
                {
                    exists = File.Exists(Path.Combine(StructuresDir(), id + ".json"));
                }
                catch
                {
                    // A bad path just means no overwrite warning.
                }
                if (game != null)
                {
                    SetHint(_idHint, Loc.F("gravebuilder.export.id_hint_game_clash", "{lower}The game structure '{label}' already uses this ID, so the framework will skip your export. Pick another ID.", ("lower", lower), ("label", game.Label)), WarnText);
                }
                else if (exists)
                {
                    SetHint(_idHint, Loc.F("gravebuilder.export.id_hint_overwrite", "{lower}Exporting overwrites your existing export '{id}'.", ("lower", lower), ("id", id)), WarnText);
                }
                else
                {
                    SetHint(_idHint, Loc.F("gravebuilder.export.id_hint_writes", "{lower}Writes {id}.json and {id}_cells.json.", ("lower", lower), ("id", id)), DimText);
                }
            }

            string name = (_nameText ?? "").Trim();
            SetHint(_nameHint, name.Length == 0
                ? (IdPattern.IsMatch(id) ? Loc.F("gravebuilder.export.name_hint_blank_id", "Blank: the ID '{id}' is used as the name.", ("id", id)) : Loc.T("gravebuilder.export.name_hint_blank", "Blank: the ID is used as the name."))
                : Loc.F("gravebuilder.export.name_hint_shown", "Shown in the Contracts list as {name}.", ("name", name.ToUpperInvariant())), DimText);

            if (!int.TryParse((_moneyText ?? "").Trim(), out int money) || money < 0)
            {
                SetHint(_moneyHint, Loc.T("gravebuilder.export.money_hint_invalid", "Must be a whole number, 0 or more (digits only, no commas)."), MetaUi.Crimson);
            }
            else
            {
                SetHint(_moneyHint, Loc.F("gravebuilder.export.money_hint", "The player starts with {money}.", ("money", money)), DimText);
            }
        }

        // ---- floating panels ---------------------------------------------------------

        // The builder's painting panels. Key order = bar order, MAP last (right end). Stats
        // live in the foot band (BuildStatsReadout); loading structures and parts and
        // exporting live in the library view (BuildLibraryView).
        // Widths fit each panel's widest row (PairRows of two full-size keys need ~600).
        private List<PanelDef> Panels()
        {
            return new List<PanelDef>
            {
                new PanelDef { Id = PanelPalette, Label = Loc.T("gravebuilder.panels.palette", "PALETTE"), Width = 480f, Build = BuildPalette },
                new PanelDef { Id = PanelTools, Label = Loc.T("gravebuilder.panels.tools", "TOOLS"), Width = 620f, Build = BuildTools },
                new PanelDef { Id = PanelMap, Label = Loc.T("gravebuilder.panels.map", "MAP"), Width = 640f, AnchorRight = true, Build = BuildMap },
            };
        }

        private static bool IsPanelOpen(string id) => _panelOpen.TryGetValue(id, out bool open) && open;

        // One floating panel: the game's MetaUi.Panel slab (opaque Fill + accent edge) with a
        // scroll inside, absolutely positioned on the stage (LayoutPanels sets left/top/max
        // height). Click-through: MetaUi.Panel is a plain VisualElement whose only built-in
        // child is its SlabFrame (PickingMode.Ignore), and the panel is a later sibling of the
        // canvas on the stage. UI Toolkit picks top-down, last child first, and a subtree whose
        // children don't take the point returns its Position-mode root; so a pointer anywhere
        // in a panel's rect resolves to the panel or something inside it (even over the
        // Ignore-picking text labels), and since the canvas is not its ancestor, neither the
        // pointer event nor the wheel event ever propagates to it.
        private VisualElement BuildPanel(PanelDef def)
        {
            VisualElement panel = MetaUi.Panel(Accent, 18f, dim: false, 16f);
            panel.name = "map-builder-panel-" + def.Id;
            panel.pickingMode = PickingMode.Position;
            panel.style.position = Position.Absolute;
            panel.style.width = def.Width;
            panel.style.left = PanelEdge;
            panel.style.top = 80f; // Placeholder until the first LayoutPanels.
            panel.style.flexDirection = FlexDirection.Column;
            panel.style.display = IsPanelOpen(def.Id) ? DisplayStyle.Flex : DisplayStyle.None;

            // Grows to its content, shrinks (and scrolls) once the panel hits its max height.
            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 0f;
            scroll.style.flexShrink = 1f;
            scroll.style.minHeight = 0f;
            panel.Add(scroll);
            MetaUi.AccentScroll(scroll, new Color(1f, 1f, 1f, 0.06f), MetaUi.Fade(Accent, 0.45f), MetaUi.Fade(Accent, 0.75f));
            VisualElement content = scroll.contentContainer;
            content.style.paddingRight = 14f;
            def.Build(content);
            return panel;
        }

        // Toggle key for one panel, styled like FILL MODE / MIRROR: accent + pulse while open,
        // neutral grey while closed. MetaUi.Primary bakes its colours in, so a state change
        // rebuilds the key inside its (fixed) host.
        private void RefreshPanelKey(string id)
        {
            if (!_panelKeyHosts.TryGetValue(id, out VisualElement host))
            {
                return;
            }
            PanelDef def = _panelDefs.FirstOrDefault(d => d.Id == id);
            if (def == null)
            {
                return;
            }
            host.Clear();
            bool open = IsPanelOpen(id);
            VisualElement key = ActionBtn(def.Label, () => TogglePanel(id), family: open ? Accent : Neutral, pulse: open);
            key.style.alignSelf = Align.FlexStart;
            key.style.marginBottom = 0f;
            host.Add(key);
        }

        private void TogglePanel(string id)
        {
            bool open = !IsPanelOpen(id);
            _panelOpen[id] = open;
            if (_panels.TryGetValue(id, out VisualElement panel))
            {
                panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                if (open)
                {
                    // A newly opened panel goes on top of any it overlaps.
                    panel.BringToFront();
                }
            }
            RefreshPanelKey(id);
            LayoutPanels();
        }

        // Closes a panel once its job is done (e.g. after loading a structure/part), so it
        // doesn't keep eating space other panels could use. No-op if already closed.
        private void ClosePanel(string id)
        {
            if (!IsPanelOpen(id))
            {
                return;
            }
            _panelOpen[id] = false;
            if (_panels.TryGetValue(id, out VisualElement panel))
            {
                panel.style.display = DisplayStyle.None;
            }
            RefreshPanelKey(id);
            LayoutPanels();
        }

        // Drops each panel under its own key: left-anchored panels start at their key's left
        // edge but are pushed right past any earlier open panel, so side-by-side panels don't
        // overlap until the canvas runs out of width; MAP's right edge lines up with its key's.
        // Everything is clamped inside the stage, with a max height down to its bottom edge.
        // Re-run on stage/key geometry changes and on every toggle.
        private void LayoutPanels()
        {
            if (_stage == null)
            {
                return;
            }
            float stageW = _stage.contentRect.width;
            float stageH = _stage.contentRect.height;
            if (stageW < 1f || stageH < 1f)
            {
                return;
            }
            float cursor = PanelEdge;
            foreach (PanelDef def in _panelDefs)
            {
                if (!_panels.TryGetValue(def.Id, out VisualElement panel) || !_panelKeyHosts.TryGetValue(def.Id, out VisualElement host))
                {
                    continue;
                }
                Rect key = host.ChangeCoordinatesTo(_stage, new Rect(Vector2.zero, host.layout.size));
                float w = def.Width;
                float left = def.AnchorRight ? key.xMax - w : Mathf.Max(key.xMin, cursor);
                left = Mathf.Clamp(left, PanelEdge, Mathf.Max(PanelEdge, stageW - w - PanelEdge));
                float top = key.yMax + PanelGap;
                panel.style.left = left;
                panel.style.top = top;
                panel.style.maxHeight = Mathf.Max(80f, stageH - top - PanelEdge);
                if (!def.AnchorRight && IsPanelOpen(def.Id))
                {
                    cursor = left + w + PanelGap;
                }
            }
        }

        // Stops a non-interactive subtree (the status callout) from taking pointer input.
        private static void IgnorePicking(VisualElement e)
        {
            e.pickingMode = PickingMode.Ignore;
            foreach (VisualElement child in e.Children())
            {
                IgnorePicking(child);
            }
        }

        // ---- hover tooltips -------------------------------------------------------------
        // The plain VisualElement.tooltip string does nothing here: UI Toolkit only draws it
        // in the Unity Editor, and a built player has to supply its own (the game's
        // ModTreeScreen hand-builds one too). So: one shared box on the stage, shown by
        // AttachTooltip's handlers.
        //
        // PointerEnterEvent/PointerLeaveEvent, not Over/Out: Enter/Leave are hierarchy-aware
        // (moving from the target onto one of its own children raises neither on the target),
        // so the box doesn't flicker as the pointer crosses a button's inner label/frame. The
        // game's own hover code (MetaUi.Primary, MetaUi.Lift) uses the same pair.

        private const float TooltipOffset = 18f;
        private const float TooltipMaxWidth = 440f;

        // Opaque MetaUi slab with one wrapping label; never pickable, so it can't steal the
        // hover from the element it describes or block painting.
        private void BuildTooltip()
        {
            _tooltip = MetaUi.Panel(Accent, 10f, dim: false, 10f);
            _tooltip.name = "map-builder-tooltip";
            _tooltip.style.position = Position.Absolute;
            _tooltip.style.maxWidth = TooltipMaxWidth;
            _tooltip.style.display = DisplayStyle.None;
            _tooltipText = MetaUi.Text("", MetaUi.Face.Body, 17f, MetaUi.Ink, 0f, wrap: true);
            _tooltip.Add(_tooltipText);
            IgnorePicking(_tooltip);
            _tipStage.Add(_tooltip);
            _tooltipOwner = null;
        }

        // Shows text near the cursor while the pointer is over target (or any of its
        // children). Returns target so it can wrap a button where it's built. The handlers
        // read the current _tooltip, so they keep working across a mode-toggle rebuild.
        private VisualElement AttachTooltip(VisualElement target, string text)
        {
            if (target == null || string.IsNullOrEmpty(text))
            {
                return target;
            }
            target.RegisterCallback<PointerEnterEvent>(e => ShowTooltip(target, text, e.position));
            target.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (_tooltipOwner == target)
                {
                    PlaceTooltip(e.position);
                }
            });
            target.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip(target));
            // Buttons are rebuilt under the pointer (a toggle flipping, a row refreshing); a
            // detached element never gets its PointerLeave, so hide here instead.
            target.RegisterCallback<DetachFromPanelEvent>(_ => HideTooltip(target));
            return target;
        }

        private void ShowTooltip(VisualElement owner, string text, Vector2 panelPos)
        {
            if (_tooltip == null || _tipStage == null)
            {
                return;
            }
            _tooltipOwner = owner;
            _tooltipText.text = text;
            _tooltip.style.display = DisplayStyle.Flex;
            // Panels are brought to front when opened; keep the box above all of them.
            _tooltip.BringToFront();
            PlaceTooltip(panelPos);
        }

        // Below-right of the cursor; flipped to the cursor's left / above it when it would
        // run off the stage's right / bottom edge, so it stays inside the canvas.
        private void PlaceTooltip(Vector2 panelPos)
        {
            if (_tooltip == null || _tipStage == null)
            {
                return;
            }
            Vector2 p = _tipStage.WorldToLocal(panelPos);
            float w = _tipStage.contentRect.width;
            float h = _tipStage.contentRect.height;
            float boxH = _tooltip.layout.height > 1f ? _tooltip.layout.height : 60f;
            if (p.x + TooltipOffset + TooltipMaxWidth > w - PanelEdge)
            {
                _tooltip.style.left = StyleKeyword.Null;
                _tooltip.style.right = Mathf.Max(PanelEdge, w - p.x + TooltipOffset * 0.5f);
            }
            else
            {
                _tooltip.style.right = StyleKeyword.Null;
                _tooltip.style.left = Mathf.Max(PanelEdge, p.x + TooltipOffset);
            }
            if (p.y + TooltipOffset + boxH > h - PanelEdge)
            {
                _tooltip.style.top = StyleKeyword.Null;
                _tooltip.style.bottom = Mathf.Max(PanelEdge, h - p.y + TooltipOffset * 0.5f);
            }
            else
            {
                _tooltip.style.bottom = StyleKeyword.Null;
                _tooltip.style.top = Mathf.Max(PanelEdge, p.y + TooltipOffset);
            }
        }

        // Only the element that showed the box can hide it, so leaving one button and
        // entering its neighbour (in either event order) never blanks the new text.
        private void HideTooltip(VisualElement owner)
        {
            if (_tooltipOwner != owner)
            {
                return;
            }
            _tooltipOwner = null;
            if (_tooltip != null)
            {
                _tooltip.style.display = DisplayStyle.None;
            }
        }

        // RESET VIEW, with the zoom/pan controls as its hover text since those have no button
        // of their own.
        private VisualElement ResetViewBtn()
        {
            return AttachTooltip(ActionBtn(Loc.T("gravebuilder.screen.reset_view", "RESET VIEW"), () =>
            {
                _canvas.ResetView();
                SetStatus(Loc.T("gravebuilder.status.view_reset", "View reset (zoom 1x, no pan)."));
            }), Loc.T("gravebuilder.screen.reset_view_tooltip", "Back to zoom 1x, centred. Mouse wheel zooms towards the cursor; middle-drag pans."));
        }

        // PALETTE: substrate layers + both special-tile groups + erase.
        private void BuildPalette(VisualElement content)
        {
            // Palette: substrate layers. Each swatch is a game Key recoloured to the layer's
            // own StructureSilhouette.LayerColour (the same source HexCanvas draws with), with
            // an exact-colour chip under it since the Key itself lightens its family colour.
            // Caption per swatch: which weapon type deals full damage to that layer, the rest
            // deal only a fraction (ColourRules._family + ColourMath.Mult, verified 2026-11-22
            // against ColourRules.cs/ColourMath.cs). Layer 1 (Black) has no resistance at all —
            // Mult always returns full damage against it, whatever the attacker.
            content.Add(Head(Loc.T("gravebuilder.palette.head", "PALETTE"), first: true));
            content.Add(SubHead(Loc.T("gravebuilder.palette.substrate_header", "SUBSTRATE LAYERS")));
            content.Add(BodyText_(Loc.T("gravebuilder.palette.substrate_body", "Each layer resists every weapon type except the one named on its swatch, which still does full damage; Layer 1 has no resistance at all.")));
            VisualElement swatches = Row();
            swatches.style.flexWrap = Wrap.Wrap;
            for (int layer = 1; layer <= 5; layer++)
            {
                int l = layer;
                Color c = StructureSilhouette.LayerColour(l);
                swatches.Add(ToolHolder(SubstrateKey(l), MetaUi.Key(LayerColourName(l), () => SelectTool(Substrate, l), 120f, c), c, LayerCaption(l)));
            }
            content.Add(swatches);
            // Always-visible legend, not just the swatches' hover text — confirmed live
            // (2026-11-24) that a first-time player never thinks to hover these to learn which
            // colour resists which weapon.
            content.Add(BodyText_(string.Join("  ·  ", Enumerable.Range(1, 5).Select(l => Loc.F("gravebuilder.palette.layer_legend_item", "{colour}: {caption}", ("colour", LayerColourName(l)), ("caption", LayerCaption(l)))))));

            // Palette: special tiles, split into the two groups the game's own tile data
            // distinguishes by "role" (gamedata_tiles.json, verified 2026-11-22): "behavior"
            // tiles seed a hidden enemy encounter, "material" tiles seed an extra resource
            // pickup. Labels are the game's own tile "label" field, Title Case, not SCREAMING_
            // SNAKE_CASE with the underscore left in (that was the old ToUpperInvariant() text).
            content.Add(SubHead(Loc.T("gravebuilder.palette.special_header", "SPECIAL TILES")));

            content.Add(GroupLabel(Loc.T("gravebuilder.palette.enemy_header", "ENEMY ENCOUNTERS")));
            content.Add(BodyText_(Loc.T("gravebuilder.palette.enemy_body", "Paints a hidden enemy marker. Destroying this tile (or mining close to it) wakes the enemy it hides.")));
            VisualElement behaviorTiles = Row();
            behaviorTiles.style.flexWrap = Wrap.Wrap;
            for (int i = 0; i < BehaviorTileCount; i++)
            {
                string t = SpecialTiles[i];
                Color c = MarkerColour(t);
                behaviorTiles.Add(ToolHolder(t, MetaUi.Key(SpecialTileLabel[t], () => SelectTool(t, _layer), 196f, c), c));
            }
            content.Add(behaviorTiles);

            content.Add(GroupLabel(Loc.T("gravebuilder.palette.resource_header", "RESOURCE PICKUPS")));
            content.Add(BodyText_(Loc.T("gravebuilder.palette.resource_body", "Seeds an extra resource pickup into this tile to mine.")));
            VisualElement materialTiles = Row();
            materialTiles.style.flexWrap = Wrap.Wrap;
            for (int i = BehaviorTileCount; i < SpecialTiles.Length; i++)
            {
                string t = SpecialTiles[i];
                Color c = MarkerColour(t);
                materialTiles.Add(ToolHolder(t, MetaUi.Key(SpecialTileLabel[t], () => SelectTool(t, _layer), 196f, c), c));
            }
            materialTiles.Add(ToolHolder(Erase, MetaUi.Key(Loc.T("gravebuilder.palette.erase", "ERASE"), () => SelectTool(Erase, _layer), 196f, MetaUi.Crimson), MetaUi.Crimson));
            content.Add(materialTiles);
            content.Add(BodyText_(Loc.T("gravebuilder.palette.paint_help", "Left-drag paints the selected tool. Right-drag erases. Special tiles keep the cell's existing layer colour.")));
        }

        // TOOLS: brush size (shared control, see BuildBrushControl), fill/mirror, undo/redo,
        // reset view, selection & clipboard.
        private void BuildTools(VisualElement content)
        {
            content.Add(Head(Loc.T("gravebuilder.tools.head", "TOOLS"), first: true));
            BuildBrushControl(content);
            content.Add(BodyText_(Loc.T("gravebuilder.tools.intro", "These change how your next paint stroke behaves — hover any button for exactly what it does.")));
            // Column: FILL/MIRROR pair, then LINE/CIRCLE pair (RefreshToggles).
            _toggleRow = new VisualElement();
            content.Add(_toggleRow);
            _historyRow = Row();
            content.Add(_historyRow);
            content.Add(ResetViewBtn());

            // Selection & clipboard. How each button works is its hover text (RefreshToggles /
            // RefreshClipboardButtons).
            content.Add(SubHead(Loc.T("gravebuilder.tools.selection_header", "SELECTION & CLIPBOARD")));
            content.Add(BodyText_(Loc.T("gravebuilder.tools.selection_body", "Select cells, then copy/cut/paste them elsewhere on the map.")));
            _selectRow = Row();
            content.Add(_selectRow);
            _loopRow = new VisualElement();
            content.Add(_loopRow);
            _clipRow = new VisualElement();
            content.Add(_clipRow);
        }

        // MAP: radius, new filled/empty, grow, sandbox-override actions.
        private void BuildMap(VisualElement content)
        {
            content.Add(Head(Loc.T("gravebuilder.map.head", "MAP"), first: true));
            BuildRadiusControl(content);
            content.Add(PairRow(
                ActionBtn(Loc.T("gravebuilder.map.new_filled", "NEW (FILLED)"), () => NewMap(filled: true)),
                ActionBtn(Loc.T("gravebuilder.map.new_empty", "NEW (EMPTY)"), () => NewMap(filled: false))));
            content.Add(ActionBtn(Loc.T("gravebuilder.map.grow_to_radius", "GROW TO RADIUS"), GrowToRadius));
            content.Add(BodyText_(Loc.T("gravebuilder.map.override_intro", "Quick, private test: your next real mining sortie uses this map, but it never appears in the Contracts list. Use the EXPORT tab (STRUCTURES & PARTS, bottom bar) to publish a map properly instead.")));
            content.Add(AttachTooltip(ActionBtn(Loc.T("gravebuilder.map.save_override", "SAVE AS SANDBOX OVERRIDE"), SaveOverride),
                Loc.T("gravebuilder.map.save_override_tooltip", "Writes this map as a one-off override: the next time you fly a real mining sortie, it uses this instead of its normal structure. Doesn't touch the Contracts list.")));
            content.Add(PairRow(
                AttachTooltip(ActionBtn(Loc.T("gravebuilder.map.load_override", "LOAD OVERRIDE"), LoadOverride),
                    Loc.T("gravebuilder.map.load_override_tooltip", "Loads the saved override back into this editor, replacing the current map (can be undone).")),
                AttachTooltip(ActionBtn(Loc.T("gravebuilder.map.remove_override", "REMOVE OVERRIDE"), RemoveOverride),
                    Loc.T("gravebuilder.map.remove_override_tooltip", "Deletes the saved override. Your next sortie goes back to the game's normal structures; this editor's map is unchanged."))));
        }

        private static bool FillActive => _fillMode;

        private static bool MirrorActive => _mirror;

        private static bool SelectActive => _selectMode;

        private static bool PasteModeActive => _pasteMode && _clipboard.Count > 0;

        private static bool LineActive => _lineMode;

        private static bool CircleActive => _circleMode;

        private static bool AutoFillLoopsActive => _autoFillLoops;

        // Brush size: one control, built by this one method in TOOLS.
        // A SliderInt (0..MaxBrush) and a small number box side by side, both over the same
        // static _brush/_brushText state, so the value survives a reopen and the
        // two widgets never disagree. Dragging the slider writes both and refreshes the box;
        // typing in the box keeps the raw text in _brushText (half-typed values survive a
        // rebuild), updates _brush only when it parses, moves the slider, and on focus-out
        // NormaliseIntField shows the clamped value actually in use.
        //
        // The game's UI never uses a slider, so its panel theme may not style one: the
        // tracker/dragger get inline styles here rather than relying on the theme.
        // _brush itself stays the 0-based radius every painting method (DiscFootprint etc.)
        // already expects (0 = just the cell under the cursor) — only the slider/box/caption
        // shown to the player are offset by +1, since a displayed "brush size 0" reads as
        // "paints nothing" even though it actually paints one cell.
        private const int BrushDisplayOffset = 1;

        private void BuildBrushControl(VisualElement content)
        {
            Label caption = MetaUi.Text(Loc.F("gravebuilder.tools.brush_caption", "Brush size (1-{max}): cells around the cursor, 1 = one cell", ("max", MaxBrush + BrushDisplayOffset)), MetaUi.Face.Body, 18f, BodyText, 0f, wrap: true);
            caption.style.marginBottom = 4f;
            content.Add(caption);

            VisualElement row = Row();
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 10f;
            content.Add(row);

            _brushSlider = new SliderInt(BrushDisplayOffset, MaxBrush + BrushDisplayOffset);
            _brushSlider.SetValueWithoutNotify(_brush + BrushDisplayOffset);
            _brushSlider.style.flexGrow = 1f;
            _brushSlider.style.marginLeft = 0f;
            _brushSlider.style.marginRight = 12f;
            _brushSlider.style.height = 30f;
            StyleSlider(_brushSlider);
            _brushSlider.RegisterValueChangedCallback(e =>
            {
                _brush = Mathf.Clamp(e.newValue - BrushDisplayOffset, 0, MaxBrush);
                _brushText = (_brush + BrushDisplayOffset).ToString();
                _brushField?.SetValueWithoutNotify(_brushText);
                _canvas?.Repaint();
            });
            row.Add(_brushSlider);

            _brushField = new TextField();
            _brushField.SetValueWithoutNotify(_brushText ?? "");
            StyleInput(_brushField, 20f);
            _brushField.style.width = 80f;
            _brushField.style.flexShrink = 0f;
            _brushField.style.marginLeft = 0f;
            _brushField.style.marginRight = 0f;
            _brushField.RegisterValueChangedCallback(e =>
            {
                _brushText = e.newValue;
                if (int.TryParse((e.newValue ?? "").Trim(), out int n))
                {
                    _brush = Mathf.Clamp(n - BrushDisplayOffset, 0, MaxBrush);
                    _brushSlider?.SetValueWithoutNotify(_brush + BrushDisplayOffset);
                    _canvas?.Repaint();
                }
            });
            _brushField.RegisterCallback<FocusOutEvent>(_ => NormaliseIntField(_brushField, ref _brushText, _brush + BrushDisplayOffset));
            row.Add(_brushField);
        }

        // Map radius: same slider + number box control as brush size (same bounded-number
        // function), over the existing _radiusText state ReadRadius already parses/clamps from.
        private void BuildRadiusControl(VisualElement content)
        {
            Label caption = MetaUi.Text(Loc.F("gravebuilder.map.radius_caption", "Radius ({min}-{max}): size of a new map, or the target for GROW TO RADIUS", ("min", MinRadius), ("max", MaxRadius)), MetaUi.Face.Body, 18f, BodyText, 0f, wrap: true);
            caption.style.marginBottom = 4f;
            content.Add(caption);

            VisualElement row = Row();
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 10f;
            content.Add(row);

            int.TryParse((_radiusText ?? "").Trim(), out int current);
            current = Mathf.Clamp(current == 0 ? DefaultRadius : current, MinRadius, MaxRadius);

            _radiusSlider = new SliderInt(MinRadius, MaxRadius);
            _radiusSlider.SetValueWithoutNotify(current);
            _radiusSlider.style.flexGrow = 1f;
            _radiusSlider.style.marginLeft = 0f;
            _radiusSlider.style.marginRight = 12f;
            _radiusSlider.style.height = 30f;
            StyleSlider(_radiusSlider);
            _radiusSlider.RegisterValueChangedCallback(e =>
            {
                int clamped = Mathf.Clamp(e.newValue, MinRadius, MaxRadius);
                _radiusText = clamped.ToString();
                _radiusField?.SetValueWithoutNotify(_radiusText);
            });
            row.Add(_radiusSlider);

            _radiusField = new TextField();
            _radiusField.SetValueWithoutNotify(_radiusText ?? "");
            StyleInput(_radiusField, 20f);
            _radiusField.style.width = 80f;
            _radiusField.style.flexShrink = 0f;
            _radiusField.style.marginLeft = 0f;
            _radiusField.style.marginRight = 0f;
            _radiusField.RegisterValueChangedCallback(e =>
            {
                _radiusText = e.newValue;
                if (int.TryParse((e.newValue ?? "").Trim(), out int n))
                {
                    _radiusSlider?.SetValueWithoutNotify(Mathf.Clamp(n, MinRadius, MaxRadius));
                }
            });
            _radiusField.RegisterCallback<FocusOutEvent>(_ =>
            {
                ReadRadius(out int radius);
                _radiusSlider?.SetValueWithoutNotify(radius);
            });
            row.Add(_radiusField);
        }

        // Inline look for a SliderInt in the accent family: dark track with an accent edge,
        // light accent handle. Every part is null-checked (theme-dependent children).
        private static void StyleSlider(SliderInt slider)
        {
            VisualElement drag = slider.Q(null, BaseSlider<int>.dragContainerUssClassName);
            if (drag != null)
            {
                drag.style.flexGrow = 1f;
                drag.style.height = 30f;
                drag.style.marginLeft = 8f;
                drag.style.marginRight = 8f;
            }
            VisualElement tracker = slider.Q(null, BaseSlider<int>.trackerUssClassName);
            if (tracker != null)
            {
                tracker.style.position = Position.Absolute;
                tracker.style.left = 0f;
                tracker.style.right = 0f;
                tracker.style.top = 12f;
                tracker.style.height = 6f;
                tracker.style.marginTop = 0f;
                tracker.style.backgroundColor = Color.Lerp(Accent, Color.black, 0.75f);
                TerminalUi.Border(tracker, 1f, MetaUi.Fade(Accent, 0.7f));
            }
            VisualElement dragger = slider.Q(null, BaseSlider<int>.draggerUssClassName);
            if (dragger != null)
            {
                dragger.style.position = Position.Absolute;
                dragger.style.top = 3f;
                dragger.style.width = 16f;
                dragger.style.height = 24f;
                dragger.style.marginTop = 0f;
                dragger.style.marginLeft = 0f;
                dragger.style.backgroundColor = AccentLt;
                TerminalUi.Border(dragger, 2f, Accent);
                dragger.style.borderTopLeftRadius = 0f;
                dragger.style.borderTopRightRadius = 0f;
                dragger.style.borderBottomLeftRadius = 0f;
                dragger.style.borderBottomRightRadius = 0f;
            }
        }

        // Same theme as the SliderInt overload above, for the one float slider (preview ring
        // size, 0-1) — BaseSlider<float> rather than BaseSlider<int>.
        private static void StyleSlider(Slider slider)
        {
            VisualElement drag = slider.Q(null, BaseSlider<float>.dragContainerUssClassName);
            if (drag != null)
            {
                drag.style.flexGrow = 1f;
                drag.style.height = 30f;
                drag.style.marginLeft = 8f;
                drag.style.marginRight = 8f;
            }
            VisualElement tracker = slider.Q(null, BaseSlider<float>.trackerUssClassName);
            if (tracker != null)
            {
                tracker.style.position = Position.Absolute;
                tracker.style.left = 0f;
                tracker.style.right = 0f;
                tracker.style.top = 12f;
                tracker.style.height = 6f;
                tracker.style.marginTop = 0f;
                tracker.style.backgroundColor = Color.Lerp(Accent, Color.black, 0.75f);
                TerminalUi.Border(tracker, 1f, MetaUi.Fade(Accent, 0.7f));
            }
            VisualElement dragger = slider.Q(null, BaseSlider<float>.draggerUssClassName);
            if (dragger != null)
            {
                dragger.style.position = Position.Absolute;
                dragger.style.top = 3f;
                dragger.style.width = 16f;
                dragger.style.height = 24f;
                dragger.style.marginTop = 0f;
                dragger.style.marginLeft = 0f;
                dragger.style.backgroundColor = AccentLt;
                TerminalUi.Border(dragger, 2f, Accent);
                dragger.style.borderTopLeftRadius = 0f;
                dragger.style.borderTopRightRadius = 0f;
                dragger.style.borderBottomLeftRadius = 0f;
                dragger.style.borderBottomRightRadius = 0f;
            }
        }

        private static VisualElement Row()
        {
            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            return row;
        }

        // Section header (MetaUi.PanelHead in the accent family).
        private static Label Head(string text, bool first = false)
        {
            Label label = MetaUi.PanelHead(text, Accent);
            label.style.marginTop = first ? 4f : 26f;
            label.style.marginBottom = 12f;
            return label;
        }

        private static Label SubHead(string text)
        {
            Label label = MetaUi.Text(text, MetaUi.Face.DisplayBold, 16f, AccentLt, 3f);
            label.style.marginTop = 8f;
            label.style.marginBottom = 8f;
            return label;
        }

        // A sub-group heading nested one level below SubHead (e.g. "ENEMY ENCOUNTERS" under
        // "SPECIAL TILES", or a BandDef [Header] group inside one band's panel).
        private static Label GroupLabel(string text)
        {
            Label label = MetaUi.Text(text, MetaUi.Face.Body, 14f, DimText, 2f);
            label.style.marginTop = 6f;
            label.style.marginBottom = 4f;
            return label;
        }

        // The 5 substrate layers by their actual on-screen colour (StructureSilhouette.
        // LayerColour, the same source the swatches/canvas/stats all draw with) rather than
        // "Layer N" — easier to recognise at a glance since the swatch IS that colour.
        // Layer 1's real colour is a dark blue-grey, not black, despite ColourRules.Colour
        // naming it "Black" internally — "Grey" matches what's actually drawn.
        private static string LayerColourName(int layer)
        {
            switch (layer)
            {
                case 1: return Loc.T("gravebuilder.tiles.layer_grey", "Grey");
                case 2: return Loc.T("gravebuilder.tiles.layer_blue", "Blue");
                case 3: return Loc.T("gravebuilder.tiles.layer_pink", "Pink");
                case 4: return Loc.T("gravebuilder.tiles.layer_gold", "Gold");
                case 5: return Loc.T("gravebuilder.tiles.layer_purple", "Purple");
                default: return Loc.F("gravebuilder.tiles.layer_other", "Layer {layer}", ("layer", layer));
            }
        }

        // Which weapon type deals full damage to a substrate layer (ColourRules._family maps
        // each weapon family to the one colour it's effective against; everything else only
        // deals a fraction — ColourMath.Mult). Family names confirmed verbatim from the game's
        // own handbook ("Damage & colors" entry, dumped 2026-11-22): Laser=Gold, Dynamo=Blue,
        // Pulse=Purple, "Cannon & Drill"=Pink — these are the real in-game weapon-family names,
        // not a guess.
        private static string LayerCaption(int layer)
        {
            switch (layer)
            {
                case 1: return Loc.T("gravebuilder.tiles.layer_caption_1", "No resistance — every weapon at full damage");
                case 2: return Loc.T("gravebuilder.tiles.layer_caption_2", "Full dmg: Dynamo weapons only");
                case 3: return Loc.T("gravebuilder.tiles.layer_caption_3", "Full dmg: Cannon & Drill weapons only");
                case 4: return Loc.T("gravebuilder.tiles.layer_caption_4", "Full dmg: Laser weapons only");
                case 5: return Loc.T("gravebuilder.tiles.layer_caption_5", "Full dmg: Pulse weapons only");
                default: return "";
            }
        }

        private static Label BodyText_(string text)
        {
            Label label = MetaUi.Text(text, MetaUi.Face.Body, 18f, BodyText, 0f, wrap: true);
            label.style.marginTop = 4f;
            label.style.marginBottom = 8f;
            return label;
        }

        // Full-width game Primary button in the accent family.
        private static VisualElement ActionBtn(string text, Action onClick, bool enabled = true, Color? family = null, bool pulse = false)
        {
            VisualElement b = MetaUi.Primary(text, onClick, enabled, 18f, pulse, family ?? Accent, 20f);
            b.style.alignSelf = Align.Stretch;
            b.style.marginBottom = 10f;
            return b;
        }

        // A smaller, non-stretched button for form-style actions (ADD BAND, EXPORT AS
        // STRUCTURE): ActionBtn's full-width Align.Stretch reads as an oversized slab against
        // a short label once it's sized to a whole form column rather than a tool panel row.
        private static VisualElement CompactActionBtn(string text, Action onClick, bool enabled = true, Color? family = null, float size = 15f, float padX = 28f)
        {
            VisualElement b = MetaUi.Primary(text, onClick, enabled, size, pulse: false, family ?? Accent, padX);
            b.style.alignSelf = Align.FlexStart;
            b.style.marginBottom = 10f;
            return b;
        }

        private static VisualElement PairRow(VisualElement a, VisualElement b)
        {
            VisualElement row = Row();
            foreach (VisualElement e in new[] { a, b })
            {
                e.style.flexGrow = 1f;
                e.style.flexBasis = 0f;
                row.Add(e);
            }
            a.style.marginRight = 10f;
            return row;
        }

        // Palette entry: a recoloured game Key, an exact-colour chip, a selection border, and
        // (substrate swatches only) a short caption explaining what painting that colour means.
        // caption is shown as a hover tooltip (AttachTooltip; the native `.tooltip` property
        // was tried first and draws nothing in a built player) rather than a permanently
        // visible label under the button. Attached to the holder, so hovering the swatch or
        // its chip both show it (Enter/Leave cover the holder's whole subtree).
        private VisualElement ToolHolder(string key, VisualElement button, Color exact, string caption = null)
        {
            VisualElement holder = new VisualElement();
            holder.style.paddingLeft = 4f;
            holder.style.paddingRight = 4f;
            holder.style.paddingTop = 4f;
            holder.style.paddingBottom = 4f;
            holder.style.marginRight = 6f;
            holder.style.marginBottom = 6f;
            TerminalUi.Border(holder, 2f, Color.clear);
            AttachTooltip(holder, caption);
            holder.Add(button);
            VisualElement chip = new VisualElement { pickingMode = PickingMode.Ignore };
            chip.style.height = 6f;
            chip.style.marginTop = 4f;
            chip.style.backgroundColor = exact;
            holder.Add(chip);
            _toolHolders[key] = holder;
            return holder;
        }


        // A caption line over a bare TextField (CompactField's shape), styled the way the game
        // styles its one runtime text input (DroneBayScreen.RenameField): toolkit font, tinted
        // input fill, family-coloured edge. Not TextField's own built-in label (new
        // TextField(label)) — that one doesn't pick up StyleInput's explicit box height below,
        // confirmed live 2026-11-24: its box rendered far shorter than CompactField's same box,
        // despite the same height style being set on both. field is the input itself, for
        // value access, tooltips and extra styling; the returned wrapper is what actually goes
        // in the layout.
        private static VisualElement Field(string label, string value, Action<string> onChange, out TextField field)
        {
            VisualElement wrap = new VisualElement();
            wrap.style.marginBottom = 10f;
            Label cap = MetaUi.Text(label, MetaUi.Face.Body, 18f, BodyText, 0f);
            cap.style.marginBottom = 4f;
            wrap.Add(cap);

            TextField tf = new TextField();
            tf.SetValueWithoutNotify(value ?? "");
            StyleInput(tf, 20f);
            tf.style.marginLeft = 0f;
            tf.style.marginRight = 0f;
            tf.style.marginBottom = 0f;
            wrap.Add(tf);
            field = tf;
            if (onChange != null)
            {
                tf.RegisterValueChangedCallback(e => onChange(e.newValue));
            }
            return wrap;
        }

        private static void StyleInput(TextField tf, float size)
        {
            MetaUi.Style(tf, MetaUi.Face.BodyBold, size, MetaUi.Ink);
            float height = size + 22f;
            tf.style.height = height;
            VisualElement input = tf.Q(null, TextField.inputUssClassName) ?? tf.Q("unity-text-input");
            if (input != null)
            {
                input.style.flexGrow = 1f;
                // Unity's own base stylesheet gives this element a fixed default height meant
                // for its small default font size; bumping the font size above (which cascades
                // down to this element's text) doesn't widen that box to match, so the enlarged
                // glyphs clip against the bottom edge unless the height is forced explicitly.
                input.style.height = height;
                input.style.minHeight = 0f;
                input.style.maxHeight = new StyleLength(StyleKeyword.None);
                input.style.marginTop = 0f;
                input.style.marginBottom = 0f;
                input.style.backgroundColor = Color.Lerp(Accent, Color.black, 0.88f);
                TerminalUi.Border(input, 2f, MetaUi.Fade(Accent, 0.7f));
                input.style.borderTopLeftRadius = 0f;
                input.style.borderTopRightRadius = 0f;
                input.style.borderBottomLeftRadius = 0f;
                input.style.borderBottomRightRadius = 0f;
                input.style.paddingLeft = 10f;
                input.style.paddingRight = 10f;
                input.style.paddingTop = 0f;
                input.style.paddingBottom = 0f;
                input.style.color = MetaUi.Ink;
                // Forcing the box height (above) wasn't the actual fix — the text itself still
                // anchors wherever Unity's base style puts it regardless of box height, which is
                // why it kept clipping. This actually centres it.
                CenterInputText(input);
            }
            // Deprecated in favour of USS vars, but it's what DroneBayScreen does and we have no
            // stylesheet of our own.
#pragma warning disable CS0618
            tf.textSelection.cursorColor = AccentLt;
            tf.textSelection.selectionColor = MetaUi.Fade(Accent, 0.35f);
#pragma warning restore CS0618
        }

        // On focus-out, show the clamped value actually in use (or the last good one).
        private static void NormaliseIntField(TextField tf, ref string text, int value)
        {
            text = value.ToString();
            tf.SetValueWithoutNotify(text);
        }

        // unityTextAlign is nominally an inherited style, but TextField's base control sets
        // an explicit value directly on its inner TextElement that wins over whatever this
        // wrapper inherits — setting it only on the input wrapper (the previous approach here)
        // left the glyphs stuck top-anchored. Mirrors the game's own DroneBayScreen.RenameField,
        // which sets MiddleLeft on both the wrapper and the inner TextElement, zeroes the
        // TextElement's own margin/padding, and centres via flexDirection Column + Justify.Center
        // on the wrapper.
        private static void CenterInputText(VisualElement input)
        {
            input.style.unityTextAlign = TextAnchor.MiddleLeft;
            input.style.flexDirection = FlexDirection.Column;
            input.style.justifyContent = Justify.Center;
            UnityEngine.UIElements.TextElement textElement = input.Q<UnityEngine.UIElements.TextElement>();
            if (textElement != null)
            {
                textElement.style.unityTextAlign = TextAnchor.MiddleLeft;
                textElement.style.marginTop = 0f;
                textElement.style.marginBottom = 0f;
                textElement.style.paddingTop = 0f;
                textElement.style.paddingBottom = 0f;
            }
        }

        private static void TryApplyFont(VisualElement el)
        {
            FontAsset font = AssetCatalog.Load<FontAsset>(FontPath);
            if (font == null)
            {
                Plugin.Log.LogWarning($"MapBuilder: could not load font '{FontPath}', using the default.");
                return;
            }
            el.style.unityFontDefinition = new StyleFontDefinition(font);
        }

        // MetaUi.Text's tracking (letterSpacing) compensation — a paddingRight sized from the
        // text present at creation, to stop the last glyph clipping — goes stale the moment the
        // label's .text is reassigned later. Use instead of `label.text = ...` for any label
        // that was created with tracking > 0 and gets re-texted (stat readouts, hover labels).
        private static void RetextTracked(Label label, string text, float tracking)
        {
            label.text = text;
            label.style.paddingRight = Mathf.Max(0, (text ?? "").Length - 1) * tracking;
        }

        // MetaUi.Text skips its trailing-letterSpacing compensation entirely when wrap: true (it
        // only sizes for a single line), so a word that nearly fills its wrapped box can have its
        // last glyph clipped at the wrap boundary — confirmed on the band field labels (SHARD
        // SPAWNS/ENEMY SPAWNS captions like "EXPLOSIVE"/"LIGHTNING" rendering as "EXPLOSIV"/
        // "LIGHTNIN"). One tracking unit of right padding is enough slack. Call right after
        // creating any wrap: true label made with tracking > 0.
        private static void PadWrappedTracking(Label label, float tracking)
        {
            if (tracking > 0f)
            {
                label.style.paddingRight = tracking;
            }
        }

        // ---- dynamic button rows ------------------------------------------------------
        // MetaUi.Primary bakes its enabled/hover colours in when built, so state changes
        // rebuild the button rather than restyling it.

        private void RefreshToggles()
        {
            if (_toggleRow == null)
            {
                return;
            }
            _toggleRow.Clear();
            _toggleRow.Add(PairRow(
                AttachTooltip(
                    ActionBtn(_fillMode ? Loc.T("gravebuilder.tools.fill_on", "FILL MODE: ON") : Loc.T("gravebuilder.tools.fill_off", "FILL MODE: OFF"), ToggleFill, family: _fillMode ? Accent : Neutral, pulse: _fillMode),
                    Loc.T("gravebuilder.tools.fill_tooltip", "When on, one left click floods every connected cell matching the clicked one with the selected tool. Right-click floods erase instead.")),
                AttachTooltip(
                    ActionBtn(_mirror ? Loc.T("gravebuilder.tools.mirror_on", "MIRROR (180°): ON") : Loc.T("gravebuilder.tools.mirror_off", "MIRROR (180°): OFF"), ToggleMirror, family: _mirror ? Accent : Neutral, pulse: _mirror),
                    Loc.T("gravebuilder.tools.mirror_tooltip", "When on, every paint, erase, fill, line and circle is repeated on the opposite side of the map, rotated 180° about the centre (Q R becomes -Q -R)."))));
            _toggleRow.Add(PairRow(
                AttachTooltip(
                    ActionBtn(_lineMode ? Loc.T("gravebuilder.tools.line_on", "LINE: ON") : Loc.T("gravebuilder.tools.line_off", "LINE: OFF"), ToggleLine, family: _lineMode ? Accent : Neutral, pulse: _lineMode),
                    Loc.T("gravebuilder.tools.line_tooltip", "When on, left-drag draws a straight line from where you pressed to the cursor with the selected tool, at the current brush size; it's painted when you let go (one undo step). Right-drag draws an erasing line. Turns fill mode, select and circle off.")),
                AttachTooltip(
                    ActionBtn(_circleMode ? Loc.T("gravebuilder.tools.circle_on", "CIRCLE: ON") : Loc.T("gravebuilder.tools.circle_off", "CIRCLE: OFF"), ToggleCircle, family: _circleMode ? Accent : Neutral, pulse: _circleMode),
                    Loc.T("gravebuilder.tools.circle_tooltip", "When on, press to set the centre and drag outwards to size a filled circle; it's painted with the selected tool when you let go (one undo step). Right-drag erases a circle. Brush size doesn't apply. Turns fill mode, select and line off."))));
        }

        private void RefreshHistoryButtons()
        {
            if (_historyRow == null)
            {
                return;
            }
            _historyRow.Clear();
            VisualElement row = PairRow(
                AttachTooltip(ActionBtn(_undo.Count > 0 ? Loc.F("gravebuilder.tools.undo_count", "UNDO ({n})", ("n", _undo.Count)) : Loc.T("gravebuilder.tools.undo", "UNDO"), Undo, enabled: _undo.Count > 0),
                    Loc.F("gravebuilder.tools.undo_tooltip", "Steps back one action. Shortcut: {combo}.",
                        ("combo", ShortcutBindings.ComboText(ShortcutBindings.Undo) ?? Loc.T("gravebuilder.shortcuts.unbound_note", "not currently bound")))),
                AttachTooltip(ActionBtn(_redo.Count > 0 ? Loc.F("gravebuilder.tools.redo_count", "REDO ({n})", ("n", _redo.Count)) : Loc.T("gravebuilder.tools.redo", "REDO"), Redo, enabled: _redo.Count > 0),
                    Loc.F("gravebuilder.tools.redo_tooltip", "Re-applies the last undone action. Shortcut: {combo} or Ctrl+Shift+{undoKey}.",
                        ("combo", ShortcutBindings.ComboText(ShortcutBindings.Redo) ?? Loc.T("gravebuilder.shortcuts.unbound_note", "not currently bound")),
                        ("undoKey", ShortcutBindings.KeyText(ShortcutBindings.Get(ShortcutBindings.Undo))))));
            row.style.flexGrow = 1f;
            _historyRow.Add(row);
        }

        private void ToggleFill()
        {
            _fillMode = !_fillMode;
            string off = _fillMode ? TurnOffShapeTools() : null;
            RefreshToggles();
            // The brush-footprint preview hides in fill mode.
            _canvas?.Repaint();
            SetStatus(_fillMode ? Loc.F("gravebuilder.status.fill_on", "Fill mode on: a click flood-fills the connected region.{note}", ("note", Suffix(off))) : Loc.T("gravebuilder.status.fill_off", "Fill mode off: brush painting."));
        }

        // ---- line & circle tools ------------------------------------------------------
        // LINE and CIRCLE are toggles like FILL MODE/SELECT. Exclusivity: turning one on turns
        // off the other, fill mode and SELECT (and cancels paste placement, which would
        // otherwise win every click, as ToggleSelect does); turning fill mode or SELECT on
        // turns both off. Fill mode + SELECT together is left as it always was (SELECT wins).
        // So at most one drag-shape tool is ever on, and never alongside another tool that
        // claims the same left/right click. Canvas precedence (HexCanvas.OnPointerDown):
        // paste > LINE/CIRCLE > SELECT > fill > brush.
        //
        // Both draw through BrushFootprint's disc logic (DiscFootprint): a line is the brush
        // disc at every cell of HexCanvas.HexLine(start, end); a circle is one disc centred on
        // the start cell with radius = hex distance start -> cursor. ShapeFootprint is both
        // the canvas ghost and exactly the cell set ShapeCommit paints, mirrors included.

        // Turns LINE/CIRCLE off; returns a status note naming what was turned off, or null.
        private static string TurnOffShapeTools()
        {
            string off = _lineMode ? Loc.T("gravebuilder.status.turned_off_shape_line", "Line") : _circleMode ? Loc.T("gravebuilder.status.turned_off_shape_circle", "Circle") : null;
            _lineMode = false;
            _circleMode = false;
            return off == null ? null : Loc.F("gravebuilder.status.turned_off_shape", "{shape} turned off.", ("shape", off));
        }

        private static string Suffix(string note) => string.IsNullOrEmpty(note) ? "" : " " + note;

        private void ToggleLine() => ToggleShape(HexShape.Line);

        private void ToggleCircle() => ToggleShape(HexShape.Circle);

        private void ToggleShape(HexShape shape)
        {
            CommitAction();
            bool on = shape == HexShape.Line ? !_lineMode : !_circleMode;
            List<string> off = new List<string>();
            if (on)
            {
                if (shape == HexShape.Line && _circleMode)
                {
                    off.Add(Loc.T("gravebuilder.status.turned_off_item_circle", "circle"));
                }
                if (shape == HexShape.Circle && _lineMode)
                {
                    off.Add(Loc.T("gravebuilder.status.turned_off_item_line", "line"));
                }
                if (_fillMode)
                {
                    off.Add(Loc.T("gravebuilder.status.turned_off_item_fill_mode", "fill mode"));
                }
                if (_selectMode)
                {
                    off.Add(Loc.T("gravebuilder.status.turned_off_item_select", "select"));
                }
                if (PasteModeActive)
                {
                    off.Add(Loc.T("gravebuilder.status.turned_off_item_paste_placement", "paste placement"));
                }
                _fillMode = false;
                _selectMode = false;
                _pasteMode = false;
            }
            _lineMode = shape == HexShape.Line && on;
            _circleMode = shape == HexShape.Circle && on;
            RefreshToggles();
            RefreshClipboardButtons();
            _canvas?.Repaint();
            string note = off.Count > 0 ? Loc.F("gravebuilder.status.turned_off_list", " Turned off: {list}.", ("list", string.Join(", ", off))) : "";
            if (shape == HexShape.Line)
            {
                SetStatus(on
                    ? Loc.F("gravebuilder.status.line_on", "Line on: drag from start to end; it's painted on release at the current brush size. Right-drag erases.{note}", ("note", note))
                    : Loc.T("gravebuilder.status.line_off", "Line off: brush painting."));
            }
            else
            {
                SetStatus(on
                    ? Loc.F("gravebuilder.status.circle_on", "Circle on: press at the centre and drag outwards; it's painted on release. Right-drag erases.{note}", ("note", note))
                    : Loc.T("gravebuilder.status.circle_off", "Circle off: brush painting."));
            }
        }

        public HexShape ShapeTool => LineActive ? HexShape.Line : CircleActive ? HexShape.Circle : HexShape.None;

        // Every cell within hex distance radius of (q, r) that's inside the extent, plus each
        // one's (-q, -r) mirror when mirror is set (the extent is symmetric, so a mirror is in
        // it exactly when its source is). The same radius loop and test StrokePaint/SelectPaint
        // use; BrushFootprint is this at _brush, CIRCLE at the dragged radius.
        // The loops are clipped to the extent's (q, r) bounding square first (cells outside it
        // are out of extent anyway), so a big CIRCLE only walks the part that can land.
        private void DiscFootprint(int q, int r, int radius, bool mirror, HashSet<Vector2Int> into)
        {
            int e = _canvas != null ? _canvas.Extent : int.MaxValue / 4;
            int drMin = Mathf.Max(-radius, -e - r), drMax = Mathf.Min(radius, e - r);
            int dqMin = Mathf.Max(-radius, -e - q), dqMax = Mathf.Min(radius, e - q);
            for (int dr = drMin; dr <= drMax; dr++)
            {
                for (int dq = dqMin; dq <= dqMax; dq++)
                {
                    if (HexLayout.Distance(0, 0, dq, dr) > radius)
                    {
                        continue;
                    }
                    Vector2Int c = new Vector2Int(q + dq, r + dr);
                    if (_canvas != null && !_canvas.InExtent(c.x, c.y))
                    {
                        continue;
                    }
                    into.Add(c);
                    if (mirror && (c.x != 0 || c.y != 0))
                    {
                        into.Add(-c);
                    }
                }
            }
        }

        // Circle radius for a drag from centre to end, capped at the distance past which a
        // bigger disc can't reach any more of the extent (centre's own distance + extent), so
        // a drag far off the canvas can't ask for a huge loop.
        private int CircleRadius(Vector2Int centre, Vector2Int end)
        {
            int radius = HexLayout.Distance(centre.x, centre.y, end.x, end.y);
            int extent = _canvas != null ? _canvas.Extent : radius;
            return Mathf.Min(radius, HexLayout.Distance(0, 0, centre.x, centre.y) + extent);
        }

        private readonly List<Vector2Int> _lineCells = new List<Vector2Int>();

        public void ShapeFootprint(HexShape shape, Vector2Int start, Vector2Int end, HashSet<Vector2Int> into)
        {
            if (shape == HexShape.Line)
            {
                _lineCells.Clear();
                HexCanvas.HexLine(start, end, _lineCells);
                foreach (Vector2Int c in _lineCells)
                {
                    BrushFootprint(c.x, c.y, into);
                }
            }
            else if (shape == HexShape.Circle)
            {
                DiscFootprint(start.x, start.y, CircleRadius(start, end), MirrorActive, into);
            }
        }

        // One shape = one undo step: exactly the ShapeFootprint cells (mirrors already in
        // it), each through PaintOne, the same per-cell paint the brush uses.
        public bool ShapeCommit(HexShape shape, Vector2Int start, Vector2Int end, bool erase)
        {
            if (_map == null || _canvas == null || shape == HexShape.None)
            {
                return false;
            }
            HashSet<Vector2Int> cells = new HashSet<Vector2Int>();
            ShapeFootprint(shape, start, end, cells);
            string what = shape == HexShape.Line ? Loc.T("gravebuilder.history.line", "Line") : Loc.T("gravebuilder.history.circle", "Circle");
            BeginAction(erase ? Loc.F("gravebuilder.history.shape_erase", "{shape} erase", ("shape", what)) : what);
            bool changed = false;
            foreach (Vector2Int c in cells)
            {
                changed |= PaintOne(c.x, c.y, erase);
            }
            CommitAction();
            string mirrored = MirrorActive ? Loc.T("gravebuilder.status.shape_mirrored_note", ", mirrored") : "";
            if (changed)
            {
                UpdateStatsNow();
                SetStatus(shape == HexShape.Line
                    ? (erase
                        ? Loc.F("gravebuilder.status.line_erased", "Erased a line from Q {q1} R {r1} to Q {q2} R {r2} ({n} cells{mirrored}, one undo step).", ("q1", start.x), ("r1", start.y), ("q2", end.x), ("r2", end.y), ("n", cells.Count), ("mirrored", mirrored))
                        : Loc.F("gravebuilder.status.line_drawn", "Drew a line from Q {q1} R {r1} to Q {q2} R {r2} ({n} cells{mirrored}, one undo step).", ("q1", start.x), ("r1", start.y), ("q2", end.x), ("r2", end.y), ("n", cells.Count), ("mirrored", mirrored)))
                    : (erase
                        ? Loc.F("gravebuilder.status.circle_erased", "Erased a circle of radius {radius} at Q {q} R {r} ({n} cells{mirrored}, one undo step).", ("radius", CircleRadius(start, end)), ("q", start.x), ("r", start.y), ("n", cells.Count), ("mirrored", mirrored))
                        : Loc.F("gravebuilder.status.circle_drawn", "Drew a circle of radius {radius} at Q {q} R {r} ({n} cells{mirrored}, one undo step).", ("radius", CircleRadius(start, end)), ("q", start.x), ("r", start.y), ("n", cells.Count), ("mirrored", mirrored))));
            }
            else
            {
                SetStatus(Loc.F("gravebuilder.status.shape_unchanged", "Nothing changed: the {shape} matches what's already there.", ("shape", what.ToLowerInvariant())));
            }
            return changed;
        }

        // Ghost fill: the selected tool's colour (substrate layer, or a special tile's marker
        // colour), null for an erasing shape or the ERASE tool.
        public Color? ShapePaintColour(bool erase)
        {
            if (erase || _tool == Erase)
            {
                return null;
            }
            return _tool == Substrate ? StructureSilhouette.LayerColour(_layer) : MarkerColour(_tool);
        }

        private void ToggleMirror()
        {
            _mirror = !_mirror;
            RefreshToggles();
            // The brush-footprint preview includes the mirrored cells.
            _canvas?.Repaint();
            SetStatus(_mirror ? Loc.T("gravebuilder.status.mirror_on", "Mirror on: every edit is repeated at (-q, -r).") : Loc.T("gravebuilder.status.mirror_off", "Mirror off."));
        }

        // ---- selection & clipboard ----------------------------------------------------
        // SELECT is a toggle like FILL MODE/MIRROR; PASTE is a toggle too (it reads CANCEL
        // PASTE, accent-coloured and pulsing, while placement is active). Canvas input precedence is
        // paste placement > LINE/CIRCLE > SELECT > fill > brush (HexCanvas.OnPointerDown).
        //
        // Clipboard anchor: the selection's centre, i.e. the rounded mean of its axial q and r,
        // so the ghost and the stamp sit centred under the cursor. Each entry is
        // (cell - anchor, state); a stamp writes state at (cursor cell + offset). Axial
        // translation is exact on a hex grid, so the pattern lands with the same shape.

        private void RefreshClipboardButtons()
        {
            if (_selectRow != null)
            {
                _selectRow.Clear();
                VisualElement row = PairRow(
                    AttachTooltip(
                        ActionBtn(_selectMode ? Loc.T("gravebuilder.tools.select_on", "SELECT: ON") : Loc.T("gravebuilder.tools.select_off", "SELECT: OFF"), ToggleSelect, family: _selectMode ? Accent : Neutral, pulse: _selectMode),
                        Loc.T("gravebuilder.tools.select_tooltip", "When on, left-drag adds cells to the selection and right-drag removes them (brush size applies). Painting is paused while it's on.")),
                    AttachTooltip(
                        ActionBtn(_selection.Count > 0 ? Loc.F("gravebuilder.tools.clear_selection_count", "CLEAR SELECTION ({n})", ("n", _selection.Count)) : Loc.T("gravebuilder.tools.clear_selection", "CLEAR SELECTION"), ClearSelection, enabled: _selection.Count > 0),
                        Loc.T("gravebuilder.tools.clear_selection_tooltip", "Deselects every cell. The map itself isn't changed.")));
                row.style.flexGrow = 1f;
                _selectRow.Add(row);
            }
            if (_loopRow != null)
            {
                _loopRow.Clear();
                _loopRow.Add(AttachTooltip(
                    ActionBtn(_autoFillLoops ? Loc.T("gravebuilder.tools.auto_fill_loops_on", "AUTO-FILL LOOPS: ON") : Loc.T("gravebuilder.tools.auto_fill_loops_off", "AUTO-FILL LOOPS: OFF"), ToggleAutoFillLoops, family: _autoFillLoops ? Accent : Neutral, pulse: _autoFillLoops),
                    Loc.T("gravebuilder.tools.auto_fill_loops_tooltip", "When on, each time you finish a SELECT left-drag, any cells the selection completely surrounds are selected too, so drawing a closed outline selects its inside. Right-drags (removing) never auto-fill.")));
            }
            if (_clipRow != null)
            {
                _clipRow.Clear();
                bool any = _selection.Count > 0;
                _clipRow.Add(PairRow(
                    AttachTooltip(
                        ActionBtn(Loc.T("gravebuilder.tools.copy", "COPY"), Copy, enabled: any),
                        Loc.F("gravebuilder.tools.copy_tooltip", "Copies every selected cell to the clipboard, empty ones included (those erase when pasted). The map isn't changed. Shortcut: {combo}.",
                            ("combo", ShortcutBindings.ComboText(ShortcutBindings.Copy) ?? Loc.T("gravebuilder.shortcuts.unbound_note", "not currently bound")))),
                    AttachTooltip(
                        ActionBtn(Loc.T("gravebuilder.tools.cut", "CUT"), Cut, enabled: any),
                        Loc.F("gravebuilder.tools.cut_tooltip", "Copies every selected cell to the clipboard, empty ones included, then erases them from the map (one undo step). Shortcut: {combo}.",
                            ("combo", ShortcutBindings.ComboText(ShortcutBindings.Cut) ?? Loc.T("gravebuilder.shortcuts.unbound_note", "not currently bound"))))));
                bool placing = PasteModeActive;
                string pasteLabel = placing
                    ? Loc.T("gravebuilder.tools.cancel_paste", "CANCEL PASTE")
                    : (_clipboard.Count > 0 ? Loc.F("gravebuilder.tools.paste_count", "PASTE ({n} CELLS)", ("n", _clipboard.Count)) : Loc.T("gravebuilder.tools.paste", "PASTE"));
                _clipRow.Add(AttachTooltip(
                    ActionBtn(pasteLabel, TogglePaste, enabled: placing || _clipboard.Count > 0, family: placing ? Accent : (Color?)null, pulse: placing),
                    Loc.F("gravebuilder.tools.paste_tooltip", "Each left click on the map stamps the clipboard centred on the cursor (empty cells erase) and stays ready for another. Right-click or CANCEL PASTE stops. Shortcut: {combo}.",
                        ("combo", ShortcutBindings.ComboText(ShortcutBindings.Paste) ?? Loc.T("gravebuilder.shortcuts.unbound_note", "not currently bound")))));
            }
            // The PARTS tab's save button follows the clipboard too (every clipboard change ends here).
            RefreshPartSaveButton();
        }

        private void ToggleSelect()
        {
            _selectMode = !_selectMode;
            bool cancelled = false;
            if (_selectMode && _pasteMode)
            {
                // Paste placement would otherwise win every click and SELECT would seem dead.
                _pasteMode = false;
                cancelled = true;
            }
            // LINE/CIRCLE would win every click over SELECT (see "line & circle tools").
            string off = _selectMode ? TurnOffShapeTools() : null;
            if (off != null)
            {
                RefreshToggles();
            }
            // Paste ghost gone, and/or the brush-footprint preview changed (SELECT overrides
            // fill mode, and its footprint has no mirror).
            _canvas?.Repaint();
            RefreshClipboardButtons();
            SetStatus(_selectMode
                ? Loc.F("gravebuilder.status.select_on", "Select on: left-drag adds cells to the selection, right-drag removes them; painting is paused.{cancelled}{note}", ("cancelled", cancelled ? Loc.T("gravebuilder.status.select_on_paste_cancelled", " Paste placement cancelled.") : ""), ("note", Suffix(off)))
                : Loc.T("gravebuilder.status.select_off", "Select off: brush painting. The selection is kept for COPY/CUT."));
        }

        private void ClearSelection()
        {
            int n = _selection.Count;
            _selection.Clear();
            _canvas?.Repaint();
            RefreshClipboardButtons();
            SetStatus(n > 0 ? Loc.F("gravebuilder.status.selection_cleared", "Cleared the selection ({n} cells).", ("n", n)) : Loc.T("gravebuilder.status.selection_clear_nothing", "Nothing is selected."));
        }

        // Snapshot every selected cell (inside the extent), empty ones included. Returns the
        // number of cells copied (0 = nothing selected, clipboard untouched).
        private int CopySelection()
        {
            List<Vector2Int> cells = _selection.Where(c => _canvas.InExtent(c.x, c.y)).ToList();
            if (cells.Count == 0)
            {
                return 0;
            }
            Vector2Int anchor = new Vector2Int(
                Mathf.RoundToInt((float)cells.Average(c => c.x)),
                Mathf.RoundToInt((float)cells.Average(c => c.y)));
            _clipboard.Clear();
            foreach (Vector2Int c in cells)
            {
                _clipboard.Add(new ClipCell { Offset = c - anchor, Cell = CellAt(c) });
            }
            return cells.Count;
        }

        private void Copy()
        {
            if (_map == null || _canvas == null)
            {
                return;
            }
            CommitAction();
            int n = CopySelection();
            if (n == 0)
            {
                SetStatus(Loc.T("gravebuilder.status.copy_nothing", "Nothing selected to copy. Turn SELECT on and drag over the cells first."), error: true);
                return;
            }
            int empty = _clipboard.Count(c => !c.Cell.HasValue);
            RefreshClipboardButtons();
            SetStatus(Loc.F("gravebuilder.status.copied", "Copied {n} cells{empty_note}. Press PASTE to stamp them.", ("n", n), ("empty_note", empty > 0 ? Loc.F("gravebuilder.status.copied_empty_note", " ({empty} empty, which erase when pasted)", ("empty", empty)) : "")));
        }

        // Copy, then erase every selected cell as one undoable action, then clear the selection.
        private void Cut()
        {
            if (_map == null || _canvas == null)
            {
                return;
            }
            CommitAction();
            int n = CopySelection();
            if (n == 0)
            {
                SetStatus(Loc.T("gravebuilder.status.cut_nothing", "Nothing selected to cut. Turn SELECT on and drag over the cells first."), error: true);
                return;
            }
            // Same erase path as the brush (SandboxMap.Paint "erase"), no mirror: Cut acts on
            // exactly the cells it copied.
            BeginAction(Loc.T("gravebuilder.history.cut", "Cut"));
            foreach (Vector2Int c in _selection)
            {
                if (_canvas.InExtent(c.x, c.y))
                {
                    Touch(c);
                    _map.Paint(c.x, c.y, Erase);
                }
            }
            CommitAction();
            _selection.Clear();
            _canvas.Refresh();
            UpdateStatsNow();
            RefreshClipboardButtons();
            SetStatus(Loc.F("gravebuilder.status.cut", "Cut {n} cells to the clipboard (one undo step). Press PASTE to stamp them.", ("n", n)));
        }

        private void TogglePaste()
        {
            CommitAction();
            if (PasteModeActive)
            {
                CancelPaste();
                return;
            }
            if (_clipboard.Count == 0)
            {
                SetStatus(Loc.T("gravebuilder.status.paste_clipboard_empty", "The clipboard is empty. Select some cells and press COPY or CUT first."), error: true);
                return;
            }
            _pasteMode = true;
            _canvas?.Repaint();
            RefreshClipboardButtons();
            SetStatus(Loc.F("gravebuilder.status.paste_ready", "Paste: left-click stamps the {n}-cell clipboard centred on the cursor (each stamp is its own undo step). Right-click or CANCEL PASTE stops.", ("n", _clipboard.Count)));
        }

        // ---- tools & painting ---------------------------------------------------------

        private static string SubstrateKey(int layer) => Substrate + "_" + layer;

        private void SelectTool(string tool, int layer)
        {
            _tool = tool;
            if (tool == Substrate)
            {
                _layer = Mathf.Clamp(layer, 1, 5);
            }
            RefreshToolButtons();
        }

        private void RefreshToolButtons()
        {
            string selectedKey = _tool == Substrate ? SubstrateKey(_layer) : _tool;
            foreach (KeyValuePair<string, VisualElement> kv in _toolHolders)
            {
                TerminalUi.Border(kv.Value, 2f, kv.Key == selectedKey ? SelectedEdge : Color.clear);
            }
        }

        // IHexCanvasHost ------------------------------------------------------------------

        public SandboxMap Map => _map;

        public bool FillMode => FillActive;

        public bool SelectMode => SelectActive;

        public IEnumerable<Vector2Int> SelectedCells => _selection;

        public bool PasteActive => PasteModeActive;

        public IReadOnlyList<ClipCell> Clipboard => _clipboard;

        // Brush-footprint preview: on whenever a click would run a brush-radius action, i.e.
        // a brush stroke (any palette tool) or a SELECT drag, mirroring the canvas's dispatch
        // precedence (paste > SELECT > fill > brush): never during paste placement (it has its
        // own ghost), never in fill mode unless SELECT overrides it. LINE uses the brush, so
        // its hover shows the footprint (a drag swaps it for the line ghost); CIRCLE ignores
        // brush size, so it shows none.
        public bool BrushPreviewActive => !PasteModeActive && !CircleActive && (SelectActive || LineActive || !FillActive);

        // Exactly the cells a click at (q, r) would touch: the same hex-radius loop as
        // StrokePaint/SelectPaint (DiscFootprint at _brush), plus the (-q, -r) mirrors when
        // painting with MIRROR on (PaintWithMirror; SELECT never mirrors). Cells outside the
        // extent are left out, as PaintOne/SelectPaint skip them.
        public void BrushFootprint(int q, int r, HashSet<Vector2Int> into)
        {
            DiscFootprint(q, r, _brush, MirrorActive && !SelectActive, into);
        }

        // One select-drag sample: every cell within the brush radius of (q, r), no mirror
        // (the selection is not an edit). Not recorded in undo.
        public bool SelectPaint(int q, int r, bool remove)
        {
            if (_canvas == null)
            {
                return false;
            }
            bool changed = false;
            int b = _brush;
            for (int dr = -b; dr <= b; dr++)
            {
                for (int dq = -b; dq <= b; dq++)
                {
                    if (HexLayout.Distance(0, 0, dq, dr) > b)
                    {
                        continue;
                    }
                    Vector2Int c = new Vector2Int(q + dq, r + dr);
                    if (!_canvas.InExtent(c.x, c.y))
                    {
                        continue;
                    }
                    changed |= remove ? _selection.Remove(c) : _selection.Add(c);
                }
            }
            return changed;
        }

        public void SelectEnd(bool removed)
        {
            // Only after an add-drag: a remove-drag that carves a hole out of a closed shape
            // must not have the hole re-selected straight away.
            int filled = !removed && AutoFillLoopsActive ? FillEnclosedSelection() : 0;
            if (filled > 0)
            {
                _canvas?.Repaint();
            }
            RefreshClipboardButtons();
            SetStatus(_selection.Count > 0
                ? Loc.F("gravebuilder.status.selection_count", "{n} cells selected{filled_note}. COPY or CUT them, or keep dragging to adjust.", ("n", _selection.Count), ("filled_note", filled > 0 ? Loc.F("gravebuilder.status.selection_filled_note", " ({filled} enclosed cells auto-filled)", ("filled", filled)) : ""))
                : Loc.T("gravebuilder.status.selection_none", "Nothing selected."));
        }

        private void ToggleAutoFillLoops()
        {
            _autoFillLoops = !_autoFillLoops;
            RefreshClipboardButtons();
            SetStatus(_autoFillLoops
                ? Loc.T("gravebuilder.status.auto_fill_loops_on", "Auto-fill loops on: finishing a SELECT left-drag also selects every cell the selection encloses.")
                : Loc.T("gravebuilder.status.auto_fill_loops_off", "Auto-fill loops off: SELECT selects exactly what you drag over."));
        }

        // Selects every unselected cell of the extent that the selection walls off from the
        // extent's outer edge. Flood fill ("outside") from every unselected cell on the edge
        // ring (distance == extent), stepping only through unselected cells via the same
        // HexCanvas.Neighbours adjacency and InExtent bound the map flood fill (Fill) uses;
        // whatever unselected cell of the extent it never reaches is enclosed. Iterative BFS
        // (a Queue, each cell enqueued at most once thanks to the visited set), so it's
        // O(cells in extent) time and memory: ~270k cells at the max radius 300, no recursion.
        // Returns how many cells were added.
        private int FillEnclosedSelection()
        {
            if (_canvas == null || _selection.Count == 0)
            {
                return 0;
            }
            int e = _canvas.Extent;
            HashSet<Vector2Int> outside = new HashSet<Vector2Int>();
            Queue<Vector2Int> frontier = new Queue<Vector2Int>();

            // Seeds: the edge ring, walked as the same bounded (q, r) square loop Draw uses.
            for (int r = -e; r <= e; r++)
            {
                for (int q = -e; q <= e; q++)
                {
                    if (HexLayout.Distance(0, 0, q, r) != e)
                    {
                        continue;
                    }
                    Vector2Int c = new Vector2Int(q, r);
                    if (!_selection.Contains(c) && outside.Add(c))
                    {
                        frontier.Enqueue(c);
                    }
                }
            }
            while (frontier.Count > 0)
            {
                Vector2Int c = frontier.Dequeue();
                foreach (Vector2Int d in HexCanvas.Neighbours)
                {
                    Vector2Int n = c + d;
                    if (!_canvas.InExtent(n.x, n.y) || _selection.Contains(n) || !outside.Add(n))
                    {
                        continue;
                    }
                    frontier.Enqueue(n);
                }
            }

            // Collected first, added after, so the scan never sees its own additions.
            List<Vector2Int> enclosed = new List<Vector2Int>();
            for (int r = -e; r <= e; r++)
            {
                for (int q = -e; q <= e; q++)
                {
                    if (HexLayout.Distance(0, 0, q, r) > e)
                    {
                        continue;
                    }
                    Vector2Int c = new Vector2Int(q, r);
                    if (!_selection.Contains(c) && !outside.Contains(c))
                    {
                        enclosed.Add(c);
                    }
                }
            }
            foreach (Vector2Int c in enclosed)
            {
                _selection.Add(c);
            }
            return enclosed.Count;
        }

        // One stamp: every clipboard entry at (q, r) + offset, empty entries erasing, as its
        // own undo step (BeginAction/CommitAction per click, never batched across clicks).
        // No mirror, and offsets outside the extent are skipped (as PaintOne does).
        public bool PasteAt(int q, int r)
        {
            if (_map == null || _canvas == null || !PasteModeActive)
            {
                return false;
            }
            BeginAction(Loc.T("gravebuilder.history.paste", "Paste"));
            bool changed = false;
            foreach (ClipCell cc in _clipboard)
            {
                int tq = q + cc.Offset.x;
                int tr = r + cc.Offset.y;
                if (!_canvas.InExtent(tq, tr))
                {
                    continue;
                }
                Touch(new Vector2Int(tq, tr));
                changed |= cc.Cell.HasValue
                    ? _map.Paint(tq, tr, cc.Cell.Value.tile ?? Substrate, cc.Cell.Value.layer)
                    : _map.Paint(tq, tr, Erase);
            }
            CommitAction();
            if (changed)
            {
                UpdateStatsNow();
                SetStatus(Loc.F("gravebuilder.status.pasted", "Pasted at Q {q} R {r} (one undo step). Click again for another copy; right-click or CANCEL PASTE stops.", ("q", q), ("r", r)));
            }
            else
            {
                SetStatus(Loc.F("gravebuilder.status.paste_unchanged", "Nothing changed at Q {q} R {r}: the area already matches the clipboard.", ("q", q), ("r", r)));
            }
            return changed;
        }

        public void CancelPaste()
        {
            bool was = _pasteMode;
            _pasteMode = false;
            _canvas?.Repaint();
            RefreshClipboardButtons();
            if (was)
            {
                SetStatus(Loc.T("gravebuilder.status.paste_cancelled", "Paste placement cancelled. The clipboard is kept."));
            }
        }

        public void StrokeBegin()
        {
            BeginAction(Loc.T("gravebuilder.history.brush_stroke", "Brush stroke"));
        }

        // One stroke sample: every cell within the brush radius of (q, r), plus mirrors.
        public bool StrokePaint(int q, int r, bool erase)
        {
            if (_map == null || _canvas == null)
            {
                return false;
            }
            if (_pending == null)
            {
                BeginAction(Loc.T("gravebuilder.history.brush_stroke", "Brush stroke"));
            }
            bool changed = false;
            int b = _brush;
            for (int dr = -b; dr <= b; dr++)
            {
                for (int dq = -b; dq <= b; dq++)
                {
                    if (HexLayout.Distance(0, 0, dq, dr) <= b)
                    {
                        changed |= PaintWithMirror(q + dq, r + dr, erase);
                    }
                }
            }
            if (changed)
            {
                _statsDirty = true;
            }
            return changed;
        }

        public void StrokeEnd()
        {
            CommitAction();
        }

        // BFS flood fill from (q, r) over every connected cell in the same exact state
        // (tile + layer, or "no cell"), bounded to the canvas extent.
        public bool Fill(int q, int r, bool erase)
        {
            if (_map == null || _canvas == null || !_canvas.InExtent(q, r))
            {
                return false;
            }
            Vector2Int start = new Vector2Int(q, r);
            SandboxMap.Cell? match = CellAt(start);

            List<Vector2Int> region = new List<Vector2Int>();
            HashSet<Vector2Int> seen = new HashSet<Vector2Int> { start };
            Queue<Vector2Int> frontier = new Queue<Vector2Int>();
            frontier.Enqueue(start);
            while (frontier.Count > 0)
            {
                Vector2Int c = frontier.Dequeue();
                region.Add(c);
                foreach (Vector2Int d in HexCanvas.Neighbours)
                {
                    Vector2Int n = c + d;
                    if (seen.Contains(n) || !_canvas.InExtent(n.x, n.y))
                    {
                        continue;
                    }
                    seen.Add(n);
                    if (SameCell(CellAt(n), match))
                    {
                        frontier.Enqueue(n);
                    }
                }
            }

            // Collected first, painted after, so painting can't change what "matches".
            BeginAction(erase ? Loc.T("gravebuilder.history.flood_erase", "Flood erase") : Loc.T("gravebuilder.history.flood_fill", "Flood fill"));
            bool changed = false;
            foreach (Vector2Int c in region)
            {
                changed |= PaintWithMirror(c.x, c.y, erase);
            }
            CommitAction();
            if (changed)
            {
                UpdateStatsNow();
                SetStatus(erase
                    ? Loc.F("gravebuilder.status.flood_erased", "Flood-erased a region of {n} cells{mirrored}.", ("n", region.Count), ("mirrored", MirrorActive ? Loc.T("gravebuilder.status.flood_mirrored_note", " (mirrored)") : ""))
                    : Loc.F("gravebuilder.status.flood_filled", "Flood-filled a region of {n} cells{mirrored}.", ("n", region.Count), ("mirrored", MirrorActive ? Loc.T("gravebuilder.status.flood_mirrored_note", " (mirrored)") : "")));
            }
            return changed;
        }

        public void Hover(int q, int r)
        {
            if (_hoverLabel == null || _map == null)
            {
                return;
            }
            // Was (cell.tile ?? Substrate).ToUpperInvariant(), which showed the raw snake_case
            // tile key with its underscore intact (e.g. "SPECIAL_1") — the exact complaint this
            // pass is fixing. TileLabel resolves it to the game's own tile name first.
            string what = _map.cells.TryGetValue(new Vector2Int(q, r), out SandboxMap.Cell cell)
                ? Loc.F("gravebuilder.screen.hover_cell_contents", "{colour} · {tile}", ("colour", LayerColourName(cell.layer).ToUpperInvariant()), ("tile", TileLabel(cell.tile).ToUpperInvariant()))
                : Loc.T("gravebuilder.screen.hover_cell_empty", "EMPTY");
            RetextTracked(_hoverLabel, Loc.F("gravebuilder.screen.hover_cell", "CELL  Q {q}  R {r}  ·  {what}", ("q", q), ("r", r), ("what", what)), 2f);
        }

        // Plain display name for a cell's tile key, matching the game's own tile "label" field
        // (gamedata_tiles.json) rather than the raw snake_case id.
        private static string TileLabel(string tile)
        {
            string t = tile ?? Substrate;
            if (t == Substrate)
            {
                return Loc.T("gravebuilder.tiles.substrate", "Substrate");
            }
            return SpecialTileLabel.TryGetValue(t, out string label) ? label : t;
        }

        // Marker colours. The four shard tiles use the game's own real shard colours
        // (ColourTokens.GoldColor/PinkColor/BlueColor/PurpleColor, matched via
        // ColourTokens.TokenKeyOfTile — verified 2026-11-22) instead of invented ones, so the
        // palette matches what these actually look like elsewhere in the game (wallet icons,
        // the Mods screen). The 4 enemy-encounter tiles have no real 2D colour in the game
        // (they're hidden markers, not a drawn tile type), so those stay UI-only choices.
        public Color MarkerColour(string tile)
        {
            switch (tile)
            {
                case "explosive": return new Color(1f, 0.33f, 0.16f);
                case "lightning": return new Color(1f, 0.92f, 0.25f);
                case "void": return new Color(0.05f, 0.05f, 0.05f);
                case "swarm": return new Color(0.36f, 0.92f, 0.52f);
                case "special_1": return ColourTokens.GoldColor;
                case "special_2": return ColourTokens.PinkColor;
                case "special_3": return ColourTokens.BlueColor;
                case "meta_1": return ColourTokens.PurpleColor;
                default: return new Color(0.6f, 0.6f, 0.6f);
            }
        }

        private bool PaintWithMirror(int q, int r, bool erase)
        {
            bool changed = PaintOne(q, r, erase);
            if (MirrorActive && (q != 0 || r != 0))
            {
                changed |= PaintOne(-q, -r, erase);
            }
            return changed;
        }

        // Apply the current tool to one cell inside the extent, recording its prior state.
        private bool PaintOne(int q, int r, bool erase)
        {
            if (!_canvas.InExtent(q, r))
            {
                return false;
            }
            Touch(new Vector2Int(q, r));
            if (erase || _tool == Erase)
            {
                return _map.Paint(q, r, Erase);
            }
            if (_tool == Substrate)
            {
                return _map.Paint(q, r, Substrate, _layer);
            }
            // Special tile: layer -1 keeps an existing cell's layer (new cells get layer 1).
            return _map.Paint(q, r, _tool, -1);
        }

        private static SandboxMap.Cell? CellAt(Vector2Int pos)
        {
            return _map.cells.TryGetValue(pos, out SandboxMap.Cell c) ? c : (SandboxMap.Cell?)null;
        }

        private static bool SameCell(SandboxMap.Cell? a, SandboxMap.Cell? b)
        {
            if (!a.HasValue || !b.HasValue)
            {
                return a.HasValue == b.HasValue;
            }
            return a.Value.layer == b.Value.layer && (a.Value.tile ?? Substrate) == (b.Value.tile ?? Substrate);
        }

        // ---- undo / redo --------------------------------------------------------------

        private void BeginAction(string label)
        {
            CommitAction();
            _pending = new Dictionary<Vector2Int, SandboxMap.Cell?>();
            _pendingLabel = label;
            _pendingRadius = _map != null ? _map.radius : 0;
        }

        private void Touch(Vector2Int pos)
        {
            if (_pending != null && !_pending.ContainsKey(pos))
            {
                _pending[pos] = CellAt(pos);
            }
        }

        // Close the open action: keep only cells that really changed (before != after), push
        // it if anything did, and clear redo (a new edit forks the history).
        private void CommitAction()
        {
            if (_pending == null)
            {
                return;
            }
            Dictionary<Vector2Int, SandboxMap.Cell?> pending = _pending;
            _pending = null;
            if (_map == null)
            {
                return;
            }
            Edit edit = new Edit
            {
                Label = _pendingLabel,
                Cells = new List<(Vector2Int, SandboxMap.Cell?, SandboxMap.Cell?)>(),
                RadiusBefore = _pendingRadius,
                RadiusAfter = _map.radius
            };
            foreach (KeyValuePair<Vector2Int, SandboxMap.Cell?> kv in pending)
            {
                SandboxMap.Cell? after = CellAt(kv.Key);
                if (!SameCell(kv.Value, after))
                {
                    edit.Cells.Add((kv.Key, kv.Value, after));
                }
            }
            if (edit.Cells.Count == 0 && edit.RadiusBefore == edit.RadiusAfter)
            {
                return;
            }
            _undo.Add(edit);
            _redo.Clear();
            TrimHistory();
            RefreshHistoryButtons();
        }

        private static void TrimHistory()
        {
            while (_undo.Count > MaxHistory)
            {
                _undo.RemoveAt(0);
            }
            int total = _undo.Sum(e => e.Cells.Count) + _redo.Sum(e => e.Cells.Count);
            while (total > MaxHistoryCells && _undo.Count > 1)
            {
                total -= _undo[0].Cells.Count;
                _undo.RemoveAt(0);
            }
        }

        private void Undo()
        {
            CommitAction();
            if (_undo.Count == 0)
            {
                SetStatus(Loc.T("gravebuilder.status.undo_nothing", "Nothing to undo."));
                return;
            }
            Edit edit = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            ApplyEdit(edit, after: false);
            _redo.Add(edit);
            AfterHistoryMove(Loc.F("gravebuilder.status.undid", "Undid: {action} ({n} cells).", ("action", edit.Label), ("n", edit.Cells.Count)));
        }

        private void Redo()
        {
            CommitAction();
            if (_redo.Count == 0)
            {
                SetStatus(Loc.T("gravebuilder.status.redo_nothing", "Nothing to redo."));
                return;
            }
            Edit edit = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            ApplyEdit(edit, after: true);
            _undo.Add(edit);
            AfterHistoryMove(Loc.F("gravebuilder.status.redid", "Redid: {action} ({n} cells).", ("action", edit.Label), ("n", edit.Cells.Count)));
        }

        // Restore the exact recorded states (not a replay of the original tool), so redo is
        // correct whatever happened in between.
        private static void ApplyEdit(Edit edit, bool after)
        {
            foreach ((Vector2Int pos, SandboxMap.Cell? before, SandboxMap.Cell? afterState) in edit.Cells)
            {
                SandboxMap.Cell? target = after ? afterState : before;
                if (target.HasValue)
                {
                    _map.cells[pos] = target.Value;
                }
                else
                {
                    _map.cells.Remove(pos);
                }
            }
            _map.radius = after ? edit.RadiusAfter : edit.RadiusBefore;
        }

        private void AfterHistoryMove(string message)
        {
            _canvas?.Refresh();
            UpdateStatsNow();
            RefreshHistoryButtons();
            SetStatus(message);
        }

        // ---- stats & status -----------------------------------------------------------

        private void UpdateStatsNow()
        {
            _statsDirty = false;
            if (_map == null || _statValues.Count == 0)
            {
                return;
            }
            Dictionary<int, int> counts = _map.LayerCellCounts();
            SetStat("cells", _map.cells.Count.ToString());
            SetStat("radius", _map.radius.ToString());
            for (int l = 1; l <= 5; l++)
            {
                SetStat("L" + l, (counts.TryGetValue(l, out int n) ? n : 0).ToString());
            }
            SetStat("specials", _map.cells.Values.Count(c => (c.tile ?? Substrate) != Substrate).ToString());
        }

        private void SetStat(string id, string value)
        {
            if (_statValues.TryGetValue(id, out Label label) && label != null)
            {
                RetextTracked(label, value, 1f);
            }
        }

        private void SetStatus(string message, bool error = false, bool log = true)
        {
            if (log)
            {
                if (error)
                {
                    Plugin.Log.LogWarning("MapBuilder: " + message);
                }
                else
                {
                    Plugin.Log.LogInfo("MapBuilder: " + message);
                }
            }
            _lastStatus = message;
            _lastStatusError = error;
            if (_statusHost != null)
            {
                _statusHost.Clear();
                MetaUi.Callout(_statusHost, message, error ? MetaUi.Tone.Danger : MetaUi.Tone.Rich);
                // It floats over the canvas; keep every part of it (whatever picking mode
                // Callout gave its icon) from blocking painting.
                IgnorePicking(_statusHost);
            }
        }

        // ---- map actions --------------------------------------------------------------

        // Every whole-map change (New, Load override, Load structure) goes through here, as
        // one undoable action: the diff covers every cell of the old and new maps.
        private void ReplaceMap(SandboxMap map, string what)
        {
            BeginAction(what);
            foreach (Vector2Int k in _map.cells.Keys)
            {
                Touch(k);
            }
            foreach (Vector2Int k in map.cells.Keys)
            {
                Touch(k);
            }
            _map = map;
            CommitAction();
            _canvas?.Refresh();
            UpdateStatsNow();
            SetStatus(Loc.F("gravebuilder.status.map_replaced", "{what}: {n} cells (radius {radius}).", ("what", what), ("n", _map.cells.Count), ("radius", _map.radius)));
        }

        private bool ReadRadius(out int radius)
        {
            if (!int.TryParse((_radiusText ?? "").Trim(), out radius))
            {
                SetStatus(Loc.T("gravebuilder.status.radius_invalid", "Radius must be a whole number from 1 to 300."), error: true);
                return false;
            }
            int clamped = Mathf.Clamp(radius, MinRadius, MaxRadius);
            if (clamped != radius)
            {
                radius = clamped;
                _radiusText = radius.ToString();
                _radiusField?.SetValueWithoutNotify(_radiusText);
            }
            return true;
        }

        private void NewMap(bool filled)
        {
            if (!ReadRadius(out int radius))
            {
                return;
            }
            CreateMap(filled, radius);
        }

        // NEW (FILLED)/NEW (EMPTY), radius from the field.
        private void CreateMap(bool filled, int radius)
        {
            SandboxMap map;
            if (filled)
            {
                map = SandboxMap.NewFilled(radius);
                // NewFilled floors its radius at 24; use the number actually asked for so a
                // small map fills the canvas.
                map.radius = radius;
            }
            else
            {
                map = SandboxMap.NewEmpty(radius);
            }
            ReplaceMap(map, filled ? Loc.T("gravebuilder.history.new_filled_map", "New filled map") : Loc.T("gravebuilder.history.new_empty_map", "New empty map"));
        }

        // Non-destructive: only extends the paintable extent, never touches cells.
        private void GrowToRadius()
        {
            if (!ReadRadius(out int radius))
            {
                return;
            }
            if (radius <= _map.radius)
            {
                SetStatus(Loc.F("gravebuilder.status.grow_unchanged", "Nothing changed: {radius} is not larger than the current radius ({current}). Grow only extends the map; use NEW for a smaller one.", ("radius", radius), ("current", _map.radius)));
                return;
            }
            int old = _map.radius;
            BeginAction(Loc.T("gravebuilder.history.grow_radius", "Grow radius"));
            _map.radius = radius;
            CommitAction();
            _canvas?.Refresh();
            UpdateStatsNow();
            SetStatus(Loc.F("gravebuilder.status.grew", "Grew the radius from {old} to {radius}; painted cells are unchanged.", ("old", old), ("radius", radius)));
        }

        // ---- image import (LOAD IMAGE) ------------------------------------------------
        // In-game port of tools/image_to_structure.py (palette mode is the one proven on the
        // smiley test), with the same tunables. That script stays as-is for exporting
        // standalone contracts.

        // ReadRadius's idiom for an int field: parse, clamp, write the clamped value back.
        private bool ReadClampedInt(string what, ref string text, TextField field, int min, int max, out int value)
        {
            if (!int.TryParse((text ?? "").Trim(), out value))
            {
                SetStatus(Loc.F("gravebuilder.status.int_field_invalid", "{what} must be a whole number from {min} to {max}.", ("what", what), ("min", min), ("max", max)), error: true);
                return false;
            }
            int clamped = Mathf.Clamp(value, min, max);
            if (clamped != value)
            {
                value = clamped;
                text = value.ToString();
                field?.SetValueWithoutNotify(text);
            }
            return true;
        }

        private void LoadImage()
        {
            if (!ReadClampedInt(Loc.T("gravebuilder.image.status_size_field_name", "Max grid size"), ref _imageSizeText, _imageSizeField, MinRadius, MaxImageSize, out int size)
                || !ReadClampedInt(Loc.T("gravebuilder.image.status_alpha_field_name", "Alpha threshold"), ref _imageAlphaText, _imageAlphaField, 0, 255, out int alphaThreshold))
            {
                return;
            }
            // Explorer's Copy as path wraps the path in quotes.
            string path = (_imagePathText ?? "").Trim().Trim('"').Trim();
            if (path.Length == 0)
            {
                SetStatus(Loc.T("gravebuilder.image.status_no_path", "Type the path of a PNG or JPEG file first."), error: true);
                return;
            }
            if (!File.Exists(path))
            {
                SetStatus(Loc.F("gravebuilder.image.status_no_file", "No file at '{path}'.", ("path", path)), error: true);
                return;
            }
            Texture2D tex = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes))
                {
                    SetStatus(Loc.F("gravebuilder.image.status_unreadable", "'{file}' couldn't be read as an image. PNG and JPEG are supported.", ("file", Path.GetFileName(path))), error: true);
                    return;
                }
                SandboxMap map = ImageToMap(tex, size, alphaThreshold, _imageLuminance, _imageInvert, _imageDither);
                if (map == null)
                {
                    SetStatus(Loc.F("gravebuilder.image.status_no_cells", "'{file}' produced no cells: every pixel is below the alpha threshold ({threshold}). Lower it to keep more.", ("file", Path.GetFileName(path)), ("threshold", alphaThreshold)), error: true);
                    return;
                }
                ReplaceMap(map, _imageLuminance
                    ? Loc.F("gravebuilder.history.loaded_image_luminance", "Loaded image (luminance{inverted})", ("inverted", _imageInvert ? Loc.T("gravebuilder.history.loaded_image_inverted", ", inverted") : ""))
                    : Loc.F("gravebuilder.history.loaded_image_palette", "Loaded image (palette, dither {dither})", ("dither", _imageDither ? Loc.T("gravebuilder.history.loaded_image_dither_on", "on") : Loc.T("gravebuilder.history.loaded_image_dither_off", "off"))));
                CloseLibrary();
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.image.status_load_failed", "Loading the image failed: {error}", ("error", e.Message)), error: true);
            }
            finally
            {
                if (tex != null)
                {
                    Destroy(tex);
                }
            }
        }

        // Null if every pixel is a hole.
        private static SandboxMap ImageToMap(Texture2D tex, int size, int alphaThreshold, bool luminance, bool invert, bool dither)
        {
            int w = tex.width;
            int h = tex.height;
            // A column step is HEX_W wide but a row step only HEX_ROW tall, so a 1:1 pixel
            // mapping squashes the picture; solve for the grid whose on-screen aspect matches
            // the image, with the longer side (in hex units) at size.
            float scale = size / Mathf.Max(w / HexLayout.HEX_W, h / HexLayout.HEX_ROW);
            int gw = Mathf.Max(1, Mathf.RoundToInt(scale * w / HexLayout.HEX_W));
            int gh = Mathf.Max(1, Mathf.RoundToInt(scale * h / HexLayout.HEX_ROW));

            // One bilinear tap per cell aliases badly when a big photo shrinks to ~140 cells
            // (PIL's LANCZOS in the Python tool averages instead), so average up to 4x4 taps
            // over the cell's footprint. Colour is alpha-weighted so transparent pixels' RGB
            // (often black) doesn't darken the edges of a cutout.
            tex.wrapMode = TextureWrapMode.Clamp;
            int taps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max((float)w / gw, (float)h / gh)), 1, 4);
            float[,,] rgb = new float[gh, gw, 3];
            bool[,] solid = new bool[gh, gw];
            for (int row = 0; row < gh; row++)
            {
                for (int col = 0; col < gw; col++)
                {
                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    for (int ty = 0; ty < taps; ty++)
                    {
                        for (int tx = 0; tx < taps; tx++)
                        {
                            float u = (col + (tx + 0.5f) / taps) / gw;
                            // Texture V runs bottom-up; row 0 is the image's top.
                            float v = 1f - (row + (ty + 0.5f) / taps) / gh;
                            Color c = tex.GetPixelBilinear(u, v);
                            r += c.r * c.a;
                            g += c.g * c.a;
                            b += c.b * c.a;
                            a += c.a;
                        }
                    }
                    // Compared on the 0-255 scale like the script's integer alpha, so a
                    // threshold of 0 keeps even fully transparent pixels.
                    if (a / (taps * taps) * 255f < alphaThreshold)
                    {
                        continue;
                    }
                    solid[row, col] = true;
                    // a is 0 only at threshold 0 on a fully transparent spot: no colour, black.
                    float inv = a > 0f ? 255f / a : 0f;
                    rgb[row, col, 0] = r * inv;
                    rgb[row, col, 1] = g * inv;
                    rgb[row, col, 2] = b * inv;
                }
            }

            // 0 = hole, else 1-5.
            int[,] layers = luminance
                ? LuminanceLayers(rgb, solid, gw, gh, invert)
                : PaletteLayers(rgb, solid, gw, gh, dither);

            List<(Vector2Int cell, byte layer)> placed = new List<(Vector2Int, byte)>();
            int qMin = int.MaxValue, qMax = int.MinValue, rMin = int.MaxValue, rMax = int.MinValue;
            for (int row = 0; row < gh; row++)
            {
                for (int col = 0; col < gw; col++)
                {
                    if (layers[row, col] == 0)
                    {
                        continue;
                    }
                    Vector2Int axial = HexLayout.OffsetToAxial(col, row);
                    placed.Add((axial, (byte)layers[row, col]));
                    qMin = Mathf.Min(qMin, axial.x);
                    qMax = Mathf.Max(qMax, axial.x);
                    rMin = Mathf.Min(rMin, axial.y);
                    rMax = Mathf.Max(rMax, axial.y);
                }
            }
            if (placed.Count == 0)
            {
                return null;
            }

            // Centre near the origin, like a hand-painted map.
            int qMid = Mathf.RoundToInt((qMin + qMax) / 2f);
            int rMid = Mathf.RoundToInt((rMin + rMax) / 2f);
            SandboxMap map = new SandboxMap();
            int reach = 0;
            foreach ((Vector2Int cell, byte layer) in placed)
            {
                Vector2Int k = new Vector2Int(cell.x - qMid, cell.y - rMid);
                // tile left null: plain substrate.
                map.cells[k] = new SandboxMap.Cell { layer = layer };
                reach = Mathf.Max(reach, HexLayout.Distance(0, 0, k.x, k.y));
            }
            map.radius = Mathf.Clamp(reach, MinRadius, MaxRadius);
            return map;
        }

        // Nearest substrate colour per pixel, optionally Floyd-Steinberg dithered over the
        // float buffer (modified in place), in reading order.
        private static int[,] PaletteLayers(float[,,] rgb, bool[,] solid, int gw, int gh, bool dither)
        {
            // Live, not copied: the Python tool's hardcoded Pink went stale once the recolour
            // patch was dropped. Index = layer; Black (1) is gated separately.
            Color[] palette = { Color.black, Color.black, ColourTokens.BlueColor, ColourTokens.PinkColor, ColourTokens.GoldColor, ColourTokens.PurpleColor };
            int[,] layers = new int[gh, gw];
            for (int row = 0; row < gh; row++)
            {
                for (int col = 0; col < gw; col++)
                {
                    if (!solid[row, col])
                    {
                        continue;
                    }
                    float pr = Mathf.Clamp(Mathf.Round(rgb[row, col, 0]), 0f, 255f);
                    float pg = Mathf.Clamp(Mathf.Round(rgb[row, col, 1]), 0f, 255f);
                    float pb = Mathf.Clamp(Mathf.Round(rgb[row, col, 2]), 0f, 255f);
                    int layer = ClassifyImagePixel(pr, pg, pb, palette);
                    layers[row, col] = layer;

                    // A Black-gated pixel has no meaningful hue error to spread.
                    if (!dither || layer == 1)
                    {
                        continue;
                    }
                    Color target = palette[layer];
                    float er = rgb[row, col, 0] - target.r * 255f;
                    float eg = rgb[row, col, 1] - target.g * 255f;
                    float eb = rgb[row, col, 2] - target.b * 255f;
                    Diffuse(col + 1, row, 7f / 16f);
                    Diffuse(col - 1, row + 1, 3f / 16f);
                    Diffuse(col, row + 1, 5f / 16f);
                    Diffuse(col + 1, row + 1, 1f / 16f);

                    void Diffuse(int c, int rr, float weight)
                    {
                        if (c < 0 || c >= gw || rr >= gh || !solid[rr, c])
                        {
                            return;
                        }
                        // Clamped: with only five colours and wide gaps between them (no
                        // green, cyan or true red), unbounded error let a run of far-off
                        // pixels tip into a worse colour than plain nearest-match (a cyan
                        // stretch went Pink instead of Blue on the hue-wheel test).
                        rgb[rr, c, 0] = Mathf.Clamp(rgb[rr, c, 0] + er * weight, 0f, 255f);
                        rgb[rr, c, 1] = Mathf.Clamp(rgb[rr, c, 1] + eg * weight, 0f, 255f);
                        rgb[rr, c, 2] = Mathf.Clamp(rgb[rr, c, 2] + eb * weight, 0f, 255f);
                    }
                }
            }
            return layers;
        }

        // Brightness only, hue ignored: darker = higher layer. The tonal range is stretched
        // first (PIL's ImageOps.autocontrast(cutoff=1) over solid pixels only, as the script
        // does), since a flat or low-contrast source otherwise collapses into one or two of
        // the five steps; holes are left out so a transparent background can't skew it.
        private static int[,] LuminanceLayers(float[,,] rgb, bool[,] solid, int gw, int gh, bool invert)
        {
            // PIL's "L" conversion weights, rounded to its integer grey.
            int[,] grey = new int[gh, gw];
            int[] hist = new int[256];
            int n = 0;
            for (int row = 0; row < gh; row++)
            {
                for (int col = 0; col < gw; col++)
                {
                    if (!solid[row, col])
                    {
                        continue;
                    }
                    float l = rgb[row, col, 0] * 0.299f + rgb[row, col, 1] * 0.587f + rgb[row, col, 2] * 0.114f;
                    int gv = Mathf.Clamp(Mathf.RoundToInt(l), 0, 255);
                    grey[row, col] = gv;
                    hist[gv]++;
                    n++;
                }
            }

            // Trim 1% of the pixels off each end of the histogram, then stretch what's left.
            int cut = n / 100;
            for (int lo = 0; lo < 256 && cut > 0; lo++)
            {
                int take = Mathf.Min(cut, hist[lo]);
                hist[lo] -= take;
                cut -= take;
            }
            cut = n / 100;
            for (int hi = 255; hi >= 0 && cut > 0; hi--)
            {
                int take = Mathf.Min(cut, hist[hi]);
                hist[hi] -= take;
                cut -= take;
            }
            int min = 0;
            while (min < 255 && hist[min] == 0)
            {
                min++;
            }
            int max = 255;
            while (max > 0 && hist[max] == 0)
            {
                max--;
            }
            // A single-tone image has nothing to stretch; leave it as it is.
            bool stretch = max > min;
            float stretchScale = stretch ? 255f / (max - min) : 1f;

            int[,] layers = new int[gh, gw];
            for (int row = 0; row < gh; row++)
            {
                for (int col = 0; col < gw; col++)
                {
                    if (!solid[row, col])
                    {
                        continue;
                    }
                    int gv = grey[row, col];
                    if (stretch)
                    {
                        gv = Mathf.Clamp((int)((gv - min) * stretchScale), 0, 255);
                    }
                    float lum = gv / 255f;
                    if (invert)
                    {
                        lum = 1f - lum;
                    }
                    layers[row, col] = Mathf.Clamp(Mathf.RoundToInt(1f + (1f - lum) * 4f), 1, 5);
                }
            }
            return layers;
        }

        // Black is a value/saturation gate, not a palette entry in the nearest search: a grey
        // pixel has no real hue and reads as "nearest" to an arbitrary hue colour otherwise
        // (it turned the smiley test's solid dark face into coloured noise).
        private static int ClassifyImagePixel(float r, float g, float b, Color[] palette)
        {
            Color.RGBToHSV(new Color(r / 255f, g / 255f, b / 255f), out _, out float s, out float v);
            if (v < 0.18f || s < 0.15f)
            {
                return 1;
            }
            int best = 2;
            float bestDist = float.MaxValue;
            for (int layer = 2; layer <= 5; layer++)
            {
                Color t = palette[layer];
                float d = RedmeanDistSq(r, g, b, t.r * 255f, t.g * 255f, t.b * 255f);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = layer;
                }
            }
            return best;
        }

        // "Redmean" weighted RGB distance: a cheap perceptual approximation that weights red
        // and blue by how red the pair is, unlike plain Euclidean RGB.
        private static float RedmeanDistSq(float r1, float g1, float b1, float r2, float g2, float b2)
        {
            float rmean = (r1 + r2) / 2f;
            float dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
            return (2f + rmean / 256f) * dr * dr + 4f * dg * dg + (2f + (255f - rmean) / 256f) * db * db;
        }

        private void SaveOverride()
        {
            if (_map.cells.Count == 0)
            {
                SetStatus(Loc.T("gravebuilder.map.status_override_empty", "The map is empty; paint some cells before saving it as the override."), error: true);
                return;
            }
            try
            {
                _map.Save(Plugin.OverrideFilePath);
                SetStatus(Loc.F("gravebuilder.map.status_override_saved", "Saved {n} cells as the sandbox override ({path}). The next time you fly out and mine, it'll be used.", ("n", _map.cells.Count), ("path", Plugin.OverrideFilePath)));
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.map.status_override_save_failed", "Saving the override failed: {error}", ("error", e.Message)), error: true);
            }
        }

        private void LoadOverride()
        {
            if (!File.Exists(Plugin.OverrideFilePath))
            {
                SetStatus(Loc.F("gravebuilder.map.status_override_missing", "No override file exists yet ({path}).", ("path", Plugin.OverrideFilePath)), error: true);
                return;
            }
            try
            {
                SandboxMap loaded = SandboxMap.Load(Plugin.OverrideFilePath);
                if (loaded == null)
                {
                    SetStatus(Loc.T("gravebuilder.map.status_override_unreadable", "The override file could not be read."), error: true);
                    return;
                }
                ReplaceMap(loaded, Loc.T("gravebuilder.history.loaded_override", "Loaded the current override"));
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.map.status_override_load_failed", "Loading the override failed: {error}", ("error", e.Message)), error: true);
            }
        }

        // Deleting the file switches the override off (Patches.cs keys ActiveOverride on it),
        // so mining goes back to the game's normal structures. Doesn't touch the map, so there
        // is nothing to undo.
        private void RemoveOverride()
        {
            if (!File.Exists(Plugin.OverrideFilePath))
            {
                SetStatus(Loc.T("gravebuilder.map.status_override_remove_none", "No override file to remove; mining already uses the normal structures."));
                return;
            }
            try
            {
                File.Delete(Plugin.OverrideFilePath);
                SetStatus(Loc.T("gravebuilder.map.status_override_removed", "Removed the sandbox override. Mining uses the normal structures again (the map here is unchanged)."));
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.map.status_override_remove_failed", "Removing the override failed: {error}", ("error", e.Message)), error: true);
            }
        }

        // ---- load existing structure --------------------------------------------------

        private static string StructuresDir()
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(pluginDir, "content", "structures");
        }

        // Our own struct-defs first (same folder ExportStructure writes), then every
        // StructureDef in the game's catalogue. Catalogue entries whose id matches one of our
        // local files are skipped: those are our own exports injected by the framework, and the
        // local entry loads the same cells plus the form.
        private void RefreshLoadList()
        {
            _loadEntries.Clear();
            HashSet<string> localIds = new HashSet<string>();
            try
            {
                string dir = StructuresDir();
                if (Directory.Exists(dir))
                {
                    foreach (string path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        if (path.EndsWith("_cells.json", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        try
                        {
                            JObject o = JObject.Parse(File.ReadAllText(path));
                            string id = (string)o["id"] ?? Path.GetFileNameWithoutExtension(path);
                            localIds.Add(id);
                            _loadEntries.Add(new LoadEntry
                            {
                                Label = (string)o["display_name"] ?? id,
                                Local = true,
                                Id = id,
                                DefPath = path
                            });
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"MapBuilder: skipping unreadable struct-def '{path}': {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MapBuilder: could not list local structures: {e.Message}");
            }

            StructureCatalog cat = StructureCatalog.I;
            if (cat != null)
            {
                HashSet<StructureDef> seen = new HashSet<StructureDef>();
                StructureDef[][] arrays = { cat.ladder, cat.campaign, cat.chapters, cat.oneShipChapters, cat.demoCampaign, cat.demoTeasers, cat.testLevels };
                foreach (StructureDef[] arr in arrays)
                {
                    if (arr == null)
                    {
                        continue;
                    }
                    foreach (StructureDef def in arr)
                    {
                        if (def == null || !seen.Add(def) || (def.id != null && localIds.Contains(def.id)) || _deletedInjected.Contains(def))
                        {
                            continue;
                        }
                        _loadEntries.Add(new LoadEntry
                        {
                            Label = string.IsNullOrEmpty(def.displayName) ? (def.id ?? def.name) : def.displayName,
                            Local = false,
                            Id = def.id,
                            Def = def
                        });
                    }
                }
            }

            // Two genuinely different entries (different ids, possibly different arrays or
            // local vs. catalogue) can legitimately share a display name — e.g. the game's own
            // test-level data has had duplicate "Wreck Seed N" style labels. Rather than merge
            // or hide either, disambiguate every label that collides by appending its id, so a
            // user picking from the list isn't stuck guessing which one is which.
            Dictionary<string, int> labelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (LoadEntry e in _loadEntries)
            {
                labelCounts[e.Label] = labelCounts.TryGetValue(e.Label, out int n) ? n + 1 : 1;
            }
            foreach (LoadEntry e in _loadEntries)
            {
                if (labelCounts[e.Label] > 1 && !string.IsNullOrEmpty(e.Id))
                {
                    e.Label = $"{e.Label} ({e.Id})";
                }
            }

            _loadSelected = -1;
            RebuildLoadList();
            RefreshLoadDetail();
            // The export form's ID hint checks against this list (overwrite / game-ID clash).
            RefreshExportHints();
        }

        private void RebuildLoadList()
        {
            if (_loadListHost == null)
            {
                return;
            }
            _loadListHost.Clear();
            _loadHolders.Clear();
            _loadRowPaint.Clear();
            _loadHover = -1;
            if (_loadSummary != null)
            {
                int mine = _loadEntries.Count(e => e.Local);
                _loadSummary.text = Loc.F("gravebuilder.structures.list_summary", "{mine} YOURS · {game} GAME", ("mine", mine), ("game", _loadEntries.Count - mine));
            }
            // Your exports first, then the game's catalogue (RefreshLoadList's order), each
            // under its own eyebrow like the Contracts list's IN PROGRESS / ALL CONTRACTS.
            bool anyLocal = _loadEntries.Any(e => e.Local);
            _loadListHost.Add(ListHeader(Loc.T("gravebuilder.structures.list_header_yours", "YOUR EXPORTS"), first: true));
            if (!anyLocal)
            {
                _loadListHost.Add(BodyText_(Loc.T("gravebuilder.structures.list_yours_empty", "Nothing exported yet. Use the EXPORT tab.")));
            }
            bool gameHeader = false;
            for (int i = 0; i < _loadEntries.Count; i++)
            {
                LoadEntry entry = _loadEntries[i];
                if (!entry.Local && !gameHeader)
                {
                    _loadListHost.Add(ListHeader(Loc.T("gravebuilder.structures.list_header_game", "GAME STRUCTURES")));
                    gameHeader = true;
                }
                VisualElement row = LoadRow(i, entry);
                _loadListHost.Add(row);
                _loadHolders.Add(row);
            }
            if (!gameHeader)
            {
                _loadListHost.Add(ListHeader(Loc.T("gravebuilder.structures.list_header_game", "GAME STRUCTURES")));
                _loadListHost.Add(BodyText_(Loc.T("gravebuilder.structures.list_game_empty", "No game structures found.")));
            }
            PaintLoadRows();
        }

        // One load-list row, as a Contracts-list row: a slab (orange for your exports, grey for
        // game structures) with the name, a short tag on the right and, for your own exports
        // only, a delete key (game structures aren't files this mod manages). Click selects,
        // double-click loads.
        // ---- load-list thumbnails --------------------------------------------------------
        // The Contracts list's hex silhouette per row. Game structures go through the game's
        // own StructureSilhouette.ThumbLazy (what ModeSelectController.StructureThumb uses:
        // bakes one texture per frame, cached by id). Our exports can't: that cache is keyed by
        // id and never invalidated, so re-exporting a map would keep showing its old shape.
        // They get the same rendering (StructureSilhouette.Of's maths and LayerColour) into a
        // cache of our own keyed by the cells file's path and write time.

        // Fits inside the compact row (LoadRow) without making it taller.
        private const float LoadThumbSize = 34f;

        private static readonly Dictionary<string, (DateTime stamp, Texture2D tex, int count)> _localThumbs = new Dictionary<string, (DateTime, Texture2D, int)>(StringComparer.OrdinalIgnoreCase);

        // A fixed-size slot either way, so names line up whether or not a thumbnail exists.
        private static VisualElement LoadThumb(LoadEntry entry)
        {
            VisualElement slot = EntryPreview(entry, LoadThumbSize, out _);
            slot.style.marginRight = 14f;
            return slot;
        }

        // Same rendering as LoadThumb, parameterised by size so the load-list row (small) and
        // the detail pane (big) share one path. cellCount comes along for free: local entries
        // only know their own cell count by parsing the same cells file this renders from.
        private static VisualElement EntryPreview(LoadEntry entry, float size, out int cellCount)
        {
            cellCount = !entry.Local && entry.Def != null ? entry.Def.cellCount : 0;
            VisualElement slot = new VisualElement { pickingMode = PickingMode.Ignore };
            slot.style.width = size;
            slot.style.height = size;
            slot.style.flexShrink = 0f;
            VisualElement thumb = null;
            try
            {
                if (entry.Local)
                {
                    Texture2D tex = LocalThumbTexture(entry.DefPath, out cellCount);
                    if (tex != null)
                    {
                        thumb = new VisualElement { pickingMode = PickingMode.Ignore };
                        thumb.style.flexGrow = 1f;
                        thumb.style.backgroundImage = new StyleBackground(tex);
                        thumb.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                        thumb.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                        thumb.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                        thumb.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                    }
                }
                else if (entry.Def != null)
                {
                    thumb = StructureSilhouette.ThumbLazy(entry.Def, size);
                }
            }
            catch (Exception e)
            {
                // A thumbnail is decoration; never let one break the list.
                Plugin.Log.LogWarning($"MapBuilder: no thumbnail for '{entry.Label}': {e.Message}");
            }
            if (thumb != null)
            {
                slot.Add(thumb);
            }
            return slot;
        }

        private static Texture2D LocalThumbTexture(string defPath, out int cellCount)
        {
            cellCount = 0;
            JObject def = JObject.Parse(File.ReadAllText(defPath));
            string cellsFile = (string)def["cells_file"];
            if (string.IsNullOrEmpty(cellsFile))
            {
                return null;
            }
            string cellsPath = Path.Combine(Path.GetDirectoryName(defPath), cellsFile);
            if (!File.Exists(cellsPath))
            {
                return null;
            }
            DateTime stamp = File.GetLastWriteTimeUtc(cellsPath);
            if (_localThumbs.TryGetValue(cellsPath, out (DateTime stamp, Texture2D tex, int count) cached))
            {
                if (cached.stamp == stamp && cached.tex != null)
                {
                    cellCount = cached.count;
                    return cached.tex;
                }
                if (cached.tex != null)
                {
                    UnityEngine.Object.Destroy(cached.tex);
                }
                _localThumbs.Remove(cellsPath);
            }
            MegastructureData data = MegastructureData.FromJson(File.ReadAllText(cellsPath));
            Texture2D tex = RenderSilhouette(data);
            cellCount = data?.count ?? 0;
            if (tex != null)
            {
                _localThumbs[cellsPath] = (stamp, tex, cellCount);
            }
            return tex;
        }

        // StructureSilhouette.Of's rendering, minus its id cache: each cell is a 2x2 block of its
        // layer's LayerColour at its axial-to-pixel position, y flipped (texture rows run
        // bottom-up) so the thumbnail matches the game's own orientation.
        private static Texture2D RenderSilhouette(MegastructureData d)
        {
            if (d == null || d.count == 0)
            {
                return null;
            }
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < d.count; i++)
            {
                float x = Mathf.Sqrt(3f) * (d.q[i] + d.r[i] / 2f);
                float y = 1.5f * d.r[i];
                xMin = Mathf.Min(xMin, x);
                xMax = Mathf.Max(xMax, x);
                yMin = Mathf.Min(yMin, y);
                yMax = Mathf.Max(yMax, y);
            }
            float s = 2f / Mathf.Sqrt(3f);
            int w = Mathf.CeilToInt((xMax - xMin) * s) + 4;
            int h = Mathf.CeilToInt((yMax - yMin) * s) + 4;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            Color32[] px = new Color32[w * h];
            for (int i = 0; i < d.count; i++)
            {
                Color32 c = StructureSilhouette.LayerColour(d.layer[i]);
                float x = Mathf.Sqrt(3f) * (d.q[i] + d.r[i] / 2f);
                int bx = Mathf.RoundToInt((x - xMin) * s) + 1;
                int by = h - 2 - Mathf.RoundToInt((1.5f * d.r[i] - yMin) * s);
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int tx = bx + dx;
                        int ty = by - dy;
                        if (tx >= 0 && tx < w && ty >= 0 && ty < h)
                        {
                            px[ty * w + tx] = c;
                        }
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return tex;
        }

        private VisualElement LoadRow(int index, LoadEntry entry)
        {
            Color family = entry.Local ? Accent : Neutral;
            VisualElement row = new VisualElement { name = "map-builder-load-row" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 18f;
            row.style.paddingRight = 18f;
            row.style.paddingTop = 9f;
            row.style.paddingBottom = 9f;
            // 8 apart (AddRow uses 16 at twice the height) so each chamfered outline reads as
            // its own box, as on the Contracts list.
            row.style.marginBottom = 8f;
            SlabFrame frame = MetaUi.Slab(row, family, dim: false, 12f);

            row.Add(LoadThumb(entry));

            Label name = MetaUi.Text(entry.Label.ToUpperInvariant(), MetaUi.Face.DisplayBold, 17f, MetaUi.TextTone(family), 1.5f);
            name.pickingMode = PickingMode.Ignore;
            name.style.flexGrow = 1f;
            name.style.flexShrink = 1f;
            // minWidth: 0 for the same reason as the band field labels (CompactField) — a flex
            // child's default min-width is its own unwrapped content width, so without this the
            // box never actually shrinks below the full name and textOverflow never engages.
            // Confirmed live (2026-11-24): long names ("Modded Dreadnought") were hard-clipping
            // mid-word ("MODDED DREADNOI") in the narrower post-split list column — ellipsis
            // reads as a deliberate truncation instead of a typo.
            name.style.minWidth = 0f;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(name);

            // A cell count reads as AddRow's right-hand stat (Display face, MetaUi.Green); the
            // word tags stay quiet.
            bool hasCount = !entry.Local && entry.Def != null && entry.Def.cellCount > 0;
            string tag = entry.Local ? Loc.T("gravebuilder.structures.row_tag_yours", "YOUR EXPORT") : (hasCount ? Loc.F("gravebuilder.structures.row_tag_cells", "{n} CELLS", ("n", entry.Def.cellCount)) : Loc.T("gravebuilder.structures.row_tag_game", "GAME"));
            Label tagLabel = hasCount
                ? MetaUi.Text(tag, MetaUi.Face.Display, 17f, MetaUi.Green, 1.5f)
                : MetaUi.Text(tag, MetaUi.Face.Body, 13f, DimText, 1.5f);
            tagLabel.pickingMode = PickingMode.Ignore;
            tagLabel.style.flexShrink = 0f;
            tagLabel.style.marginLeft = 16f;
            row.Add(tagLabel);

            // Deleting a local export is an action on the selected structure, not the row
            // itself now — it lives in the detail pane's DELETE button (RefreshLoadDetail).

            row.RegisterCallback<ClickEvent>(e =>
            {
                SelectLoadEntry(index);
                if (e.clickCount >= 2)
                {
                    LoadSelected();
                }
            });
            row.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _loadHover = index;
                PaintLoadRows();
            });
            row.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (_loadHover == index)
                {
                    _loadHover = -1;
                }
                PaintLoadRows();
            });
            MetaUi.Lift(row, slide: true);
            _loadRowPaint.Add((frame, name, family));
            // The name label can still ellipsis-truncate a long one; the full name is always
            // one hover away rather than just unreadable.
            return AttachTooltip(row, entry.Label);
        }

        // Hover/selection look, as ModeSelectController.PaintRows: the selected row's slab at
        // full strength with a heavier edge and white name, hovered rows brighter than rest.
        private void PaintLoadRows()
        {
            for (int i = 0; i < _loadRowPaint.Count; i++)
            {
                (SlabFrame frame, Label name, Color family) = _loadRowPaint[i];
                bool selected = i == _loadSelected;
                bool hover = i == _loadHover;
                MetaUi.PaintSlab(frame, family, dim: false, selected ? 1f : (hover ? 0.85f : 0.45f));
                frame.Stroke = selected ? 3f : 2f;
                name.style.color = selected ? Color.white : MetaUi.TextTone(family);
            }
        }

        private void SelectLoadEntry(int index)
        {
            _loadSelected = index;
            PaintLoadRows();
            RefreshLoadDetail();
            SetStatus(Loc.F("gravebuilder.structures.status_selected", "Selected '{name}'.", ("name", _loadEntries[index].Label)));
        }

        private void LoadSelected()
        {
            if (_loadSelected < 0 || _loadSelected >= _loadEntries.Count)
            {
                SetStatus(Loc.T("gravebuilder.structures.status_select_first", "Select a structure in the list first."), error: true);
                return;
            }
            LoadEntry entry = _loadEntries[_loadSelected];
            try
            {
                bool loaded = entry.Local ? LoadLocal(entry) : LoadCatalogue(entry);
                // Done picking a structure: back to the map to see it. Only on success: a
                // failed load (its reason is in the status line) probably means the user
                // wants to pick another.
                if (loaded)
                {
                    CloseLibrary();
                }
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.structures.status_load_failed", "Loading '{name}' failed: {error}", ("name", entry.Label), ("error", e.Message)), error: true);
            }
        }

        // True if the map was replaced; false (with the reason in the status line) if not.
        private bool LoadLocal(LoadEntry entry)
        {
            JObject def = JObject.Parse(File.ReadAllText(entry.DefPath));
            string cellsFile = (string)def["cells_file"];
            if (string.IsNullOrEmpty(cellsFile))
            {
                SetStatus(Loc.F("gravebuilder.structures.status_no_cells_file", "'{file}' has no cells_file.", ("file", Path.GetFileName(entry.DefPath))), error: true);
                return false;
            }
            string cellsPath = Path.Combine(Path.GetDirectoryName(entry.DefPath), cellsFile);
            if (!File.Exists(cellsPath))
            {
                SetStatus(Loc.F("gravebuilder.structures.status_cells_file_missing", "Cells file '{cells_file}' is missing next to '{file}'.", ("cells_file", cellsFile), ("file", Path.GetFileName(entry.DefPath))), error: true);
                return false;
            }
            SandboxMap map = SandboxMap.FromMegastructure(MegastructureData.FromJson(File.ReadAllText(cellsPath)));
            ReplaceMap(map, Loc.F("gravebuilder.history.loaded_structure_local", "Loaded '{name}'", ("name", entry.Label)));

            // Pre-fill the export form so re-exporting overwrites the same files.
            string id = (string)def["id"] ?? Path.GetFileNameWithoutExtension(entry.DefPath);
            SetForm(id, (string)def["display_name"] ?? id, ((int?)def["starting_money"] ?? DefaultStartingMoney).ToString());
            _bands.Clear();
            if (def["bands"] is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    if (t is JObject b)
                    {
                        _bands.Add(BandFromJson(b));
                    }
                }
            }
            if (_bands.Count == 0)
            {
                _bands.Add(DefaultBand());
            }
            RebuildBands();
            SetStatus(_bands.Count == 1
                ? Loc.F("gravebuilder.structures.status_loaded_local_one", "Loaded '{name}': {n} cells; the export form now targets '{id}' ({stages} stage).", ("name", entry.Label), ("n", _map.cells.Count), ("id", id), ("stages", _bands.Count))
                : Loc.F("gravebuilder.structures.status_loaded_local_other", "Loaded '{name}': {n} cells; the export form now targets '{id}' ({stages} stages).", ("name", entry.Label), ("n", _map.cells.Count), ("id", id), ("stages", _bands.Count)));
            return true;
        }

        private bool LoadCatalogue(LoadEntry entry)
        {
            if (entry.Def.cells == null)
            {
                SetStatus(Loc.F("gravebuilder.structures.status_no_cell_data", "'{name}' has no cell data to load.", ("name", entry.Label)), error: true);
                return false;
            }
            SandboxMap map = SandboxMap.FromMegastructure(MegastructureData.FromJson(entry.Def.cells.text));
            ReplaceMap(map, Loc.F("gravebuilder.history.loaded_structure_game", "Loaded '{name}'", ("name", entry.Label)));
            // Official structures never had a struct-def JSON, so don't guess: blank form.
            SetForm(DefaultId, DefaultName, DefaultStartingMoney.ToString());
            _bands.Clear();
            _bands.Add(DefaultBand());
            RebuildBands();
            SetStatus(Loc.F("gravebuilder.structures.status_loaded_game", "Loaded game structure '{name}': {n} cells. The export form was reset to defaults.", ("name", entry.Label), ("n", _map.cells.Count)));
            return true;
        }

        // Delete one of our own exports (orange entries only; RebuildLoadList never offers it for
        // a game structure). Asks first with the game's own TerminalUi.Confirm modal, added to
        // this screen's root so it covers the whole map builder; the canvas and panels can't
        // be clicked until it's answered (KEEP GOING just closes it).
        private void DeleteLocalStructure(LoadEntry entry)
        {
            VisualElement root = _doc != null ? _doc.rootVisualElement : null;
            if (root == null || entry == null || !entry.Local)
            {
                return;
            }
            CommitAction();
            TerminalUi.Confirm(root, "map-builder-confirm-delete", Accent, Loc.T("gravebuilder.structures.confirm_delete_title", "DELETE STRUCTURE"),
                Loc.F("gravebuilder.structures.confirm_delete_body", "Delete '{name}'?", ("name", entry.Label)),
                Loc.T("gravebuilder.structures.confirm_delete_button", "DELETE"), () => DeleteLocalStructureConfirmed(entry));
        }

        // Removes <id>.json and its cells file from StructuresDir() (the folder ExportStructure
        // writes, and RefreshLoadList read entry.DefPath from), then rescans the list.
        //
        // Already-injected copies: the framework's RELOAD MODS (StructuresInjectPatch.Reload ->
        // InjectedTracker.Reload) first strips every id it injected last pass out of
        // StructureCatalog.campaign and its id cache, then rescans the folders, so a deleted
        // file is gone from the Contracts list after the next RELOAD MODS, not before (verified
        // 2026-11-22 against StructuresInjectPatch.cs / ContentInjector.cs). Until then the
        // live catalogue still holds it, so it's remembered in _deletedInjected to keep it out
        // of this list. (Hides it by object reference; the only way that could also hide a game
        // structure is an export that reused a game contract's own id, which the framework
        // refuses to inject anyway.)
        private void DeleteLocalStructureConfirmed(LoadEntry entry)
        {
            try
            {
                string dir = Path.GetFullPath(StructuresDir());
                string defPath = Path.GetFullPath(entry.DefPath);
                if (!string.Equals(Path.GetDirectoryName(defPath), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(Loc.F("gravebuilder.structures.status_delete_refused", "Refusing to delete '{path}': it isn't in {dir}.", ("path", defPath), ("dir", dir)), error: true);
                    return;
                }

                // The cells file the def points at (as LoadLocal resolves it), else
                // ExportStructure's own naming. File name only, so a hand-edited cells_file
                // can't reach outside the folder.
                string id = entry.Id ?? Path.GetFileNameWithoutExtension(defPath);
                string cellsFile = null;
                try
                {
                    if (File.Exists(defPath))
                    {
                        JObject def = JObject.Parse(File.ReadAllText(defPath));
                        string fromDef = (string)def["cells_file"];
                        if (!string.IsNullOrEmpty(fromDef))
                        {
                            cellsFile = Path.GetFileName(fromDef);
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"MapBuilder: could not read '{defPath}' for its cells_file, using {id}_cells.json: {e.Message}");
                }
                if (string.IsNullOrEmpty(cellsFile))
                {
                    cellsFile = id + "_cells.json";
                }
                string cellsPath = Path.Combine(dir, cellsFile);

                List<string> removed = new List<string>();
                if (File.Exists(defPath))
                {
                    File.Delete(defPath);
                    removed.Add(Path.GetFileName(defPath));
                }
                if (File.Exists(cellsPath))
                {
                    File.Delete(cellsPath);
                    removed.Add(cellsFile);
                }

                // Any live catalogue copy (injected at boot or by an earlier RELOAD MODS).
                bool inGame = false;
                StructureCatalog cat = StructureCatalog.I;
                if (cat != null && !string.IsNullOrEmpty(id))
                {
                    StructureDef[][] arrays = { cat.ladder, cat.campaign, cat.chapters, cat.oneShipChapters, cat.demoCampaign, cat.demoTeasers, cat.testLevels };
                    foreach (StructureDef[] arr in arrays)
                    {
                        if (arr == null)
                        {
                            continue;
                        }
                        foreach (StructureDef d in arr)
                        {
                            if (d != null && d.id == id)
                            {
                                _deletedInjected.Add(d);
                                inGame = true;
                            }
                        }
                    }
                }

                RefreshLoadList();
                string files = removed.Count > 0 ? string.Join(" + ", removed) : Loc.T("gravebuilder.structures.delete_no_files", "no files were left to remove");
                SetStatus(inGame
                    ? Loc.F("gravebuilder.structures.status_deleted_in_game", "Deleted structure '{name}' ({files}). It's still in the game's Contracts list this session: click RELOAD MODS on the main menu's Mods page to remove it there too. The map here is unchanged.", ("name", entry.Label), ("files", files))
                    : Loc.F("gravebuilder.structures.status_deleted", "Deleted structure '{name}' ({files}). The map here is unchanged.", ("name", entry.Label), ("files", files)));
            }
            catch (Exception e)
            {
                RefreshLoadList();
                SetStatus(Loc.F("gravebuilder.structures.status_delete_failed", "Deleting '{name}' failed: {error}", ("name", entry.Label), ("error", e.Message)), error: true);
            }
        }

        private void SetForm(string id, string name, string money)
        {
            _idText = id;
            _nameText = name;
            _moneyText = money;
            _idField?.SetValueWithoutNotify(id);
            _nameField?.SetValueWithoutNotify(name);
            _moneyField?.SetValueWithoutNotify(money);
            RefreshExportHints();
        }

        // ---- band editor --------------------------------------------------------------

        private static string[] DefaultBand()
        {
            return BandFields.Select(f => f.Default).ToArray();
        }

        private static string[] BandFromJson(JObject b)
        {
            string[] values = DefaultBand();
            for (int i = 0; i < BandFields.Length; i++)
            {
                JToken t = b[BandFields[i].Key];
                if (t == null || t.Type == JTokenType.Null)
                {
                    continue;
                }
                values[i] = t.Type == JTokenType.Float
                    ? ((float)t).ToString(CultureInfo.InvariantCulture)
                    : t.ToString();
            }
            return values;
        }

        private void RebuildBands()
        {
            if (_bandHost == null)
            {
                return;
            }
            _bandHost.Clear();
            Dictionary<int, int> counts = _map != null ? _map.LayerCellCounts() : new Dictionary<int, int>();
            for (int i = 0; i < _bands.Count; i++)
            {
                _bandHost.Add(BandRow(i, counts));
            }
        }

        // One band, grouped by what the fields are for rather than one flat grid:
        //   title: BAND n + when it unlocks, remove key;
        //   SUBSTRATE COLOUR: the five colour swatches (instead of a 1-5 number box), with a
        //     live note on how many cells the map has in that colour, or why this band gets
        //     none (no such cells, or an earlier band already took the colour);
        //   TILE HEALTH / SHARD SPAWNS / ENEMY SPAWNS: side by side, each count behind a chip of
        //     that tile's palette colour (MarkerColour);
        //   COSMETIC: the two no-gameplay-effect fields (preview ring size, core look), folded
        //     away behind a key with a one-line summary unless opened.
        // Every field keeps its BandFields key and value slot, so export and load are unchanged.
        private VisualElement BandRow(int index, Dictionary<int, int> counts)
        {
            string[] values = _bands[index];
            VisualElement panel = MetaUi.Panel(Accent, 15f, dim: true, 12f);
            panel.style.marginBottom = 10f;

            // Title.
            VisualElement title = Row();
            title.style.justifyContent = Justify.SpaceBetween;
            title.style.alignItems = Align.Center;
            title.style.marginBottom = 4f;
            VisualElement titleText = Row();
            titleText.style.alignItems = Align.Center;
            titleText.Add(MetaUi.Text(Loc.F("gravebuilder.export.stage_title", "STAGE {stage}", ("stage", index + 1)), MetaUi.Face.DisplayBold, 20f, AccentLt, 3f));
            Label order = MetaUi.Text(index == 0 ? Loc.T("gravebuilder.export.stage_unlocks_first", "UNLOCKS FIRST") : Loc.F("gravebuilder.export.stage_unlocks_after", "UNLOCKS AFTER STAGE {stage}", ("stage", index)), MetaUi.Face.Body, 14f, DimText, 2f);
            order.style.marginLeft = 16f;
            titleText.Add(order);
            title.Add(titleText);
            title.Add(AttachTooltip(MetaUi.Key("×", () =>
            {
                if (index < _bands.Count)
                {
                    _bands.RemoveAt(index);
                }
                RebuildBands();
                SetStatus(_bands.Count == 0
                    ? Loc.T("gravebuilder.export.status_removed_last_stage", "Removed the last stage; export needs at least one (ADD STAGE).")
                    : Loc.F("gravebuilder.export.status_removed_stage", "Removed stage {stage}.", ("stage", index + 1)));
            }, 54f, MetaUi.Crimson), Loc.T("gravebuilder.export.remove_stage_tooltip", "Remove this stage. The stages after it move up one place in the unlock order.")));
            panel.Add(title);

            // Substrate colour.
            panel.Add(AttachTooltip(GroupLabel(Loc.T("gravebuilder.export.stage_colour_header", "SUBSTRATE COLOUR")), BandFields[BandLayer].Tooltip));
            int.TryParse((values[BandLayer] ?? "").Trim(), out int layer);
            VisualElement swatches = Row();
            swatches.style.flexWrap = Wrap.Wrap;
            for (int l = 1; l <= 5; l++)
            {
                int pick = l;
                Color c = StructureSilhouette.LayerColour(l);
                VisualElement key = MetaUi.Key(LayerColourName(l), () =>
                {
                    values[BandLayer] = pick.ToString();
                    // Rebuild every band: a colour change can start or stop clashing with
                    // another band's.
                    RebuildBands();
                }, 120f, c);
                swatches.Add(SwatchHolder(key, c, l == layer));
            }
            panel.Add(swatches);
            // Same always-visible weapon legend as the PALETTE panel (LayerCaption) — this is
            // the one other place a player picks a substrate colour, and it had no explanation
            // at all before, not even a tooltip.
            Label legend = BodyText_(string.Join("  ·  ", Enumerable.Range(1, 5).Select(l => Loc.F("gravebuilder.export.layer_legend_item", "{colour}: {caption}", ("colour", LayerColourName(l)), ("caption", LayerCaption(l))))));
            legend.style.marginBottom = 4f;
            panel.Add(legend);
            BandColourNote(panel, index, layer, values[BandLayer], counts);

            // Tile health: the only other field the framework actually reads today, so it
            // stays visible alongside the colour (everything else below is folded away).
            VisualElement groups = Row();
            groups.style.flexWrap = Wrap.Wrap;
            groups.style.alignItems = Align.FlexStart;
            groups.style.marginTop = 4f;
            panel.Add(groups);

            VisualElement health = BandGroup(groups, Loc.T("gravebuilder.export.stage_health_header", "TILE HEALTH"), BandFields[BandHpSealed].Tooltip);
            health.Add(CompactField(values, BandHpSealed, null, 190f));

            // Shard/enemy counts, preview ring size and core look all got a UI pass this session
            // (steppers, a live preview) but stayed ~100% inert: BuildFromJson still only reads
            // layer, outer_frac and hp_sealed, and even outer_frac only ever shows up on a tiny
            // Contracts-list icon. The user's own call (2026-11-24): showing controls for
            // something that does nothing is just clutter, not "transparency" — removed entirely
            // rather than folded away. values[] still carries whatever these fields were last
            // set to (DefaultBand()'s defaults for a new stage, or whatever an older export/load
            // already had) and BandsToJson still writes them, so re-exporting an existing
            // structure round-trips them unchanged — there's just no UI to edit them until a
            // future update actually reads them. See StepperField/PreviewRingField/
            // CoreLookField/BuildAppearancePreview in git history if this needs reviving.
            return panel;
        }

        // A titled column inside a band (TILE HEALTH, SHARD SPAWNS, ...); the title carries the
        // group's hover text. Returns the column to add fields to.
        private VisualElement BandGroup(VisualElement host, string titleText, string tooltip)
        {
            VisualElement group = new VisualElement();
            group.style.marginRight = 34f;
            group.style.marginBottom = 4f;
            group.Add(AttachTooltip(GroupLabel(titleText), tooltip));
            host.Add(group);
            return group;
        }

        // A colour swatch with a selection border and an exact-colour chip, as the palette's
        // ToolHolder but local to the band editor (not in _toolHolders).
        private static VisualElement SwatchHolder(VisualElement button, Color exact, bool selected)
        {
            VisualElement holder = new VisualElement();
            holder.style.paddingLeft = 4f;
            holder.style.paddingRight = 4f;
            holder.style.paddingTop = 4f;
            holder.style.paddingBottom = 4f;
            holder.style.marginRight = 6f;
            holder.style.marginBottom = 4f;
            TerminalUi.Border(holder, 2f, selected ? SelectedEdge : Color.clear);
            holder.Add(button);
            VisualElement chip = new VisualElement { pickingMode = PickingMode.Ignore };
            chip.style.height = 6f;
            chip.style.marginTop = 4f;
            chip.style.backgroundColor = exact;
            holder.Add(chip);
            return holder;
        }

        // The line under a band's colour swatches: how many cells it covers, or why it covers
        // none. BandIndexOfLayer returns the first band with a colour, so a later duplicate
        // gets no tiles.
        private static void BandColourNote(VisualElement panel, int index, int layer, string raw, Dictionary<int, int> counts)
        {
            string text;
            Color colour;
            if (layer < 1 || layer > 5 || !int.TryParse((raw ?? "").Trim(), out _))
            {
                text = Loc.F("gravebuilder.export.colour_note_invalid", "Pick a colour: the saved value '{value}' isn't one of the five (1-5).", ("value", raw));
                colour = MetaUi.Crimson;
            }
            else
            {
                string name = LayerColourName(layer);
                int earlier = -1;
                for (int j = 0; j < index; j++)
                {
                    if (int.TryParse((_bands[j][BandLayer] ?? "").Trim(), out int other) && other == layer)
                    {
                        earlier = j;
                        break;
                    }
                }
                int n = counts.TryGetValue(layer, out int c) ? c : 0;
                if (earlier >= 0)
                {
                    text = Loc.F("gravebuilder.export.colour_note_taken", "Stage {stage} already uses {colour}, so this stage gets no tiles (the first stage with a colour takes them all).", ("stage", earlier + 1), ("colour", name));
                    colour = WarnText;
                }
                else if (n == 0)
                {
                    text = Loc.F("gravebuilder.export.colour_note_unpainted", "No cells on this map are painted {colour}, so this stage has no tiles yet.", ("colour", name));
                    colour = WarnText;
                }
                else
                {
                    text = n == 1
                        ? Loc.F("gravebuilder.export.colour_note_count_one", "{n} cell on this map is {colour}.", ("n", n), ("colour", name))
                        : Loc.F("gravebuilder.export.colour_note_count_other", "{n} cells on this map are {colour}.", ("n", n), ("colour", name));
                    colour = DimText;
                }
            }
            Label note = MetaUi.Text(text, MetaUi.Face.Body, 15f, colour, 0.5f, wrap: true);
            note.style.marginTop = 2f;
            note.style.marginBottom = 6f;
            PadWrappedTracking(note, 0.5f);
            panel.Add(note);
        }

        // One labelled number box with its hover text and an inline error line (shown while
        // the value wouldn't export, with the same rule BandsToJson enforces). chip: a small
        // square of the tile's palette colour before the label.
        private VisualElement CompactField(string[] values, int fieldIndex, Color? chip, float width = 168f)
        {
            BandField spec = BandFields[fieldIndex];
            VisualElement cell = new VisualElement();
            cell.style.width = width;
            cell.style.marginRight = 10f;
            cell.style.marginBottom = 4f;

            VisualElement labelRow = Row();
            labelRow.style.alignItems = Align.Center;
            if (chip.HasValue)
            {
                VisualElement sw = new VisualElement { pickingMode = PickingMode.Ignore };
                sw.style.width = 11f;
                sw.style.height = 11f;
                sw.style.marginRight = 6f;
                sw.style.flexShrink = 0f;
                sw.style.backgroundColor = chip.Value;
                TerminalUi.Border(sw, 1f, MetaUi.Fade(MetaUi.Ink, 0.35f));
                labelRow.Add(sw);
            }
            // tracking: 0, not the usual letter-spaced caps style, because MetaUi.Text's
            // trailing-letterSpacing compensation is skipped under wrap: true (see
            // PadWrappedTracking's comment) and 1 unit of compensation wasn't enough slack —
            // these short all-caps words (EXPLOSIVE, LIGHTNING, VOID, SWARM) still lost their
            // last glyph. No letter-spacing at all sidesteps the bug rather than re-tuning it.
            Label label = MetaUi.Text(spec.Label.ToUpperInvariant(), MetaUi.Face.Body, 13f, BodyText, 0f, wrap: true);
            label.style.flexShrink = 1f;
            // Flexbox gotcha: a flex child's default min-width is its own unwrapped content
            // width ("min-width: auto"), so whiteSpace:Normal alone still doesn't let it wrap
            // below that — the layout measures it at full single-line width regardless of the
            // box it's actually given, and the render just clips mid-word. Explicit 0 is needed
            // for wrap to actually take effect.
            label.style.minWidth = 0f;
            labelRow.Add(label);
            cell.Add(labelRow);

            TextField tf = new TextField();
            tf.SetValueWithoutNotify(values[fieldIndex]);
            StyleInput(tf, 17f);
            tf.style.marginLeft = 0f;
            tf.style.marginRight = 0f;
            tf.style.marginTop = 2f;
            cell.Add(tf);

            Label error = MetaUi.Text(Loc.F("gravebuilder.export.field_error", "Must be {rule}.", ("rule", BandValueRule(spec))), MetaUi.Face.Body, 13f, MetaUi.Crimson, 0f, wrap: true);
            error.style.marginTop = 2f;
            cell.Add(error);

            void Validate(string raw)
            {
                bool ok = BandValueValid(spec, raw);
                error.style.display = ok ? DisplayStyle.None : DisplayStyle.Flex;
                VisualElement input = tf.Q(null, TextField.inputUssClassName) ?? tf.Q("unity-text-input");
                if (input != null)
                {
                    TerminalUi.Border(input, 2f, ok ? MetaUi.Fade(Accent, 0.7f) : MetaUi.Crimson);
                }
            }
            tf.RegisterValueChangedCallback(e =>
            {
                values[fieldIndex] = e.newValue;
                Validate(e.newValue);
            });
            Validate(values[fieldIndex]);
            return AttachTooltip(cell, spec.Tooltip);
        }

        // What a band field accepts, phrased to follow "must be".
        private static string BandValueRule(BandField spec)
        {
            if (spec.IsFloat)
            {
                return Loc.F("gravebuilder.export.rule_decimal", "a number from {min} to {max} (use a '.' for decimals)", ("min", spec.Min), ("max", spec.Max));
            }
            return spec.Max >= int.MaxValue
                ? Loc.F("gravebuilder.export.rule_whole_min", "a whole number of {min} or more", ("min", spec.Min))
                : Loc.F("gravebuilder.export.rule_whole_range", "a whole number from {min} to {max}", ("min", spec.Min), ("max", spec.Max));
        }

        private static bool BandValueValid(BandField spec, string raw)
        {
            raw = (raw ?? "").Trim();
            if (spec.IsFloat)
            {
                return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f >= spec.Min && f <= spec.Max;
            }
            return int.TryParse(raw, out int v) && v >= spec.Min && v <= spec.Max;
        }

        // Validate every band row into JSON; null + message on the first bad value.
        private static JArray BandsToJson(out string error)
        {
            error = null;
            JArray arr = new JArray();
            for (int i = 0; i < _bands.Count; i++)
            {
                JObject b = new JObject();
                for (int f = 0; f < BandFields.Length; f++)
                {
                    BandField spec = BandFields[f];
                    string raw = (_bands[i][f] ?? "").Trim();
                    if (!BandValueValid(spec, raw))
                    {
                        error = Loc.F("gravebuilder.export.status_stage_field_invalid", "Stage {stage}: {field} must be {rule}.", ("stage", i + 1), ("field", spec.Label), ("rule", BandValueRule(spec)));
                        return null;
                    }
                    if (spec.IsFloat)
                    {
                        b[spec.Key] = float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        b[spec.Key] = int.Parse(raw);
                    }
                }
                arr.Add(b);
            }
            return arr;
        }

        // ---- export -------------------------------------------------------------------

        // Writes <id>.json (the struct-def StructuresInjectPatch.BuildFromJson reads) and
        // <id>_cells.json into this plugin's content/structures/. Flat, not in an <id>/
        // subfolder: ContentInjector.ScanFolder only lists *.json directly in that folder.
        //
        // Bands: every BandDef field is written. BuildFromJson currently reads only layer,
        // outer_frac and hp_sealed; the seed/look keys are written in the same snake_case
        // style so they're ready when the reader learns them.
        private void ExportStructure()
        {
            string id = (_idText ?? "").Trim().ToLowerInvariant();
            if (!IdPattern.IsMatch(id))
            {
                SetStatus(Loc.T("gravebuilder.export.status_id_invalid", "The ID must be lowercase letters, digits and underscores only."), error: true);
                return;
            }
            string displayName = (_nameText ?? "").Trim();
            if (displayName.Length == 0)
            {
                displayName = id;
            }
            if (!int.TryParse((_moneyText ?? "").Trim(), out int money) || money < 0)
            {
                SetStatus(Loc.T("gravebuilder.export.status_money_invalid", "Starting money must be a whole number of 0 or more."), error: true);
                return;
            }
            if (_map.cells.Count == 0)
            {
                SetStatus(Loc.T("gravebuilder.export.status_map_empty", "The map is empty; paint some cells before exporting it."), error: true);
                return;
            }
            if (_bands.Count == 0)
            {
                SetStatus(Loc.T("gravebuilder.export.status_no_stages", "Add at least one stage before exporting."), error: true);
                return;
            }
            JArray bands = BandsToJson(out string bandError);
            if (bands == null)
            {
                SetStatus(bandError, error: true);
                return;
            }

            try
            {
                string dir = StructuresDir();
                Directory.CreateDirectory(dir);

                string cellsFile = id + "_cells.json";
                string defPath = Path.Combine(dir, id + ".json");
                bool overwrote = File.Exists(defPath);

                // Same keys and defaults as tools/gen_wreck_structure.py's struct-defs.
                JObject def = new JObject
                {
                    ["id"] = id,
                    ["display_name"] = displayName,
                    ["description"] = "Built in the in-game map builder.",
                    ["cells_file"] = cellsFile,
                    ["starting_money"] = money,
                    ["sealed_layers"] = true,
                    ["bands"] = bands,
                    ["glyph_count"] = 0,
                    ["green_count"] = -1
                };
                File.WriteAllText(Path.Combine(dir, cellsFile), _map.ToStructureJson("builder"));
                File.WriteAllText(defPath, def.ToString(Formatting.Indented));

                // Only reached once both files are written: run the framework's reload (the
                // RELOAD MODS button's path) so the contract is live straight away. Its own
                // try: the export itself has succeeded either way, and the status must say so.
                string reloadNote;
                try
                {
                    GraveFramework.Plugin.ReloadAll();
                    reloadNote = Loc.T("gravebuilder.export.reload_live", "It's already live in the Contracts list.");
                }
                catch (Exception re)
                {
                    Plugin.Log.LogWarning($"MapBuilder: reload after export failed: {re}");
                    reloadNote = Loc.F("gravebuilder.export.reload_failed", "Reloading the Contracts list failed ({error}); click RELOAD MODS on the main menu's Mods page to try again.", ("error", re.Message));
                }
                // After the reload, so the list reflects the re-injected catalogue.
                RefreshLoadList();
                SetStatus(overwrote
                    ? (bands.Count == 1
                        ? Loc.F("gravebuilder.export.status_overwrote_one", "Overwrote structure '{id}' ({n} cells, {stages} stage) to {dir}. {note}", ("id", id), ("n", _map.cells.Count), ("stages", bands.Count), ("dir", dir), ("note", reloadNote))
                        : Loc.F("gravebuilder.export.status_overwrote_other", "Overwrote structure '{id}' ({n} cells, {stages} stages) to {dir}. {note}", ("id", id), ("n", _map.cells.Count), ("stages", bands.Count), ("dir", dir), ("note", reloadNote)))
                    : (bands.Count == 1
                        ? Loc.F("gravebuilder.export.status_exported_one", "Exported structure '{id}' ({n} cells, {stages} stage) to {dir}. {note}", ("id", id), ("n", _map.cells.Count), ("stages", bands.Count), ("dir", dir), ("note", reloadNote))
                        : Loc.F("gravebuilder.export.status_exported_other", "Exported structure '{id}' ({n} cells, {stages} stages) to {dir}. {note}", ("id", id), ("n", _map.cells.Count), ("stages", bands.Count), ("dir", dir), ("note", reloadNote))));
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.export.status_failed", "Export failed: {error}", ("error", e.Message)), error: true);
            }
        }

        // ---- saved parts --------------------------------------------------------------
        // A part is the clipboard written to disk, entry for entry:
        //     { "name": "My Part",
        //       "cells": [ { "q": -1, "r": 0, "cell": { "layer": 3, "tile": "substrate" } },
        //                  { "q":  0, "r": 0, "cell": null } ] }
        // q/r = ClipCell.Offset (from the clipboard's anchor); "cell" = ClipCell.Cell, i.e.
        // SandboxMap.Cell's layer (1-5) + tile, or null for "empty here" (pasting it erases).
        // Loading a part replaces the clipboard, the same end state as a COPY.

        private static string PartsDir()
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(pluginDir, PartsDirName);
        }

        // The part name as a file name: lowercased, every run of characters outside
        // IdPattern's a-z/0-9/_ squeezed to one '_', trimmed of '_', capped. Null if nothing
        // usable is left (e.g. a name of only punctuation).
        private static string PartFileId(string name)
        {
            string s = PartNameInvalid.Replace((name ?? "").Trim().ToLowerInvariant(), "_").Trim('_');
            if (s.Length > MaxPartFileNameLength)
            {
                s = s.Substring(0, MaxPartFileNameLength).TrimEnd('_');
            }
            return IdPattern.IsMatch(s) ? s : null;
        }

        private static JObject PartToJson(string name, IEnumerable<ClipCell> clip)
        {
            JArray cells = new JArray();
            foreach (ClipCell cc in clip)
            {
                JObject o = new JObject { ["q"] = cc.Offset.x, ["r"] = cc.Offset.y };
                o["cell"] = cc.Cell.HasValue
                    ? new JObject { ["layer"] = (int)cc.Cell.Value.layer, ["tile"] = cc.Cell.Value.tile ?? Substrate }
                    : (JToken)JValue.CreateNull();
                cells.Add(o);
            }
            return new JObject { ["name"] = name, ["cells"] = cells };
        }

        // Parses and validates one part file; throws (with a message saying what's wrong) on
        // anything malformed, so a bad file is skipped whole rather than half-loaded.
        private static List<ClipCell> ReadPart(string path, out string name)
        {
            JObject root = JObject.Parse(File.ReadAllText(path));
            JToken jn = root["name"];
            name = jn != null && jn.Type == JTokenType.String && ((string)jn).Trim().Length > 0
                ? ((string)jn).Trim()
                : Path.GetFileNameWithoutExtension(path);
            if (!(root["cells"] is JArray arr) || arr.Count == 0)
            {
                throw new InvalidDataException("no \"cells\" array, or it is empty");
            }
            List<ClipCell> list = new List<ClipCell>(arr.Count);
            HashSet<Vector2Int> seen = new HashSet<Vector2Int>();
            for (int i = 0; i < arr.Count; i++)
            {
                if (!(arr[i] is JObject o))
                {
                    throw new InvalidDataException($"cells[{i}] is not an object");
                }
                JToken jq = o["q"];
                JToken jr = o["r"];
                if (jq == null || jq.Type != JTokenType.Integer || jr == null || jr.Type != JTokenType.Integer)
                {
                    throw new InvalidDataException($"cells[{i}] needs whole-number \"q\" and \"r\"");
                }
                Vector2Int offset = new Vector2Int((int)jq, (int)jr);
                if (!seen.Add(offset))
                {
                    throw new InvalidDataException($"cells[{i}] repeats offset q {offset.x} r {offset.y}");
                }
                JToken jc = o["cell"];
                if (jc == null)
                {
                    throw new InvalidDataException($"cells[{i}] has no \"cell\" (use null for an empty cell)");
                }
                if (jc.Type == JTokenType.Null)
                {
                    list.Add(new ClipCell { Offset = offset, Cell = null });
                    continue;
                }
                if (!(jc is JObject c))
                {
                    throw new InvalidDataException($"cells[{i}].cell must be an object or null");
                }
                JToken jl = c["layer"];
                if (jl == null || jl.Type != JTokenType.Integer || (long)jl < 1 || (long)jl > 5)
                {
                    throw new InvalidDataException($"cells[{i}].cell.layer must be a whole number from 1 to 5");
                }
                JToken jt = c["tile"];
                string tile;
                // Any tile string is accepted, as SandboxMap.Load accepts any (a map loaded from
                // a hand-edited override can hold tiles outside SpecialTiles, and a part copied
                // from it must still load back); null/missing/empty = substrate, as there.
                if (jt == null || jt.Type == JTokenType.Null)
                {
                    tile = Substrate;
                }
                else if (jt.Type == JTokenType.String)
                {
                    tile = string.IsNullOrEmpty((string)jt) ? Substrate : (string)jt;
                }
                else
                {
                    throw new InvalidDataException($"cells[{i}].cell.tile must be a string");
                }
                list.Add(new ClipCell { Offset = offset, Cell = new SandboxMap.Cell { layer = (byte)(long)jl, tile = tile } });
            }
            return list;
        }

        private void RefreshPartSaveButton()
        {
            if (_partSaveHost == null)
            {
                return;
            }
            _partSaveHost.Clear();
            bool any = _clipboard.Count > 0;
            _partSaveHost.Add(CompactActionBtn(any ? Loc.F("gravebuilder.parts.save_button_count", "SAVE CLIPBOARD AS PART ({n} CELLS)", ("n", _clipboard.Count)) : Loc.T("gravebuilder.parts.save_button", "SAVE CLIPBOARD AS PART"), SavePart, enabled: any));
        }

        private void SavePart()
        {
            CommitAction();
            if (_clipboard.Count == 0)
            {
                SetStatus(Loc.T("gravebuilder.parts.status_clipboard_empty", "The clipboard is empty. Select some cells and press COPY or CUT first."), error: true);
                return;
            }
            string name = (_partNameText ?? "").Trim();
            if (name.Length == 0)
            {
                SetStatus(Loc.T("gravebuilder.parts.status_no_name", "Type a name for the part first."), error: true);
                return;
            }
            string fileId = PartFileId(name);
            if (fileId == null)
            {
                SetStatus(Loc.T("gravebuilder.parts.status_bad_name", "The part name needs at least one letter or digit (it also becomes the file name)."), error: true);
                return;
            }
            try
            {
                string dir = PartsDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileId + ".json");
                bool overwrote = File.Exists(path);
                File.WriteAllText(path, PartToJson(name, _clipboard).ToString(Formatting.Indented));
                RefreshPartsList();
                SetStatus(overwrote
                    ? Loc.F("gravebuilder.parts.status_overwrote", "Overwrote part '{name}' ({n} cells) as {file}.json in {dir}.", ("name", name), ("n", _clipboard.Count), ("file", fileId), ("dir", dir))
                    : Loc.F("gravebuilder.parts.status_saved", "Saved part '{name}' ({n} cells) as {file}.json in {dir}.", ("name", name), ("n", _clipboard.Count), ("file", fileId), ("dir", dir)));
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.parts.status_save_failed", "Saving the part failed: {error}", ("error", e.Message)), error: true);
            }
        }

        // Rescans the parts folder (like RefreshLoadList does for structures). Unreadable or
        // malformed files are skipped with a logged warning and counted in _partsSkipped.
        private void RefreshPartsList()
        {
            _partEntries.Clear();
            _partsSkipped = 0;
            try
            {
                string dir = PartsDir();
                if (Directory.Exists(dir))
                {
                    foreach (string path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            List<ClipCell> cells = ReadPart(path, out string name);
                            _partEntries.Add(new PartEntry
                            {
                                Name = name,
                                Path = path,
                                CellCount = cells.Count,
                                EmptyCount = cells.Count(c => !c.Cell.HasValue)
                            });
                        }
                        catch (Exception e)
                        {
                            _partsSkipped++;
                            Plugin.Log.LogWarning($"MapBuilder: skipping unreadable part '{path}': {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MapBuilder: could not list saved parts: {e.Message}");
            }
            // A rescan can reorder or drop entries, so the selection (an index) is cleared, as
            // RefreshLoadList does.
            _partSelected = -1;
            RebuildPartsList();
            RefreshPartDetail();
        }

        // The parts list as the load list (RebuildLoadList): PartRow slabs under one eyebrow.
        private void RebuildPartsList()
        {
            if (_partsListHost == null)
            {
                return;
            }
            _partsListHost.Clear();
            _partRowPaint.Clear();
            _partHover = -1;
            if (_partsSummary != null)
            {
                _partsSummary.text = _partEntries.Count == 1
                    ? Loc.F("gravebuilder.parts.list_summary_one", "{n} SAVED PART", ("n", _partEntries.Count))
                    : Loc.F("gravebuilder.parts.list_summary_other", "{n} SAVED PARTS", ("n", _partEntries.Count));
            }
            _partsListHost.Add(ListHeader(Loc.T("gravebuilder.parts.list_header", "SAVED PARTS"), first: true));
            if (_partEntries.Count == 0)
            {
                _partsListHost.Add(BodyText_(Loc.T("gravebuilder.parts.list_empty", "No saved parts yet. Select some cells on the map, COPY them, then save them below.")));
                return;
            }
            for (int i = 0; i < _partEntries.Count; i++)
            {
                _partsListHost.Add(PartRow(i, _partEntries[i]));
            }
            PaintPartRows();
        }

        // One parts-list row, as LoadRow minus the thumbnail (a part is bare clipboard cells,
        // nothing StructureSilhouette can draw): the name, the cell count on the right. Click
        // selects, double-click loads it into the clipboard.
        private VisualElement PartRow(int index, PartEntry entry)
        {
            Color family = Accent;
            VisualElement row = new VisualElement { name = "map-builder-part-row" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 18f;
            row.style.paddingRight = 18f;
            row.style.paddingTop = 9f;
            row.style.paddingBottom = 9f;
            row.style.marginBottom = 8f;
            SlabFrame frame = MetaUi.Slab(row, family, dim: false, 12f);

            Label name = MetaUi.Text(entry.Name.ToUpperInvariant(), MetaUi.Face.DisplayBold, 17f, MetaUi.TextTone(family), 1.5f);
            name.pickingMode = PickingMode.Ignore;
            name.style.flexGrow = 1f;
            name.style.flexShrink = 1f;
            // Ellipsis rather than a hard clip, as LoadRow's name.
            name.style.minWidth = 0f;
            name.style.whiteSpace = WhiteSpace.NoWrap;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(name);

            Label count = MetaUi.Text(Loc.F("gravebuilder.parts.row_cells", "{n} CELLS", ("n", entry.CellCount)), MetaUi.Face.Display, 17f, MetaUi.Green, 1.5f);
            count.pickingMode = PickingMode.Ignore;
            count.style.flexShrink = 0f;
            count.style.marginLeft = 16f;
            row.Add(count);

            row.RegisterCallback<ClickEvent>(e =>
            {
                SelectPartEntry(index);
                if (e.clickCount >= 2)
                {
                    LoadSelectedPart();
                }
            });
            row.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _partHover = index;
                PaintPartRows();
            });
            row.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (_partHover == index)
                {
                    _partHover = -1;
                }
                PaintPartRows();
            });
            MetaUi.Lift(row, slide: true);
            _partRowPaint.Add((frame, name, family));
            return AttachTooltip(row, entry.Name);
        }

        // PaintLoadRows' look for the parts list.
        private void PaintPartRows()
        {
            for (int i = 0; i < _partRowPaint.Count; i++)
            {
                (SlabFrame frame, Label name, Color family) = _partRowPaint[i];
                bool selected = i == _partSelected;
                bool hover = i == _partHover;
                MetaUi.PaintSlab(frame, family, dim: false, selected ? 1f : (hover ? 0.85f : 0.45f));
                frame.Stroke = selected ? 3f : 2f;
                name.style.color = selected ? Color.white : MetaUi.TextTone(family);
            }
        }

        private void SelectPartEntry(int index)
        {
            _partSelected = index;
            PaintPartRows();
            RefreshPartDetail();
            SetStatus(Loc.F("gravebuilder.parts.status_selected", "Selected part '{name}'.", ("name", _partEntries[index].Name)));
        }

        private void LoadSelectedPart()
        {
            if (_partSelected < 0 || _partSelected >= _partEntries.Count)
            {
                SetStatus(Loc.T("gravebuilder.parts.status_select_first", "Select a part in the list first."), error: true);
                return;
            }
            LoadPart(_partEntries[_partSelected]);
        }

        // The PARTS tab's detail pane, as RefreshLoadDetail without the preview: the name, its
        // cell count and file, what loading it does, LOAD INTO CLIPBOARD and DELETE.
        private void RefreshPartDetail()
        {
            if (_partDetailHost == null)
            {
                return;
            }
            _partDetailHost.Clear();
            if (_partSelected < 0 || _partSelected >= _partEntries.Count)
            {
                VisualElement empty = MetaUi.Panel(Neutral, 15f, dim: true, 12f);
                empty.style.flexGrow = 1f;
                empty.style.justifyContent = Justify.Center;
                empty.style.alignItems = Align.Center;
                empty.Add(BodyText_(Loc.T("gravebuilder.parts.detail_empty", "Select a part from the list to see it here.")));
                _partDetailHost.Add(empty);
                return;
            }

            PartEntry entry = _partEntries[_partSelected];
            // Fills the whole detail column height and centres this (short, no-preview)
            // content in it, as RefreshLoadDetail does, rather than a small text block
            // pinned to the top with empty space under it.
            VisualElement panel = MetaUi.Panel(Accent, 15f, dim: true, 14f);
            panel.style.flexGrow = 1f;
            panel.style.justifyContent = Justify.Center;
            _partDetailHost.Add(panel);

            panel.Add(MetaUi.Text(entry.Name.ToUpperInvariant(), MetaUi.Face.DisplayBold, 28f, MetaUi.TextTone(Accent), 2f));

            string cells = Loc.F("gravebuilder.parts.detail_sub", "SAVED PART · {n} cells{empty_note} · {file}", ("n", entry.CellCount), ("empty_note", entry.EmptyCount > 0 ? Loc.F("gravebuilder.parts.detail_empty_note", " ({empty} empty, which erase when pasted)", ("empty", entry.EmptyCount)) : ""), ("file", Path.GetFileName(entry.Path)));
            Label sub = MetaUi.Text(cells, MetaUi.Face.Body, 16f, DimText, 1.5f, wrap: true);
            sub.style.marginTop = 6f;
            sub.style.marginBottom = 20f;
            panel.Add(sub);

            Label descLabel = MetaUi.Text(Loc.T("gravebuilder.parts.detail_desc", "A saved clipboard. Loading it replaces the clipboard and takes you back to the map in paste mode: left-click stamps it. Deleting it removes its file; the clipboard is unchanged either way."), MetaUi.Face.Body, 17f, BodyText, 0.5f, wrap: true);
            descLabel.style.maxWidth = 640f;
            descLabel.style.marginBottom = 24f;
            panel.Add(descLabel);

            VisualElement actions = Row();
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.alignItems = Align.Center;
            VisualElement loadBtn = CompactActionBtn(Loc.T("gravebuilder.parts.load_button", "LOAD INTO CLIPBOARD"), () => LoadPart(entry), family: Accent, size: 17f, padX: 36f);
            loadBtn.style.marginRight = 10f;
            actions.Add(AttachTooltip(loadBtn, Loc.T("gravebuilder.parts.load_tooltip", "Replaces the clipboard with this part and takes you back to the map in paste mode: left-click stamps it.")));
            actions.Add(AttachTooltip(CompactActionBtn(Loc.T("gravebuilder.parts.delete_button", "DELETE"), () => DeletePart(entry), family: MetaUi.Crimson, size: 17f, padX: 36f),
                Loc.T("gravebuilder.parts.delete_tooltip", "Delete this part's file. The clipboard is unchanged.")));
            panel.Add(actions);
        }

        // Replaces the clipboard with the part, ready for PASTE (same end state as COPY). Re-reads
        // the file so what loads is what's on disk now.
        private void LoadPart(PartEntry entry)
        {
            CommitAction();
            List<ClipCell> cells;
            string name;
            try
            {
                cells = ReadPart(entry.Path, out name);
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.parts.status_load_failed", "Loading part '{name}' failed: {error}", ("name", entry.Name), ("error", e.Message)), error: true);
                RefreshPartsList();
                return;
            }
            _clipboard.Clear();
            _clipboard.AddRange(cells);
            // So re-saving after an edit overwrites the same part.
            _partNameText = name;
            _partNameField?.SetValueWithoutNotify(name);
            // Go straight into paste placement — loading a part is pointless unless you're
            // about to stamp it, so skip the extra PASTE click.
            _pasteMode = true;
            _canvas?.Repaint();
            RefreshClipboardButtons();
            int empty = cells.Count(c => !c.Cell.HasValue);
            SetStatus(Loc.F("gravebuilder.parts.status_loaded", "Loaded part '{name}' ({n} cells{empty_note}) and entered paste mode. Left-click to stamp it; right-click or CANCEL PASTE stops.", ("name", name), ("n", cells.Count), ("empty_note", empty > 0 ? Loc.F("gravebuilder.parts.status_loaded_empty_note", ", {empty} empty", ("empty", empty)) : "")));
            // Done picking a part: back to the painting view to stamp it.
            CloseLibrary();
        }

        // Deletes the file only; the clipboard (even if it came from this part) is untouched.
        private void DeletePart(PartEntry entry)
        {
            try
            {
                if (File.Exists(entry.Path))
                {
                    File.Delete(entry.Path);
                }
                RefreshPartsList();
                SetStatus(Loc.F("gravebuilder.parts.status_deleted", "Deleted part '{name}' ({file}). The clipboard is unchanged.", ("name", entry.Name), ("file", Path.GetFileName(entry.Path))));
            }
            catch (Exception e)
            {
                SetStatus(Loc.F("gravebuilder.parts.status_delete_failed", "Deleting part '{name}' failed: {error}", ("name", entry.Name), ("error", e.Message)), error: true);
            }
        }
    }
}
