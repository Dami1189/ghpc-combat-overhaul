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
        static MelonPreferences_Entry<string> _missionPref;
        static MelonPreferences_Entry<bool> _buttonPref;
        static GameObject _button;
        static bool _launching;

        /// This mission was started by the menu button: the sim opens by itself and offers "back to main menu".
        public static bool FromMenu { get; private set; }
        public static GameObject Button => _button;

        public static void Init()
        {
            _buttonPref = Settings.Cat.CreateEntry("ArmorSimMenuButton", true, description: "ARMOR SIM button in the main menu: pick a vehicle and a round, see where it penetrates, fire.");
            _missionPref = Settings.Cat.CreateEntry("ArmorSimMission", "FG85_GT03,GT03_gunnery_duel", description: "The mission that button loads (advanced: theatre key, mission scene).");
        }

        static bool IsMenuScene(string scene) => scene != null && scene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;

        public static void OnSceneLoaded(string scene)
        {
            if (!IsMenuScene(scene)) return;
            FromMenu = false;
            _launching = false;
            if (_buttonPref.Value) MelonCoroutines.Start(AddButton());
        }

        // ------------------------------------------------------------------ the button

        static IEnumerator AddButton()
        {
            float t0 = Time.realtimeSinceStartup;
            bool dumped = false;
            while (Time.realtimeSinceStartup - t0 < 40f)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                if (_button != null || !SceneUtil.IsGameplayGone()) yield break;
                List<Button> buttons;
                try { buttons = Object.FindObjectsOfType<Button>().Where(b => b != null && b.isActiveAndEnabled && Label(b) != null).ToList(); }
                catch { continue; }
                if (buttons.Count == 0) continue;
                if (!dumped) { dumped = true; Dump(buttons); }
                // the main list: the container with the most labelled buttons (one level up if each button sits in a wrapper)
                int up = 1;
                var list = MainList(buttons, 1);
                if (list == null || list.Count < 3) { up = 2; list = MainList(buttons, 2); }
                if (list == null || list.Count < 3) continue;
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
                        if (list2 != null && list2.Count >= 3) PgButton = Insert(list2, up, "PROVING GROUND", "Proving Ground", LaunchProvingGround, "ProvingGround menu button");
                    }
                    catch (Exception e) { HitCamMod.Log.Warning("Proving ground: menu button failed: " + e); }
                }
                try { Relayout(layout, up); Badge(); } catch (Exception e) { HitCamMod.Log.Warning("Main menu layout: " + e.Message); }
                yield break;
            }
        }

        /// Buttons grouped by their ancestor `up` levels up; the biggest group, in screen order.
        static List<Button> MainList(List<Button> buttons, int up)
        {
            Transform Anc(Button b) { var t = b.transform; for (int i = 0; i < up && t != null; i++) t = t.parent; return t; }
            var g = buttons.Where(b => Anc(b) != null).GroupBy(Anc).OrderByDescending(x => x.Count()).FirstOrDefault();
            return g?.OrderBy(b => Row(b, up)).ToList();
        }

        static Transform RowOf(Button b, int up) { var t = b.transform; for (int i = 1; i < up; i++) t = t.parent; return t; }

        static int Row(Button b, int up) { var t = b.transform; for (int i = 1; i < up; i++) t = t.parent; return t.GetSiblingIndex(); }

        public static GameObject PgButton { get; private set; }

        static GameObject Insert(List<Button> list, int up, string upper, string title, UnityEngine.Events.UnityAction click, string goName)
        {
            bool Is(Button b, params string[] words) { var l = Label(b)?.text?.Trim().ToUpperInvariant() ?? ""; return words.Any(w => l.StartsWith(w)); }
            // goes just above OPTIONS / SETTINGS (else above QUIT / EXIT, else at the end); copies an ordinary entry
            var before = list.FirstOrDefault(b => Is(b, "OPTIONS", "SETTINGS")) ?? list.FirstOrDefault(b => Is(b, "QUIT", "EXIT")) ?? null;
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
            if (label != null) label.text = old.Length > 0 && old == old.ToUpperInvariant() ? upper : title;
            HitCamMod.Log.Msg($"{title}: menu button added after copying '{old.Trim()}' ({Path(row)}), above '{(before != null ? Label(before)?.text?.Trim() : "(end)")}'"
                + (removed.Count > 0 ? "; dropped from the copy: " + string.Join(", ", removed.Distinct()) : ""));
            return go;
        }

        /// The menu's rows as the game placed them by hand: their top, the row above Exit's gap, and the step between rows.
        class MenuLayout { public Transform Panel; public float Top, Bottom, Step, ExitY = float.NaN; public bool Manual; public readonly List<Transform> Rows = new List<Transform>(); }

        static MenuLayout Original(List<Button> list, int up)
        {
            bool IsExit(Button b) { var l = Label(b)?.text?.Trim().ToUpperInvariant() ?? ""; return l.StartsWith("QUIT") || l.StartsWith("EXIT"); }
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
            txt.color = new Color(0.86f, 0.22f, 0.16f, 1f);
            var rt = (RectTransform)go.transform;
            var srt = (RectTransform)sub.transform;
            rt.anchoredPosition = srt.anchoredPosition - new Vector2(0f, Mathf.Max(sub.preferredHeight, sub.fontSize) * 0.95f);
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
                var parts = (_missionPref.Value ?? "").Split(',');
                if (parts.Length == 2)
                {
                    theater = theaters.FirstOrDefault(t => t.Key == parts[0].Trim());
                    mission = theater?.Missions?.FirstOrDefault(m => m != null && !m.IsCategory && SceneName(m) == parts[1].Trim() && InBuild(m));
                }
                if (mission == null)
                {
                    HitCamMod.Log.Warning("Armor Sim: mission '" + _missionPref.Value + "' not found, taking the first one in the build");
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
                var pi = PlayerInput.Instance;
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
}
