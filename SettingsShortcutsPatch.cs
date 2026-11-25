using System;
using System.Collections.Generic;
using System.Reflection;
using GraveyardOrbit;
using GraveyardOrbit.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace GraveBuilder
{
    // Settings > Controls: a MAP BUILDER SHORTCUTS section under the stock ship rebind rows, one
    // row per ShortcutBindings action (always Ctrl + the key shown) plus a reset row. The stock
    // method runs untouched; this appends after it, building rows through SettingsModal's own
    // private Section / MakeRow / ActionButton / Bare so they match the stock look and hover.
    // The capture is our own (the stock Cell/Capture are tied to Controls.Scheme/Act): click a
    // key cell, then the next key press binds it (Esc cancels, modifiers are ignored).
    //
    // Layout: the modal is a fixed 1240x1020 (x the stock _k scale) with no scroll around the
    // tab pages. The stock Controls page alone nearly fills it with keyboard steering on (7
    // rebind rows), so there's no room for this section. So the page's existing children and
    // this section are moved into one ScrollView, styled like the stock dropdown list's
    // (MetaUi.AccentScroll); its bar sits in the page's side gutter so the stock rows keep
    // their width. The stock code only holds references to those children (the tab row, the
    // body it re-renders), never to the page's child list, so reparenting doesn't disturb it.
    // Hidden on the CONTROLLER scheme tab: these shortcuts are keyboard-only.
    [HarmonyPatch(typeof(SettingsModal), "BuildControls")]
    internal static class SettingsShortcutsPatch
    {
        private static readonly Type RowType = AccessTools.Inner(typeof(SettingsModal), "Row");
        private static readonly FieldInfo RowRootField = RowType == null ? null : AccessTools.Field(RowType, "root");
        private static readonly FieldInfo ScaleField = AccessTools.Field(typeof(SettingsModal), "_k");
        private static readonly FieldInfo ControlsPadField = AccessTools.Field(typeof(SettingsModal), "_controlsPad");
        private static readonly MethodInfo SectionMethod = AccessTools.Method(typeof(SettingsModal), "Section", new[] { typeof(VisualElement), typeof(string) });
        private static readonly MethodInfo MakeRowMethod = AccessTools.Method(typeof(SettingsModal), "MakeRow", new[] { typeof(VisualElement), typeof(string), typeof(float), typeof(bool), typeof(bool) });
        private static readonly MethodInfo ActionButtonMethod = RowType == null ? null : AccessTools.Method(typeof(SettingsModal), "ActionButton", new[] { RowType, typeof(string), typeof(bool), typeof(Action) });
        private static readonly MethodInfo BareMethod = AccessTools.Method(typeof(SettingsModal), "Bare", new[] { typeof(float), typeof(float) });

        // SettingsModal's own private palette (Orange, OrangeLt, PanelCol, Control, White).
        private static readonly Color Orange = new Color32(255, 157, 46, 255);
        private static readonly Color OrangeLt = new Color32(255, 196, 112, 255);
        private static readonly Color PanelCol = new Color32(10, 10, 16, 255);
        private static readonly Color Control = new Color32(18, 18, 26, 255);
        private static readonly Color White = new Color32(244, 246, 245, 255);

        // The stock key cell's size and the gap to its ALT column: a shortcut cell sits in the
        // MAIN column, so its right margin skips the (empty) ALT one.
        private const float CellW = 200f;
        private const float CellH = 42f;
        private const float CellGap = 20f;

        private static bool _warned;

        // No `footer` parameter: its type is SettingsModal's private Footer.
        private static void Postfix(VisualElement page, VisualElement modal)
        {
            try
            {
                if (page == null)
                {
                    return;
                }
                if (RowRootField == null || SectionMethod == null || MakeRowMethod == null || ActionButtonMethod == null || BareMethod == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogError("Shortcuts: SettingsModal's Row.root, Section, MakeRow, ActionButton or Bare not found; no MAP BUILDER SHORTCUTS section in Settings > Controls.");
                    }
                    return;
                }
                float k = ScaleField?.GetValue(null) is float scale && scale > 0f ? scale : 1f;
                ShortcutsSection section = new ShortcutsSection(k, modal);

                // The stock scheme tabs (KEYBOARD & MOUSE, CONTROLLER) are the page's first
                // child; read before the page's children move.
                List<Button> schemeTabs = page.childCount > 0 ? page[0].Query<Button>().ToList() : new List<Button>();

                ScrollView scroll = new ScrollView(ScrollViewMode.Vertical) { name = "gravebuilder-controls-scroll" };
                MetaUi.AccentScroll(scroll, MetaUi.Fade(White, 0.06f), MetaUi.Fade(Orange, 0.6f), Orange, null, section.P(4f));
                scroll.style.flexGrow = 1f;
                scroll.style.flexShrink = 1f;
                scroll.style.flexBasis = 0f;
                scroll.style.minHeight = 0f;
                // The bar in the page's side gutter (the page has 147 px side padding), not over
                // the rows' right edge.
                scroll.style.marginRight = -section.P(24f);
                scroll.contentContainer.style.paddingRight = section.P(24f);

                List<VisualElement> stock = new List<VisualElement>(page.Children());
                foreach (VisualElement child in stock)
                {
                    scroll.Add(child);
                }
                scroll.Add(section.Root);
                page.Add(scroll);
                // The page fills the space between the header and the footer exactly, whatever
                // its content, so the scroll view is what holds the overflow.
                page.style.flexGrow = 1f;
                page.style.flexShrink = 1f;
                page.style.flexBasis = 0f;
                page.style.minHeight = 0f;

                if (schemeTabs.Count >= 2)
                {
                    schemeTabs[0].clicked += () => section.SetVisible(true);
                    schemeTabs[1].clicked += () => section.SetVisible(false);
                }
                // The stock render has just run and stored the scheme it showed.
                bool pad = ControlsPadField?.GetValue(null) is bool b && b;
                section.SetVisible(!pad);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Shortcuts: building the MAP BUILDER SHORTCUTS section failed: {ex}");
            }
        }

        private sealed class ShortcutsSection
        {
            public readonly VisualElement Root;

            private readonly float _k;
            private readonly List<KeyCell> _cells = new List<KeyCell>();
            private readonly EventCallback<KeyDownEvent> _onKey;
            private KeyCell _listening;
            private VisualElement _keyHost;

            public ShortcutsSection(float k, VisualElement modal)
            {
                _k = k;
                _onKey = OnKey;
                Root = new VisualElement { name = "gravebuilder-shortcuts" };
                Root.style.marginTop = P(20f);
                Root.style.flexShrink = 0f;

                SectionMethod.Invoke(null, new object[] { Root, Loc.T("gravebuilder.shortcuts.header", "MAP BUILDER SHORTCUTS") });
                MakeRowMethod.Invoke(null, new object[] { Root, Loc.T("gravebuilder.shortcuts.note", "Each shortcut is Ctrl + the key shown. Ctrl + Shift + the Undo key also redoes. Esc cancels a rebind."), 52f, false, true });

                foreach (ShortcutAction action in ShortcutBindings.Actions)
                {
                    // Upper case like the stock rows' names (UP, BOOST...).
                    object row = MakeRowMethod.Invoke(null, new object[] { Root, action.DisplayName.ToUpperInvariant(), 62f, true, false });
                    VisualElement rowRoot = (VisualElement)RowRootField.GetValue(row);
                    KeyCell cell = new KeyCell(this, action.Id);
                    cell.Button.style.marginRight = P(CellGap + CellW);
                    cell.Button.clicked += () => Start(cell);
                    rowRoot.Add(cell.Button);
                    _cells.Add(cell);
                }

                object resetRow = MakeRowMethod.Invoke(null, new object[] { Root, Loc.T("gravebuilder.shortcuts.reset_row", "Put every map builder shortcut back to its default key"), 66f, false, true });
                ActionButtonMethod.Invoke(null, new object[] { resetRow, Loc.T("gravebuilder.shortcuts.reset_button", "RESET SHORTCUTS"), false, (Action)ResetAll });

                // While listening, keep the game's menu input (Esc closing Settings, the stock
                // pad/menu keys, In.KeyDown hotkeys such as the builder toggle) held off, as the
                // stock capture does. Runs only while this section is on a panel.
                Root.schedule.Execute(() =>
                {
                    if (_listening != null)
                    {
                        In.HoldMenuInput();
                    }
                }).Every(0L);
                // A click anywhere else (another cell, a stock rebind cell, CLOSE) cancels.
                modal?.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (_listening != null && !(e.target is VisualElement t && _listening.Button.Contains(t)))
                    {
                        Stop();
                    }
                }, TrickleDown.TrickleDown);
                Root.RegisterCallback<DetachFromPanelEvent>(_ => Stop());
            }

            public float P(float px)
            {
                return px * _k;
            }

            public Label Text(string s, MetaUi.Face face, float px, Color c, float trackEm = 0f)
            {
                return MetaUi.Text(s, face, P(px), c, P(px) * trackEm);
            }

            public void SetVisible(bool on)
            {
                if (!on)
                {
                    Stop();
                }
                Root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            }

            private void Start(KeyCell cell)
            {
                if (_listening == cell)
                {
                    return;
                }
                Stop();
                VisualElement host = cell.Button.panel?.visualTree;
                if (host == null)
                {
                    return;
                }
                foreach (KeyCell c in _cells)
                {
                    c.Cleared = false;
                }
                _listening = cell;
                cell.Listening = true;
                // TrickleDown on the panel root: sees the key before anything focused does.
                _keyHost = host;
                _keyHost.RegisterCallback(_onKey, TrickleDown.TrickleDown);
                RedrawAll();
            }

            private void Stop()
            {
                if (_keyHost != null)
                {
                    _keyHost.UnregisterCallback(_onKey, TrickleDown.TrickleDown);
                    _keyHost = null;
                }
                if (_listening != null)
                {
                    _listening.Listening = false;
                    _listening.Draw();
                    _listening = null;
                }
            }

            private void OnKey(KeyDownEvent e)
            {
                if (_listening == null)
                {
                    Stop();
                    return;
                }
                KeyCode key = e.keyCode;
                if (key == KeyCode.None)
                {
                    return; // the character half of a key press
                }
                e.StopPropagation();
                if (key == KeyCode.Escape)
                {
                    Stop();
                    return;
                }
                // Modifiers (Ctrl may well be held) and keys a shortcut can't use: keep listening.
                if (!ShortcutBindings.IsBindable(key))
                {
                    return;
                }
                string id = _listening.Id;
                string previous = ShortcutBindings.BoundTo(key);
                ShortcutBindings.Set(id, key);
                Stop();
                if (previous != null && previous != id)
                {
                    foreach (KeyCell c in _cells)
                    {
                        if (c.Id == previous)
                        {
                            c.Cleared = true; // its key moved here; shown unbound and marked
                        }
                    }
                }
                RedrawAll();
            }

            private void ResetAll()
            {
                Stop();
                ShortcutBindings.ResetAll();
                foreach (KeyCell c in _cells)
                {
                    c.Cleared = false;
                }
                RedrawAll();
            }

            private void RedrawAll()
            {
                foreach (KeyCell c in _cells)
                {
                    c.Draw();
                }
            }
        }

        // One key cell, drawn like the stock rebind Cell: a slab with "CTRL + <key>" as key caps,
        // a dash when unbound, PRESS A KEY while listening.
        private sealed class KeyCell
        {
            public readonly string Id;
            public readonly Button Button;
            public bool Listening;
            public bool Cleared;

            private readonly ShortcutsSection _s;
            private readonly SlabFrame _f;
            private readonly VisualElement _combo;
            private readonly VisualElement _keyCap;
            private readonly VisualElement _dash;
            private readonly Label _word;
            private bool _hot;

            public KeyCell(ShortcutsSection s, string id)
            {
                _s = s;
                Id = id;
                Button = (Button)BareMethod.Invoke(null, new object[] { CellW, CellH });
                _f = new SlabFrame { Notch = s.P(10f), TintColor = MetaUi.Fade(Orange, 0f) }.Behind(Button);

                _combo = new VisualElement { pickingMode = PickingMode.Ignore };
                _combo.style.flexDirection = FlexDirection.Row;
                _combo.style.alignItems = Align.Center;
                VisualElement ctrlCap = new VisualElement { pickingMode = PickingMode.Ignore };
                BindingGlyph.Apply(ctrlCap, (string)null, Loc.T("gravebuilder.shortcuts.ctrl_cap", "CTRL"), s.P(28f), 2f, 1f);
                _combo.Add(ctrlCap);
                Label plus = s.Text("+", MetaUi.Face.Display, 15f, MetaUi.Fade(Color.white, 0.6f));
                plus.style.marginLeft = s.P(7f);
                plus.style.marginRight = s.P(7f);
                _combo.Add(plus);
                _keyCap = new VisualElement { pickingMode = PickingMode.Ignore };
                _combo.Add(_keyCap);
                Button.Add(_combo);

                _dash = new VisualElement { pickingMode = PickingMode.Ignore };
                _dash.style.width = s.P(18f);
                _dash.style.height = Mathf.Max(1f, s.P(2f));
                _dash.style.backgroundColor = MetaUi.Fade(Color.white, 0.35f);
                Button.Add(_dash);

                _word = MetaUi.Center(s.Text(Loc.T("gravebuilder.shortcuts.press_a_key", "PRESS A KEY"), MetaUi.Face.Display, 13f, Color.white, 0.2f));
                Button.Add(_word);

                Button.RegisterCallback<PointerEnterEvent>(_ => { _hot = true; Draw(); });
                Button.RegisterCallback<PointerLeaveEvent>(_ => { _hot = false; Draw(); });
                Draw();
            }

            public void Draw()
            {
                // The stock Cell's states: listening, hover, marked (here: lost its key to
                // another shortcut), rest.
                if (Listening)
                {
                    Paint(3f, Color.white, Color.Lerp(Control, Orange, 0.35f));
                }
                else if (_hot)
                {
                    Paint(3f, Orange, Color.Lerp(Control, Orange, 0.2f));
                }
                else if (Cleared)
                {
                    Paint(2f, OrangeLt, Color.Lerp(Control, OrangeLt, 0.14f));
                }
                else
                {
                    Paint(2f, Color.Lerp(PanelCol, Color.white, 0.16f), Control);
                }

                KeyCode key = ShortcutBindings.Get(Id);
                bool unbound = key == KeyCode.None;
                _word.style.display = Listening ? DisplayStyle.Flex : DisplayStyle.None;
                _combo.style.display = !Listening && !unbound ? DisplayStyle.Flex : DisplayStyle.None;
                _dash.style.display = !Listening && unbound ? DisplayStyle.Flex : DisplayStyle.None;
                if (!Listening && !unbound)
                {
                    BindingGlyph.Apply(_keyCap, (string)null, ShortcutBindings.KeyText(key), _s.P(28f), 2f, 1f);
                }
            }

            private void Paint(float stroke, Color edge, Color fill)
            {
                _f.Stroke = _s.P(stroke);
                _f.StrokeColor = edge;
                _f.FillColor = fill;
            }
        }
    }
}
