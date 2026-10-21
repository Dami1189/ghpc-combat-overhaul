using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GHPC;
using GHPC.Mission;
using GHPC.Mission.Data;
using GHPC.Player;
using GHPC.State;
using GHPC.State.Data;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GHPCHitCam
{
    /// The ARMOR SIM entry in the game's main menu: a copy of one of the menu's own buttons (so it looks native) that
    /// loads a quiet mission (setting ArmorSimMission, the gunnery duel by default) and opens the Armor Sim as soon as
    /// the mission is up. Leaving the sim from there offers the way back to the main menu.
    internal static class ArmorSimMenu
    {
        static MelonPreferences_Entry<string> _missionPref, _pgMissionPref;
        static MelonPreferences_Entry<bool> _buttonPref;
        static GameObject _button;
        static bool _launching, _preloadSim;

        /// This mission was started by the menu button: the sim opens by itself and offers "back to main menu".
        public static bool FromMenu { get; private set; }
        public static MelonPreferences_Entry<string> MissionPref => _missionPref;
        public static GameObject Button => _button;

        public static void Init()
        {
            _buttonPref = Settings.Cat.CreateEntry("ArmorSimMenuButton", true, description: "ARMOR SIM button in the main menu: pick a vehicle and a round, see where it penetrates, fire.");
            _missionPref = Settings.Cat.CreateEntry("ArmorSimMission", "FG85_TR01,TR01_M1_Range", description: "The mission that button loads: the M1 Abrams training range, almost empty, so the game's own units cost next to nothing (advanced: theatre key, mission scene).");
            _pgMissionPref = Settings.Cat.CreateEntry("ProvingGroundMission", "FG85_TR01,TR01_M1_Range", description: "The mission the PROVING GROUND button loads: the M1 Abrams training range at Grafenwoehr (advanced: theatre key, mission scene).");
        }

        static bool IsMenuScene(string scene) => scene != null && scene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;

        /// The main menu is up (its scene loaded last, no mission since).
        public static bool InMenu { get; private set; }

        public static void OnSceneLoaded(string scene)
        {
            // a scene another mod loads next to the menu is no mission: the menu stays the menu (taken for one, it took
            // the whole menu part away, What's new and the buttons). A mission's own scenes replace the menu's.
            bool was = InMenu;
            InMenu = IsMenuScene(scene) || (InMenu && MenuLoaded() && SceneUtil.IsGameplayGone());
            if (was && InMenu && !IsMenuScene(scene)) HitCamMod.Log.Msg("Main menu: scene " + scene + " loaded next to it, still the menu");
            // the Armor Sim on its way (its button): the vehicles vehicle mods change load with the mission, so the mods get
            // them ready as the mission's own (else their conversion fails on a vehicle the mission didn't have)
            if (!IsMenuScene(scene) && _preloadSim) ModdedVehicles.PreloadForMission(ModdedVehicles.ArmorSimKeys, "Armor Sim");
            if (!IsMenuScene(scene)) return;
            _preloadSim = false;
            MelonCoroutines.Start(FreeCursor());
            FromMenu = false;
            _launching = false;
            if (_buttonPref.Value) MelonCoroutines.Start(AddButton());
        }

        public static void OnSceneUnloaded(string scene)
        {
            if (InMenu && IsMenuScene(scene) && !MenuLoaded()) InMenu = false;
        }

        /// The main menu needs the mouse: leaving a mission with one of our windows open (the proving ground's tools, the
        /// Armor Sim) put back the mission's hidden, locked cursor as the window closed, and the menu had none.
        static IEnumerator FreeCursor()
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 4f && InMenu)
            {
                if (!Cursor.visible || Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                yield return null;
            }
        }

        // ------------------------------------------------------------------ the button

        static bool _leftSaid;

        static string Leftover()
        {
            try { var s = GHPC.World.SceneUnitsManager.Instance; return s == null ? "gone now" : $"{s.name} in scene {s.gameObject.scene.name}"; }
            catch (Exception e) { return e.Message; }
        }

        static IEnumerator AddButton()
        {
            bool dumped = false, waitSaid = false;
            int tries = 0;
            // until the title menu shows: the game may come back to another of its pages first (one with more buttons
            // than the title list), and only the title list gets our rows
            while (true)
            {
                yield return new WaitForSecondsRealtime(tries++ < 80 ? 0.5f : 1.5f);
                // (the menu scene is the test: a mission's unit manager left behind kept the whole menu part away)
                if (_button != null || !InMenu || !MenuLoaded()) yield break;
                if (!SceneUtil.IsGameplayGone() && !_leftSaid) { _leftSaid = true; HitCamMod.Log.Warning("Main menu: a mission's unit manager is still around (" + Leftover() + ")"); }
                List<Button> buttons;
                try { buttons = Object.FindObjectsOfType<Button>().Where(b => b != null && b.isActiveAndEnabled && Label(b) != null).ToList(); }
                catch { continue; }
                if (buttons.Count == 0) continue;
                if (!dumped) { dumped = true; Dump(buttons); }
                // the title list (one level up if each button sits in a wrapper)
                int up = 1;
                var list = MainList(buttons, 1);
                if (list == null) { up = 2; list = MainList(buttons, 2); }
                if (list == null)
                {
                    if (!waitSaid && tries > 6) { waitSaid = true; HitCamMod.Log.Msg("Main menu: another page is showing, waiting for the title menu"); }
                    continue;
                }
                var layout = Original(list, up);
                try { _button = Insert(list, up, "ARMOR SIM", "Armor Sim", Launch, "ArmorSim menu button"); }
                catch (Exception e) { HitCamMod.Log.Warning("Armor Sim: menu button failed: " + e); yield break; }
                if (ProvingGround.MenuButton != null && ProvingGround.MenuButton.Value)
                {
                    yield return null;
                    try
                    {
                        // the list again, with the ARMOR SIM copy in it: the new row goes between it and OPTIONS
                        var again = Object.FindObjectsOfType<Button>().Where(b => b != null && b.isActiveAndEnabled && Label(b) != null).ToList();
                        var list2 = MainList(again, up);
                        if (list2 != null) PgButton = Insert(list2, up, "PROVING GROUND", "Proving Ground", LaunchProvingGround, "ProvingGround menu button");
                    }
                    catch (Exception e) { HitCamMod.Log.Warning("Proving ground: menu button failed: " + e); }
                }
                try { Relayout(layout, up); Badge(); } catch (Exception e) { HitCamMod.Log.Warning("Main menu layout: " + e.Message); }
                yield break;
            }
        }

        /// Buttons grouped by their ancestor `up` levels up: the title menu's list (the one with Settings and Exit in
        /// it), in screen order; null while it isn't showing.
        static List<Button> MainList(List<Button> buttons, int up)
        {
            Transform Anc(Button b) { var t = b.transform; for (int i = 0; i < up && t != null; i++) t = t.parent; return t; }
            foreach (var g in buttons.Where(b => Anc(b) != null).GroupBy(Anc).OrderByDescending(x => x.Count()))
            {
                if (g.Count() < 3) break;
                if (g.Any(b => Is(b, up, "OPTIONS", "SETTINGS")) && g.Any(b => Is(b, up, "QUIT", "EXIT"))) return g.OrderBy(b => Row(b, up)).ToList();
            }
            return null;
        }

        /// The button's label or its object's name starts with one of the words (the names hold in any language).
        static bool Is(Button b, int up, params string[] words)
        {
            var l = Label(b)?.text?.Trim().ToUpperInvariant() ?? "";
            var n = (RowOf(b, up)?.name ?? "").ToUpperInvariant();
            var own = b.name.ToUpperInvariant();
            return words.Any(w => l.StartsWith(w) || n.StartsWith(w) || own.StartsWith(w));
        }

        internal static bool MenuLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (IsMenuScene(SceneManager.GetSceneAt(i).name)) return true;
            return false;
        }

        static Transform RowOf(Button b, int up) { var t = b.transform; for (int i = 1; i < up; i++) t = t.parent; return t; }

        static int Row(Button b, int up) { var t = b.transform; for (int i = 1; i < up; i++) t = t.parent; return t.GetSiblingIndex(); }

        public static GameObject PgButton { get; private set; }

        static GameObject Insert(List<Button> list, int up, string upper, string title, UnityEngine.Events.UnityAction click, string goName)
        {
            // goes just above OPTIONS / SETTINGS (else above QUIT / EXIT, else at the end); copies an ordinary entry
            var before = list.FirstOrDefault(b => Is(b, up, "OPTIONS", "SETTINGS")) ?? list.FirstOrDefault(b => Is(b, up, "QUIT", "EXIT")) ?? null;
            var template = before ?? list[list.Count - 1];
            // when each button sits in its own wrapper, the row to copy is the wrapper
            Transform row = template.transform;
            for (int i = 1; i < up; i++) row = row.parent;
            var rows = list.Select(b => RowOf(b, up)).Distinct().ToList();
            var go = Object.Instantiate(row.gameObject, row.parent, false);
            go.name = goName;
            go.transform.SetSiblingIndex(before != null ? row.GetSiblingIndex() : row.GetSiblingIndex() + 1);
            // a layout group places it by the sibling order; GHPC's title menu places every row by hand, so the copy goes one
            // step above the row it was copied from and the rows above it move up one step (Exit, its gap and the
            // Steam / version lines under it stay where they are; there's room under the title)
            // (placed with the rest by Relayout)
            // keep the looks, drop the game's own behaviour (localisation, menu navigation, click actions)
            var removed = new List<string>();
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Assembly.GetName().Name.StartsWith("Assembly-CSharp")) { removed.Add(t.Name); Object.DestroyImmediate(mb); }
            }
            // hover / click feedback that stays inside the button (its own sound, its own image) is kept; a trigger that
            // reaches outside it (menu navigation) goes
            bool Inside(Object t) => t is Component c ? c != null && c.transform.IsChildOf(go.transform) : t is GameObject g && g != null && g.transform.IsChildOf(go.transform);
            var triggerLog = new List<string>();
            foreach (var et in go.GetComponentsInChildren<UnityEngine.EventSystems.EventTrigger>(true))
            {
                bool safe = true;
                foreach (var e in et.triggers)
                    for (int i = 0; i < e.callback.GetPersistentEventCount(); i++)
                    {
                        var target = e.callback.GetPersistentTarget(i);
                        bool inside = target != null && Inside(target);
                        triggerLog.Add($"{e.eventID}: {(target != null ? target.GetType().Name : "null")}.{e.callback.GetPersistentMethodName(i)}{(inside ? "" : " (outside)")}");
                        if (!inside) safe = false;
                    }
                if (!safe) { removed.Add("EventTrigger"); Object.DestroyImmediate(et); }
            }
            var btn = go.GetComponentInChildren<Button>(true);
            var clicks = new List<string>();
            for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++)
                clicks.Add((btn.onClick.GetPersistentTarget(i)?.GetType().Name ?? "null") + "." + btn.onClick.GetPersistentMethodName(i));
            btn.onClick = new Button.ButtonClickedEvent();
            var sound = btn.GetComponent<AudioSource>();
            btn.onClick.AddListener(click);   // its click sound comes from the kept trigger
            HitCamMod.Log.Msg("Armor Sim: menu button copy: trigger calls [" + string.Join("; ", triggerLog) + "], original click calls [" + string.Join("; ", clicks) + "]"
                + $", sound {(sound != null && sound.clip != null ? sound.clip.name : "none")}");
            btn.interactable = true;
            var label = Label(btn);
            string old = label != null ? label.text ?? "" : "";
            if (label != null)
            {
                label.text = old.Length > 0 && old == old.ToUpperInvariant() ? upper : title;
                // one line, never wider than the game's widest entry: the menu narrows while one of its pages is open
                // (a longer name broke onto two small lines then), and a sub-menu opens right beside the entries
                float widest = 0f;
                foreach (var b in list) { var l = Label(b); if (l != null) widest = Mathf.Max(widest, l.preferredWidth); }
                label.enableWordWrapping = false;
                label.ForceMeshUpdate();
                float size = label.fontSize, mine = label.preferredWidth;
                label.enableAutoSizing = true;
                label.fontSizeMax = mine > widest && widest > 1f ? size * widest / mine : size;
                label.fontSizeMin = label.fontSizeMax * 0.6f;
            }
            HitCamMod.Log.Msg($"{title}: menu button added after copying '{old.Trim()}' ({Path(row)}), above '{(before != null ? Label(before)?.text?.Trim() : "(end)")}'"
                + (removed.Count > 0 ? "; dropped from the copy: " + string.Join(", ", removed.Distinct()) : ""));
            return go;
        }

        /// The menu's rows as the game placed them by hand: their top, the row above Exit's gap, and the step between rows.
        class MenuLayout { public Transform Panel; public float Top, Bottom, Step, ExitY = float.NaN; public bool Manual; public readonly List<Transform> Rows = new List<Transform>(); }

        static MenuLayout Original(List<Button> list, int up)
        {
            bool IsExit(Button b) => Is(b, up, "QUIT", "EXIT");
            var rows = list.Where(b => !IsExit(b)).Select(b => RowOf(b, up)).Distinct().ToList();
            var m = new MenuLayout { Panel = rows.Count > 0 ? rows[0].parent : null };
            if (m.Panel == null) return m;
            m.Rows.AddRange(rows);
            var exit = list.FirstOrDefault(IsExit);
            if (exit != null) m.ExitY = RowOf(exit, up).localPosition.y;
            m.Manual = m.Panel.GetComponent<LayoutGroup>() == null;
            var ys = rows.Select(r => r.localPosition.y).OrderByDescending(y => y).ToList();
            m.Top = ys[0];
            m.Bottom = ys[ys.Count - 1];
            float step = float.MaxValue;
            for (int i = 1; i < ys.Count; i++) if (ys[i - 1] - ys[i] > 0.5f) step = Mathf.Min(step, ys[i - 1] - ys[i]);
            m.Step = step == float.MaxValue ? 30f : step;
            return m;
        }

        /// GHPC's title menu places every row by hand. With our two rows in it the list starts a little lower (room for
        /// the COMBAT OVERHAUL line under the title, which the moved-up rows used to cover) and the rows close up a bit,
        /// down to just into the gap above Exit; Exit and everything under it stay where they are.
        static void Relayout(MenuLayout m, int up)
        {
            if (m == null || m.Panel == null || !m.Manual) return;
            // the game's rows and ours, in the panel's order
            var rows = m.Rows.Where(t => t != null).ToList();
            foreach (var mine in new[] { _button, PgButton }) if (mine != null && mine.transform.parent == m.Panel && !rows.Contains(mine.transform)) rows.Add(mine.transform);
            rows.Sort((a, b) => a.GetSiblingIndex().CompareTo(b.GetSiblingIndex()));
            if (rows.Count < 2) return;
            // from a little above the game's first row (the badge sits above it) down to Exit's gap, near the game's spacing
            float top = m.Top + 0.7f * m.Step;
            float exitY = float.IsNaN(m.ExitY) ? m.Bottom - 2f * m.Step : m.ExitY;
            float step = Mathf.Min(m.Step, (top - exitY) / (rows.Count - 1 + 1.3f));
            for (int i = 0; i < rows.Count; i++)
            {
                var p = rows[i].localPosition;
                rows[i].localPosition = new Vector3(p.x, top - i * step, p.z);
            }
            HitCamMod.Log.Msg($"Main menu: {rows.Count} rows ({string.Join(", ", rows.Select(r => r.name))}) from {top:0} down, {step:0.0} apart (the game's {m.Step:0.0}), Exit at {exitY:0}");
        }

        static GameObject _badge;
        public static bool HasBadge => _badge != null;

        /// "COMBAT OVERHAUL" under the game's title, so a modded game is told apart at a glance: a copy of the title's own
        /// subtitle text, smaller, in the GHPC logo's red.
        static void Badge()
        {
            if (_badge != null) return;
            TMP_Text sub = null;
            foreach (var t in Object.FindObjectsOfType<TMP_Text>())
            {
                if (t == null || !t.isActiveAndEnabled) continue;
                string x = t.text ?? "";
                if (x.IndexOf("HEAT", StringComparison.OrdinalIgnoreCase) >= 0 && x.IndexOf("Gunner", StringComparison.OrdinalIgnoreCase) >= 0) { sub = t; break; }
            }
            if (sub == null) { HitCamMod.Log.Msg("Main menu: no title text found for the badge"); return; }
            var go = Object.Instantiate(sub.gameObject, sub.transform.parent, false);
            go.name = "Combat Overhaul badge";
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb is TMP_Text) continue;
                var tn = mb.GetType();
                if (tn.Assembly.GetName().Name.StartsWith("Assembly-CSharp") || tn.FullName.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0) Object.DestroyImmediate(mb);
            }
            var txt = go.GetComponent<TMP_Text>();
            txt.text = "COMBAT OVERHAUL";
            txt.fontSize = sub.fontSize * 0.62f;
            txt.enableAutoSizing = false;
            txt.enableWordWrapping = false;
            txt.overflowMode = TextOverflowModes.Overflow;
            txt.color = new Color(0.86f, 0.22f, 0.16f, 1f);
            var rt = (RectTransform)go.transform;
            var srt = (RectTransform)sub.transform;
            rt.anchoredPosition = srt.anchoredPosition - new Vector2(0f, Mathf.Max(sub.preferredHeight, sub.fontSize) * 0.95f);
            // the title breaks onto two lines while the menu is narrowed (a page open): the line stays under its last line
            go.AddComponent<UnderText>().Init(sub, txt);
            _badge = go;
            HitCamMod.Log.Msg($"Main menu: COMBAT OVERHAUL badge under '{sub.text.Trim()}'");
        }

        static TMP_Text Label(Button b)
        {
            if (b == null) return null;
            var t = b.GetComponentInChildren<TMP_Text>(true);
            return t != null && !string.IsNullOrEmpty(t.text) && t.text.Trim().Length > 0 ? t : null;
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 5; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static void Dump(List<Button> buttons)
        {
            try
            {
                HitCamMod.Log.Msg("Armor Sim: main menu buttons: " + string.Join(" | ", buttons.Select(b =>
                    $"'{Label(b).text.Trim()}' {Path(b.transform)} #{b.transform.GetSiblingIndex()} [{string.Join(",", b.GetComponents<Component>().Select(c => c.GetType().Name))}]")));
            }
            catch { }
        }

        // ------------------------------------------------------------------ launching

        static void Launch() => LaunchMission(false);

        static void LaunchProvingGround() => LaunchMission(true);

        static void LaunchMission(bool provingGround)
        {
            if (_launching) return;
            try
            {
                var theaters = Resources.FindObjectsOfTypeAll<MissionTheaterScriptable>();
                MissionTheaterScriptable theater = null;
                MissionMetaData mission = null;
                var pref = provingGround ? _pgMissionPref : _missionPref;
                var parts = (pref.Value ?? "").Split(',');
                if (parts.Length == 2)
                {
                    theater = theaters.FirstOrDefault(t => t.Key == parts[0].Trim());
                    mission = theater?.Missions?.FirstOrDefault(m => m != null && !m.IsCategory && SceneName(m) == parts[1].Trim() && InBuild(m));
                }
                if (mission == null)
                {
                    HitCamMod.Log.Warning("Armor Sim: mission '" + pref.Value + "' not found, taking the first one in the build");
                    foreach (var th in theaters)
                    {
                        mission = th.Missions?.FirstOrDefault(m => m != null && !m.IsCategory && InBuild(m));
                        if (mission != null) { theater = th; break; }
                    }
                }
                var sc = Object.FindObjectOfType<SceneController>();
                if (mission == null || sc == null) { HitCamMod.Log.Warning("Armor Sim: no mission to load"); return; }
                int idx = SceneUtility.GetBuildIndexByScenePath(theater.TerrainSceneReference.Path);
                // the mission's own side (forcing the other one leaves the player without a vehicle)
                var sides = mission.FactionInfo?.Select(f => f.Allegiance).Where(f => f != Faction.Neutral).ToList() ?? new List<Faction>();
                SceneController.TargetSpawningFaction = sides.Count > 0 ? sides[0] : Faction.Blue;
                SceneController.AllowTimeChoice = mission.HasTimeChoices;
                if (mission.HasTimeChoices) SceneController.IsDaytime = true;
                PersistentDataManager.PersistMissionInfo(new MissionInfoData(mission.MissionName, ""), mission, theater.Key);
                DynamicMissionComposer.CampaignMode = false;
                DynamicMissionComposer.DynamicMission = mission.IsFlexMission;
                DynamicMissionComposer.NeedsFlexMissionSetup = mission.IsFlexMission;
                DynamicMissionComposer.CurrentMissionNameKey = SceneName(mission);
                _launching = true;
                FromMenu = !provingGround;
                _preloadSim = !provingGround;
                ProvingGround.FromMenu = provingGround;
                if (provingGround) ProvingGround.NoteLoading();
                HitCamMod.Log.Msg($"{(provingGround ? "Proving ground" : "Armor Sim")}: menu button -> loading {theater.Key},{SceneName(mission)} \"{mission.MissionName}\"");
                MelonCoroutines.Start(OpenWhenReady(provingGround));
                sc.LoadSceneByMission($"{idx},{SceneName(mission)}");
            }
            catch (Exception e)
            {
                HitCamMod.Log.Error("Armor Sim: could not load the mission: " + e);
                _launching = false;
                FromMenu = false;
            }
        }

        static bool InBuild(MissionMetaData m)
        {
            try { return m.MissionSceneReference != null && SceneUtility.GetBuildIndexByScenePath(m.MissionSceneReference.Path) >= 0; }
            catch { return false; }
        }

        static string SceneName(MissionMetaData m)
        {
            try { return m.MissionSceneReference.Name; } catch { return "?"; }
        }

        /// Waits for the mission (player vehicle, unit spawner) and gives the loading screen a moment, then opens the sim.
        static IEnumerator OpenWhenReady(bool provingGround)
        {
            float t0 = Time.realtimeSinceStartup;
            bool ready = false;
            while (Time.realtimeSinceStartup - t0 < 180f)
            {
                yield return null;
                if (SceneUtil.IsGameplayGone()) continue;
                var pi = SceneUtil.Player;
                if (pi == null || pi.CurrentPlayerUnit == null) continue;
                if (UnitSpawner.Instance == null && Object.FindObjectOfType<UnitSpawner>() == null) continue;
                ready = true;
                break;
            }
            _launching = false;
            if (!ready) { HitCamMod.Log.Warning("Armor Sim: the mission didn't come up, not opening the sim"); FromMenu = false; yield break; }
            yield return new WaitForSecondsRealtime(4f);
            if (provingGround)
            {
                if (!SceneUtil.IsGameplayGone() && !ProvingGround.Active)
                {
                    // the mission starts in its planning phase (map open): start it, then build the range
                    try
                    {
                        var msc = GHPC.State.MissionStateController.Instance;
                        if (msc != null && GHPC.State.MissionStateController.CurrentState == GHPC.State.MissionState.Planning) msc.EndPlanningPhase();
                    }
                    catch { }
                    yield return new WaitForSecondsRealtime(1f);
                    yield return ProvingGround.Build();
                }
                yield break;
            }
            if (!SceneUtil.IsGameplayGone() && !ArmorSim.Active) ArmorSim.OpenFromMenu();
        }

        public static void BackToMainMenu()
        {
            var sc = Object.FindObjectOfType<SceneController>();
            if (sc == null) { HitCamMod.Log.Warning("Armor Sim: no scene controller, can't go back to the menu"); return; }
            HitCamMod.Log.Msg("Armor Sim: back to the main menu");
            FromMenu = false;
            sc.LoadMainMenu();
        }

        // ------------------------------------------------------------------ self-test

        /// From the title screen: photograph the menu with the button, click it, wait for the sim to open by itself,
        /// optionally run Armor Sim shots there, then leave through "back to main menu" and photograph the menu again.
        public static IEnumerator SelfTest(Func<string, string, string> cfg, string outDir, Action done)
        {
            HitCamMod.Log.Msg("ARMORSIM MENU TEST: start");
            float t0 = Time.realtimeSinceStartup;
            while (_button == null && Time.realtimeSinceStartup - t0 < 45f) yield return null;
            if (_button == null) { HitCamMod.Log.Warning("ARMORSIM MENU TEST: no button in the main menu"); done(); yield break; }
            yield return new WaitForSecondsRealtime(1.5f);
            Capture(outDir, "menu0_button.png");
            yield return new WaitForSecondsRealtime(0.5f);
            HitCamMod.Log.Msg("ARMORSIM MENU TEST: clicking the button");
            _button.GetComponentInChildren<Button>(true).onClick.Invoke();
            t0 = Time.realtimeSinceStartup;
            while (!ArmorSim.ReadyForShots && Time.realtimeSinceStartup - t0 < 150f) yield return null;
            if (!ArmorSim.ReadyForShots) { HitCamMod.Log.Warning("ARMORSIM MENU TEST: the sim did not open by itself"); done(); yield break; }
            HitCamMod.Log.Msg($"ARMORSIM MENU TEST: sim open {Time.realtimeSinceStartup - t0:0.0} s after the click (from menu: {FromMenu})");
            t0 = Time.realtimeSinceStartup;
            while (!ArmorSim.PenMapDone && Time.realtimeSinceStartup - t0 < 20f) yield return null;
            yield return null;
            Capture(outDir, "menu1_sim_opened.png");
            if (cfg("aspicker", "false") == "true")
            {
                ArmorSim.TestPicker(true);
                yield return new WaitForSecondsRealtime(0.6f);
                Capture(outDir, "menu1b_vehicle_list.png");
                yield return new WaitForSecondsRealtime(0.4f);
                ArmorSim.TestPicker(false);
                ArmorSim.TestPan(1.2f, 0.4f);
                yield return new WaitForSecondsRealtime(0.8f);
                Capture(outDir, "menu1c_view_moved.png");
                yield return new WaitForSecondsRealtime(0.4f);
                ArmorSim.TestPan(0f, 0f);
            }
            if (cfg("cfgopen", "false") == "true")
            {
                // the settings window over the sim: alone, the sim's panels out of the way
                ConfigMenu.Open();
                yield return new WaitForSecondsRealtime(0.8f);
                Capture(outDir, "menu1d_settings_open.png");
                yield return new WaitForSecondsRealtime(0.4f);
                ConfigMenu.Close();
                yield return new WaitForSecondsRealtime(0.5f);
            }
            yield return new WaitForSecondsRealtime(0.5f);

            string veh = cfg("armorsim", "");
            if (veh.Length > 0)
                yield return ArmorSim.SelfTest(veh, cfg("armorsim_ammo", "apfsds"), cfg("armorsim_shots", "front"), cfg("armorsim_range", "1000"), outDir, leaveOpen: true);

            ArmorSim.RequestClose();
            yield return null;
            yield return null;
            Capture(outDir, "menu2_leave_dialog.png");
            yield return new WaitForSecondsRealtime(0.5f);
            ArmorSim.LeaveToMainMenu();
            t0 = Time.realtimeSinceStartup;
            while ((!SceneUtil.IsGameplayGone() || _button == null) && Time.realtimeSinceStartup - t0 < 90f) yield return null;
            HitCamMod.Log.Msg($"ARMORSIM MENU TEST: back in the main menu: {SceneUtil.IsGameplayGone()}, button again: {_button != null}, sim active: {ArmorSim.Active}");
            yield return new WaitForSecondsRealtime(1.5f);
            Capture(outDir, "menu3_back_in_menu.png");
            yield return new WaitForSecondsRealtime(0.5f);
            done();
        }

        static void Capture(string dir, string name)
        {
            try { ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, name)); HitCamMod.Log.Msg("ARMORSIM MENU TEST: captured " + name); }
            catch (Exception e) { HitCamMod.Log.Warning("capture failed: " + e.Message); }
        }
    }

    /// Keeps a text centred just under another one's last drawn line (the title wraps when its panel narrows).
    internal class UnderText : MonoBehaviour
    {
        TMP_Text _above, _me;
        float _size;

        public void Init(TMP_Text above, TMP_Text me) { _above = above; _me = me; _size = me.fontSize; }

        void LateUpdate()
        {
            if (_above == null || _me == null || !_above.isActiveAndEnabled) return;
            var a = _above.textBounds;
            var m = _me.textBounds;
            if (a.size.y <= 0f || m.size.y <= 0f) return;
            // never wider than the title's widest line (its own size again once the title is wide)
            float full = m.size.x * _size / Mathf.Max(_me.fontSize, 0.01f);
            float fit = Mathf.Clamp(_size * a.size.x / Mathf.Max(full, 0.01f), _size * 0.5f, _size);
            if (Mathf.Abs(fit - _me.fontSize) > 0.05f) { _me.fontSize = fit; return; }
            float gap = 0.12f * m.size.y;
            Vector3 want = _above.transform.TransformPoint(new Vector3(a.center.x, a.min.y - gap, 0f));
            Vector3 have = _me.transform.TransformPoint(new Vector3(m.center.x, m.max.y, 0f));
            Vector3 d = want - have;
            if (d.sqrMagnitude > 1e-6f) transform.position += d;
        }
    }
}
