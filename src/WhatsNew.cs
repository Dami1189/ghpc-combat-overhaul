using System;
using System.Collections.Generic;
using System.Linq;
using GHPC.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GHPCHitCam
{
    /// The mod's own corner of the main menu, built from the game's UI pieces so it looks like the rest of the menu (its
    /// black panels, its font, outlined buttons, the badge's red): a box in the top right corner (version, What's new,
    /// a new version), a big note when one is out (and the question to restart once it's installed), and the What's new
    /// page with every version's notes. What's new opens by itself once after an update; a first install gets the
    /// settings window's Quick setup instead (ConfigMenu.Welcome).
    internal static class WhatsNew
    {
        class Notes
        {
            public string Version, Date;
            public string[] New = Array.Empty<string>(), Keys = Array.Empty<string>(), Fixes = Array.Empty<string>(), Coming = Array.Empty<string>(), Known = Array.Empty<string>();
        }

        /// Every version's notes, newest first (the same as on the release page).
        static readonly Notes[] History =
        {
            new Notes
            {
                Version = "1.1.1", Date = "October 2026",
                New = new[]
                {
                    "What's new page in the main menu, and a short welcome in the settings on the first start.",
                    "Updates from the main menu. The menu panel now tells you when a new version is out, one click installs it and the game offers to restart. Settings > Tools > Updates: Ask, Auto or Off.",
                    "Full vehicle mod compatibility. It should work with every vehicle mod now (tested with Fulda Expansion Pack and Pact Increased Lethality). The proving ground and the Armor Sim spawn the mod's versions (Leopard 2K variants, T-80U/BV, T-72B, BMP-2D...), with their ERA and armour, the hit cam sees all of it, and modded vehicles are tagged in the lists, like [PIL > T-80U]. If a mod you use doesn't work with it, let me know.",
                    "Proving ground reworked. It loads on the game's M1 Abrams training range and draws only the deck and the vehicles, no map under it (looks like a VR range, and about twice the frame rate). The vehicle list opens when it's ready, a short briefing on the keys, and knocked-out targets come back by themselves after 20 s (all of it in the settings if you want to change it).",
                    "Armor Sim loads on the same quiet range and starts on the M1IP, which everyone has. Right mouse turns around the vehicle, middle mouse moves the view (double click to centre).",
                    "Light hit cam for autocannons. A 20-30 mm burst plays a different way: one x-ray for the whole burst, the camera holds still and every round arrives into it, with simple holes - much more fps.",
                    "Calmer defaults. A steadier camera, one constant slow motion for x-ray (WT style), the crew stay in their seats. If your settings were untouched they switch over by themselves; the Cinematic preset brings the ragdolls and the rest of the effects back.",
                    "New keys that don't clash with the game. The old ones sat on the game's own keys (waypoints, screenshot, weapon switch, cruise speed, the game's AAR). The new ones are in the list below: none of them are used by the game, and the ones that change the mission you're in need Alt so you won't hit them by accident. They do nothing while the game's own AAR is open. Rebind anything in F12 > Keys (Alt / Ctrl / Shift work too); it tells you if the game already uses a key. If you never changed your keys, they move by themselves. Keys can also be unbound if needed.",
                    "\"Later ammo blasts\" setting: how loud a vehicle's remaining ammo is after the first big explosion. (Realistically it would still be loud but it can get pretty intense with all the detonations so I offer the option to lessen the effect)",
                },
                Keys = new[]
                {
                    "F12 settings, F9 hit cam on / off, F10 replay your last hit (as before)",
                    "F2 After Action Report (was F11)",
                    "H arcade HUD on / off (was F6)",
                    "Insert Armor Sim (was F5)",
                    "Home proving ground tools (was F4)",
                    "Alt+Home build a proving ground in the mission you're in (was Ctrl+F4)",
                    "Alt+K test range, spawns targets around you (was F7)",
                    "U Arsenal (was F8), then 9 / 0 to pick a round and J to fire it at once (was [ ] and B)",
                    "Num * / Num / minimap zoom in / out (was Num + / Num -)",
                },
                Fixes = new[]
                {
                    "Closing the settings window with ESC also opened the game's own menu, and until that was closed again Q (restock, platoon orders) and Tab (switching vehicles) did nothing. ESC now closes only the window.",
                    "The settings window, the Armor Sim and the proving ground tools could leave the game's input held after they closed (the gun or the camera not answering, Q and Tab dead), when the game's Q menu or a mission's planning map was up around them.",
                    "With another mod's hook on the game's spawn failing (seen in a mission with several mods), the test range lost targets that then stood there untracked and fighting. They are taken as spawned now, and the log says which code failed.",
                    "Low frame rate in the main menu (56 fps instead of 144). Should now be smooth sailing.",
                    "The main menu lost ARMOR SIM, PROVING GROUND and the update box after leaving a mission, broke while one of the game's pages was open, and sometimes had no mouse cursor after the proving ground. If the mod menu still disappears after exiting a mission, the logs should contain the reason - please let me know then!",
                    "Couldn't switch vehicles in the next mission after a proving ground.",
                    "With Fulda / PIL installed, half the proving ground targets didn't spawn, and some conversions failed (\"Marder donor was not loaded\"). VanillaAssetUtil frees the stock vehicles it lent to those mods once the mission starts; the mod keeps them now.",
                    "Autocannon bursts killed the frame rate: a 20-round 25 mm burst took the game from ~75 to ~18 fps. Now a burst costs about a quarter of the frame rate (around 120 to 90 fps on the proving ground), with fewer micro-stutters. It is still not perfect, but I'm not done with it.",
                    "No engine sounds on vehicles the mod spawns, your own one on the proving ground included.",
                    "The settings window covered the other windows (Armor Sim, proving ground tools), and in the Armor Sim a click on it could fire a round. Now the rest steps aside while it's open.",
                    "Settings saved by GHPC Mod Manager on Windows with a decimal comma (German...) were reset to their defaults.",
                    "X-ray plates stretched to the far wall of hollow parts (fender tanks, boxes), and a ricochet groove on cast turrets was drawn far too big.",
                    "Several hits on a gun barrel looked like a chain of rings; overlapping holes merge now. (Dynamic damage decals will still be upgraded as time goes on)",
                    "The game's \"Thank you for supporting the GHPC project!\" line in the main menu covered the bottom of the settings window. It steps aside while the window is open.",
                    "Later ammo blasts were too loud and shook the camera too much.",
                    "\"Fire: In Manual, this is labeled as oil tank...\" in the after-hit HUD: a BMP-2 part whose name in the game is a note developers left. It shows correctly as \"Oil tank\" now.",
                    "The guard against broken units (rounds dying at the muzzle) was attached to the wrong game method.",
                },
                Coming = new[]
                {
                    "Gun barrel damage physics: a barrel barely holding on sags, swings and can tear off - possibly with real colliders for house/trees collisions.",
                    "Onboarding: on the first start you build your own preset by comparing the options side by side.",
                    "ERA physics.",
                    "Further optimizations.",
                    "More dynamic damage: wheels coming off, flat tyres...",
                    "Many more... what's that buzzing sound?",
                },
                Known = new[]
                {
                    "ERA is the game's own for now: its armour counts, but the x-ray doesn't show the bricks going off yet.",
                    "On the empty proving ground one far hill can still show on the horizon.",
                    "Autocannon bursts can still stutter a little.",
                    "Helicopters in the x-ray hit cam don't show their whole model correctly yet.",
                    "A proving ground built inside a mission (Alt+Home): vehicle mods that get their parts ready only for the vehicles a mission starts with (PIL) can't convert the others, so those come as the game's own.",
                },
            },
            new Notes
            {
                Version = "1.0.0", Date = "October 2026",
                New = new[]
                {
                    "X-ray kill cam: every hit of yours replayed in slow motion on a see-through copy of the target, from the game's own simulation of it.",
                    "APFSDS rods that erode, bend and break; damage that stays on the vehicles; explosions as loud as their size.",
                    "Optional War Thunder style HUD, the proving ground, the Armor Sim and the After Action Report.",
                },
            },
        };

        public static bool Open { get; set; }
        internal static bool Built => _root != null && _root.activeInHierarchy;
        static bool _checkedFirstRun, _bannerOff, _restartOff;

        /// The main menu is up: the update check starts, and after an update What's new opens once (a first install gets
        /// the Quick setup instead).
        public static void OnMenu()
        {
            Updater.OnMenu();
            if (_checkedFirstRun) return;
            _checkedFirstRun = true;
            string seen = Settings.LastSeenVersion.Value ?? "";
            string now = Updater.CurrentText;
            if (seen == now || (AutoTest.Active && !AutoTest.Opt("firstrun", false))) return;
            if (Settings.FreshInstall) ConfigMenu.OpenWelcome();
            else Open = true;
            Settings.LastSeenVersion.Value = now;
            try { Settings.Cat.SaveToFile(false); } catch { }
        }

        public static void Draw() { }

        // ------------------------------------------------------------------ the menu's UI

        static readonly Color Red = new Color(0.86f, 0.22f, 0.16f, 1f);
        static readonly Color Panel = new Color(0f, 0f, 0f, 0.84f);
        static readonly Color Dim = new Color(0.72f, 0.74f, 0.77f, 1f);

        static GameObject _root, _corner, _prompt, _page;
        static TextMeshProUGUI _cornerTitle, _cornerLine, _promptTitle, _promptBody, _pageText;
        static GameObject _btnUpdateCorner, _btnPromptUpdate, _btnPromptNew, _btnPromptLater, _btnRestart, _btnRestartLater;
        static TMP_FontAsset _font;
        static Material _fontMat;
        static string _shown = "";
        static float _menuSince = -1f;
        static BuildMenuController _bmc;

        public static void Update()
        {
            try
            {
                bool menu = ArmorSimMenu.InMenu && ArmorSimMenu.MenuLoaded();
                if (!menu)
                {
                    // out of the main menu (a mission, its loading screen): none of it may be left on screen
                    _menuSince = -1f;
                    if (_root != null) { Object.Destroy(_root); _root = null; }
                    return;
                }
                if (_menuSince < 0f) _menuSince = Time.realtimeSinceStartup;
                if (_root == null)
                {
                    // once the title menu is there (our rows in it give the font), or after a few seconds anyway
                    if (ArmorSimMenu.Button == null && Time.realtimeSinceStartup - _menuSince < 6f) return;
                    Build();
                    if (_root == null) return;
                }
                if (_bmc == null) _bmc = Object.FindObjectOfType<BuildMenuController>();
                Notice();
                bool page = _bmc != null && (_bmc.IsFullMenuActive || _bmc.IsSubMenuActive || _bmc.IsModalMenuActive);
                bool show = !ConfigMenu.IsOpen && !page;
                if (_root.activeSelf != show) _root.SetActive(show);
                if (!show) return;
                Refresh();
            }
            catch (Exception e) { HitCamMod.Log.Warning("Main menu box: " + e.Message); }
        }

        static GameObject _notice;
        static bool _noticeHidden;
        internal static bool NoticeShown => _notice != null && _notice.activeInHierarchy;

        /// The game's own line along the bottom of the title menu ("Thank you for supporting the GHPC project! ...") sat
        /// over the lower part of the settings window: it steps aside while the window is open, and comes back after (if
        /// the game hasn't hidden it meanwhile itself).
        static void Notice()
        {
            if (_bmc == null) return;
            if (_notice == null)
            {
                var t = HarmonyLib.AccessTools.Field(typeof(BuildMenuController), "_disclaimer")?.GetValue(_bmc) as TMP_Text;
                if (t == null || t.transform.parent == null) return;
                _notice = t.transform.parent.gameObject;
                _noticeHidden = false;
            }
            if (ConfigMenu.IsOpen) { if (_notice.activeSelf) { _notice.SetActive(false); _noticeHidden = true; } }
            else if (_noticeHidden) { _noticeHidden = false; _notice.SetActive(true); }
        }

        /// The texts and which parts show, when something changed.
        static void Refresh()
        {
            var st = Updater.Now;
            string key = $"{st}|{Open}|{_bannerOff}|{_restartOff}|{Updater.Latest}|{Updater.Error}";
            if (key == _shown) return;
            _shown = key;
            bool available = st == Updater.State.Available;
            _cornerTitle.text = $"<color=#{ColorUtility.ToHtmlStringRGB(Red)}>COMBAT OVERHAUL</color>  {Updater.CurrentText}";
            _cornerLine.text = available ? $"Version {Updater.Latest} is out"
                             : st == Updater.State.Downloading ? $"Downloading {Updater.Latest}..."
                             : st == Updater.State.Installed ? $"{Updater.Latest} installed: restart the game to use it"
                             : st == Updater.State.Failed ? "Update: " + Updater.Error
                             : st == Updater.State.Checking ? "Looking for updates..."
                             : st == Updater.State.UpToDate ? "Up to date"
                             : st == Updater.State.Skipped ? $"{Updater.Latest} is out (skipped)"
                             : st == Updater.State.Off ? "Update check off" : "";
            _btnUpdateCorner.SetActive(available || st == Updater.State.Skipped);
            _btnUpdateCorner.GetComponentInChildren<TextMeshProUGUI>().text = $"UPDATE TO {Updater.Latest}";

            bool restart = st == Updater.State.Installed && !_restartOff;
            if (restart && Open) Open = false;   // the question comes first
            bool banner = !restart && available && !_bannerOff && !Open;
            _prompt.SetActive(restart || banner);
            _btnPromptUpdate.SetActive(banner);
            _btnPromptNew.SetActive(banner);
            _btnPromptLater.SetActive(banner);
            _btnRestart.SetActive(restart);
            _btnRestartLater.SetActive(restart);
            if (restart)
            {
                _promptTitle.text = $"COMBAT OVERHAUL {Updater.Latest} IS INSTALLED";
                _promptBody.text = "Restart the game now to use it? Your settings stay as they are.";
            }
            else if (banner)
            {
                _promptTitle.text = $"COMBAT OVERHAUL {Updater.Latest} IS OUT";
                _promptBody.text = $"You have {Updater.CurrentText}.\n" + Rich(Short(Updater.Notes, 8));
            }

            _page.SetActive(Open);
            if (Open) _pageText.text = PageText();
        }

        static string PageText()
        {
            var sb = new System.Text.StringBuilder();
            string red = ColorUtility.ToHtmlStringRGB(Red), dim = ColorUtility.ToHtmlStringRGB(Dim);
            var st = Updater.Now;
            if ((st == Updater.State.Available || st == Updater.State.Skipped || st == Updater.State.Downloading || st == Updater.State.Installed) && Updater.Notes.Length > 0)
            {
                sb.Append($"<size=30><color=#{red}>{Updater.Latest}</color></size>  <color=#{dim}>new, {(st == Updater.State.Installed ? "installed: restart the game" : "not installed yet")}</color>\n\n");
                sb.Append(Rich(Updater.Notes)).Append("\n\n");
            }
            foreach (var n in History)
            {
                sb.Append($"<size=30><color=#{red}>{n.Version}</color></size>  <color=#{dim}>{n.Date}{(n.Version == Updater.CurrentText ? ",  this version" : "")}</color>\n");
                Section(sb, "NEW", n.New);
                Section(sb, "NEW KEYS", n.Keys);
                Section(sb, "BUGFIXES", n.Fixes);
                Section(sb, "COMING UP", n.Coming);
                Section(sb, "KNOWN ISSUES", n.Known);
                sb.Append("\n");
            }
            sb.Append($"<size=15><color=#{dim}>All releases: {Updater.Page}\nThe mod's settings: {Hotkeys.Label(Settings.ConfigKey)} (updates: Tools > Updates)</color></size>");
            return sb.ToString();
        }

        static void Section(System.Text.StringBuilder sb, string title, string[] lines)
        {
            if (lines == null || lines.Length == 0) return;
            sb.Append($"\n<b>{title}</b>\n");
            foreach (var l in lines) sb.Append("<indent=1.2em>•</indent><indent=2.4em>").Append(l).Append("</indent>\n");
        }

        /// A release's notes from GitHub (its "## New / ## Bugfixes / ## Coming up" headings, "- " lines), for the page.
        static string Rich(string notes)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var raw in notes.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("•")) sb.Append("<indent=1.2em>•</indent><indent=2.4em>").Append(l.Substring(1).Trim()).Append("</indent>\n");
                else sb.Append("\n<b>").Append(l.TrimEnd(':').ToUpperInvariant()).Append("</b>\n");
            }
            return sb.ToString().Trim();
        }

        static string Short(string notes, int lines)
        {
            var l = notes.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            string s = string.Join("\n", l.Take(lines));
            return l.Count > lines ? s + "\n..." : s;
        }

        // ------------------------------------------------------------------ building it

        static void Build()
        {
            var label = ArmorSimMenu.Button != null ? ArmorSimMenu.Button.GetComponentInChildren<TMP_Text>(true) : null;
            if (label == null) label = Object.FindObjectsOfType<TMP_Text>().FirstOrDefault(t => t != null && t.isActiveAndEnabled && (t.text ?? "").Trim().Length > 2);
            _font = label != null ? label.font : TMP_Settings.defaultFontAsset;
            _fontMat = label != null ? label.fontSharedMaterial : null;
            if (_font == null) return;

            _root = new GameObject("Combat Overhaul menu");
            // it belongs to the menu's own scene and goes with it (made in the game's always-loaded loader scene it stayed
            // up on loading screens and in missions)
            var menuScene = ArmorSimMenu.Button != null ? ArmorSimMenu.Button.scene : default;
            if (!menuScene.IsValid())
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (sc.name.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0) { menuScene = sc; break; }
                }
            if (!menuScene.IsValid() || !menuScene.isLoaded) { Object.Destroy(_root); _root = null; return; }
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_root, menuScene);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;   // over the game's own menu canvases (its notice at the bottom)
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            _root.AddComponent<GraphicRaycaster>();

            // the corner box
            _corner = Box(_root.transform, "corner", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -28f), 400f, 16f);
            _cornerTitle = Text(_corner.transform, "", 21f, Color.white, FontStyles.Bold, 4f);
            _cornerLine = Text(_corner.transform, "", 16f, Dim);
            var row = Row(_corner.transform);
            Btn(row, "WHAT'S NEW", 150f, false, () => { Open = !Open; _shown = ""; });
            _btnUpdateCorner = Btn(row, "UPDATE", 190f, true, () => { Updater.StartInstall(); _shown = ""; });

            // the note: a new version, or restart now
            _prompt = Box(_root.transform, "prompt", new Vector2(0.62f, 0.76f), new Vector2(0.5f, 1f), Vector2.zero, 720f, 24f, accent: true);
            _promptTitle = Text(_prompt.transform, "", 32f, Red, FontStyles.Bold, 3f);
            _promptBody = Text(_prompt.transform, "", 19f, Color.white);
            var prow = Row(_prompt.transform);
            _btnPromptUpdate = Btn(prow, "UPDATE NOW", 230f, true, () => { Updater.StartInstall(); _shown = ""; });
            _btnPromptNew = Btn(prow, "WHAT'S NEW", 190f, false, () => { Open = true; _shown = ""; });
            _btnPromptLater = Btn(prow, "LATER", 130f, false, () => { _bannerOff = true; _shown = ""; });
            _btnRestart = Btn(prow, "RESTART NOW", 230f, true, () => Updater.Restart());
            _btnRestartLater = Btn(prow, "LATER", 130f, false, () => { _restartOff = true; _shown = ""; });

            // the What's new page: right of the game's menu, under the corner box
            _page = new GameObject("whats new", typeof(RectTransform));
            _page.transform.SetParent(_root.transform, false);
            var prt = (RectTransform)_page.transform;
            prt.anchorMin = new Vector2(0.27f, 0.2f);   // above the game's notice at the bottom (it's drawn over everything)
            prt.anchorMax = new Vector2(0.92f, 1f);
            prt.offsetMin = new Vector2(40f, 0f);
            prt.offsetMax = new Vector2(-28f, -190f);
            _page.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.94f);
            var bar = new GameObject("accent", typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(_page.transform, false);
            var brt = (RectTransform)bar.transform;
            brt.anchorMin = new Vector2(0f, 1f); brt.anchorMax = new Vector2(1f, 1f); brt.pivot = new Vector2(0.5f, 1f);
            brt.sizeDelta = new Vector2(0f, 4f);
            bar.GetComponent<Image>().color = Red;
            var head = new GameObject("head", typeof(RectTransform));
            head.transform.SetParent(_page.transform, false);
            var hrt = (RectTransform)head.transform;
            hrt.anchorMin = new Vector2(0f, 1f); hrt.anchorMax = new Vector2(1f, 1f); hrt.pivot = new Vector2(0.5f, 1f);
            hrt.offsetMin = new Vector2(28f, -66f); hrt.offsetMax = new Vector2(-24f, -14f);
            var title = Text(head.transform, "WHAT'S NEW IN COMBAT OVERHAUL", 26f, Color.white, FontStyles.Bold, 4f);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = new Vector2(-150f, 0f);
            title.alignment = TextAlignmentOptions.MidlineLeft;
            var close = Btn(head.transform, "CLOSE", 130f, false, () => { Open = false; _shown = ""; });
            var crt = (RectTransform)close.transform;
            crt.anchorMin = new Vector2(1f, 0.5f); crt.anchorMax = new Vector2(1f, 0.5f); crt.pivot = new Vector2(1f, 0.5f);
            crt.anchoredPosition = Vector2.zero;
            _pageText = Scroll(_page.transform, new Vector2(28f, 24f), new Vector2(-24f, -80f));
            _page.SetActive(false);
            _prompt.SetActive(false);
            _shown = "";
            HitCamMod.Log.Msg("Main menu: Combat Overhaul box built (font " + _font.name + ")");
        }

        /// A black panel that grows with what's in it (top to bottom), at an anchor.
        static GameObject Box(Transform parent, string name, Vector2 anchor, Vector2 pivot, Vector2 pos, float width, float pad, bool accent = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 100f);
            go.GetComponent<Image>().color = Panel;
            var v = go.GetComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset((int)pad, (int)pad, (int)(pad * (accent ? 1.3f : 0.8f)), (int)pad);
            v.spacing = 8f;
            v.childControlWidth = true; v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            var f = go.GetComponent<ContentSizeFitter>();
            f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            if (accent)
            {
                // the badge's red along the top, outside the layout
                var bar = new GameObject("accent", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                bar.transform.SetParent(go.transform, false);
                bar.GetComponent<LayoutElement>().ignoreLayout = true;
                var brt = (RectTransform)bar.transform;
                brt.anchorMin = new Vector2(0f, 1f); brt.anchorMax = new Vector2(1f, 1f); brt.pivot = new Vector2(0.5f, 1f);
                brt.sizeDelta = new Vector2(0f, 4f);
                bar.GetComponent<Image>().color = Red;
            }
            return go;
        }

        static TextMeshProUGUI Text(Transform parent, string text, float size, Color color, FontStyles style = FontStyles.Normal, float spacing = 0f)
        {
            var go = new GameObject("text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = _font;
            if (_fontMat != null) t.fontSharedMaterial = _fontMat;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.characterSpacing = spacing;
            t.enableWordWrapping = true;
            t.richText = true;
            t.raycastTarget = false;
            return t;
        }

        static Transform Row(Transform parent)
        {
            var go = new GameObject("buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 12f;
            h.childControlWidth = false; h.childControlHeight = false;
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            h.childAlignment = TextAnchor.MiddleLeft;
            go.GetComponent<LayoutElement>().preferredHeight = 46f;
            return go.transform;
        }

        /// The game's outlined button (CUSTOMIZE / START in Instant Action): a thin white frame, the label in capitals,
        /// lighter under the mouse. The one to press is framed in the badge's red.
        static GameObject Btn(Transform parent, string label, float width, bool hot, Action click)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(width, 42f);
            var bg = go.GetComponent<Image>();
            bg.color = Color.white;
            var b = go.GetComponent<Button>();
            b.targetGraphic = bg;
            var cb = b.colors;
            cb.normalColor = new Color(0f, 0f, 0f, 0.35f);
            cb.highlightedColor = new Color(1f, 1f, 1f, 0.14f);
            cb.selectedColor = cb.normalColor;
            cb.pressedColor = new Color(1f, 1f, 1f, 0.28f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            b.onClick.AddListener(() => { try { click(); } catch (Exception e) { HitCamMod.Log.Warning("Main menu box: " + e.Message); } });
            Color edge = hot ? Red : new Color(0.92f, 0.93f, 0.95f, 1f);
            float th = hot ? 2.5f : 1.5f;
            Edge(go.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, th), edge);
            Edge(go.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, th), edge);
            Edge(go.transform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(th, 0f), edge);
            Edge(go.transform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(th, 0f), edge);
            var t = Text(go.transform, label, 18f, hot ? new Color(1f, 0.72f, 0.62f) : Color.white, FontStyles.Bold, 3f);
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            var trt = (RectTransform)t.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            return go;
        }

        static void Edge(Transform parent, Vector2 a, Vector2 b, Vector2 size, Color c)
        {
            var go = new GameObject("edge", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = a; rt.anchorMax = b;
            rt.pivot = new Vector2(a.x == b.x ? a.x : 0.5f, a.y == b.y ? a.y : 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
        }

        /// A scrolling text (the mouse wheel moves it) filling the parent between the offsets.
        static TextMeshProUGUI Scroll(Transform parent, Vector2 min, Vector2 max)
        {
            var view = new GameObject("scroll", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            view.transform.SetParent(parent, false);
            var vrt = (RectTransform)view.transform;
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one; vrt.offsetMin = min; vrt.offsetMax = max;
            view.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.001f);   // catches the wheel
            var content = new GameObject("content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(view.transform, false);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f); crt.pivot = new Vector2(0.5f, 1f);
            crt.sizeDelta = Vector2.zero;
            var v = content.GetComponent<VerticalLayoutGroup>();
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            v.padding = new RectOffset(0, 16, 0, 10);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var sr = view.GetComponent<ScrollRect>();
            sr.content = crt;
            sr.horizontal = false;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 40f;
            var t = Text(content.transform, "", 19f, new Color(0.9f, 0.91f, 0.93f));
            t.lineSpacing = 6f;
            t.paragraphSpacing = 4f;
            return t;
        }
    }
}
