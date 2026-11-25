using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GraveyardOrbit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GraveBuilder
{
    // One rebindable map builder shortcut: its id (the key in shortcuts.json), its display
    // name (a Loc key plus the English fallback) and its default key.
    public sealed class ShortcutAction
    {
        public readonly string Id;
        public readonly string LocKey;
        public readonly string English;
        public readonly KeyCode Default;

        public ShortcutAction(string id, string locKey, string english, KeyCode def)
        {
            Id = id;
            LocKey = locKey;
            English = english;
            Default = def;
        }

        public string DisplayName => Loc.T(LocKey, English);

        public KeyCode Current => ShortcutBindings.Get(Id);
    }

    // The player's map builder shortcuts. Every one is always Ctrl + the bound key; only the
    // key is rebindable, never the modifier. Redo also always fires on Ctrl+Shift + Undo's key
    // (a fixed alias, not its own binding). KeyCode.None means unbound: that action never fires.
    // Stored as { "<id>": "<KeyCode name>" } in shortcuts.json beside the plugin DLL; a missing
    // or corrupt file, or a missing/invalid entry, falls back to the defaults. Rebound in
    // Settings > Controls (SettingsShortcutsPatch).
    public static class ShortcutBindings
    {
        public const string ToggleBuilder = "toggle_builder";
        public const string Undo = "undo";
        public const string Redo = "redo";
        public const string Copy = "copy";
        public const string Cut = "cut";
        public const string Paste = "paste";

        private const string FileName = "shortcuts.json";

        // Display order for the Settings section.
        private static readonly ShortcutAction[] _actions =
        {
            new ShortcutAction(ToggleBuilder, "gravebuilder.shortcuts.toggle_builder", "Open/Close Map Builder", KeyCode.F7),
            new ShortcutAction(Undo, "gravebuilder.shortcuts.undo", "Undo", KeyCode.Z),
            new ShortcutAction(Redo, "gravebuilder.shortcuts.redo", "Redo", KeyCode.Y),
            new ShortcutAction(Copy, "gravebuilder.shortcuts.copy", "Copy", KeyCode.C),
            new ShortcutAction(Cut, "gravebuilder.shortcuts.cut", "Cut", KeyCode.X),
            new ShortcutAction(Paste, "gravebuilder.shortcuts.paste", "Paste", KeyCode.V),
        };

        private static readonly Dictionary<string, KeyCode> _keys = new Dictionary<string, KeyCode>();
        private static bool _loaded;
        private static string _path;

        public static IReadOnlyList<ShortcutAction> Actions => _actions;

        // The bound key, or KeyCode.None when unbound or the id is unknown.
        public static KeyCode Get(string actionId)
        {
            EnsureLoaded();
            return actionId != null && _keys.TryGetValue(actionId, out KeyCode key) ? key : KeyCode.None;
        }

        // Binds the key and saves. Any other action holding the same key is unbound
        // (KeyCode.None), so one key never fires two actions.
        public static void Set(string actionId, KeyCode key)
        {
            EnsureLoaded();
            if (actionId == null || !_keys.ContainsKey(actionId))
            {
                Plugin.Log?.LogWarning($"Shortcuts: unknown action '{actionId}', not bound.");
                return;
            }
            if (key != KeyCode.None)
            {
                foreach (ShortcutAction a in _actions)
                {
                    if (a.Id != actionId && _keys[a.Id] == key)
                    {
                        _keys[a.Id] = KeyCode.None;
                    }
                }
            }
            _keys[actionId] = key;
            Save();
        }

        // The id of the action bound to this key, or null.
        public static string BoundTo(KeyCode key)
        {
            EnsureLoaded();
            if (key == KeyCode.None)
            {
                return null;
            }
            foreach (ShortcutAction a in _actions)
            {
                if (_keys[a.Id] == key)
                {
                    return a.Id;
                }
            }
            return null;
        }

        public static void ResetAll()
        {
            EnsureLoaded();
            foreach (ShortcutAction a in _actions)
            {
                _keys[a.Id] = a.Default;
            }
            Save();
        }

        // Keys a shortcut can use: letters, top-row digits and F1-F12. That is the set the
        // game's own In.KeyDown can read (its private In.Map), which the builder toggle goes
        // through, so a key outside it would never fire.
        public static bool IsBindable(KeyCode key)
        {
            return (key >= KeyCode.A && key <= KeyCode.Z)
                || (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                || (key >= KeyCode.F1 && key <= KeyCode.F12);
        }

        // Short key-cap text: "Z", "7", "F7".
        public static string KeyText(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            {
                return ((int)(key - KeyCode.Alpha0)).ToString();
            }
            return key.ToString().ToUpperInvariant();
        }

        // "Ctrl+<Key>" for a tooltip, e.g. "Ctrl+Z" — or null if the action is unbound, so
        // callers can fall back to their own "not currently bound" wording.
        public static string ComboText(string actionId)
        {
            KeyCode key = Get(actionId);
            return key == KeyCode.None ? null : "Ctrl+" + KeyText(key);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            foreach (ShortcutAction a in _actions)
            {
                _keys[a.Id] = a.Default;
            }
            try
            {
                _path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), FileName);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Shortcuts: couldn't resolve the plugin folder, using the defaults and not saving: {ex.Message}");
                _path = null;
                return;
            }
            if (!File.Exists(_path))
            {
                return;
            }
            try
            {
                JObject json = JObject.Parse(File.ReadAllText(_path));
                // In display order, so if a hand-edited file gives two actions one key, the
                // first keeps it and the later one is unbound.
                HashSet<KeyCode> used = new HashSet<KeyCode>();
                HashSet<string> fromFile = new HashSet<string>();
                foreach (ShortcutAction a in _actions)
                {
                    if (!(json[a.Id] is JValue value) || value.Type != JTokenType.String)
                    {
                        continue; // missing or not a string: keep the default
                    }
                    string name = (string)value;
                    KeyCode key;
                    if (string.Equals(name, nameof(KeyCode.None), StringComparison.OrdinalIgnoreCase))
                    {
                        key = KeyCode.None;
                    }
                    else if (!Enum.TryParse(name, true, out key) || !IsBindable(key))
                    {
                        Plugin.Log?.LogWarning($"Shortcuts: '{name}' isn't a usable key for '{a.Id}' in {FileName}; using the default ({a.Default}).");
                        continue;
                    }
                    if (key != KeyCode.None && !used.Add(key))
                    {
                        Plugin.Log?.LogWarning($"Shortcuts: {key} is bound to more than one action in {FileName}; '{a.Id}' is left unbound.");
                        key = KeyCode.None;
                    }
                    _keys[a.Id] = key;
                    fromFile.Add(a.Id);
                }
                // A default (entry missing or invalid) that a loaded entry now duplicates: the
                // loaded one wins. Defaults never collide with each other.
                foreach (ShortcutAction a in _actions)
                {
                    if (!fromFile.Contains(a.Id) && _keys[a.Id] != KeyCode.None && CountBound(_keys[a.Id]) > 1)
                    {
                        _keys[a.Id] = KeyCode.None;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Shortcuts: couldn't read {_path} ({ex.Message}); using the defaults.");
                foreach (ShortcutAction a in _actions)
                {
                    _keys[a.Id] = a.Default;
                }
            }
        }

        private static int CountBound(KeyCode key)
        {
            int n = 0;
            foreach (ShortcutAction a in _actions)
            {
                if (_keys[a.Id] == key)
                {
                    n++;
                }
            }
            return n;
        }

        private static void Save()
        {
            if (_path == null)
            {
                return;
            }
            try
            {
                JObject json = new JObject();
                foreach (ShortcutAction a in _actions)
                {
                    json[a.Id] = _keys[a.Id].ToString();
                }
                File.WriteAllText(_path, json.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Shortcuts: couldn't save {_path}: {ex.Message}");
            }
        }
    }
}
