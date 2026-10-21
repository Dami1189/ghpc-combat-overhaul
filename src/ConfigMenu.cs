using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.Player;
using GHPC.State;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GHPCHitCam
{
    /// In-game settings window (ConfigKey, default F12, the gear button while the mouse cursor is visible, or the HitCam tab
    /// in the game's own Options, OptionsHook): every MelonPreferences entry of the mod's categories, in tabs and sections
    /// (Layout), each with what it does in plain words (Help) and, for the ones that cost frame rate, how much (Cost); a
    /// Quick setup tab with the quality presets (Presets) and the main switches. Toggles / sliders / choices / key capture.
    /// Changes apply at once (keys are re-read too) and are saved with MelonPreferences.Save() on close or "Save".
    /// While open: the game's own pause (ConfigPauseGame), cursor free, keyboard input to the game blocked; the arcade HUD
    /// stays visible behind it with example callout / kill feed lines so size changes can be seen.
    internal static class ConfigMenu
    {
        public static bool IsOpen { get; private set; }
        public static bool PausedByMenu { get; private set; }
        static bool _cursorVisible, _blockKb, _dirty;
        static EventSystem _uiOff;      // the game's menu input, off while the window is up (clicks would fall through to it)
        static CursorLockMode _cursorLock;
        static int _tab;
        internal static int Tab { get => _tab; set => _tab = value; }
        static Vector2 _scroll;
        static MelonPreferences_Entry _capturing;
        static string _status = "";
        static float _statusAt = -99f;

        static readonly string[] Tabs = { "Quick setup", "Hit cam", "X-ray", "World & sound", "HUD", "Tools", "Keys", "Advanced" };
        internal static int TabIndex(string name) => Math.Max(0, Array.IndexOf(Tabs, name));

        /// The window: tab, section, its settings in this order. A setting listed nowhere goes to Advanced (a key: to Keys).
        static readonly (string tab, string title, string[] ids)[] Layout =
        {
            ("Hit cam", "Window", new[] { "Enabled", "PipSize", "PipCorner", "Style", "HullTexture", "HitCamAntiAliasing" }),
            ("Hit cam", "Which hits", new[] { "PlayerShotsOnly", "MinCaliberMm", "BurstCaliberMm", "QueueHits" }),
            ("Hit cam", "Timing", new[] { "HitCamPace", "ConstantSlowMo", "ApproachSeconds", "InteriorSeconds", "AftermathSeconds", "ImpactSlowMo" }),
            ("Hit cam", "Ammunition fires and cook-offs", new[] { "CookOffWait", "CookOffReplay" }),
            ("X-ray", "Camera", new[] { "CinematicXray", "RodCam" }),
            ("X-ray", "Crew and physics", new[] { "XrayPhysics", "XrayPhysicsSpeed", "DetailedCrew", "CrewPanic", "AmmoExplosionKillsCrew" }),
            ("X-ray", "Round and armour", new[] { "RodSoftBody", "ShowAllSpall", "ArmorHoles" }),
            ("World & sound", "Damage on the real vehicles", new[] { "WorldDamage", "CutModels", "MaxWorldPlates", "WorldVentJets" }),
            ("World & sound", "Shrapnel", new[] { "WorldShrapnel", "MaxDebris", "DebrisSeconds" }),
            ("World & sound", "Explosions", new[] { "ExplosionSound", "ExplosionLoudness", "ShockwaveShake" }),
            ("HUD", "Arcade HUD", new[] { "ArcadeHud", "ArcadeCallouts", "ArcadeKillFeed", "KillFeedSide", "KillCreditSeconds", "ArcadeSfx", "ArcadeSfxVolume" }),
            ("HUD", "Aiming aids", new[] { "ArcadePenIndicator", "ArcadePenNumbers", "ArcadeImpactMarker", "ArcadeLeadMarker" }),
            ("HUD", "Enemy markers", new[] { "ArcadeMarkers", "MarkerSource", "MarkerRange", "MarkerMemorySeconds", "MarkerClassIcons" }),
            ("HUD", "Minimap", new[] { "ArcadeMinimap", "MinimapSize", "MinimapCorner", "MinimapPlayerUp", "MinimapTerrain", "MinimapEnemies", "MinimapInfantry", "MinimapObjectives" }),
            ("HUD", "Sizes", new[] { "HudScaleCallouts", "HudScaleMarkers", "HudScaleCross", "HudScaleKillFeed" }),
            ("Tools", "After Action Report", new[] { "AarEnabled", "AarPauseGame", "AarFullModels", "SaveRecords", "PastMissionsKept" }),
            ("Tools", "Proving ground", new[] { "ProvingGroundMenuButton", "ProvingGroundRows", "ProvingGroundPerRow", "ProvingGroundBigCam" }),
            ("Tools", "Armor Sim", new[] { "ArmorSimMenuButton", "ArmorSimMission", "ArmorSimMapBudgetMs" }),
            ("Keys", "Hit cam", new[] { "ToggleKey", "ReplayKey", "ConfigKey" }),
            ("Keys", "Tools", new[] { "AarKey", "ArmorSimKey", "ProvingGroundKey", "RangeKey", "ArsenalKey", "FireNowKey" }),
            ("Keys", "HUD", new[] { "ArcadeKey", "MinimapZoomInKey", "MinimapZoomOutKey" }),
            ("Advanced", "Settings window", new[] { "ConfigPauseGame" }),
            ("Advanced", "Troubleshooting", new[] { "DebugLog" }),
        };
        static readonly HashSet<string> Unlisted = new HashSet<string> { "PipWidth" };   // old, unused

        /// What a setting costs in frame rate (1 small, 2 noticeable, 3 big) and why. The presets set these.
        static readonly Dictionary<string, (int lvl, string why)> Cost = new Dictionary<string, (int, string)>
        {
            { "XrayPhysics", (3, "a physics world of its own runs while the hit cam plays (ragdolls, plate pieces)") },
            { "CutModels", (2, "each hit cuts the vehicle's own model open") },
            { "DetailedCrew", (2, "each hit copies the posed crew models") },
            { "ShowAllSpall", (2, "draws hundreds of fragment paths instead of the few that hit something") },
            { "CookOffReplay", (2, "builds the x-ray a second time for the cook-off") },
            { "HitCamAntiAliasing", (2, "graphics card: 2x / 4x / 8x the pixel work in the hit cam window") },
            { "WorldShrapnel", (2, "each piece is a physics body in the game's own physics") },
            { "MaxDebris", (2, "each shrapnel piece on the ground is a physics body") },
            { "MaxWorldPlates", (2, "each 3D hole is a mesh of its own on the vehicle") },
            { "WorldDamage", (2, "holes, scorch, sparks and shrapnel on the real vehicles") },
            { "DebrisSeconds", (1, "how long those physics bodies stay") },
            { "RodSoftBody", (1, "a deforming rod mesh, rebuilt every frame while it is in view") },
            { "CrewPanic", (1, "burning crew move in the hit cam physics") },
            { "WorldVentJets", (1, "flame particles out of the holes while ammo burns") },
            { "AarEnabled", (1, "keeps a copy of every hit of the mission in memory") },
            { "AarFullModels", (1, "memory: full x-ray models kept for the replays") },
            { "PipSize", (1, "graphics card: a bigger window is more pixels to draw") },
            { "MinimapTerrain", (1, "a short pause once at the start of a mission (draws the map)") },
            { "MarkerSource", (1, "LineOfSight checks the line to every enemy each frame") },
            { "DebugLog", (1, "a little bookkeeping on every frame") },
        };


        /// Section lists of the current tab: (title, entries).
        static readonly List<(int tab, string title, List<MelonPreferences_Entry> rows)> _sections = new List<(int, string, List<MelonPreferences_Entry>)>();
        static readonly Dictionary<string, MelonPreferences_Entry> _byId = new Dictionary<string, MelonPreferences_Entry>();

        static void BuildSections()
        {
            _sections.Clear();
            _byId.Clear();
            foreach (var e in _entries) _byId[e.Identifier] = e;
            var placed = new HashSet<string>(Unlisted);
            foreach (var (tab, title, ids) in Layout)
            {
                var rows = new List<MelonPreferences_Entry>();
                foreach (var id in ids) if (_byId.TryGetValue(id, out var e) && placed.Add(id)) rows.Add(e);
                if (rows.Count > 0) _sections.Add((TabIndex(tab), title, rows));
            }
            var keys = new List<MelonPreferences_Entry>();
            var other = new List<MelonPreferences_Entry>();
            foreach (var e in _entries)
                if (!placed.Contains(e.Identifier)) (e.Identifier.EndsWith("Key") ? keys : other).Add(e);
            if (keys.Count > 0) _sections.Add((TabIndex("Keys"), "Other", keys));
            if (other.Count > 0) _sections.Add((TabIndex("Advanced"), "Other", other));
        }

        static readonly Dictionary<string, Vector2> Ranges = new Dictionary<string, Vector2>
        {
            { "PipWidth", new Vector2(0.15f, 0.6f) }, { "PipSize", new Vector2(0.12f, 0.6f) }, { "ApproachSeconds", new Vector2(0f, 4f) }, { "InteriorSeconds", new Vector2(0.5f, 10f) },
            { "AftermathSeconds", new Vector2(0f, 8f) }, { "ImpactSlowMo", new Vector2(0f, 4f) }, { "ConstantSlowMo", new Vector2(20f, 400f) }, { "MinCaliberMm", new Vector2(0f, 60f) },
            { "BurstCaliberMm", new Vector2(0f, 120f) }, { "HullTexture", new Vector2(0f, 1f) }, { "XrayPhysicsSpeed", new Vector2(0.05f, 1f) },
            { "DebrisSeconds", new Vector2(0f, 120f) }, { "MaxDebris", new Vector2(0f, 600f) }, { "PastMissionsKept", new Vector2(0f, 20f) }, { "MaxWorldPlates", new Vector2(0f, 200f) },
            { "MarkerRange", new Vector2(200f, 6000f) }, { "MarkerMemorySeconds", new Vector2(0f, 60f) }, { "MinimapSize", new Vector2(0.1f, 0.6f) },
            { "KillCreditSeconds", new Vector2(5f, 180f) }, { "ArcadeSfxVolume", new Vector2(0f, 1f) }, { "ArmorSimMapBudgetMs", new Vector2(1f, 20f) },
            { "AarFullModels", new Vector2(0f, 300f) }, { "CookOffWait", new Vector2(0f, 30f) }, { "ExplosionLoudness", new Vector2(1f, 3f) }, { "ProvingGroundRows", new Vector2(1f, 20f) }, { "ProvingGroundPerRow", new Vector2(1f, 5f) },
        };

        static readonly Dictionary<string, string[]> Options = new Dictionary<string, string[]>
        {
            { "PipCorner", new[] { "TopRight", "TopLeft", "BottomRight", "BottomLeft" } },
            { "MinimapCorner", new[] { "BottomRight", "BottomLeft", "TopLeft", "TopRight" } },
            { "Style", new[] { "WarThunder", "Blueprint" } },
            { "MarkerSource", new[] { "Spotted", "LineOfSight" } },
            { "KillFeedSide", new[] { "Left", "Right" } },
            { "MinimapEnemies", new[] { "Spotted", "Always", "Off" } },
            { "HitCamAntiAliasing", new[] { "Off", "2x", "4x", "8x" } },
            { "HitCamPace", new[] { "Cinematic", "Constant" } },
        };

        /// The mod's entries (its own category, and any other category whose name says HitCam).
        static readonly List<MelonPreferences_Entry> _entries = new List<MelonPreferences_Entry>();
        static void Collect()
        {
            _entries.Clear();
            foreach (var cat in MelonPreferences.Categories)
            {
                if (cat == null) continue;
                if (cat != Settings.Cat && cat.Identifier.IndexOf("HitCam", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var e in cat.Entries) if (e != null && !e.IsHidden) _entries.Add(e);
            }
            BuildSections();
        }

        // ------------------------------------------------------------------ open / close

        public static void Update()
        {
            try
            {
                OptionsHook.Update();
                if (_capturing == null && Input.GetKeyDown(Settings.ConfigKeyCode)) { if (IsOpen) Close(); else Open(); }
                else if (IsOpen && _capturing == null && Input.GetKeyDown(KeyCode.Escape)) Close();
                if (IsOpen)
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                    var pi = PlayerInput.Instance;
                    if (pi != null) pi.BlockKeyboardInput = true;
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Settings window: " + e.Message); }
        }

        public static void Open()
        {
            if (IsOpen) return;
            Collect();
            IsOpen = true;
            _capturing = null;
            _cursorVisible = Cursor.visible;
            _cursorLock = Cursor.lockState;
            var pi = PlayerInput.Instance;
            if (pi != null) _blockKb = pi.BlockKeyboardInput;
            PausedByMenu = false;
            try
            {
                if (Settings.ConfigPauseGame.Value && pi != null && pi.CurrentPlayerUnit != null && TimeController.Instance != null && !TimeController.Paused)
                {
                    TimeController.Pause();
                    PausedByMenu = TimeController.Paused;
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Settings window: could not pause: " + e.Message); }
            var es = EventSystem.current;
            if (es != null && es.enabled) { es.enabled = false; _uiOff = es; }
            HitCamMod.Log.Msg($"Settings window open ({_entries.Count} settings)");
        }

        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _capturing = null;
            if (_dirty) Save();
            try { if (PausedByMenu && TimeController.Instance != null && TimeController.Paused) TimeController.Unpause(); } catch { }
            PausedByMenu = false;
            if (_uiOff != null) _uiOff.enabled = true;
            _uiOff = null;
            var pi = PlayerInput.Instance;
            if (pi != null) pi.BlockKeyboardInput = _blockKb;
            Cursor.visible = _cursorVisible;
            Cursor.lockState = _cursorLock;
        }

        static void Save()
        {
            try { MelonPreferences.Save(); _dirty = false; Say("Saved to UserData/MelonPreferences.cfg"); }
            catch (Exception e) { Say("Save failed: " + e.Message); }
        }

        static void Say(string m) { _status = m; _statusAt = Time.unscaledTime; }

        /// Keys are parsed once at start in several places: re-read them all after a key setting changes.
        static void RefreshKeys()
        {
            foreach (var e in _entries)
            {
                if (!(e is MelonPreferences_Entry<string> se) || !e.Identifier.EndsWith("Key")) continue;
                KeyCode k;
                try { k = (KeyCode)Enum.Parse(typeof(KeyCode), se.Value, true); } catch { continue; }
                SetKeyField(typeof(Settings), e.Identifier + "Code", k);
                if (e.Identifier == "AarKey") SetKeyField(Type.GetType("GHPCHitCam.AfterAction"), "KeyCode", k);
                if (e.Identifier == "ArmorSimKey") SetKeyField(Type.GetType("GHPCHitCam.ArmorSim"), "_key", k);
            }
        }

        static void SetKeyField(Type t, string name, KeyCode k)
        {
            var f = t?.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(KeyCode)) f.SetValue(null, k);
        }

        // ------------------------------------------------------------------ drawing

        static GUIStyle _bg, _head, _label, _value, _tip, _gear, _section, _sub, _presetOn, _presetOff;
        static readonly GUIStyle[] _cost = new GUIStyle[4];
        static Texture2D _dark;

        static void Styles()
        {
            if (_bg != null) return;
            _dark = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _dark.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.08f, 0.93f));
            _dark.Apply();
            _bg = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14, 14, 10, 10) };
            _bg.normal.background = _dark;
            _head = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = false };
            _head.normal.textColor = new Color(1f, 0.82f, 0.35f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false };
            _label.normal.textColor = new Color(0.92f, 0.94f, 0.97f);
            _value = new GUIStyle(_label) { alignment = TextAnchor.MiddleRight };
            _value.normal.textColor = new Color(1f, 0.9f, 0.6f);
            _tip = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            _tip.normal.textColor = new Color(0.7f, 0.75f, 0.8f);
            _gear = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(4, 4, 4, 4) };
            _section = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            _section.normal.textColor = new Color(0.55f, 0.8f, 1f);
            _sub = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.LowerLeft };
            _sub.normal.textColor = new Color(0.75f, 0.8f, 0.85f);
            _presetOff = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold };
            _presetOn = new GUIStyle(_presetOff);
            _presetOn.normal.textColor = _presetOn.hover.textColor = new Color(1f, 0.82f, 0.35f);
            _presetOn.normal.background = _presetOn.onNormal.background;
            Color[] cc = { Color.white, new Color(0.95f, 0.85f, 0.45f), new Color(1f, 0.62f, 0.3f), new Color(1f, 0.42f, 0.38f) };
            for (int i = 1; i < 4; i++)
            {
                _cost[i] = new GUIStyle(_tip) { fontSize = 12 };
                _cost[i].normal.textColor = cc[i];
            }
        }

        public static void Draw()
        {
            try
            {
                Styles();
                float s = Mathf.Max(Screen.height / 1080f, 0.75f);
                var old = GUI.matrix;
                GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
                float W = Screen.width / s, H = Screen.height / s;
                try
                {
                    if (!IsOpen) { Gear(W, H, s); return; }
                    Window(W, H);
                }
                finally { GUI.matrix = old; }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Settings window draw: " + e.Message); }
        }

        /// A gear button above the minimap corner, only while the mouse cursor is free (map, pause, menus).
        static void Gear(float W, float H, float s)
        {
            if (!Cursor.visible || PlayerInput.Instance == null || PlayerInput.Instance.CurrentPlayerUnit == null) return;
            float size = 34f;
            float mm = Settings.ArcadeHud.Value && Settings.ArcadeMinimap.Value ? Mathf.Clamp(Settings.MinimapSize.Value, 0.1f, 0.6f) * Screen.height / s + 18f : 0f;
            var r = new Rect(W - size - 14f, H - size - 14f - mm, size, size);
            HudGfx.Init();
            if (GUI.Button(r, new GUIContent(HudGfx.GearTex, "Combat Overhaul settings (" + Settings.ConfigKey.Value + ")"), _gear)) Open();
        }

        static void Window(float W, float H)
        {
            float w = Mathf.Min(940f, W - 40f), h = Mathf.Min(1000f, H - 60f);
            var area = new Rect(24f, 30f, w, h);
            GUI.Box(area, GUIContent.none, _bg);
            GUILayout.BeginArea(new Rect(area.x + 14f, area.y + 10f, area.width - 28f, area.height - 20f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Combat Overhaul settings", _head);
            GUILayout.Space(14);
            GUILayout.Label("preset: " + Presets.Current(), _sub, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Save", GUILayout.Width(80), GUILayout.Height(26))) Save();
            if (GUILayout.Button("Close  (" + Settings.ConfigKey.Value + ")", GUILayout.Width(130), GUILayout.Height(26))) { GUILayout.EndHorizontal(); GUILayout.EndArea(); Close(); return; }
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            int nt = GUILayout.Toolbar(_tab, Tabs, GUILayout.Height(28));
            if (nt != _tab) { _tab = nt; _scroll = Vector2.zero; }
            GUILayout.Space(6);
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_tab == 0) QuickSetup();
            else
            {
                int shown = 0;
                foreach (var (tab, title, rows) in _sections)
                {
                    if (tab != _tab) continue;
                    Section(title);
                    foreach (var e in rows) Row(e);
                    shown++;
                }
                if (shown == 0) GUILayout.Label("Nothing here.", _tip);
            }
            GUILayout.EndScrollView();
            GUILayout.Space(4);
            string tip = _capturing != null ? "Press the new key for " + Pretty(_capturing.Identifier) + "  (Escape = cancel)"
                : Time.unscaledTime - _statusAt < 4f ? _status : "Changes apply at once and are saved when the window closes. Settings with an FPS cost are the ones the presets set.";
            GUILayout.Label(tip, _tip, GUILayout.Height(36));
            GUILayout.EndArea();
        }

        static void Section(string title)
        {
            GUILayout.Space(10);
            GUILayout.Label(title, _section);
            GUILayout.Space(2);
        }

        /// First tab: the presets, the main switches, the hit cam window, the keys.
        static void QuickSetup()
        {
            Section("Quality preset");
            string cur = Presets.Current();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Presets.Names.Length; i++)
            {
                bool on = cur == Presets.Names[i];
                if (GUILayout.Button((on ? "> " : "") + Presets.Names[i] + (i == Presets.Default ? "  (default)" : "") + (on ? " <" : ""), on ? _presetOn : _presetOff, GUILayout.Height(40)))
                {
                    Presets.Apply(i);
                    _dirty = true;
                    Say("Preset " + Presets.Names[i] + " applied");
                }
            }
            GUILayout.EndHorizontal();
            if (cur == "Custom") GUILayout.Label("Custom: your own mix (you changed a setting a preset sets).", _tip);
            for (int i = 0; i < Presets.Names.Length; i++)
                GUILayout.Label("<b>" + Presets.Names[i] + "</b>: " + Presets.Blurbs[i], _richTip);
            GUILayout.Label("A preset sets only the settings marked with an FPS cost (in the other tabs). Sizes, keys, the HUD and timings stay as they are.", _tip);

            Section("Main switches");
            foreach (var id in new[] { "Enabled", "ArcadeHud", "WorldDamage", "ExplosionSound", "AarEnabled", "AmmoExplosionKillsCrew" })
                if (_byId.TryGetValue(id, out var e)) Row(e);

            Section("Hit cam window");
            foreach (var id in new[] { "PipSize", "PipCorner" })
                if (_byId.TryGetValue(id, out var e)) Row(e);

            Section("Keys");
            GUILayout.Label($"{Settings.ConfigKey.Value} this window   |   {Settings.ToggleKey.Value} hit cam on/off   |   {Settings.ReplayKey.Value} replay the last hit   |   "
                + $"{KeyOf("AarKey")} After Action Report   |   {KeyOf("ArmorSimKey")} Armor Sim   |   {KeyOf("ProvingGroundKey")} proving ground tools   |   {KeyOf("ArcadeKey")} arcade HUD on/off   (change them in Keys)", _tip);
        }

        static string KeyOf(string id) => _byId.TryGetValue(id, out var e) ? e.GetValueAsString() : "?";

        static GUIStyle _rich;
        static GUIStyle _richTip => _rich ??= new GUIStyle(_tip) { richText = true };

        static void Row(MelonPreferences_Entry e)
        {
            var t = e.GetReflectedType();
            object v = e.BoxedValue;
            GUILayout.BeginHorizontal(GUILayout.Height(28));
            GUILayout.Label(Pretty(e.Identifier), _label, GUILayout.Width(250));
            object nv = v;
            if (t == typeof(bool))
            {
                bool b = (bool)v;
                bool nb = GUILayout.Toggle(b, b ? " on" : " off", GUILayout.Width(90));
                if (nb != b) nv = nb;
                GUILayout.FlexibleSpace();
            }
            else if (t == typeof(float) || t == typeof(int))
            {
                float f = t == typeof(float) ? (float)v : (int)v;
                Vector2 range = Ranges.TryGetValue(e.Identifier, out var rr) ? rr
                    : e.Identifier.StartsWith("HudScale") ? new Vector2(0.4f, 3f) : new Vector2(0f, Mathf.Max(t == typeof(int) ? 10f : 1f, Mathf.Abs(f) * 4f));
                GUILayout.BeginVertical();
                GUILayout.Space(9);
                float nf = GUILayout.HorizontalSlider(f, range.x, range.y, GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                if (t == typeof(int)) nf = Mathf.Round(nf);
                else nf = Mathf.Round(nf * 100f) / 100f;
                GUILayout.Label(t == typeof(int) ? ((int)nf).ToString() : nf.ToString("0.00"), _value, GUILayout.Width(70));
                if (Mathf.Abs(nf - f) > 1e-5f) nv = t == typeof(int) ? (object)(int)nf : nf;
            }
            else if (t == typeof(string))
            {
                string str = (string)v ?? "";
                if (Options.TryGetValue(e.Identifier, out var opts))
                {
                    int i = Array.IndexOf(opts, str);
                    int ni = GUILayout.Toolbar(i, opts, GUILayout.ExpandWidth(true));
                    if (ni != i && ni >= 0) nv = opts[ni];
                }
                else if (e.Identifier.EndsWith("Key"))
                {
                    bool cap = _capturing == e;
                    if (GUILayout.Button(cap ? "press a key…" : str, GUILayout.Width(180))) _capturing = cap ? null : e;
                    GUILayout.FlexibleSpace();
                    if (cap)
                    {
                        var ev = Event.current;
                        if (ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None)
                        {
                            if (ev.keyCode != KeyCode.Escape) nv = ev.keyCode.ToString();
                            _capturing = null;
                            ev.Use();
                        }
                    }
                }
                else
                {
                    string ns = GUILayout.TextField(str, GUILayout.ExpandWidth(true));
                    if (ns != str) nv = ns;
                }
            }
            else
            {
                GUILayout.Label(e.GetValueAsString(), _value);
            }
            bool changed = e.GetValueAsString() != e.GetDefaultValueAsString();
            GUI.enabled = changed;
            if (GUILayout.Button(new GUIContent("default", "Back to the default (" + e.GetDefaultValueAsString() + ")"), GUILayout.Width(64)))
            {
                e.ResetToDefault();
                After(e);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            // under it: what it does, and what it costs
            string help = e.Description;
            if (!string.IsNullOrEmpty(help))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(14);
                GUILayout.Label(help, _tip);
                GUILayout.EndHorizontal();
            }
            if (Cost.TryGetValue(e.Identifier, out var cost))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(14);
                GUILayout.Label("FPS cost: " + (cost.lvl >= 3 ? "high" : cost.lvl == 2 ? "medium" : "low") + " - " + cost.why, _cost[Mathf.Clamp(cost.lvl, 1, 3)]);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(5);
            if (!Equals(nv, v))
            {
                try { e.BoxedValue = nv; After(e); }
                catch (Exception ex) { Say("Could not set " + e.Identifier + ": " + ex.Message); }
            }
        }

        static void After(MelonPreferences_Entry e)
        {
            _dirty = true;
            if (e.Identifier.EndsWith("Key")) RefreshKeys();
        }

        /// Names that read better written out than split from the identifier.
        static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { "Enabled", "Hit cam" }, { "CinematicXray", "Cinematic x-ray camera" }, { "PipSize", "Hit cam size" }, { "PipCorner", "Hit cam corner" },
            { "HitCamAntiAliasing", "Anti-aliasing" }, { "XrayPhysics", "Ragdolls and physics" }, { "XrayPhysicsSpeed", "Physics speed" },
            { "DetailedCrew", "Real crew models" }, { "RodSoftBody", "Deforming APFSDS rod" }, { "ShowAllSpall", "Show all spall" },
            { "CutModels", "Cut holes into models" }, { "MaxWorldPlates", "Max 3D holes" }, { "WorldVentJets", "Flames out of holes" },
            { "MaxDebris", "Max loose pieces" }, { "WorldShrapnel", "Shrapnel pieces" }, { "DebrisSeconds", "Shrapnel stays (s)" }, { "ExplosionSound", "Realistic explosion sound" },
            { "AmmoExplosionKillsCrew", "Ammo explosion kills crew" }, { "CookOffReplay", "Cook-off replay" }, { "CookOffWait", "Wait for cook-off (s)" },
            { "RodCam", "Rod cam" }, { "AarEnabled", "After Action Report" }, { "AarFullModels", "Full models kept" }, { "SaveRecords", "Save hits to disk" }, { "PastMissionsKept", "Past missions kept" },
            { "MinCaliberMm", "Min caliber (mm)" }, { "BurstCaliberMm", "Short replay below (mm)" }, { "ApproachSeconds", "Approach (s)" },
            { "InteriorSeconds", "Penetration (s)" }, { "AftermathSeconds", "Aftermath (s)" }, { "ImpactSlowMo", "Impact slow motion (s)" }, { "HitCamPace", "Hit cam pace" }, { "ConstantSlowMo", "Constant slow motion (x)" },
            { "DebugLog", "Debug log" }, { "ArmorHoles", "Armour holes" }, { "PlayerShotsOnly", "Only my shots" },
        };

        /// "ArcadeSfxVolume" -> "Arcade sfx volume".
        static readonly Dictionary<string, string> _pretty = new Dictionary<string, string>();
        static string Pretty(string id)
        {
            if (Labels.TryGetValue(id, out var lb)) return lb;
            if (_pretty.TryGetValue(id, out var p)) return p;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool split = i > 0 && char.IsUpper(c) && (char.IsLower(id[i - 1]) || (i + 1 < id.Length && char.IsLower(id[i + 1])));
                if (split) sb.Append(' ');
                sb.Append(split ? char.ToLowerInvariant(c) : c);
            }
            p = sb.ToString().Replace("mm", "mm").Replace("pip", "PiP").Replace("Aar", "AAR").Replace("sfx", "SFX").Replace("Sfx", "SFX").Replace("hud", "HUD").Replace("Hud", "HUD");
            _pretty[id] = p;
            return p;
        }
    }
}
