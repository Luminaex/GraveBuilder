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

namespace SandboxMapUnlock
{
    // In-game map builder (Ctrl+F7, or MAP BUILDER on the title screen): paint a SandboxMap on
    // a 2D hex grid, then either save it as the sandbox override (the next real mining sortie
    // loads it, see Patches.cs) or export it as a content/structures/ contract for the
    // framework's Ctrl+F6 hot-reload. Existing structures (our own exports and the game's
    // catalogue) can be loaded back in to edit, and our own exports deleted (with a confirm).
    // Button explanations are hover tooltips (AttachTooltip), not static paragraphs.
    //
    // Styled with the game's own UI toolkits (TerminalUi.Frame shell, MetaUi buttons, heads,
    // table rows and callouts) so it reads like one of the game's menus; the accent family is
    // MetaUi.Teal (Campaign is orange, Overtime red). Owns its own UIDocument like
    // BoostersScreen.Create. Destroyed on close and rebuilt on open (MainMenuBoot's
    // convention); the map, the tool, the undo history and the form values are static, so
    // closing and reopening keeps the work.
    //
    // Layout: two views. The painting view: the HexCanvas fills the whole frame body, a row of
    // toggle keys sits over its top edge (PALETTE, TOOLS, MAP on the right), and each key
    // shows/hides its own floating panel dropped down under it; any number can be open at
    // once. These are the "ongoing tool" panels you keep open while painting. The library
    // view (Advanced only, STRUCTURES & PARTS in the foot band): a full-screen page that
    // replaces the canvas and panels while it's open, styled like the game's Contracts list
    // (section heads, slab rows), with three sections: LOAD STRUCTURE, LOAD PART (and save
    // the clipboard as one), EXPORT AS STRUCTURE (contract details + band editor). BACK TO MAP
    // returns to the painting view with the same panels open; the map, selection, clipboard
    // and undo history are static, so the swap never touches them.
    //
    // Two modes, switched by the MODE key in the foot band and remembered in
    // mapbuilder_prefs.json: Simple (default; PALETTE/TOOLS/MAP with colours + erase, brush
    // size, undo/redo, reset view, sandbox-override actions) and Advanced (adds the live stats
    // readout in the foot band, the library view, and the full PALETTE/TOOLS/MAP: special
    // tiles, fill/mirror, line/circle drag tools, select (with auto-fill loops) +
    // copy/cut/paste, a brush-footprint preview on the canvas, radius/grow). The brush size
    // control (slider + number box) is the same in both modes. Saved parts are the clipboard
    // written to parts/<name>.json next to the DLL.
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
        private const int DefaultStartingMoney = 500;
        private const string DefaultId = "builder_map";
        private const string DefaultName = "Builder Map";

        // Simple/Advanced layout preference, next to this plugin's DLL (Plugin.OverrideFilePath's
        // pattern), so the choice survives game restarts.
        private const string PrefsFileName = "mapbuilder_prefs.json";
        private const string PrefsAdvancedKey = "advancedMode";

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
            ["explosive"] = "Explosive",
            ["lightning"] = "Lightning",
            ["void"] = "Void",
            ["swarm"] = "Swarm",
            ["special_1"] = "Laser Shard",
            ["special_2"] = "Breacher Shard",
            ["special_3"] = "Dynamo Shard",
            ["meta_1"] = "Pulse Shard",
        };

        private static readonly Regex IdPattern = new Regex("^[a-z0-9_]+$");

        // Saved parts (the library view's LOAD PART section): one JSON file per part in a parts/ folder next to
        // this plugin's DLL. The file name is the part name squeezed down to IdPattern's
        // character set (runs of anything else become one '_'), capped in length.
        private const string PartsDirName = "parts";
        private const int MaxPartFileNameLength = 48;
        private static readonly Regex PartNameInvalid = new Regex("[^a-z0-9_]+");

        // Panel accent family (see class comment).
        private static readonly Color Accent = MetaUi.Teal;
        private static readonly Color AccentLt = MetaUi.TealLt;
        private static readonly Color BodyText = MetaUi.Fade(MetaUi.Ink, 0.82f);
        private static readonly Color DimText = MetaUi.Fade(MetaUi.Ink, 0.6f);
        // Family for "off" toggles and game-catalogue list entries.
        private static readonly Color Neutral = new Color(0.55f, 0.58f, 0.62f);
        private static readonly Color SelectedEdge = Color.white;

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
            new BandField { Key = "layer", Label = "Substrate colour", Default = "1", Min = 1, Max = 5,
                Tooltip = "Which painted colour this band is made of: every cell painted this colour belongs to this band. If two bands pick the same colour, the first one gets all of its tiles." },
            // Cosmetic only (verified 2026-11-22): the only reader of outerFrac anywhere in the
            // game is StructureHexElement, the little hex icon in the Contracts menu — it draws
            // this band's ring at this fraction of that icon's radius. No sortie/mining code
            // reads it; a band's actual tile membership is purely by colour match
            // (StructureDef.BandIndexOfLayer), and unlock order is purely the bands[] array
            // order. Label reflects that it's just the preview icon, not a real boundary.
            new BandField { Key = "outer_frac", Label = "Preview icon ring size", Default = "1", IsFloat = true, Min = 0, Max = 1,
                Tooltip = "Cosmetic only. How far out this band's ring is drawn on the little hex icon in the Contracts list, as a fraction of the icon's radius (0 to 1). It doesn't change which tiles are in the band." },
            new BandField { Key = "hp_sealed", Label = "Tile health while sealed", Default = "75", Min = 0, Max = int.MaxValue,
                Tooltip = "Hit points each of this band's tiles has while the band is still sealed (locked). Higher = slower to mine through. Default 75." },
            new BandField { Key = "special_1", Label = "Laser shards", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many Laser Shard pickups (the shard currency used to research Mods) are seeded into this band's tiles." },
            new BandField { Key = "special_2", Label = "Breacher shards", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many Breacher Shard pickups (the shard currency used to research Mods) are seeded into this band's tiles." },
            new BandField { Key = "special_3", Label = "Dynamo shards", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many Dynamo Shard pickups (the shard currency used to research Mods) are seeded into this band's tiles." },
            new BandField { Key = "meta_1", Label = "Pulse shards", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many Pulse Shard pickups (the shard currency used to research Mods) are seeded into this band's tiles." },
            new BandField { Key = "explosive", Label = "Explosive", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many hidden Explosive enemy encounters are seeded into this band's tiles. Destroying such a tile (or mining close to it) wakes the enemy." },
            new BandField { Key = "lightning", Label = "Lightning", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many hidden Lightning enemy encounters are seeded into this band's tiles. Destroying such a tile (or mining close to it) wakes the enemy." },
            new BandField { Key = "void", Label = "Void", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many hidden Void enemy encounters are seeded into this band's tiles. Destroying such a tile (or mining close to it) wakes the enemy." },
            new BandField { Key = "swarm", Label = "Swarm", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "How many hidden Swarm enemy encounters are seeded into this band's tiles. Destroying such a tile (or mining close to it) wakes the enemy." },
            // TileField.LookLayer: once the band is unsealed its tiles are drawn with the look
            // of layer unsealedLook instead of their painted layer (0 or less = painted look).
            new BandField { Key = "unsealed_look", Label = "Core look after unlocking", Default = "0", Min = 0, Max = int.MaxValue,
                Tooltip = "Cosmetic only, no gameplay effect. Once this band unlocks, its tiles are drawn with the look of another colour: 1 Grey, 2 Blue, 3 Pink, 4 Gold, 5 Purple. 0 keeps the colour you painted." },
        };

        // BandFields indices, for the band editor's grouped layout (BandRow).
        private const int BandLayer = 0;
        private const int BandOuterFrac = 1;
        private const int BandHpSealed = 2;
        private const int BandShardsFrom = 3;
        private const int BandShardsTo = 6;
        private const int BandEnemiesFrom = 7;
        private const int BandEnemiesTo = 10;
        private const int BandUnsealedLook = 11;

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

        // Library view: content column width cap (it's a list page, not a full-bleed canvas)
        // and the inner list heights, so a long game catalogue doesn't push LOAD PART and
        // EXPORT far down the page.
        private const float LibraryMaxWidth = 1700f;
        private const float LoadListMaxHeight = 620f;
        private const float PartsListMaxHeight = 460f;
        // Warnings in the export form (a valid value with a consequence worth knowing).
        private static readonly Color WarnText = TerminalUi.Orange;

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
        // Advanced-only LINE / CIRCLE drag tools (see "line & circle tools" below). Exclusive
        // with each other and with fill mode and SELECT; paused in Simple like fill/mirror.
        private static bool _lineMode;
        private static bool _circleMode;

        // Advanced-only selection + clipboard (see "selection & clipboard" below). The
        // selection is a set of axial positions, independent of what's painted there; the
        // clipboard is the last Copy/Cut, as offsets from its anchor. Paste placement is
        // cancelled on switching to Simple; SELECT is just paused, like fill/mirror.
        private static bool _selectMode;
        private static bool _pasteMode;
        // AUTO-FILL LOOPS: after each SELECT add-drag, cells the selection fully encloses are
        // selected too (FillEnclosedSelection). Only affects SELECT; Advanced only.
        private static bool _autoFillLoops;
        private static readonly HashSet<Vector2Int> _selection = new HashSet<Vector2Int>();
        private static readonly List<ClipCell> _clipboard = new List<ClipCell>();

        // Simple (default) or Advanced panel set. Loaded from PrefsFileName the first time Open
        // runs, saved on every toggle.
        private static bool _advancedMode;
        private static bool _prefsLoaded;

        // Which floating panels are open; kept across close/reopen and mode toggles (an
        // Advanced-only panel's flag just waits while Simple doesn't build it). PALETTE starts
        // open so there's something to paint with straight away.
        private static readonly Dictionary<string, bool> _panelOpen = new Dictionary<string, bool> { [PanelPalette] = true };

        private static readonly List<Edit> _undo = new List<Edit>();
        private static readonly List<Edit> _redo = new List<Edit>();

        // Form values, kept as the raw text so a half-typed value survives a rebuild.
        private static string _radiusText = DefaultRadius.ToString();
        private static string _brushText = "0";
        private static string _idText = DefaultId;
        private static string _nameText = DefaultName;
        private static string _moneyText = DefaultStartingMoney.ToString();
        private static string _partNameText = "";
        private static readonly List<string[]> _bands = new List<string[]> { DefaultBand() };
        // Bands whose cosmetic fields are expanded in the band editor, by their _bands value
        // array (reference equality), so removing a band doesn't shift the others' state.
        private static readonly HashSet<string[]> _bandCosmeticShown = new HashSet<string[]>();

        private static string _lastStatus;
        private static bool _lastStatusError;

        // ---- per-instance UI -------------------------------------------------------------

        private UIDocument _doc;
        private PanelSettings _panelCopy;
        private HexCanvas _canvas;
        // Which view Build shows: the painting view (false) or the full-screen library (true,
        // Advanced only). Per instance, so reopening the builder always starts on the canvas.
        private bool _libraryOpen;
        // Fills the frame body: the canvas plus everything floating over it (painting view),
        // or the library page (library view). Either way the tooltip box lives in it.
        private VisualElement _stage;
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
        private VisualElement _partSaveHost;
        private VisualElement _partsListHost;
        private TextField _partNameField;
        private TextField _radiusField;
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

        private readonly List<LoadEntry> _loadEntries = new List<LoadEntry>();
        private readonly List<VisualElement> _loadHolders = new List<VisualElement>();
        // Per load-list row (same index as _loadEntries/_loadHolders): its slab, name label
        // and colour family, repainted for hover/selection (PaintLoadRows).
        private readonly List<(SlabFrame frame, Label name, Color family)> _loadRowPaint = new List<(SlabFrame, Label, Color)>();
        private int _loadSelected = -1;
        private int _loadHover = -1;

        private readonly List<PartEntry> _partEntries = new List<PartEntry>();
        private int _partsSkipped;

        // Hover tooltip (see "hover tooltips" below): one shared box on the stage, rebuilt
        // with it, and the element whose text it is showing right now.
        private VisualElement _tooltip;
        private Label _tooltipText;
        private VisualElement _tooltipOwner;

        // Catalogue entries for our own exports deleted this session (DeleteLocalStructure):
        // the framework injected them into StructureCatalog at load, and they stay there until
        // the next Ctrl+F6, so without this they'd reappear in the load list as grey game
        // structures the moment their local file is gone. Exact references, so a re-export +
        // Ctrl+F6 (a new StructureDef object) shows normally. Static: survives close/reopen.
        private static readonly HashSet<StructureDef> _deletedInjected = new HashSet<StructureDef>();

        // Open action (stroke / fill / new / load / grow): prior state of every touched cell.
        private Dictionary<Vector2Int, SandboxMap.Cell?> _pending;
        private string _pendingLabel;
        private int _pendingRadius;

        public static bool IsOpen => _instance != null;

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public static void Open()
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
            if (!_prefsLoaded)
            {
                _advancedMode = LoadAdvancedPref();
                _prefsLoaded = true;
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
                Close();
                return;
            }
            screen.Build(screen._doc.rootVisualElement);
            Plugin.Log.LogInfo("MapBuilder: opened.");
        }

        public static void Close()
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

        // Rebuilds the whole screen from the static state: on open, when the Simple/Advanced
        // toggle flips, and when the library view opens or closes. Simple and Advanced are two
        // separate sets of floating panels (SimplePanels / AdvancedPanels) over the same shared
        // state, so switching never loses work; the library view (BuildLibraryView) is a
        // second top-level view over that same state, branched on _libraryOpen.
        private void Build(VisualElement root)
        {
            root.Clear();
            // Element references from a previous build point at detached elements; drop them so
            // the Refresh* helpers no-op for sections the current view doesn't show.
            _stage = null;
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
            _loadHolders.Clear();
            _partSaveHost = null;
            _partsListHost = null;
            _partNameField = null;
            _radiusField = null;
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

            // The library is Advanced-only (Simple has nothing to show in it).
            if (!_advancedMode)
            {
                _libraryOpen = false;
            }

            // Shell: the game's terminal frame (chamfered glass panel, header band, foot band).
            TerminalUi.Frame(root, Accent, _libraryOpen ? "MAP BUILDER · LIBRARY" : "MAP BUILDER", out VisualElement body, out VisualElement foot, "map-builder-frame");

            // A hair of inset so the canvas doesn't paint over the frame's own edge stroke.
            body.style.flexDirection = FlexDirection.Column;
            body.style.paddingLeft = 3f;
            body.style.paddingRight = 3f;
            body.style.paddingTop = 0f;
            body.style.paddingBottom = 0f;

            if (_libraryOpen)
            {
                BuildLibraryView(body, foot);
            }
            else
            {
                BuildPaintingView(body, foot);
            }

            UpdateStatsNow();
            if (_lastStatus != null)
            {
                SetStatus(_lastStatus, _lastStatusError, log: false);
            }
            else
            {
                SetStatus("Map builder ready.", log: false);
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
        }

        // The painting view: canvas, key bar, floating panels; foot band with the hovered-cell
        // readout, the stats readout (Advanced) and the screen keys.
        private void BuildPaintingView(VisualElement body, VisualElement foot)
        {
            // Foot: hovered-cell readout on the left; on the right, the stats readout
            // (Advanced) then the keys (library, mode toggle, CLOSE). The right group is its
            // own row so the readout sits beside the keys rather than mid-band; FootKeys is
            // given that row, so the foot's right padding is cleared here as FootKeys would.
            _hoverLabel = MetaUi.Text("CELL  -", MetaUi.Face.BodyBold, 18f, DimText, 2f);
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
            if (_advancedMode)
            {
                right.Add(BuildStatsReadout());
            }
            VisualElement keys = TerminalUi.FootKeys(right, 18f);
            keys.style.flexShrink = 0f;
            if (_advancedMode)
            {
                keys.Add(TerminalUi.FootKey("map-builder-library", "STRUCTURES & PARTS", Accent, OpenLibrary, primary: false, 440f));
            }
            keys.Add(TerminalUi.FootKey("map-builder-mode", _advancedMode ? "MODE: ADVANCED" : "MODE: SIMPLE", Accent, ToggleMode, primary: false, 330f));
            keys.Add(TerminalUi.FootKey("map-builder-close", "CLOSE", Accent, Close, primary: true, 280f));

            // Stage: fills the body. Children in paint/pick order (later = on top): canvas,
            // status callout, key bar, panels. No padding, so its local space is the space the
            // absolute overlays are positioned in.
            _stage = new VisualElement { name = "map-builder-stage" };
            _stage.style.flexGrow = 1f;
            _stage.style.flexDirection = FlexDirection.Column;
            _stage.style.overflow = Overflow.Hidden;
            body.Add(_stage);

            // Kept across a mode-toggle rebuild (re-parented into the new stage) so zoom and pan
            // survive; HexCanvas holds no panel-attach state, only the view and stroke state.
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

            _panelDefs = _advancedMode ? AdvancedPanels() : SimplePanels();

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

        // Live map stats (Advanced, painting view): a compact two-line block in the foot band
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
            ReadoutItem(totals, "cells", "CELLS", null);
            ReadoutItem(totals, "radius", "RADIUS", null);
            ReadoutItem(totals, "specials", "SPECIAL", null);

            VisualElement layers = Row();
            layers.style.alignItems = Align.Center;
            layers.style.justifyContent = Justify.FlexEnd;
            box.Add(layers);
            for (int l = 1; l <= 5; l++)
            {
                ReadoutItem(layers, "L" + l, null, StructureSilhouette.LayerColour(l));
            }

            return AttachTooltip(box, "Live map stats. Top line: painted cells, map radius, and special tiles (enemy markers and shards). Bottom line: painted cells of each substrate colour (Grey, Blue, Pink, Gold, Purple).");
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

        // ---- library view ------------------------------------------------------------
        // A full-screen page in place of the canvas and its panels (Advanced only), laid out
        // like the game's Contracts list (ModeSelectController.FillOvertimeList / AddRow):
        // big section heads, eyebrow sub-heads, and slab rows that light up on hover and
        // selection. Built from this file's own MetaUi/TerminalUi helpers, not the game's
        // screen. Opening/closing it is just a rebuild; it only reads and writes the load
        // list, the parts list and the export form, never the map/selection/clipboard/undo
        // (except through the same LOAD actions the old panels had).

        private void OpenLibrary()
        {
            if (!_advancedMode)
            {
                return;
            }
            CommitAction();
            _libraryOpen = true;
            if (_doc != null && _doc.rootVisualElement != null)
            {
                Build(_doc.rootVisualElement);
            }
        }

        // Back to the painting view; _panelOpen is untouched, so the same panels reopen.
        private void CloseLibrary()
        {
            if (!_libraryOpen)
            {
                return;
            }
            _libraryOpen = false;
            if (_doc != null && _doc.rootVisualElement != null)
            {
                Build(_doc.rootVisualElement);
            }
        }

        private void BuildLibraryView(VisualElement body, VisualElement foot)
        {
            // Foot: what this page is on the left; BACK TO MAP (primary) + CLOSE on the right.
            foot.Add(MetaUi.Text("LOAD A STRUCTURE OR A SAVED PART, OR EXPORT THIS MAP", MetaUi.Face.BodyBold, 18f, DimText, 2f));
            VisualElement keys = TerminalUi.FootKeys(foot, 18f);
            keys.Add(TerminalUi.FootKey("map-builder-back", "BACK TO MAP", Accent, CloseLibrary, primary: true, 380f));
            keys.Add(TerminalUi.FootKey("map-builder-close", "CLOSE", Accent, Close, primary: false, 280f));

            // Stage: the page scroll, then the status line under it (in flow, so it never
            // covers a row), then the tooltip box on top.
            _stage = new VisualElement { name = "map-builder-library" };
            _stage.style.flexGrow = 1f;
            _stage.style.flexDirection = FlexDirection.Column;
            _stage.style.overflow = Overflow.Hidden;
            body.Add(_stage);

            ScrollView page = new ScrollView(ScrollViewMode.Vertical);
            page.style.flexGrow = 1f;
            page.style.flexShrink = 1f;
            page.style.minHeight = 0f;
            _stage.Add(page);
            MetaUi.AccentScroll(page, new Color(1f, 1f, 1f, 0.06f), MetaUi.Fade(Accent, 0.45f), MetaUi.Fade(Accent, 0.75f));

            VisualElement col = new VisualElement { name = "map-builder-library-column" };
            col.style.alignSelf = Align.Center;
            col.style.width = Length.Percent(100f);
            col.style.maxWidth = LibraryMaxWidth;
            col.style.paddingLeft = 40f;
            col.style.paddingRight = 40f;
            col.style.paddingTop = 20f;
            col.style.paddingBottom = 48f;
            page.contentContainer.Add(col);

            _statusHost = new VisualElement { pickingMode = PickingMode.Ignore };
            _statusHost.style.flexShrink = 0f;
            _statusHost.style.marginLeft = Length.Percent(24f);
            _statusHost.style.marginRight = Length.Percent(24f);
            _statusHost.style.marginTop = 8f;
            _statusHost.style.marginBottom = PanelEdge;
            _stage.Add(_statusHost);

            BuildTooltip();

            BuildLoadStructureSection(col);
            BuildLoadPartSection(col);
            BuildExportSection(col);

            RefreshLoadList();
            RefreshPartsList();
            RefreshPartSaveButton();
            RebuildBands();
            RefreshExportHints();
        }

        // Library section head: the game's PanelHead, with a rule above every section but the
        // first so the three read as separate blocks on one long page.
        private static VisualElement SectionHead(string text, bool first)
        {
            VisualElement wrap = new VisualElement();
            if (!first)
            {
                wrap.style.marginTop = 46f;
                wrap.style.paddingTop = 30f;
                wrap.style.borderTopWidth = 2f;
                wrap.style.borderTopColor = MetaUi.Fade(Accent, 0.3f);
            }
            Label head = MetaUi.PanelHead(text, Accent);
            head.style.marginTop = first ? 6f : 0f;
            head.style.marginBottom = 6f;
            wrap.Add(head);
            return wrap;
        }

        // Sub-heading inside a library section, as the Contracts list's ListHeader
        // (MetaUi.Eyebrow) in this screen's accent.
        private static Label ListHeader(string text, bool first = false)
        {
            Label label = MetaUi.Eyebrow(text);
            label.style.color = AccentLt;
            label.style.marginTop = first ? 14f : 28f;
            label.style.marginBottom = 12f;
            return label;
        }

        // An inner list with its own scroll that grows to its rows up to maxHeight.
        private static ScrollView ListScroll(float maxHeight)
        {
            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 0f;
            scroll.style.flexShrink = 0f;
            scroll.style.maxHeight = maxHeight;
            scroll.style.marginBottom = 12f;
            MetaUi.AccentScroll(scroll, new Color(1f, 1f, 1f, 0.06f), MetaUi.Fade(Accent, 0.45f), MetaUi.Fade(Accent, 0.75f));
            scroll.contentContainer.style.paddingRight = 14f;
            return scroll;
        }

        // LOAD STRUCTURE: the merged local-exports / game-catalogue list (was the STRUCTURES
        // panel), LOAD SELECTED + REFRESH LIST.
        private void BuildLoadStructureSection(VisualElement content)
        {
            content.Add(SectionHead("LOAD STRUCTURE", first: true));
            content.Add(BodyText_("Click a structure to select it (double-click loads it straight away). Your own exports (teal) also fill in the EXPORT AS STRUCTURE form below; game structures (grey) reset it. Loading replaces the map and can be undone."));
            ScrollView loadScroll = ListScroll(LoadListMaxHeight);
            content.Add(loadScroll);
            _loadListHost = loadScroll.contentContainer;
            content.Add(PairRow(
                ActionBtn("LOAD SELECTED", LoadSelected),
                ActionBtn("REFRESH LIST", () =>
                {
                    RefreshLoadList();
                    SetStatus($"Found {_loadEntries.Count} loadable structures.");
                })));
        }

        // LOAD PART: save the clipboard as a named part, and the saved-parts list (was the
        // PARTS panel).
        private void BuildLoadPartSection(VisualElement content)
        {
            content.Add(SectionHead("LOAD PART", first: false));
            content.Add(BodyText_("Parts are saved clipboards (COPY/CUT in TOOLS), kept between sessions. Loading one puts it in the clipboard and takes you back to the map, ready to PASTE."));

            content.Add(ListHeader("SAVED PARTS", first: true));
            ScrollView partsScroll = ListScroll(PartsListMaxHeight);
            content.Add(partsScroll);
            _partsListHost = partsScroll.contentContainer;
            content.Add(ActionBtn("REFRESH LIST", () =>
            {
                RefreshPartsList();
                SetStatus($"Found {_partEntries.Count} saved part{(_partEntries.Count == 1 ? "" : "s")}{(_partsSkipped > 0 ? $" ({_partsSkipped} unreadable file{(_partsSkipped == 1 ? "" : "s")} skipped, see the log)" : "")}.");
            }));

            content.Add(ListHeader("SAVE THE CLIPBOARD AS A PART"));
            _partNameField = Field("Part name", _partNameText, v => _partNameText = v);
            AttachTooltip(_partNameField, "Also becomes the file name (lowercased, other characters turned into _). Saving under a name that already exists overwrites that part.");
            content.Add(_partNameField);
            _partSaveHost = new VisualElement();
            content.Add(_partSaveHost);
        }

        // EXPORT AS STRUCTURE: contract details (with inline validation) + the band editor
        // (was the EXPORT panel).
        private void BuildExportSection(VisualElement content)
        {
            content.Add(SectionHead("EXPORT AS STRUCTURE", first: false));
            content.Add(BodyText_("Saves this map as a contract in content/structures/. Press Ctrl+F6 afterwards to load it into the game's Contracts list."));

            content.Add(ListHeader("CONTRACT DETAILS", first: true));
            _idField = Field("Contract ID", _idText, v =>
            {
                _idText = v;
                RefreshExportHints();
            });
            _idField.style.maxWidth = 1000f;
            AttachTooltip(_idField,"The contract's internal name, and its file name. Lowercase letters, digits and _ only. Exporting with one of your own export's IDs overwrites it.");
            content.Add(_idField);
            _idHint = FieldHint();
            content.Add(_idHint);

            _nameField = Field("Display name", _nameText, v =>
            {
                _nameText = v;
                RefreshExportHints();
            });
            _nameField.style.maxWidth = 1000f;
            AttachTooltip(_nameField,"The name shown in the game's Contracts list. Leave it blank to use the ID.");
            content.Add(_nameField);
            _nameHint = FieldHint();
            content.Add(_nameHint);

            _moneyField = Field("Starting money", _moneyText, v =>
            {
                _moneyText = v;
                RefreshExportHints();
            });
            _moneyField.style.maxWidth = 560f;
            AttachTooltip(_moneyField, "How much money the player starts this contract with. A whole number, 0 or more.");
            content.Add(_moneyField);
            _moneyHint = FieldHint();
            content.Add(_moneyHint);

            content.Add(ListHeader("BANDS"));
            content.Add(BodyText_("A band is one substrate colour of your map: its tiles are every cell painted that colour. Bands unlock in this order, band 1 first. Hover a field for what it does. Shard and enemy spawn counts are saved with the export, but the framework doesn't apply them in game yet."));
            _bandHost = new VisualElement();
            content.Add(_bandHost);
            content.Add(AttachTooltip(CompactActionBtn("ADD BAND", () =>
            {
                _bands.Add(DefaultBand());
                RebuildBands();
            }), "Adds another band at the end (it unlocks after all the others)."));
            content.Add(CompactActionBtn("EXPORT AS STRUCTURE", ExportStructure, size: 18f, padX: 44f));
        }

        // Feedback line under a form field, lined up with the field's input (Field's label
        // column is 230 wide).
        private static Label FieldHint()
        {
            Label hint = MetaUi.Text("", MetaUi.Face.Body, 15f, DimText, 0.5f, wrap: true);
            hint.style.marginLeft = 230f;
            hint.style.marginTop = -4f;
            hint.style.marginBottom = 12f;
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
                SetHint(_idHint, "Required: lowercase letters, digits and _ only (e.g. my_map_1).", MetaUi.Crimson);
            }
            else if (!IdPattern.IsMatch(id))
            {
                SetHint(_idHint, "Only letters a-z, digits 0-9 and _ are allowed: no spaces, dashes or other symbols.", MetaUi.Crimson);
            }
            else
            {
                string lower = raw != id ? $"Saved in lowercase as '{id}'. " : "";
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
                    SetHint(_idHint, $"{lower}The game structure '{game.Label}' already uses this ID, so the framework will skip your export. Pick another ID.", WarnText);
                }
                else if (exists)
                {
                    SetHint(_idHint, $"{lower}Exporting overwrites your existing export '{id}'.", WarnText);
                }
                else
                {
                    SetHint(_idHint, $"{lower}Writes {id}.json and {id}_cells.json.", DimText);
                }
            }

            string name = (_nameText ?? "").Trim();
            SetHint(_nameHint, name.Length == 0
                ? (IdPattern.IsMatch(id) ? $"Blank: the ID '{id}' is used as the name." : "Blank: the ID is used as the name.")
                : $"Shown in the Contracts list as {name.ToUpperInvariant()}.", DimText);

            if (!int.TryParse((_moneyText ?? "").Trim(), out int money) || money < 0)
            {
                SetHint(_moneyHint, "Must be a whole number, 0 or more (digits only, no commas).", MetaUi.Crimson);
            }
            else
            {
                SetHint(_moneyHint, $"The player starts with {money}.", DimText);
            }
        }

        // ---- floating panels ---------------------------------------------------------

        // Simple mode: only what a first-time user needs — the 5 substrate colours + erase,
        // the brush size slider (the same control Advanced uses), undo/redo, reset view, and
        // the sandbox-override actions.
        // Widths fit each panel's widest row (PairRows of two full-size keys need ~600).
        private List<PanelDef> SimplePanels()
        {
            return new List<PanelDef>
            {
                new PanelDef { Id = PanelPalette, Label = "PALETTE", Width = 480f, Build = BuildSimplePalette },
                new PanelDef { Id = PanelTools, Label = "TOOLS", Width = 480f, Build = BuildSimpleTools },
                new PanelDef { Id = PanelMap, Label = "MAP", Width = 640f, AnchorRight = true, Build = BuildSimpleMap },
            };
        }

        // Advanced mode: the full builder's painting panels. Key order = bar order, MAP last
        // (right end). Stats live in the foot band (BuildStatsReadout); loading structures and
        // parts and exporting live in the library view (BuildLibraryView).
        private List<PanelDef> AdvancedPanels()
        {
            return new List<PanelDef>
            {
                new PanelDef { Id = PanelPalette, Label = "PALETTE", Width = 480f, Build = BuildAdvancedPalette },
                new PanelDef { Id = PanelTools, Label = "TOOLS", Width = 620f, Build = BuildAdvancedTools },
                new PanelDef { Id = PanelMap, Label = "MAP", Width = 640f, AnchorRight = true, Build = BuildAdvancedMap },
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
            _stage.Add(_tooltip);
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
            if (_tooltip == null || _stage == null)
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
            if (_tooltip == null || _stage == null)
            {
                return;
            }
            Vector2 p = _stage.WorldToLocal(panelPos);
            float w = _stage.contentRect.width;
            float h = _stage.contentRect.height;
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

        // Simple PALETTE: the 5 substrate colours + erase.
        private void BuildSimplePalette(VisualElement content)
        {
            content.Add(Head("PALETTE", first: true));
            VisualElement swatches = Row();
            swatches.style.flexWrap = Wrap.Wrap;
            for (int layer = 1; layer <= 5; layer++)
            {
                int l = layer;
                Color c = StructureSilhouette.LayerColour(l);
                swatches.Add(ToolHolder(SubstrateKey(l), MetaUi.Key(LayerColourName(l), () => SelectTool(Substrate, l), 120f, c), c, LayerCaption(l)));
            }
            swatches.Add(ToolHolder(Erase, MetaUi.Key("ERASE", () => SelectTool(Erase, _layer), 120f, MetaUi.Crimson), MetaUi.Crimson));
            content.Add(swatches);
            content.Add(BodyText_("Left-drag paints. Right-drag erases."));
        }

        // Simple TOOLS: brush size (shared control, see BuildBrushControl), undo/redo, reset view.
        private void BuildSimpleTools(VisualElement content)
        {
            content.Add(Head("TOOLS", first: true));
            BuildBrushControl(content);
            _historyRow = Row();
            content.Add(_historyRow);
            content.Add(ResetViewBtn());
        }

        // RESET VIEW (both modes), with the zoom/pan controls as its hover text since those
        // have no button of their own.
        private VisualElement ResetViewBtn()
        {
            return AttachTooltip(ActionBtn("RESET VIEW", () =>
            {
                _canvas.ResetView();
                SetStatus("View reset (zoom 1x, no pan).");
            }), "Back to zoom 1x, centred. Mouse wheel zooms towards the cursor; middle-drag pans.");
        }

        // Simple MAP: new map + the sandbox-override actions.
        private void BuildSimpleMap(VisualElement content)
        {
            content.Add(Head("MAP", first: true));
            content.Add(ActionBtn("NEW MAP", () => CreateMap(filled: true, DefaultRadius)));
            content.Add(ActionBtn("SAVE AS SANDBOX OVERRIDE", SaveOverride));
            content.Add(PairRow(
                ActionBtn("LOAD OVERRIDE", LoadOverride),
                ActionBtn("REMOVE OVERRIDE", RemoveOverride)));
        }

        // Advanced mode: the full builder's painting panels (PALETTE, TOOLS, MAP).

        // Advanced PALETTE: substrate layers + both special-tile groups + erase.
        private void BuildAdvancedPalette(VisualElement content)
        {
            // Palette: substrate layers. Each swatch is a game Key recoloured to the layer's
            // own StructureSilhouette.LayerColour (the same source HexCanvas draws with), with
            // an exact-colour chip under it since the Key itself lightens its family colour.
            // Caption per swatch: which weapon type deals full damage to that layer, the rest
            // deal only a fraction (ColourRules._family + ColourMath.Mult, verified 2026-11-22
            // against ColourRules.cs/ColourMath.cs). Layer 1 (Black) has no resistance at all —
            // Mult always returns full damage against it, whatever the attacker.
            content.Add(Head("PALETTE", first: true));
            content.Add(SubHead("SUBSTRATE LAYERS"));
            content.Add(BodyText_("Each layer resists every weapon type except the one named on its swatch, which still does full damage; Layer 1 has no resistance at all."));
            VisualElement swatches = Row();
            swatches.style.flexWrap = Wrap.Wrap;
            for (int layer = 1; layer <= 5; layer++)
            {
                int l = layer;
                Color c = StructureSilhouette.LayerColour(l);
                swatches.Add(ToolHolder(SubstrateKey(l), MetaUi.Key(LayerColourName(l), () => SelectTool(Substrate, l), 120f, c), c, LayerCaption(l)));
            }
            content.Add(swatches);

            // Palette: special tiles, split into the two groups the game's own tile data
            // distinguishes by "role" (gamedata_tiles.json, verified 2026-11-22): "behavior"
            // tiles seed a hidden enemy encounter, "material" tiles seed an extra resource
            // pickup. Labels are the game's own tile "label" field, Title Case, not SCREAMING_
            // SNAKE_CASE with the underscore left in (that was the old ToUpperInvariant() text).
            content.Add(SubHead("SPECIAL TILES"));

            content.Add(GroupLabel("ENEMY ENCOUNTERS"));
            content.Add(BodyText_("Paints a hidden enemy marker. Destroying this tile (or mining close to it) wakes the enemy it hides."));
            VisualElement behaviorTiles = Row();
            behaviorTiles.style.flexWrap = Wrap.Wrap;
            for (int i = 0; i < BehaviorTileCount; i++)
            {
                string t = SpecialTiles[i];
                Color c = MarkerColour(t);
                behaviorTiles.Add(ToolHolder(t, MetaUi.Key(SpecialTileLabel[t], () => SelectTool(t, _layer), 196f, c), c));
            }
            content.Add(behaviorTiles);

            content.Add(GroupLabel("RESOURCE PICKUPS"));
            content.Add(BodyText_("Seeds an extra resource pickup into this tile to mine."));
            VisualElement materialTiles = Row();
            materialTiles.style.flexWrap = Wrap.Wrap;
            for (int i = BehaviorTileCount; i < SpecialTiles.Length; i++)
            {
                string t = SpecialTiles[i];
                Color c = MarkerColour(t);
                materialTiles.Add(ToolHolder(t, MetaUi.Key(SpecialTileLabel[t], () => SelectTool(t, _layer), 196f, c), c));
            }
            materialTiles.Add(ToolHolder(Erase, MetaUi.Key("ERASE", () => SelectTool(Erase, _layer), 196f, MetaUi.Crimson), MetaUi.Crimson));
            content.Add(materialTiles);
            content.Add(BodyText_("Left-drag paints the selected tool. Right-drag erases. Special tiles keep the cell's existing layer colour."));
        }

        // Advanced TOOLS: brush size (shared control, see BuildBrushControl), fill/mirror,
        // undo/redo, reset view.
        private void BuildAdvancedTools(VisualElement content)
        {
            content.Add(Head("TOOLS", first: true));
            BuildBrushControl(content);
            // Column: FILL/MIRROR pair, then LINE/CIRCLE pair (RefreshToggles).
            _toggleRow = new VisualElement();
            content.Add(_toggleRow);
            _historyRow = Row();
            content.Add(_historyRow);
            content.Add(ResetViewBtn());

            // Selection & clipboard (Advanced only: Simple never builds these rows). How each
            // button works is its hover text (RefreshToggles / RefreshClipboardButtons).
            content.Add(SubHead("SELECTION & CLIPBOARD"));
            _selectRow = Row();
            content.Add(_selectRow);
            _loopRow = new VisualElement();
            content.Add(_loopRow);
            _clipRow = new VisualElement();
            content.Add(_clipRow);
        }

        // Advanced MAP: radius, new filled/empty, grow, sandbox-override actions.
        private void BuildAdvancedMap(VisualElement content)
        {
            content.Add(Head("MAP", first: true));
            BuildRadiusControl(content);
            content.Add(PairRow(
                ActionBtn("NEW (FILLED)", () => NewMap(filled: true)),
                ActionBtn("NEW (EMPTY)", () => NewMap(filled: false))));
            content.Add(ActionBtn("GROW TO RADIUS", GrowToRadius));
            content.Add(ActionBtn("SAVE AS SANDBOX OVERRIDE", SaveOverride));
            content.Add(PairRow(
                ActionBtn("LOAD OVERRIDE", LoadOverride),
                ActionBtn("REMOVE OVERRIDE", RemoveOverride)));
        }

        // ---- Simple/Advanced mode -----------------------------------------------------

        private void ToggleMode()
        {
            CommitAction();
            _advancedMode = !_advancedMode;
            SaveAdvancedPref(_advancedMode);
            // Paste placement has no controls in Simple; drop it rather than resume a stamp
            // later by surprise. SELECT (like fill/mirror) just pauses.
            _pasteMode = false;
            if (!_advancedMode && _tool != Substrate && _tool != Erase)
            {
                // Simple mode has no special-tile palette, so don't leave one silently selected.
                _tool = Substrate;
            }
            _lastStatus = _advancedMode
                ? "Advanced mode: every tool, stats, structure loading and export."
                : (_fillMode || _mirror || _selectMode || _lineMode || _circleMode || _autoFillLoops)
                    ? "Simple mode: the essentials only. Fill mode, mirror, line, circle and select are paused until you switch back to Advanced."
                    : "Simple mode: the essentials only. Switch to Advanced for every tool.";
            _lastStatusError = false;
            Plugin.Log.LogInfo("MapBuilder: " + _lastStatus);
            if (_doc != null && _doc.rootVisualElement != null)
            {
                Build(_doc.rootVisualElement);
            }
        }

        private static string PrefsPath()
        {
            return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), PrefsFileName);
        }

        // Missing, unreadable or malformed file -> Simple mode (false); never throws.
        private static bool LoadAdvancedPref()
        {
            try
            {
                string path = PrefsPath();
                if (!File.Exists(path))
                {
                    return false;
                }
                JObject o = JObject.Parse(File.ReadAllText(path));
                JToken t = o[PrefsAdvancedKey];
                return t != null && t.Type == JTokenType.Boolean && (bool)t;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MapBuilder: could not read {PrefsFileName}, using Simple mode: {e.Message}");
                return false;
            }
        }

        private static void SaveAdvancedPref(bool advanced)
        {
            try
            {
                JObject o = new JObject { [PrefsAdvancedKey] = advanced };
                File.WriteAllText(PrefsPath(), o.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"MapBuilder: could not save {PrefsFileName}: {e.Message}");
            }
        }

        // Fill mode and mirror have no controls in Simple mode, so they only take effect in
        // Advanced (their state is kept and resumes on switching back).
        private static bool FillActive => _fillMode && _advancedMode;

        private static bool MirrorActive => _mirror && _advancedMode;

        private static bool SelectActive => _selectMode && _advancedMode;

        private static bool PasteModeActive => _pasteMode && _advancedMode && _clipboard.Count > 0;

        private static bool LineActive => _lineMode && _advancedMode;

        private static bool CircleActive => _circleMode && _advancedMode;

        private static bool AutoFillLoopsActive => _autoFillLoops && _advancedMode;

        // Brush size: one control, built by this one method in both Simple and Advanced TOOLS.
        // A SliderInt (0..MaxBrush) and a small number box side by side, both over the same
        // static _brush/_brushText state, so the value survives a mode toggle or reopen and the
        // two widgets never disagree. Dragging the slider writes both and refreshes the box;
        // typing in the box keeps the raw text in _brushText (half-typed values survive a
        // rebuild), updates _brush only when it parses, moves the slider, and on focus-out
        // NormaliseIntField shows the clamped value actually in use.
        //
        // The game's UI never uses a slider, so its panel theme may not style one: the
        // tracker/dragger get inline styles here rather than relying on the theme.
        private void BuildBrushControl(VisualElement content)
        {
            Label caption = MetaUi.Text($"Brush size (0-{MaxBrush}): cells around the cursor, 0 = one cell", MetaUi.Face.Body, 18f, BodyText, 0f, wrap: true);
            caption.style.marginBottom = 4f;
            content.Add(caption);

            VisualElement row = Row();
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 10f;
            content.Add(row);

            _brushSlider = new SliderInt(0, MaxBrush);
            _brushSlider.SetValueWithoutNotify(_brush);
            _brushSlider.style.flexGrow = 1f;
            _brushSlider.style.marginLeft = 0f;
            _brushSlider.style.marginRight = 12f;
            _brushSlider.style.height = 30f;
            StyleSlider(_brushSlider);
            _brushSlider.RegisterValueChangedCallback(e =>
            {
                _brush = Mathf.Clamp(e.newValue, 0, MaxBrush);
                _brushText = _brush.ToString();
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
                    _brush = Mathf.Clamp(n, 0, MaxBrush);
                    _brushSlider?.SetValueWithoutNotify(_brush);
                    _canvas?.Repaint();
                }
            });
            _brushField.RegisterCallback<FocusOutEvent>(_ => NormaliseIntField(_brushField, ref _brushText, _brush));
            row.Add(_brushField);
        }

        // Map radius: same slider + number box control as brush size (same bounded-number
        // function), over the existing _radiusText state ReadRadius already parses/clamps from.
        private void BuildRadiusControl(VisualElement content)
        {
            Label caption = MetaUi.Text($"Radius ({MinRadius}-{MaxRadius}): size of a new map, or the target for GROW TO RADIUS", MetaUi.Face.Body, 18f, BodyText, 0f, wrap: true);
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
                case 1: return "Grey";
                case 2: return "Blue";
                case 3: return "Pink";
                case 4: return "Gold";
                case 5: return "Purple";
                default: return "Layer " + layer;
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
                case 1: return "No resistance — every weapon at full damage";
                case 2: return "Full dmg: Dynamo weapons only";
                case 3: return "Full dmg: Cannon & Drill weapons only";
                case 4: return "Full dmg: Laser weapons only";
                case 5: return "Full dmg: Pulse weapons only";
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


        // Stock TextField styled the way the game styles its one runtime text input
        // (DroneBayScreen.RenameField): toolkit font, tinted input fill, family-coloured edge.
        private static TextField Field(string label, string value, Action<string> onChange)
        {
            TextField tf = new TextField(label);
            tf.SetValueWithoutNotify(value ?? "");
            StyleInput(tf, 20f);
            tf.style.marginLeft = 0f;
            tf.style.marginRight = 0f;
            tf.style.marginBottom = 10f;
            if (tf.labelElement != null)
            {
                MetaUi.Style(tf.labelElement, MetaUi.Face.Body, 18f, BodyText);
                tf.labelElement.style.minWidth = 230f;
                tf.labelElement.style.width = 230f;
            }
            if (onChange != null)
            {
                tf.RegisterValueChangedCallback(e => onChange(e.newValue));
            }
            return tf;
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
                    ActionBtn(_fillMode ? "FILL MODE: ON" : "FILL MODE: OFF", ToggleFill, family: _fillMode ? Accent : Neutral, pulse: _fillMode),
                    "When on, one left click floods every connected cell matching the clicked one with the selected tool. Right-click floods erase instead."),
                AttachTooltip(
                    ActionBtn(_mirror ? "MIRROR (180°): ON" : "MIRROR (180°): OFF", ToggleMirror, family: _mirror ? Accent : Neutral, pulse: _mirror),
                    "When on, every paint, erase, fill, line and circle is repeated on the opposite side of the map, rotated 180° about the centre (Q R becomes -Q -R).")));
            _toggleRow.Add(PairRow(
                AttachTooltip(
                    ActionBtn(_lineMode ? "LINE: ON" : "LINE: OFF", ToggleLine, family: _lineMode ? Accent : Neutral, pulse: _lineMode),
                    "When on, left-drag draws a straight line from where you pressed to the cursor with the selected tool, at the current brush size; it's painted when you let go (one undo step). Right-drag draws an erasing line. Turns fill mode, select and circle off."),
                AttachTooltip(
                    ActionBtn(_circleMode ? "CIRCLE: ON" : "CIRCLE: OFF", ToggleCircle, family: _circleMode ? Accent : Neutral, pulse: _circleMode),
                    "When on, press to set the centre and drag outwards to size a filled circle; it's painted with the selected tool when you let go (one undo step). Right-drag erases a circle. Brush size doesn't apply. Turns fill mode, select and line off.")));
        }

        private void RefreshHistoryButtons()
        {
            if (_historyRow == null)
            {
                return;
            }
            _historyRow.Clear();
            VisualElement row = PairRow(
                ActionBtn(_undo.Count > 0 ? $"UNDO ({_undo.Count})" : "UNDO", Undo, enabled: _undo.Count > 0),
                ActionBtn(_redo.Count > 0 ? $"REDO ({_redo.Count})" : "REDO", Redo, enabled: _redo.Count > 0));
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
            SetStatus(_fillMode ? $"Fill mode on: a click flood-fills the connected region.{Suffix(off)}" : "Fill mode off: brush painting.");
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
            string off = _lineMode ? "Line" : _circleMode ? "Circle" : null;
            _lineMode = false;
            _circleMode = false;
            return off == null ? null : off + " turned off.";
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
                    off.Add("circle");
                }
                if (shape == HexShape.Circle && _lineMode)
                {
                    off.Add("line");
                }
                if (_fillMode)
                {
                    off.Add("fill mode");
                }
                if (_selectMode)
                {
                    off.Add("select");
                }
                if (PasteModeActive)
                {
                    off.Add("paste placement");
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
            string note = off.Count > 0 ? $" Turned off: {string.Join(", ", off)}." : "";
            if (shape == HexShape.Line)
            {
                SetStatus(on
                    ? $"Line on: drag from start to end; it's painted on release at the current brush size. Right-drag erases.{note}"
                    : "Line off: brush painting.");
            }
            else
            {
                SetStatus(on
                    ? $"Circle on: press at the centre and drag outwards; it's painted on release. Right-drag erases.{note}"
                    : "Circle off: brush painting.");
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
            string what = shape == HexShape.Line ? "Line" : "Circle";
            BeginAction(erase ? what + " erase" : what);
            bool changed = false;
            foreach (Vector2Int c in cells)
            {
                changed |= PaintOne(c.x, c.y, erase);
            }
            CommitAction();
            string mirrored = MirrorActive ? ", mirrored" : "";
            if (changed)
            {
                UpdateStatsNow();
                SetStatus(shape == HexShape.Line
                    ? $"{(erase ? "Erased" : "Drew")} a line from Q {start.x} R {start.y} to Q {end.x} R {end.y} ({cells.Count} cells{mirrored}, one undo step)."
                    : $"{(erase ? "Erased" : "Drew")} a circle of radius {CircleRadius(start, end)} at Q {start.x} R {start.y} ({cells.Count} cells{mirrored}, one undo step).");
            }
            else
            {
                SetStatus($"Nothing changed: the {what.ToLowerInvariant()} matches what's already there.");
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
            SetStatus(_mirror ? "Mirror on: every edit is repeated at (-q, -r)." : "Mirror off.");
        }

        // ---- selection & clipboard ----------------------------------------------------
        // SELECT is a toggle like FILL MODE/MIRROR; PASTE is a toggle too (it reads CANCEL
        // PASTE, teal and pulsing, while placement is active). Canvas input precedence is
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
                        ActionBtn(_selectMode ? "SELECT: ON" : "SELECT: OFF", ToggleSelect, family: _selectMode ? Accent : Neutral, pulse: _selectMode),
                        "When on, left-drag adds cells to the selection and right-drag removes them (brush size applies). Painting is paused while it's on."),
                    AttachTooltip(
                        ActionBtn(_selection.Count > 0 ? $"CLEAR SELECTION ({_selection.Count})" : "CLEAR SELECTION", ClearSelection, enabled: _selection.Count > 0),
                        "Deselects every cell. The map itself isn't changed."));
                row.style.flexGrow = 1f;
                _selectRow.Add(row);
            }
            if (_loopRow != null)
            {
                _loopRow.Clear();
                _loopRow.Add(AttachTooltip(
                    ActionBtn(_autoFillLoops ? "AUTO-FILL LOOPS: ON" : "AUTO-FILL LOOPS: OFF", ToggleAutoFillLoops, family: _autoFillLoops ? Accent : Neutral, pulse: _autoFillLoops),
                    "When on, each time you finish a SELECT left-drag, any cells the selection completely surrounds are selected too, so drawing a closed outline selects its inside. Right-drags (removing) never auto-fill."));
            }
            if (_clipRow != null)
            {
                _clipRow.Clear();
                bool any = _selection.Count > 0;
                _clipRow.Add(PairRow(
                    AttachTooltip(
                        ActionBtn("COPY", Copy, enabled: any),
                        "Copies every selected cell to the clipboard, empty ones included (those erase when pasted). The map isn't changed."),
                    AttachTooltip(
                        ActionBtn("CUT", Cut, enabled: any),
                        "Copies every selected cell to the clipboard, empty ones included, then erases them from the map (one undo step).")));
                bool placing = PasteModeActive;
                string pasteLabel = placing
                    ? "CANCEL PASTE"
                    : (_clipboard.Count > 0 ? $"PASTE ({_clipboard.Count} CELLS)" : "PASTE");
                _clipRow.Add(AttachTooltip(
                    ActionBtn(pasteLabel, TogglePaste, enabled: placing || _clipboard.Count > 0, family: placing ? Accent : (Color?)null, pulse: placing),
                    "Each left click on the map stamps the clipboard centred on the cursor (empty cells erase) and stays ready for another. Right-click or CANCEL PASTE stops."));
            }
            // LOAD PART's save button follows the clipboard too (every clipboard change ends here).
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
                ? $"Select on: left-drag adds cells to the selection, right-drag removes them; painting is paused.{(cancelled ? " Paste placement cancelled." : "")}{Suffix(off)}"
                : "Select off: brush painting. The selection is kept for COPY/CUT.");
        }

        private void ClearSelection()
        {
            int n = _selection.Count;
            _selection.Clear();
            _canvas?.Repaint();
            RefreshClipboardButtons();
            SetStatus(n > 0 ? $"Cleared the selection ({n} cells)." : "Nothing is selected.");
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
                SetStatus("Nothing selected to copy. Turn SELECT on and drag over the cells first.", error: true);
                return;
            }
            int empty = _clipboard.Count(c => !c.Cell.HasValue);
            RefreshClipboardButtons();
            SetStatus($"Copied {n} cells{(empty > 0 ? $" ({empty} empty, which erase when pasted)" : "")}. Press PASTE to stamp them.");
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
                SetStatus("Nothing selected to cut. Turn SELECT on and drag over the cells first.", error: true);
                return;
            }
            // Same erase path as the brush (SandboxMap.Paint "erase"), no mirror: Cut acts on
            // exactly the cells it copied.
            BeginAction("Cut");
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
            SetStatus($"Cut {n} cells to the clipboard (one undo step). Press PASTE to stamp them.");
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
                SetStatus("The clipboard is empty. Select some cells and press COPY or CUT first.", error: true);
                return;
            }
            _pasteMode = true;
            _canvas?.Repaint();
            RefreshClipboardButtons();
            SetStatus($"Paste: left-click stamps the {_clipboard.Count}-cell clipboard centred on the cursor (each stamp is its own undo step). Right-click or CANCEL PASTE stops.");
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

        // Shown in Advanced only (Simple has no selection tools, so nothing to show).
        public IEnumerable<Vector2Int> SelectedCells => _advancedMode ? _selection : Enumerable.Empty<Vector2Int>();

        public bool PasteActive => PasteModeActive;

        public IReadOnlyList<ClipCell> Clipboard => _clipboard;

        // Brush-footprint preview: on whenever a click would run a brush-radius action, i.e.
        // a brush stroke (any palette tool) or a SELECT drag, mirroring the canvas's dispatch
        // precedence (paste > SELECT > fill > brush): never during paste placement (it has its
        // own ghost), never in fill mode unless SELECT overrides it. Advanced only. LINE uses
        // the brush, so its hover shows the footprint (a drag swaps it for the line ghost);
        // CIRCLE ignores brush size, so it shows none.
        public bool BrushPreviewActive => _advancedMode && !PasteModeActive && !CircleActive && (SelectActive || LineActive || !FillActive);

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
                ? $"{_selection.Count} cells selected{(filled > 0 ? $" ({filled} enclosed cells auto-filled)" : "")}. COPY or CUT them, or keep dragging to adjust."
                : "Nothing selected.");
        }

        private void ToggleAutoFillLoops()
        {
            _autoFillLoops = !_autoFillLoops;
            RefreshClipboardButtons();
            SetStatus(_autoFillLoops
                ? "Auto-fill loops on: finishing a SELECT left-drag also selects every cell the selection encloses."
                : "Auto-fill loops off: SELECT selects exactly what you drag over.");
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
            BeginAction("Paste");
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
                SetStatus($"Pasted at Q {q} R {r} (one undo step). Click again for another copy; right-click or CANCEL PASTE stops.");
            }
            else
            {
                SetStatus($"Nothing changed at Q {q} R {r}: the area already matches the clipboard.");
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
                SetStatus("Paste placement cancelled. The clipboard is kept.");
            }
        }

        public void StrokeBegin()
        {
            BeginAction("Brush stroke");
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
                BeginAction("Brush stroke");
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
            BeginAction(erase ? "Flood erase" : "Flood fill");
            bool changed = false;
            foreach (Vector2Int c in region)
            {
                changed |= PaintWithMirror(c.x, c.y, erase);
            }
            CommitAction();
            if (changed)
            {
                UpdateStatsNow();
                SetStatus($"{(erase ? "Flood-erased" : "Flood-filled")} a region of {region.Count} cells{(MirrorActive ? " (mirrored)" : "")}.");
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
                ? $"{LayerColourName(cell.layer).ToUpperInvariant()} · {TileLabel(cell.tile).ToUpperInvariant()}"
                : "EMPTY";
            RetextTracked(_hoverLabel, $"CELL  Q {q}  R {r}  ·  {what}", 2f);
        }

        // Plain display name for a cell's tile key, matching the game's own tile "label" field
        // (gamedata_tiles.json) rather than the raw snake_case id.
        private static string TileLabel(string tile)
        {
            string t = tile ?? Substrate;
            if (t == Substrate)
            {
                return "Substrate";
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
                SetStatus("Nothing to undo.");
                return;
            }
            Edit edit = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            ApplyEdit(edit, after: false);
            _redo.Add(edit);
            AfterHistoryMove($"Undid: {edit.Label} ({edit.Cells.Count} cells).");
        }

        private void Redo()
        {
            CommitAction();
            if (_redo.Count == 0)
            {
                SetStatus("Nothing to redo.");
                return;
            }
            Edit edit = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            ApplyEdit(edit, after: true);
            _undo.Add(edit);
            AfterHistoryMove($"Redid: {edit.Label} ({edit.Cells.Count} cells).");
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
            SetStatus($"{what}: {_map.cells.Count} cells (radius {_map.radius}).");
        }

        private bool ReadRadius(out int radius)
        {
            if (!int.TryParse((_radiusText ?? "").Trim(), out radius))
            {
                SetStatus("Radius must be a whole number from 1 to 300.", error: true);
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

        // Shared by Advanced's NEW (FILLED)/NEW (EMPTY) (radius from the field) and Simple's
        // NEW MAP (filled, DefaultRadius).
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
            ReplaceMap(map, filled ? "New filled map" : "New empty map");
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
                SetStatus($"Nothing changed: {radius} is not larger than the current radius ({_map.radius}). Grow only extends the map; use NEW for a smaller one.");
                return;
            }
            int old = _map.radius;
            BeginAction("Grow radius");
            _map.radius = radius;
            CommitAction();
            _canvas?.Refresh();
            UpdateStatsNow();
            SetStatus($"Grew the radius from {old} to {radius}; painted cells are unchanged.");
        }

        private void SaveOverride()
        {
            if (_map.cells.Count == 0)
            {
                SetStatus("The map is empty; paint some cells before saving it as the override.", error: true);
                return;
            }
            try
            {
                _map.Save(Plugin.OverrideFilePath);
                SetStatus($"Saved {_map.cells.Count} cells as the sandbox override ({Plugin.OverrideFilePath}). The next time you fly out and mine, it'll be used.");
            }
            catch (Exception e)
            {
                SetStatus($"Saving the override failed: {e.Message}", error: true);
            }
        }

        private void LoadOverride()
        {
            if (!File.Exists(Plugin.OverrideFilePath))
            {
                SetStatus($"No override file exists yet ({Plugin.OverrideFilePath}).", error: true);
                return;
            }
            try
            {
                SandboxMap loaded = SandboxMap.Load(Plugin.OverrideFilePath);
                if (loaded == null)
                {
                    SetStatus("The override file could not be read.", error: true);
                    return;
                }
                ReplaceMap(loaded, "Loaded the current override");
            }
            catch (Exception e)
            {
                SetStatus($"Loading the override failed: {e.Message}", error: true);
            }
        }

        // Deleting the file switches the override off (Patches.cs keys ActiveOverride on it),
        // so mining goes back to the game's normal structures. Doesn't touch the map, so there
        // is nothing to undo.
        private void RemoveOverride()
        {
            if (!File.Exists(Plugin.OverrideFilePath))
            {
                SetStatus("No override file to remove; mining already uses the normal structures.");
                return;
            }
            try
            {
                File.Delete(Plugin.OverrideFilePath);
                SetStatus("Removed the sandbox override. Mining uses the normal structures again (the map here is unchanged).");
            }
            catch (Exception e)
            {
                SetStatus($"Removing the override failed: {e.Message}", error: true);
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
            // Your exports first, then the game's catalogue (RefreshLoadList's order), each
            // under its own eyebrow like the Contracts list's IN PROGRESS / ALL CONTRACTS.
            bool anyLocal = _loadEntries.Any(e => e.Local);
            _loadListHost.Add(ListHeader("YOUR EXPORTS", first: true));
            if (!anyLocal)
            {
                _loadListHost.Add(BodyText_("Nothing exported yet. Use EXPORT AS STRUCTURE below."));
            }
            bool gameHeader = false;
            for (int i = 0; i < _loadEntries.Count; i++)
            {
                LoadEntry entry = _loadEntries[i];
                if (!entry.Local && !gameHeader)
                {
                    _loadListHost.Add(ListHeader("GAME STRUCTURES"));
                    gameHeader = true;
                }
                VisualElement row = LoadRow(i, entry);
                _loadListHost.Add(row);
                _loadHolders.Add(row);
            }
            if (!gameHeader)
            {
                _loadListHost.Add(ListHeader("GAME STRUCTURES"));
                _loadListHost.Add(BodyText_("No game structures found."));
            }
            PaintLoadRows();
        }

        // One load-list row, as a Contracts-list row: a slab (teal for your exports, grey for
        // game structures) with the name, a short tag on the right and, for your own exports
        // only, a delete key (game structures aren't files this mod manages). Click selects,
        // double-click loads.
        private VisualElement LoadRow(int index, LoadEntry entry)
        {
            Color family = entry.Local ? Accent : Neutral;
            VisualElement row = new VisualElement { name = "map-builder-load-row" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 22f;
            row.style.paddingRight = entry.Local ? 12f : 22f;
            row.style.paddingTop = entry.Local ? 10f : 16f;
            row.style.paddingBottom = entry.Local ? 10f : 16f;
            row.style.marginBottom = 10f;
            SlabFrame frame = MetaUi.Slab(row, family, dim: false, 13f);

            Label name = MetaUi.Text(entry.Label.ToUpperInvariant(), MetaUi.Face.DisplayBold, 21f, MetaUi.TextTone(family), 2f);
            name.pickingMode = PickingMode.Ignore;
            name.style.flexGrow = 1f;
            name.style.flexShrink = 1f;
            name.style.overflow = Overflow.Hidden;
            row.Add(name);

            string tag = entry.Local
                ? "YOUR EXPORT"
                : (entry.Def != null && entry.Def.cellCount > 0 ? $"{entry.Def.cellCount} CELLS" : "GAME");
            Label tagLabel = MetaUi.Text(tag, MetaUi.Face.Body, 15f, DimText, 2f);
            tagLabel.pickingMode = PickingMode.Ignore;
            tagLabel.style.flexShrink = 0f;
            tagLabel.style.marginLeft = 16f;
            row.Add(tagLabel);

            if (entry.Local)
            {
                VisualElement del = MetaUi.Key("×", () => DeleteLocalStructure(entry), 54f, MetaUi.Crimson);
                del.style.flexShrink = 0f;
                del.style.marginLeft = 14f;
                // The × is inside the row: don't let its click also select/load the row.
                del.RegisterCallback<ClickEvent>(e => e.StopPropagation());
                row.Add(AttachTooltip(del, "Delete this export's files (asks first)."));
            }

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
            return row;
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
            LoadEntry e = _loadEntries[index];
            SetStatus($"Selected '{e.Label}' ({(e.Local ? "your export" : "game structure")}). Press LOAD SELECTED to load it.");
        }

        private void LoadSelected()
        {
            if (_loadSelected < 0 || _loadSelected >= _loadEntries.Count)
            {
                SetStatus("Select a structure in the list first.", error: true);
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
                SetStatus($"Loading '{entry.Label}' failed: {e.Message}", error: true);
            }
        }

        // True if the map was replaced; false (with the reason in the status line) if not.
        private bool LoadLocal(LoadEntry entry)
        {
            JObject def = JObject.Parse(File.ReadAllText(entry.DefPath));
            string cellsFile = (string)def["cells_file"];
            if (string.IsNullOrEmpty(cellsFile))
            {
                SetStatus($"'{Path.GetFileName(entry.DefPath)}' has no cells_file.", error: true);
                return false;
            }
            string cellsPath = Path.Combine(Path.GetDirectoryName(entry.DefPath), cellsFile);
            if (!File.Exists(cellsPath))
            {
                SetStatus($"Cells file '{cellsFile}' is missing next to '{Path.GetFileName(entry.DefPath)}'.", error: true);
                return false;
            }
            SandboxMap map = SandboxMap.FromMegastructure(MegastructureData.FromJson(File.ReadAllText(cellsPath)));
            ReplaceMap(map, $"Loaded '{entry.Label}'");

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
            SetStatus($"Loaded '{entry.Label}': {_map.cells.Count} cells; the export form now targets '{id}' ({_bands.Count} band{(_bands.Count == 1 ? "" : "s")}).");
            return true;
        }

        private bool LoadCatalogue(LoadEntry entry)
        {
            if (entry.Def.cells == null)
            {
                SetStatus($"'{entry.Label}' has no cell data to load.", error: true);
                return false;
            }
            SandboxMap map = SandboxMap.FromMegastructure(MegastructureData.FromJson(entry.Def.cells.text));
            ReplaceMap(map, $"Loaded '{entry.Label}'");
            // Official structures never had a struct-def JSON, so don't guess: blank form.
            SetForm(DefaultId, DefaultName, DefaultStartingMoney.ToString());
            _bands.Clear();
            _bands.Add(DefaultBand());
            RebuildBands();
            SetStatus($"Loaded game structure '{entry.Label}': {_map.cells.Count} cells. The export form was reset to defaults.");
            return true;
        }

        // Delete one of our own exports (teal entries only; RebuildLoadList never offers it for
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
            TerminalUi.Confirm(root, "map-builder-confirm-delete", Accent, "DELETE STRUCTURE",
                $"Delete '{entry.Label}'?",
                "DELETE", () => DeleteLocalStructureConfirmed(entry));
        }

        // Removes <id>.json and its cells file from StructuresDir() (the folder ExportStructure
        // writes, and RefreshLoadList read entry.DefPath from), then rescans the list.
        //
        // Already-injected copies: the framework's Ctrl+F6 (StructuresInjectPatch.Reload ->
        // InjectedTracker.Reload) first strips every id it injected last pass out of
        // StructureCatalog.campaign and its id cache, then rescans the folders, so a deleted
        // file is gone from the Contracts list after the next Ctrl+F6, not before (verified
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
                    SetStatus($"Refusing to delete '{defPath}': it isn't in {dir}.", error: true);
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

                // Any live catalogue copy (injected at boot or by an earlier Ctrl+F6).
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
                string files = removed.Count > 0 ? string.Join(" + ", removed) : "no files were left to remove";
                SetStatus(inGame
                    ? $"Deleted structure '{entry.Label}' ({files}). It's still in the game's Contracts list this session: press Ctrl+F6 to remove it there too. The map here is unchanged."
                    : $"Deleted structure '{entry.Label}' ({files}). The map here is unchanged.");
            }
            catch (Exception e)
            {
                RefreshLoadList();
                SetStatus($"Deleting '{entry.Label}' failed: {e.Message}", error: true);
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
            VisualElement panel = MetaUi.Panel(Accent, 15f, dim: true, 18f);
            panel.style.marginBottom = 14f;

            // Title.
            VisualElement title = Row();
            title.style.justifyContent = Justify.SpaceBetween;
            title.style.alignItems = Align.Center;
            title.style.marginBottom = 4f;
            VisualElement titleText = Row();
            titleText.style.alignItems = Align.Center;
            titleText.Add(MetaUi.Text($"BAND {index + 1}", MetaUi.Face.DisplayBold, 20f, AccentLt, 3f));
            Label order = MetaUi.Text(index == 0 ? "UNLOCKS FIRST" : $"UNLOCKS AFTER BAND {index}", MetaUi.Face.Body, 14f, DimText, 2f);
            order.style.marginLeft = 16f;
            titleText.Add(order);
            title.Add(titleText);
            title.Add(AttachTooltip(MetaUi.Key("×", () =>
            {
                if (index < _bands.Count)
                {
                    _bandCosmeticShown.Remove(_bands[index]);
                    _bands.RemoveAt(index);
                }
                RebuildBands();
                SetStatus(_bands.Count == 0
                    ? "Removed the last band; export needs at least one (ADD BAND)."
                    : $"Removed band {index + 1}.");
            }, 54f, MetaUi.Crimson), "Remove this band. The bands after it move up one place in the unlock order."));
            panel.Add(title);

            // Substrate colour.
            panel.Add(AttachTooltip(GroupLabel("SUBSTRATE COLOUR"), BandFields[BandLayer].Tooltip));
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
            BandColourNote(panel, index, layer, values[BandLayer], counts);

            // Tile health, shard spawns, enemy spawns.
            VisualElement groups = Row();
            groups.style.flexWrap = Wrap.Wrap;
            groups.style.alignItems = Align.FlexStart;
            groups.style.marginTop = 4f;
            panel.Add(groups);

            VisualElement health = BandGroup(groups, "TILE HEALTH", BandFields[BandHpSealed].Tooltip);
            health.Add(CompactField(values, BandHpSealed, null, 190f));

            VisualElement shards = BandGroup(groups, "SHARD SPAWNS", "Shard pickups (the currency used to research Mods) seeded into this band's tiles, per shard type. Saved with the export, not yet applied in game by the framework.");
            VisualElement shardRow = Row();
            for (int f = BandShardsFrom; f <= BandShardsTo; f++)
            {
                shardRow.Add(CompactField(values, f, MarkerColour(BandFields[f].Key)));
            }
            shards.Add(shardRow);

            VisualElement enemies = BandGroup(groups, "ENEMY SPAWNS", "Hidden enemy encounters seeded into this band's tiles, per enemy type. Saved with the export, not yet applied in game by the framework.");
            VisualElement enemyRow = Row();
            for (int f = BandEnemiesFrom; f <= BandEnemiesTo; f++)
            {
                enemyRow.Add(CompactField(values, f, MarkerColour(BandFields[f].Key)));
            }
            enemies.Add(enemyRow);

            // Cosmetic (folded by default).
            // Forced open while either value wouldn't export, so its error is never hidden.
            bool cosmeticBad = !BandValueValid(BandFields[BandOuterFrac], values[BandOuterFrac])
                || !BandValueValid(BandFields[BandUnsealedLook], values[BandUnsealedLook]);
            bool shown = cosmeticBad || _bandCosmeticShown.Contains(values);
            VisualElement cosmeticBar = Row();
            cosmeticBar.style.alignItems = Align.Center;
            cosmeticBar.style.marginTop = 6f;
            cosmeticBar.Add(AttachTooltip(MetaUi.Key(shown ? "HIDE COSMETIC OPTIONS" : "COSMETIC OPTIONS", () =>
            {
                if (!_bandCosmeticShown.Remove(values))
                {
                    _bandCosmeticShown.Add(values);
                }
                RebuildBands();
            }, 340f, Neutral), "Options that only change how the structure looks, with no effect on gameplay."));
            if (!shown)
            {
                Label summary = MetaUi.Text($"Preview ring {values[BandOuterFrac]} · core look {(values[BandUnsealedLook] == "0" ? "as painted" : values[BandUnsealedLook])} (no gameplay effect)", MetaUi.Face.Body, 15f, DimText, 0.5f);
                summary.style.marginLeft = 16f;
                cosmeticBar.Add(summary);
            }
            panel.Add(cosmeticBar);
            if (shown)
            {
                VisualElement cosmetic = Row();
                cosmetic.style.flexWrap = Wrap.Wrap;
                cosmetic.style.marginTop = 8f;
                cosmetic.Add(CompactField(values, BandOuterFrac, null, 230f));
                cosmetic.Add(CompactField(values, BandUnsealedLook, null, 230f));
                panel.Add(cosmetic);
            }
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
                text = $"Pick a colour: the saved value '{raw}' isn't one of the five (1-5).";
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
                    text = $"Band {earlier + 1} already uses {name}, so this band gets no tiles (the first band with a colour takes them all).";
                    colour = WarnText;
                }
                else if (n == 0)
                {
                    text = $"No cells on this map are painted {name}, so this band has no tiles yet.";
                    colour = WarnText;
                }
                else
                {
                    text = $"{n} cell{(n == 1 ? "" : "s")} on this map {(n == 1 ? "is" : "are")} {name}.";
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
            Label label = MetaUi.Text(spec.Label.ToUpperInvariant(), MetaUi.Face.Body, 13f, BodyText, 1f, wrap: true);
            label.style.flexShrink = 1f;
            // Flexbox gotcha: a flex child's default min-width is its own unwrapped content
            // width ("min-width: auto"), so whiteSpace:Normal alone still doesn't let it wrap
            // below that — the layout measures it at full single-line width regardless of the
            // box it's actually given, and the render just clips mid-word. Explicit 0 is needed
            // for wrap to actually take effect.
            label.style.minWidth = 0f;
            PadWrappedTracking(label, 1f);
            labelRow.Add(label);
            cell.Add(labelRow);

            TextField tf = new TextField();
            tf.SetValueWithoutNotify(values[fieldIndex]);
            StyleInput(tf, 17f);
            tf.style.marginLeft = 0f;
            tf.style.marginRight = 0f;
            tf.style.marginTop = 2f;
            cell.Add(tf);

            Label error = MetaUi.Text("Must be " + BandValueRule(spec) + ".", MetaUi.Face.Body, 13f, MetaUi.Crimson, 0f, wrap: true);
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
                return $"a number from {spec.Min} to {spec.Max} (use a '.' for decimals)";
            }
            return spec.Max >= int.MaxValue
                ? $"a whole number of {spec.Min} or more"
                : $"a whole number from {spec.Min} to {spec.Max}";
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
                        error = $"Band {i + 1}: {spec.Label} must be {BandValueRule(spec)}.";
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
                SetStatus("The ID must be lowercase letters, digits and underscores only.", error: true);
                return;
            }
            string displayName = (_nameText ?? "").Trim();
            if (displayName.Length == 0)
            {
                displayName = id;
            }
            if (!int.TryParse((_moneyText ?? "").Trim(), out int money) || money < 0)
            {
                SetStatus("Starting money must be a whole number of 0 or more.", error: true);
                return;
            }
            if (_map.cells.Count == 0)
            {
                SetStatus("The map is empty; paint some cells before exporting it.", error: true);
                return;
            }
            if (_bands.Count == 0)
            {
                SetStatus("Add at least one band before exporting.", error: true);
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

                RefreshLoadList();
                SetStatus($"{(overwrote ? "Overwrote" : "Exported")} structure '{id}' ({_map.cells.Count} cells, {bands.Count} band{(bands.Count == 1 ? "" : "s")}) to {dir}. Press Ctrl+F6 to load it into the Contracts list.");
            }
            catch (Exception e)
            {
                SetStatus($"Export failed: {e.Message}", error: true);
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
            _partSaveHost.Add(ActionBtn(any ? $"SAVE CLIPBOARD AS PART ({_clipboard.Count} CELLS)" : "SAVE CLIPBOARD AS PART", SavePart, enabled: any));
        }

        private void SavePart()
        {
            CommitAction();
            if (_clipboard.Count == 0)
            {
                SetStatus("The clipboard is empty. Select some cells and press COPY or CUT first.", error: true);
                return;
            }
            string name = (_partNameText ?? "").Trim();
            if (name.Length == 0)
            {
                SetStatus("Type a name for the part first.", error: true);
                return;
            }
            string fileId = PartFileId(name);
            if (fileId == null)
            {
                SetStatus("The part name needs at least one letter or digit (it also becomes the file name).", error: true);
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
                SetStatus($"{(overwrote ? "Overwrote" : "Saved")} part '{name}' ({_clipboard.Count} cells) as {fileId}.json in {dir}.");
            }
            catch (Exception e)
            {
                SetStatus($"Saving the part failed: {e.Message}", error: true);
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
            RebuildPartsList();
        }

        // One slab row per part, in the same Contracts-list style as the load list: name over
        // its cell count and file, then LOAD INTO CLIPBOARD and the delete key on the right.
        private void RebuildPartsList()
        {
            if (_partsListHost == null)
            {
                return;
            }
            _partsListHost.Clear();
            if (_partEntries.Count == 0)
            {
                _partsListHost.Add(BodyText_("No saved parts yet. Select some cells on the map, COPY them, then save them below."));
                return;
            }
            foreach (PartEntry entry in _partEntries)
            {
                PartEntry e = entry;
                VisualElement row = new VisualElement { name = "map-builder-part-row" };
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingLeft = 22f;
                row.style.paddingRight = 12f;
                row.style.paddingTop = 10f;
                row.style.paddingBottom = 10f;
                row.style.marginBottom = 10f;
                SlabFrame frame = MetaUi.Slab(row, Accent, dim: false, 13f);
                MetaUi.PaintSlab(frame, Accent, dim: false, 0.45f);
                row.RegisterCallback<PointerEnterEvent>(_ => MetaUi.PaintSlab(frame, Accent, dim: false, 0.85f));
                row.RegisterCallback<PointerLeaveEvent>(_ => MetaUi.PaintSlab(frame, Accent, dim: false, 0.45f));

                VisualElement text = new VisualElement { pickingMode = PickingMode.Ignore };
                text.style.flexGrow = 1f;
                text.style.flexShrink = 1f;
                text.style.overflow = Overflow.Hidden;
                Label nameLabel = MetaUi.Text(e.Name.ToUpperInvariant(), MetaUi.Face.DisplayBold, 21f, MetaUi.TextTone(Accent), 2f);
                nameLabel.pickingMode = PickingMode.Ignore;
                text.Add(nameLabel);
                Label info = MetaUi.Text($"{e.CellCount} cells{(e.EmptyCount > 0 ? $" ({e.EmptyCount} empty, which erase when pasted)" : "")} · {Path.GetFileName(e.Path)}", MetaUi.Face.Body, 15f, DimText, 0.5f);
                info.pickingMode = PickingMode.Ignore;
                info.style.marginTop = 2f;
                text.Add(info);
                row.Add(text);

                VisualElement load = MetaUi.Key("LOAD INTO CLIPBOARD", () => LoadPart(e), 340f, Accent);
                load.style.flexShrink = 0f;
                load.style.marginLeft = 16f;
                row.Add(AttachTooltip(load, "Replaces the clipboard with this part and takes you back to the map in paste mode: left-click stamps it."));
                VisualElement del = MetaUi.Key("×", () => DeletePart(e), 54f, MetaUi.Crimson);
                del.style.flexShrink = 0f;
                del.style.marginLeft = 10f;
                row.Add(AttachTooltip(del, "Delete this part's file. The clipboard is unchanged."));
                MetaUi.Lift(row, slide: true);
                _partsListHost.Add(row);
            }
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
                SetStatus($"Loading part '{entry.Name}' failed: {e.Message}", error: true);
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
            SetStatus($"Loaded part '{name}' ({cells.Count} cells{(empty > 0 ? $", {empty} empty" : "")}) and entered paste mode. Left-click to stamp it; right-click or CANCEL PASTE stops.");
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
                SetStatus($"Deleted part '{entry.Name}' ({Path.GetFileName(entry.Path)}). The clipboard is unchanged.");
            }
            catch (Exception e)
            {
                SetStatus($"Deleting part '{entry.Name}' failed: {e.Message}", error: true);
            }
        }
    }
}
