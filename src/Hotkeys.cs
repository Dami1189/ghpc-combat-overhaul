using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;

namespace GHPCHitCam
{
    /// mod keys: key + modifiers ("F9", "Alt+Home", "Ctrl+Shift+K", "None"), read from settings every time so a change in
    /// the settings window works at once. old defaults sat on keys the game uses (F4/F5 waypoints, F11 screenshot, [ ]
    /// weapon switch, keypad +/- cruise speed, F5-F8 and B in its AAR controls), so defaults moved to free keys, the
    /// mission-changing ones behind Alt, and none of them do anything while the game's AAR is open
    internal static class Hotkeys
    {
        internal struct Combo
        {
            public KeyCode Key;
            public bool Alt, Ctrl, Shift;
            public bool None => Key == KeyCode.None;
        }

        static readonly Dictionary<string, Combo> _parsed = new Dictionary<string, Combo>();

        public static Combo Parse(string s)
        {
            s = (s ?? "").Trim();
            if (_parsed.TryGetValue(s, out var c)) return c;
            c = new Combo();
            foreach (var raw in s.Split('+'))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                switch (p.ToLowerInvariant())
                {
                    case "alt": c.Alt = true; continue;
                    case "ctrl": case "control": c.Ctrl = true; continue;
                    case "shift": c.Shift = true; continue;
                    case "none": c.Key = KeyCode.None; continue;
                }
                if (p.Length == 1 && char.IsDigit(p[0])) p = "Alpha" + p;
                if (Enum.TryParse(p, true, out KeyCode k)) c.Key = k;
            }
            _parsed[s] = c;
            return c;
        }

        public static string Store(Combo c)
        {
            if (c.None) return "None";
            return (c.Ctrl ? "Ctrl+" : "") + (c.Alt ? "Alt+" : "") + (c.Shift ? "Shift+" : "") + c.Key;
        }

        static readonly Dictionary<KeyCode, string> Names = new Dictionary<KeyCode, string>
        {
            { KeyCode.KeypadMultiply, "Num *" }, { KeyCode.KeypadDivide, "Num /" }, { KeyCode.KeypadPlus, "Num +" }, { KeyCode.KeypadMinus, "Num -" },
            { KeyCode.KeypadPeriod, "Num ." }, { KeyCode.KeypadEnter, "Num Enter" }, { KeyCode.LeftBracket, "[" }, { KeyCode.RightBracket, "]" },
            { KeyCode.BackQuote, "`" }, { KeyCode.Backslash, "\\" }, { KeyCode.Semicolon, ";" }, { KeyCode.Quote, "'" }, { KeyCode.Comma, "," },
            { KeyCode.Period, "." }, { KeyCode.Slash, "/" }, { KeyCode.Minus, "-" }, { KeyCode.Equals, "=" }, { KeyCode.PageUp, "Page Up" }, { KeyCode.PageDown, "Page Down" },
        };

        static string KeyName(KeyCode k)
        {
            if (Names.TryGetValue(k, out var n)) return n;
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return "Num " + (int)(k - KeyCode.Keypad0);
            return k.ToString();
        }

        /// human readable: "Alt+K", "9", "Num *", "none"
        public static string Label(string stored)
        {
            var c = Parse(stored);
            if (c.None) return "none";
            return (c.Ctrl ? "Ctrl+" : "") + (c.Alt ? "Alt+" : "") + (c.Shift ? "Shift+" : "") + KeyName(c.Key);
        }

        public static string Label(MelonPreferences_Entry<string> e) => e != null ? Label(e.Value) : "none";

        /// game's own after action report (its replay, with its own keys) is open
        static bool GameAarOpen()
        {
            try { return GHPC.AarController.InAar; } catch { return false; }
        }

        static bool AltHeld => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        static bool CtrlHeld => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        static bool ShiftHeld => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        /// pressed this frame: key went down with exactly its Alt and Ctrl (Alt+Home is not Home). Shift only counts when
        /// the combo asks for it (game's Shift toggles the outside camera, people hold it)
        public static bool Down(MelonPreferences_Entry<string> e, bool evenInGameAar = false)
        {
            if (e == null) return false;
            var c = Parse(e.Value);
            if (c.None) return false;
            bool fake = FakeFrame == Time.frameCount && FakeCombo != null && string.Equals(FakeCombo, e.Value, StringComparison.OrdinalIgnoreCase);
            if (!fake)
            {
                if (!Input.GetKeyDown(c.Key)) return false;
                if (c.Alt != AltHeld || c.Ctrl != CtrlHeld || (c.Shift && !ShiftHeld)) return false;
            }
            return evenInGameAar || !GameAarOpen();
        }

        /// self-test with the game not in front: a key the mod's check sees as pressed in that frame (the game doesn't)
        internal static string FakeCombo;
        internal static int FakeFrame = -1;

        // ------------------------------------------------------------------ the game's own bindings

        static Dictionary<KeyCode, List<(string action, bool alt, bool ctrl, bool shift)>> _game;
        static float _gameAt = -100f;

        /// what the game itself does with this key (its keyboard bindings, minus the AAR's raw keys since ours are off there),
        /// or null. rebuilt every few s while asked (player may rebind in the game's options)
        public static string GameUse(string stored)
        {
            var c = Parse(stored);
            if (c.None) return null;
            if (_game == null || Time.unscaledTime - _gameAt > 5f) ReadGame();
            if (_game == null || !_game.TryGetValue(c.Key, out var list)) return null;
            // a game binding without modifiers fires with ours held too
            var hits = list.Where(b => (!b.alt && !b.ctrl && !b.shift) || (b.alt == c.Alt && b.ctrl == c.Ctrl && b.shift == c.Shift)).Select(b => b.action).Distinct().ToList();
            return hits.Count > 0 ? string.Join(", ", hits) : null;
        }

        static void ReadGame()
        {
            _gameAt = Time.unscaledTime;
            try
            {
                var d = new Dictionary<KeyCode, List<(string, bool, bool, bool)>>();
                foreach (var p in Rewired.ReInput.players.AllPlayers)
                    foreach (var map in p.controllers.maps.GetAllMaps(Rewired.ControllerType.Keyboard))
                    {
                        string cat = Rewired.ReInput.mapping.GetMapCategory(map.categoryId)?.name ?? "";
                        if (cat == "BaseKeys") continue;
                        foreach (var a in map.ButtonMaps)
                        {
                            var act = Rewired.ReInput.mapping.GetAction(a.actionId);
                            var mods = new[] { a.modifierKey1, a.modifierKey2, a.modifierKey3 };
                            bool alt = mods.Contains(Rewired.ModifierKey.Alt), ctrl = mods.Contains(Rewired.ModifierKey.Control), shift = mods.Contains(Rewired.ModifierKey.Shift);
                            string name = act != null ? (string.IsNullOrEmpty(act.descriptiveName) ? act.name : act.descriptiveName) : "?";
                            if (!d.TryGetValue(a.keyCode, out var l)) d[a.keyCode] = l = new List<(string, bool, bool, bool)>();
                            l.Add((name, alt, ctrl, shift));
                        }
                    }
                _game = d;
            }
            catch (Exception e) { if (_game == null) HitCamMod.Log.Warning("Keys: can't read the game's key bindings: " + e.Message); }
        }

        // ------------------------------------------------------------------ the 1.1 layout

        /// keys whose old defaults clashed with the game, and their new ones (a key someone set by hand stays as is)
        public static readonly (string id, string old, string now)[] Moved =
        {
            ("AarKey", "F11", "F2"),
            ("ArcadeKey", "F6", "H"),
            ("ProvingGroundKey", "F4", "Home"),
            ("ArmorSimKey", "F5", "Insert"),
            ("RangeKey", "F7", "Alt+K"),
            ("ArsenalKey", "F8", "U"),
            ("FireNowKey", "B", "J"),
            ("MinimapZoomInKey", "KeypadPlus", "KeypadMultiply"),
            ("MinimapZoomOutKey", "KeypadMinus", "KeypadDivide"),
        };

        public static void MigrateDefaults(MelonPreferences_Category cat)
        {
            var moved = new List<string>();
            foreach (var (id, old, now) in Moved)
            {
                var e = cat.GetEntry<string>(id);
                if (e == null || !string.Equals(e.Value, old, StringComparison.OrdinalIgnoreCase)) continue;
                e.Value = now;
                moved.Add($"{id} {old} -> {now}");
            }
            if (moved.Count > 0) HitCamMod.Log.Msg("Keys: moved off the game's own keys: " + string.Join(", ", moved));
        }
    }
}
