using System;
using System.Collections.Generic;
using GraveyardOrbit;
using GraveyardOrbit.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace SandboxMapUnlock
{
    // What the canvas needs from its owning screen. The canvas only turns pointer input into
    // grid coordinates; brush size, mirroring, flood fill and undo recording all live in the
    // host, so every edit path goes through one place.
    internal interface IHexCanvasHost
    {
        SandboxMap Map { get; }
        // When true a left/right click flood-fills (or flood-erases) instead of starting a stroke.
        bool FillMode { get; }
        // One brush stroke = StrokeBegin, any number of StrokePaint, then exactly one StrokeEnd.
        void StrokeBegin();
        bool StrokePaint(int q, int r, bool erase); // returns whether the map changed
        void StrokeEnd();
        bool Fill(int q, int r, bool erase);         // returns whether the map changed
        void Hover(int q, int r);
        Color MarkerColour(string tile);

        // SELECT tool: when true a left/right drag adds/removes cells to/from the selection
        // (same brush + drag sampling as painting) instead of painting. One select drag =
        // any number of SelectPaint, then exactly one SelectEnd.
        bool SelectMode { get; }
        bool SelectPaint(int q, int r, bool remove); // returns whether the selection changed
        // removed = the drag was a right-drag (remove from selection).
        void SelectEnd(bool removed);
        // Every selected cell, drawn with a highlight ring (empty when nothing should show).
        IEnumerable<Vector2Int> SelectedCells { get; }

        // Paste placement: while true a left click stamps the clipboard with its anchor on the
        // clicked cell (and placement stays active); a right click cancels placement.
        bool PasteActive { get; }
        IReadOnlyList<ClipCell> Clipboard { get; }
        bool PasteAt(int q, int r);                  // returns whether the map changed
        void CancelPaste();

        // Brush-footprint preview: while true, the cells a click on the hovered cell would
        // touch (BrushFootprint) are drawn as a white outline + faint wash. The host decides
        // when it applies (brush strokes and SELECT drags, not paste or fill).
        bool BrushPreviewActive { get; }
        void BrushFootprint(int q, int r, HashSet<Vector2Int> into);

        // LINE / CIRCLE tools (None when neither is on). A left/right drag picks a start cell
        // (where it began) and an end cell (the cursor); while dragging, the cells
        // ShapeFootprint returns are drawn as a ghost, and on release ShapeCommit paints (or
        // erases) exactly those cells as one undo step. ShapePaintColour is the ghost's fill
        // colour: what the current tool paints, or null when the shape erases.
        HexShape ShapeTool { get; }
        void ShapeFootprint(HexShape shape, Vector2Int start, Vector2Int end, HashSet<Vector2Int> into);
        bool ShapeCommit(HexShape shape, Vector2Int start, Vector2Int end, bool erase); // returns whether the map changed
        Color? ShapePaintColour(bool erase);
    }

    internal enum HexShape
    {
        None,
        Line,   // brush-width straight hex line from start to end
        Circle  // filled hex disc centred on start, radius = hex distance start -> end
    }

    // One clipboard entry: a cell's state at an axial offset from the clipboard's anchor.
    // Cell == null records "empty at this offset", so pasting it erases the target cell.
    internal struct ClipCell
    {
        public Vector2Int Offset;
        public SandboxMap.Cell? Cell;
    }

    // 2D hex-grid view of a SandboxMap for the map builder. Draws every grid position inside
    // the map's extent as a flat hexagon (layer colour if painted, dim if empty, plus an inner
    // marker hex for special tiles) with raw generateVisualContent triangles, and turns
    // pointer input into host calls: left/right drag = paint/erase stroke (or flood fill in
    // fill mode), middle drag = pan, mouse wheel = zoom towards the cursor. SELECT mode turns
    // the same left/right drag into add/remove-from-selection; paste placement turns a left
    // click into one clipboard stamp and a right click into cancel. On top of the map it draws
    // a cyan ring per selected cell and, during paste placement, a translucent amber-outlined
    // ghost of the clipboard under the cursor; otherwise (brush/SELECT, not fill) a white
    // outline + wash over the brush footprint around the hovered cell. The LINE / CIRCLE
    // tools turn a left/right drag into one shape between the drag's start and end cells,
    // previewed while dragging through the same overlay lists as the brush footprint (a
    // translucent tool-colour fill with a lime ring, or a dark fill with a red ring when the
    // shape erases) and committed on release.
    //
    // Layout uses the game's own HexLayout.AxialToWorld/HexCorners at size 1, flattened to 2D
    // as (world.x, -world.z) so +r runs down the screen (same orientation StructureSilhouette
    // uses for its thumbnails), then a uniform pixel scale (fit scale * zoom) + offset (centre
    // + pan). The mesh is drawn on a child element so this element's overflow:hidden clips it
    // to the canvas when zoomed in. Only repaints when asked: after an edit, Refresh(), a view
    // change, or a geometry change.
    internal sealed class HexCanvas : VisualElement
    {
        // HexLayout's own constants at size 1: horizontal centre spacing and row spacing.
        private const float HexW = HexLayout.HEX_W;   // 1.71875
        private const float HexRow = HexLayout.HEX_ROW; // 1.5

        // HexCorners at size 1 spans +-55/64 horizontally and +-1 vertically.
        private const float HexHalfW = 55f / 64f;
        private const float HexHalfH = 1f;

        // Fraction of the full hex drawn, so the dark background shows through as grid lines.
        private const float CellShrink = 0.9f;
        private const float MarkerShrink = 0.5f;
        // Margin kept around the grid inside the element, as a fraction of the fitted scale.
        private const float FitMargin = 0.96f;

        public const float MinZoom = 0.25f;
        public const float MaxZoom = 4f;
        // Same wheel handling as the game's ModTreeScreen: clamp delta, 1.3x per unit.
        private const float WheelClamp = 1.5f;
        private const float WheelBase = 1.3f;

        // 7 vertices per hexagon (centre + 6 corners); indices are ushort, so cap each
        // MeshGenerationContext.Allocate at 8000 hexes = 56000 vertices.
        private const int HexesPerChunk = 8000;

        private static readonly Color EmptyColour = new Color(0.11f, 0.12f, 0.15f, 1f);
        private static readonly Color BackgroundColour = new Color(0.02f, 0.02f, 0.03f, 1f);

        // Selection highlight: an opaque bright cyan-white ring just inside each selected
        // cell's fill, so the cell's own colour still shows in the middle.
        private static readonly Color SelectionRingColour = new Color(0.55f, 1f, 0.95f, 1f);
        private const float SelectionRingOuter = CellShrink;
        private const float SelectionRingInner = 0.68f;
        // Paste ghost: the clipboard's cells as translucent fills (empty offsets as a
        // translucent dark fill, since they erase), each with a thin amber ring, so it reads
        // as "not placed yet" and can't be mistaken for the cyan selection.
        private const float GhostAlpha = 0.55f;
        private static readonly Color GhostEmptyColour = new Color(0.02f, 0.02f, 0.03f, 0.7f);
        private static readonly Color GhostRingColour = new Color(1f, 0.72f, 0.2f, 0.9f);
        private const float GhostRingOuter = 1f;
        private const float GhostRingInner = 0.86f;
        // Brush footprint: a faint white wash over each cell plus a hairline white outline on
        // the hex's outer edge. A third look: no coloured fill (paste ghost previews colours,
        // amber ring), and on the outer edge rather than inset (cyan selection ring).
        private static readonly Color BrushWashColour = new Color(1f, 1f, 1f, 0.2f);
        private static readonly Color BrushRingColour = new Color(1f, 1f, 1f, 0.95f);
        private const float BrushRingOuter = 1f;
        private const float BrushRingInner = 0.92f;
        // LINE / CIRCLE drag preview, drawn through the brush-footprint lists (the two never
        // show at once): the tool's own colour as a translucent fill (like the paste ghost, it
        // previews what lands) with a lime outer ring, so it can't be read as the white brush
        // outline, the amber paste ghost or the cyan selection. An erasing shape gets the paste
        // ghost's dark "erases" fill and a red ring instead.
        private static readonly Color ShapeRingColour = new Color(0.62f, 1f, 0.3f, 0.95f);
        private static readonly Color ShapeEraseRingColour = new Color(1f, 0.3f, 0.3f, 0.95f);
        private const float ShapeRingOuter = 1f;
        private const float ShapeRingInner = 0.86f;
        // Tie-break nudge for HexLine (see there).
        private const double LineNudge = 1e-3;

        // Ring = 12 vertices (6 outer + 6 inner corners); keep each Allocate under ushort range.
        private const int RingsPerChunk = 5000;

        // Axial neighbours, used by the host's flood fill.
        public static readonly Vector2Int[] Neighbours =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0),
            new Vector2Int(0, 1), new Vector2Int(0, -1),
            new Vector2Int(1, -1), new Vector2Int(-1, 1)
        };

        private struct Hex
        {
            public Vector2 Centre;
            public float Shrink;
            public Color32 Colour;
        }

        // A hexagonal outline: the band between Outer and Inner (fractions of the full hex).
        private struct Ring
        {
            public Vector2 Centre;
            public float Outer;
            public float Inner;
            public Color32 Colour;
        }

        private readonly IHexCanvasHost _host;
        private readonly VisualElement _drawLayer;

        // Precomputed flattened corner offsets of a size-1 hex (HexCorners about the origin).
        private readonly Vector2[] _cornerOffsets = new Vector2[6];
        private readonly List<Hex> _hexes = new List<Hex>(4096);
        private readonly List<Ring> _selectionRings = new List<Ring>();
        private readonly List<Hex> _ghostHexes = new List<Hex>();
        private readonly List<Ring> _ghostRings = new List<Ring>();
        private readonly HashSet<Vector2Int> _brushCells = new HashSet<Vector2Int>();
        private readonly List<Hex> _brushHexes = new List<Hex>();
        private readonly List<Ring> _brushRings = new List<Ring>();

        // Grid extent (hex distance from origin) drawn and paintable: max of map.radius and
        // the furthest painted cell, so loaded maps with cells past their radius still show.
        private int _extent;
        // Fit-to-element scale (recomputed from the rect) and the element centre.
        private float _scale = 1f;
        private Vector2 _origin;
        // User view: zoom multiplies _scale, pan is a pixel offset added to _origin.
        private float _zoom = 1f;
        private Vector2 _pan = Vector2.zero;

        private bool _stroking;
        private bool _strokeErase;
        // The open stroke edits the selection (SELECT tool) rather than the map.
        private bool _strokeSelect;
        private int _strokePointerId = -1;

        // Open LINE / CIRCLE drag: which shape (fixed at pointer-down), its start and current
        // end cell, and whether it erases (right button).
        private bool _shaping;
        private HexShape _shapeKind;
        private Vector2Int _shapeStart;
        private Vector2Int _shapeEnd;
        private bool _shapeErase;
        private int _shapePointerId = -1;

        // Last cell under the pointer while it is over the canvas; anchors the paste ghost.
        private Vector2Int _hoverCell;
        private bool _hasHover;
        private Vector2 _lastPos;
        private Vector2Int _lastCell;
        private bool _hasLastCell;

        private bool _panning;
        private int _panPointerId = -1;
        private Vector2 _panLast;

        public int Extent => _extent;

        public HexCanvas(IHexCanvasHost host)
        {
            _host = host;

            Vector3[] corners = HexLayout.HexCorners(Vector3.zero, 1f);
            for (int i = 0; i < 6; i++)
            {
                _cornerOffsets[i] = new Vector2(corners[i].x, -corners[i].z);
            }

            style.flexGrow = 1f;
            style.backgroundColor = BackgroundColour;
            style.overflow = Overflow.Hidden;
            pickingMode = PickingMode.Position;

            // Fills this element exactly (no padding/border here), so its local coordinates
            // are the same as ours; ignores picking so all input targets the canvas.
            _drawLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            _drawLayer.style.position = Position.Absolute;
            _drawLayer.style.left = 0f;
            _drawLayer.style.top = 0f;
            _drawLayer.style.right = 0f;
            _drawLayer.style.bottom = 0f;
            _drawLayer.generateVisualContent += Draw;
            Add(_drawLayer);

            RegisterCallback<GeometryChangedEvent>(_ => Repaint());
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => { EndStroke(); EndShape(); EndPan(); });
            // Pointer onto a floating panel (or off the canvas): hide the paste ghost and the
            // brush footprint.
            RegisterCallback<PointerLeaveEvent>(_ =>
            {
                _hasHover = false;
                if (_host.PasteActive || _host.BrushPreviewActive)
                {
                    Repaint();
                }
            });
            RegisterCallback<WheelEvent>(OnWheel);

            Refresh();
        }

        // Call after the map object is replaced or edited outside a stroke (also after a
        // radius change: the extent is recomputed here).
        public void Refresh()
        {
            SandboxMap map = _host.Map;
            int extent = map != null ? map.radius : 0;
            if (map != null)
            {
                foreach (Vector2Int c in map.cells.Keys)
                {
                    extent = Mathf.Max(extent, HexLayout.Distance(0, 0, c.x, c.y));
                }
            }
            _extent = Mathf.Max(1, extent);
            Repaint();
        }

        public void Repaint() => _drawLayer.MarkDirtyRepaint();

        public void ResetView()
        {
            _zoom = 1f;
            _pan = Vector2.zero;
            Repaint();
        }

        public bool InExtent(int q, int r) => HexLayout.Distance(0, 0, q, r) <= _extent;

        // ---- layout -------------------------------------------------------------------

        private void UpdateLayout()
        {
            Rect rect = contentRect;
            // Half-extent of the whole grid in size-1 HexLayout units.
            float halfW = HexW * _extent + HexHalfW;
            float halfH = HexRow * _extent + HexHalfH;
            float sx = rect.width / (2f * halfW);
            float sy = rect.height / (2f * halfH);
            _scale = Mathf.Max(0.0001f, Mathf.Min(sx, sy) * FitMargin);
            _origin = rect.center;
        }

        // Effective pixels per size-1 unit, and the pixel position of axial (0, 0).
        private float ViewScale => _scale * _zoom;
        private Vector2 ViewOrigin => _origin + _pan;

        // Forward: axial (q, r) -> local pixel position of the hex centre.
        //     local = (origin + pan) + flat(AxialToWorld(q, r)) * (scale * zoom)
        private Vector2 AxialToLocal(int q, int r)
        {
            Vector3 w = HexLayout.AxialToWorld(q, r, 1f);
            return ViewOrigin + new Vector2(w.x, -w.z) * ViewScale;
        }

        // Exact inverse of AxialToLocal. Undo the pixel transform (subtract the same
        // origin + pan, divide by the same scale * zoom) to recover the flattened world (x, z),
        // then invert AxialToWorld's linear map
        //     x = HexW * (q + r/2),  z = -HexRow * r
        // giving   r = -z / HexRow,  q = x / HexW - r/2,
        // and cube-round the fractional (q, r) to the containing hex. HexCorners' outline
        // (half-width 55/64 = HexW/2, half-height 1 = HexRow*2/3) maps onto the exact regular
        // hex of cube space under this same linear map, so the rounding region is exactly the
        // drawn hexagon. (Matches HexLayout.WorldToAxial, which also ignores world.y.)
        private Vector2Int LocalToAxial(Vector2 local)
        {
            Vector2 o = ViewOrigin;
            float s = ViewScale;
            float x = (local.x - o.x) / s;
            float z = -(local.y - o.y) / s;
            float fr = -z / HexRow;
            float fq = x / HexW - fr * 0.5f;
            return CubeRound(fq, fr);
        }

        private static Vector2Int CubeRound(float fq, float fr)
        {
            float fs = -fq - fr;
            int q = Mathf.RoundToInt(fq);
            int r = Mathf.RoundToInt(fr);
            int s = Mathf.RoundToInt(fs);
            float dq = Mathf.Abs(q - fq);
            float dr = Mathf.Abs(r - fr);
            float ds = Mathf.Abs(s - fs);
            if (dq > dr && dq > ds)
            {
                q = -r - s;
            }
            else if (dr > ds)
            {
                r = -q - s;
            }
            return new Vector2Int(q, r);
        }

        // Straight hex line from a to b (both included), in order: the standard cube-lerp
        // line (the hex-grid equivalent of Bresenham). n = max(hex distance, 1) steps; sample
        // i lerps the cube coordinates at t = i/n and cube-rounds (CubeRound above, the same
        // rounding LocalToAxial uses) to the nearest hex. Axial (q, r) is cube (q, r, -q-r)
        // and CubeRound derives s itself, so lerping q and r is lerping the cube point.
        // Both ends get the same tiny nudge so a sample that lands exactly on an edge between
        // two hexes always breaks the tie the same way (the textbook epsilon; 1e-3 rather than
        // 1e-6 so it survives the float CubeRound takes at q/r in the hundreds). Consecutive
        // duplicates can't occur (each step moves under one hex), but are skipped anyway.
        public static void HexLine(Vector2Int a, Vector2Int b, List<Vector2Int> into)
        {
            int n = Mathf.Max(HexLayout.Distance(a.x, a.y, b.x, b.y), 1);
            double aq = a.x + LineNudge, ar = a.y + LineNudge;
            double bq = b.x + LineNudge, br = b.y + LineNudge;
            bool any = false;
            Vector2Int last = default;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                Vector2Int c = CubeRound((float)(aq + (bq - aq) * t), (float)(ar + (br - ar) * t));
                if (any && c == last)
                {
                    continue;
                }
                into.Add(c);
                last = c;
                any = true;
            }
        }

        // ---- drawing ------------------------------------------------------------------

        private void Draw(MeshGenerationContext ctx)
        {
            SandboxMap map = _host.Map;
            Rect rect = contentRect;
            if (map == null || rect.width < 1f || rect.height < 1f)
            {
                return;
            }
            UpdateLayout();

            // Skip hexes wholly outside the visible rect (matters when zoomed in on a big map).
            float s = ViewScale;
            float padX = HexHalfW * s;
            float padY = HexHalfH * s;
            float minX = rect.xMin - padX, maxX = rect.xMax + padX;
            float minY = rect.yMin - padY, maxY = rect.yMax + padY;

            _hexes.Clear();
            for (int r = -_extent; r <= _extent; r++)
            {
                for (int q = -_extent; q <= _extent; q++)
                {
                    if (HexLayout.Distance(0, 0, q, r) > _extent)
                    {
                        continue;
                    }
                    Vector2 centre = AxialToLocal(q, r);
                    if (centre.x < minX || centre.x > maxX || centre.y < minY || centre.y > maxY)
                    {
                        continue;
                    }
                    if (map.cells.TryGetValue(new Vector2Int(q, r), out SandboxMap.Cell cell))
                    {
                        _hexes.Add(new Hex { Centre = centre, Shrink = CellShrink, Colour = StructureSilhouette.LayerColour(cell.layer) });
                        string tile = cell.tile ?? "substrate";
                        if (tile != "substrate")
                        {
                            _hexes.Add(new Hex { Centre = centre, Shrink = MarkerShrink, Colour = _host.MarkerColour(tile) });
                        }
                    }
                    else
                    {
                        _hexes.Add(new Hex { Centre = centre, Shrink = CellShrink, Colour = EmptyColour });
                    }
                }
            }

            // Selection highlight rings, on top of the normal fills.
            _selectionRings.Clear();
            IEnumerable<Vector2Int> selected = _host.SelectedCells;
            if (selected != null)
            {
                foreach (Vector2Int c in selected)
                {
                    if (!InExtent(c.x, c.y))
                    {
                        continue;
                    }
                    Vector2 centre = AxialToLocal(c.x, c.y);
                    if (centre.x < minX || centre.x > maxX || centre.y < minY || centre.y > maxY)
                    {
                        continue;
                    }
                    _selectionRings.Add(new Ring { Centre = centre, Outer = SelectionRingOuter, Inner = SelectionRingInner, Colour = SelectionRingColour });
                }
            }

            // Paste ghost: the clipboard at cursor cell + offset, on top of everything else.
            // Offsets that land outside the extent are skipped (PasteAt skips them too).
            _ghostHexes.Clear();
            _ghostRings.Clear();
            IReadOnlyList<ClipCell> clip = _host.Clipboard;
            if (_host.PasteActive && _hasHover && clip != null)
            {
                foreach (ClipCell cc in clip)
                {
                    int q = _hoverCell.x + cc.Offset.x;
                    int r = _hoverCell.y + cc.Offset.y;
                    if (!InExtent(q, r))
                    {
                        continue;
                    }
                    Vector2 centre = AxialToLocal(q, r);
                    if (centre.x < minX || centre.x > maxX || centre.y < minY || centre.y > maxY)
                    {
                        continue;
                    }
                    if (cc.Cell.HasValue)
                    {
                        SandboxMap.Cell cell = cc.Cell.Value;
                        _ghostHexes.Add(new Hex { Centre = centre, Shrink = CellShrink, Colour = Translucent(StructureSilhouette.LayerColour(cell.layer)) });
                        string tile = cell.tile ?? "substrate";
                        if (tile != "substrate")
                        {
                            _ghostHexes.Add(new Hex { Centre = centre, Shrink = MarkerShrink, Colour = Translucent(_host.MarkerColour(tile)) });
                        }
                    }
                    else
                    {
                        _ghostHexes.Add(new Hex { Centre = centre, Shrink = CellShrink, Colour = GhostEmptyColour });
                    }
                    _ghostRings.Add(new Ring { Centre = centre, Outer = GhostRingOuter, Inner = GhostRingInner, Colour = GhostRingColour });
                }
            }

            // Brush footprint around the hovered cell. Nothing when the hovered cell itself is
            // outside the extent (PaintAt does nothing there either); footprint cells outside
            // it are skipped like PaintOne/SelectPaint skip them. Never at the same time as
            // the paste ghost (the host turns it off during placement).
            // An open LINE / CIRCLE drag replaces it with the shape's ghost, through the same
            // lists and the same per-cell loop (FootprintOverlay), just in the shape's colours.
            _brushHexes.Clear();
            _brushRings.Clear();
            if (_shaping)
            {
                _brushCells.Clear();
                _host.ShapeFootprint(_shapeKind, _shapeStart, _shapeEnd, _brushCells);
                Color? paint = _host.ShapePaintColour(_shapeErase);
                FootprintOverlay(minX, maxX, minY, maxY,
                    paint.HasValue ? (Color32)Translucent(paint.Value) : (Color32)GhostEmptyColour,
                    paint.HasValue ? ShapeRingColour : ShapeEraseRingColour,
                    ShapeRingOuter, ShapeRingInner);
            }
            else if (_hasHover && _host.BrushPreviewActive && InExtent(_hoverCell.x, _hoverCell.y))
            {
                _brushCells.Clear();
                _host.BrushFootprint(_hoverCell.x, _hoverCell.y, _brushCells);
                FootprintOverlay(minX, maxX, minY, maxY, BrushWashColour, BrushRingColour, BrushRingOuter, BrushRingInner);
            }

            // Allocation order is paint order: map, selection, brush wash (or shape ghost),
            // brush outlines (or shape rings), ghost fills, ghost outlines.
            DrawHexes(ctx, _hexes, s);
            DrawRings(ctx, _selectionRings, s);
            DrawHexes(ctx, _brushHexes, s);
            DrawRings(ctx, _brushRings, s);
            DrawHexes(ctx, _ghostHexes, s);
            DrawRings(ctx, _ghostRings, s);
        }

        private static Color Translucent(Color c) => new Color(c.r, c.g, c.b, GhostAlpha);

        // _brushCells -> one fill + one ring per cell into _brushHexes/_brushRings, skipping
        // cells outside the extent (PaintOne/SelectPaint skip them too) or the visible rect.
        private void FootprintOverlay(float minX, float maxX, float minY, float maxY, Color32 fill, Color32 ring, float ringOuter, float ringInner)
        {
            foreach (Vector2Int c in _brushCells)
            {
                if (!InExtent(c.x, c.y))
                {
                    continue;
                }
                Vector2 centre = AxialToLocal(c.x, c.y);
                if (centre.x < minX || centre.x > maxX || centre.y < minY || centre.y > maxY)
                {
                    continue;
                }
                _brushHexes.Add(new Hex { Centre = centre, Shrink = CellShrink, Colour = fill });
                _brushRings.Add(new Ring { Centre = centre, Outer = ringOuter, Inner = ringInner, Colour = ring });
            }
        }

        private void DrawHexes(MeshGenerationContext ctx, List<Hex> hexes, float s)
        {
            for (int start = 0; start < hexes.Count; start += HexesPerChunk)
            {
                int n = Mathf.Min(HexesPerChunk, hexes.Count - start);
                // Both windings per triangle (the game's own CampaignHubPage mesh does the same)
                // so nothing is culled whichever way UI Toolkit expects. Map cells are opaque;
                // only the paste ghost and the brush-footprint wash use alpha.
                MeshWriteData mesh = ctx.Allocate(n * 7, n * 36);
                for (int k = 0; k < n; k++)
                {
                    Hex h = hexes[start + k];
                    float px = s * h.Shrink;
                    mesh.SetNextVertex(new Vertex { position = new Vector3(h.Centre.x, h.Centre.y, Vertex.nearZ), tint = h.Colour });
                    for (int i = 0; i < 6; i++)
                    {
                        Vector2 p = h.Centre + _cornerOffsets[i] * px;
                        mesh.SetNextVertex(new Vertex { position = new Vector3(p.x, p.y, Vertex.nearZ), tint = h.Colour });
                    }
                    ushort b = (ushort)(k * 7);
                    for (int i = 0; i < 6; i++)
                    {
                        ushort a = (ushort)(b + 1 + i);
                        ushort c = (ushort)(b + 1 + (i + 1) % 6);
                        mesh.SetNextIndex(b);
                        mesh.SetNextIndex(a);
                        mesh.SetNextIndex(c);
                        mesh.SetNextIndex(b);
                        mesh.SetNextIndex(c);
                        mesh.SetNextIndex(a);
                    }
                }
            }
        }

        // Hexagonal outlines: per ring, vertices 0-5 are the outer corners and 6-11 the inner
        // ones; each side is the quad outer i, outer i+1, inner i+1, inner i (two triangles,
        // both windings, like DrawHexes).
        private void DrawRings(MeshGenerationContext ctx, List<Ring> rings, float s)
        {
            for (int start = 0; start < rings.Count; start += RingsPerChunk)
            {
                int n = Mathf.Min(RingsPerChunk, rings.Count - start);
                MeshWriteData mesh = ctx.Allocate(n * 12, n * 72);
                for (int k = 0; k < n; k++)
                {
                    Ring g = rings[start + k];
                    float outer = s * g.Outer;
                    float inner = s * g.Inner;
                    for (int i = 0; i < 6; i++)
                    {
                        Vector2 p = g.Centre + _cornerOffsets[i] * outer;
                        mesh.SetNextVertex(new Vertex { position = new Vector3(p.x, p.y, Vertex.nearZ), tint = g.Colour });
                    }
                    for (int i = 0; i < 6; i++)
                    {
                        Vector2 p = g.Centre + _cornerOffsets[i] * inner;
                        mesh.SetNextVertex(new Vertex { position = new Vector3(p.x, p.y, Vertex.nearZ), tint = g.Colour });
                    }
                    ushort b = (ushort)(k * 12);
                    for (int i = 0; i < 6; i++)
                    {
                        int j = (i + 1) % 6;
                        ushort o0 = (ushort)(b + i);
                        ushort o1 = (ushort)(b + j);
                        ushort i0 = (ushort)(b + 6 + i);
                        ushort i1 = (ushort)(b + 6 + j);
                        mesh.SetNextIndex(o0);
                        mesh.SetNextIndex(o1);
                        mesh.SetNextIndex(i1);
                        mesh.SetNextIndex(o0);
                        mesh.SetNextIndex(i1);
                        mesh.SetNextIndex(o1);
                        mesh.SetNextIndex(o0);
                        mesh.SetNextIndex(i1);
                        mesh.SetNextIndex(i0);
                        mesh.SetNextIndex(o0);
                        mesh.SetNextIndex(i0);
                        mesh.SetNextIndex(i1);
                    }
                }
            }
        }

        // ---- input --------------------------------------------------------------------

        private void OnPointerDown(PointerDownEvent e)
        {
            if (_stroking || _shaping || _panning)
            {
                e.StopPropagation();
                return;
            }
            UpdateLayout();
            Vector2 pos = e.localPosition;

            if (e.button == 2)
            {
                _panning = true;
                _panPointerId = e.pointerId;
                _panLast = pos;
                this.CapturePointer(e.pointerId);
                e.StopPropagation();
                return;
            }
            if (e.button != 0 && e.button != 1)
            {
                return;
            }
            bool erase = e.button == 1;

            // Dispatch precedence: paste placement, then LINE/CIRCLE, then SELECT, then fill,
            // then the brush. (The host keeps LINE/CIRCLE exclusive of SELECT and fill, so the
            // order between those only matters as a fallback.)
            if (_host.PasteActive)
            {
                // Left click = one stamp (placement stays active); right click = cancel.
                // No stroke, no capture.
                Vector2Int c = LocalToAxial(pos);
                _hoverCell = c;
                _hasHover = true;
                if (erase)
                {
                    _host.CancelPaste();
                }
                else
                {
                    _host.Hover(c.x, c.y);
                    _host.PasteAt(c.x, c.y);
                }
                Repaint();
                e.StopPropagation();
                return;
            }

            HexShape shape = _host.ShapeTool;
            if (shape != HexShape.None)
            {
                // LINE / CIRCLE: the drag only moves the end cell and repaints the ghost;
                // nothing is painted until EndShape. A start outside the extent does nothing
                // (like a brush click or fill there).
                Vector2Int c = LocalToAxial(pos);
                _hoverCell = c;
                _hasHover = true;
                if (InExtent(c.x, c.y))
                {
                    _shaping = true;
                    _shapeKind = shape;
                    _shapeStart = c;
                    _shapeEnd = c;
                    _shapeErase = erase;
                    _shapePointerId = e.pointerId;
                    this.CapturePointer(e.pointerId);
                    _host.Hover(c.x, c.y);
                    Repaint();
                }
                e.StopPropagation();
                return;
            }

            if (_host.FillMode && !_host.SelectMode)
            {
                // One click = one fill; no stroke, no capture.
                Vector2Int c = LocalToAxial(pos);
                if (InExtent(c.x, c.y))
                {
                    _host.Hover(c.x, c.y);
                    if (_host.Fill(c.x, c.y, erase))
                    {
                        Repaint();
                    }
                }
                e.StopPropagation();
                return;
            }

            // A SELECT drag reuses the brush stroke's capture and drag sampling; only PaintAt
            // and EndStroke route it to the selection instead of the map.
            _stroking = true;
            _strokeErase = erase;
            _strokeSelect = _host.SelectMode;
            _strokePointerId = e.pointerId;
            _hasLastCell = false;
            this.CapturePointer(e.pointerId);
            if (!_strokeSelect)
            {
                _host.StrokeBegin();
            }

            _lastPos = pos;
            _hoverCell = LocalToAxial(pos);
            _hasHover = true;
            if (PaintAt(pos))
            {
                Repaint();
            }
            e.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            Vector2 pos = e.localPosition;

            if (_panning)
            {
                if ((e.pressedButtons & 4) == 0)
                {
                    EndPan();
                    return;
                }
                _pan += pos - _panLast;
                _panLast = pos;
                // The grid moves under a still cursor; keep the brush footprint on the cell
                // that's under it now.
                UpdateLayout();
                _hoverCell = LocalToAxial(pos);
                _hasHover = true;
                Repaint();
                e.StopPropagation();
                return;
            }

            if (_shaping)
            {
                if ((e.pressedButtons & (_shapeErase ? 2 : 1)) == 0)
                {
                    // Button released without a PointerUp reaching us: commit, as a stroke does.
                    EndShape();
                    return;
                }
                UpdateLayout();
                Vector2Int end = LocalToAxial(pos);
                _hoverCell = end;
                _hasHover = true;
                if (end != _shapeEnd)
                {
                    _shapeEnd = end;
                    _host.Hover(end.x, end.y);
                    Repaint();
                }
                e.StopPropagation();
                return;
            }

            if (!_stroking)
            {
                if (contentRect.width >= 1f)
                {
                    UpdateLayout();
                    Vector2Int c = LocalToAxial(pos);
                    _host.Hover(c.x, c.y);
                    bool moved = !_hasHover || c != _hoverCell;
                    _hoverCell = c;
                    _hasHover = true;
                    if (moved && (_host.PasteActive || _host.BrushPreviewActive))
                    {
                        // The paste ghost / brush footprint follows the cursor cell by cell.
                        Repaint();
                    }
                }
                return;
            }

            int needed = _strokeErase ? 2 : 1;
            if ((e.pressedButtons & needed) == 0)
            {
                EndStroke();
                return;
            }

            // Sample along the segment since the last event so fast drags don't skip cells.
            bool changed = false;
            float step = Mathf.Max(1f, ViewScale * 0.5f);
            float dist = Vector2.Distance(_lastPos, pos);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / step));
            for (int i = 1; i <= steps; i++)
            {
                changed |= PaintAt(Vector2.Lerp(_lastPos, pos, (float)i / steps));
            }
            _lastPos = pos;
            // The brush footprint keeps following the cursor during a stroke / select drag.
            Vector2Int hover = LocalToAxial(pos);
            if (hover != _hoverCell || !_hasHover)
            {
                _hoverCell = hover;
                _hasHover = true;
                changed |= _host.BrushPreviewActive;
            }
            if (changed)
            {
                Repaint();
            }
            e.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (_stroking && e.pointerId == _strokePointerId)
            {
                EndStroke();
                e.StopPropagation();
            }
            else if (_shaping && e.pointerId == _shapePointerId)
            {
                EndShape();
                e.StopPropagation();
            }
            else if (_panning && e.pointerId == _panPointerId && e.button == 2)
            {
                EndPan();
                e.StopPropagation();
            }
        }

        // Zoom towards the cursor: keep the world point under the cursor at the same pixel.
        // With S = scale*zoom and O = origin+pan, the point under pixel p is w = (p - O) / S.
        // After zooming to S' we need O' + w*S' = p, i.e. O' = p - (p - O) * (S'/S), and since
        // the fit scale is unchanged S'/S = zoom'/zoom = k, so
        //     pan' = O' - origin = (p - origin) - (p - origin - pan) * k
        //          = (p - origin) * (1 - k) + pan * k.
        private void OnWheel(WheelEvent e)
        {
            float d = Mathf.Clamp(e.delta.y, -WheelClamp, WheelClamp);
            if (Mathf.Abs(d) < 0.001f)
            {
                return;
            }
            UpdateLayout();
            float newZoom = Mathf.Clamp(_zoom * Mathf.Pow(WheelBase, -d), MinZoom, MaxZoom);
            if (!Mathf.Approximately(newZoom, _zoom))
            {
                Vector2 p = this.WorldToLocal(e.mousePosition);
                float k = newZoom / _zoom;
                _pan = (p - _origin) * (1f - k) + _pan * k;
                _zoom = newZoom;
                // Zooming towards the cursor keeps the same world point under it, so the
                // hovered cell is normally unchanged; recompute anyway (rounding at cell edges).
                if (_hasHover)
                {
                    _hoverCell = LocalToAxial(p);
                }
                // An open LINE / CIRCLE drag ends wherever the cursor is now.
                if (_shaping)
                {
                    _shapeEnd = LocalToAxial(p);
                }
                Repaint();
            }
            e.StopPropagation();
        }

        private void EndStroke()
        {
            if (!_stroking)
            {
                return;
            }
            // Clear the flag first: ReleasePointer raises PointerCaptureOutEvent, which calls
            // back into here and must not end (and commit) the stroke a second time.
            _stroking = false;
            bool select = _strokeSelect;
            _strokeSelect = false;
            int id = _strokePointerId;
            _strokePointerId = -1;
            _hasLastCell = false;
            if (id >= 0 && this.HasPointerCapture(id))
            {
                this.ReleasePointer(id);
            }
            if (select)
            {
                _host.SelectEnd(_strokeErase);
            }
            else
            {
                _host.StrokeEnd();
            }
        }

        // Commit the open LINE / CIRCLE drag (pointer up, button no longer held, or capture
        // lost — same as a stroke, which also keeps its paint on capture loss). Flag first:
        // ReleasePointer raises PointerCaptureOutEvent, which calls back into here.
        private void EndShape()
        {
            if (!_shaping)
            {
                return;
            }
            _shaping = false;
            int id = _shapePointerId;
            _shapePointerId = -1;
            if (id >= 0 && this.HasPointerCapture(id))
            {
                this.ReleasePointer(id);
            }
            _host.ShapeCommit(_shapeKind, _shapeStart, _shapeEnd, _shapeErase);
            // The map changed and/or the ghost has to go either way.
            Repaint();
        }

        private void EndPan()
        {
            if (!_panning)
            {
                return;
            }
            _panning = false;
            int id = _panPointerId;
            _panPointerId = -1;
            if (id >= 0 && this.HasPointerCapture(id))
            {
                this.ReleasePointer(id);
            }
        }

        private bool PaintAt(Vector2 local)
        {
            Vector2Int c = LocalToAxial(local);
            if (_hasLastCell && c == _lastCell)
            {
                return false;
            }
            _lastCell = c;
            _hasLastCell = true;
            if (!InExtent(c.x, c.y))
            {
                return false;
            }
            _host.Hover(c.x, c.y);
            return _strokeSelect
                ? _host.SelectPaint(c.x, c.y, _strokeErase)
                : _host.StrokePaint(c.x, c.y, _strokeErase);
        }
    }
}
