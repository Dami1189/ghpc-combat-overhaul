using GHPC.Player;
using GHPC.World;
using UnityEngine;

namespace GHPCHitCam
{
    internal static class SceneUtil
    {
        static SceneUnitsManager _sum;
        static PlayerInput _pi;
        static float _lookedAt = -1f, _piLookedAt = -1f;

        static System.Collections.Generic.HashSet<string> _menuScenes;
        static readonly System.Collections.Generic.HashSet<string> _learned = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        /// menu watchdog found the title menu in a scene we didn't know, it counts as a menu from now on
        public static void LearnMenuScene(string scene)
        {
            if (string.IsNullOrEmpty(scene) || scene == "DontDestroyOnLoad" || IsMenuScene(scene)) return;
            if (_learned.Add(scene)) HitCamMod.Log.Msg("Main menu: scene " + scene + " counts as a menu from now on");
        }

        /// main menu scene. game picks its menu at random from its list plus the unlocked variants (T-64 one, "t64_menu"),
        /// checking for "MainMenu" missed those and the menu came up without the mod's rows (a tester's "the menu broke")
        /// names come from the game's SceneController
        public static bool IsMenuScene(string scene)
        {
            if (string.IsNullOrEmpty(scene) || scene.StartsWith("LOADER", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (_learned.Contains(scene)) return true;
            if (AutoTest.Active && AutoTest.Opt("unknown_menu", "") == scene) return false;   // self-test: a menu variant the mod doesn't know
            if (_menuScenes == null || _menuScenes.Count == 0)
            {
                try
                {
                    var sc = Object.FindObjectOfType<GHPC.SceneController>();
                    if (sc != null)
                    {
                        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        if (sc.MenuSceneRefs != null) foreach (var r in sc.MenuSceneRefs) if (r != null && !string.IsNullOrEmpty(r.Name)) names.Add(r.Name);
                        if (sc.UnlockableMenuScenes != null) foreach (var u in sc.UnlockableMenuScenes) if (u?.UnlockedScene != null && !string.IsNullOrEmpty(u.UnlockedScene.Name)) names.Add(u.UnlockedScene.Name);
                        if (names.Count > 0) { _menuScenes = names; HitCamMod.Log.Msg("Main menu scenes of the game: " + string.Join(", ", names)); }
                    }
                }
                catch { }
            }
            if (_menuScenes != null && _menuScenes.Contains(scene)) return true;
            // no SceneController to ask yet: names the game's menus have had so far
            return scene.IndexOf("MainMenu", System.StringComparison.OrdinalIgnoreCase) >= 0 || scene.EndsWith("_menu", System.StringComparison.OrdinalIgnoreCase);
        }

        /// one of the game's own menus that holds the gun and camera is up (Q menu, unit menu, a menu page)
        /// a mod window that set those holds only puts back what one of these still wants, the value it saw on open
        /// can be stale by then (Q menu closed in between)
        public static bool GameMenuUp(PlayerInput pi)
        {
            try
            {
                if (pi == null) return false;
                if (pi.ContextMenuRoot != null && pi.ContextMenuRoot.gameObject.activeInHierarchy) return true;
                var um = GHPC.UI.UnitMenu.UnitMenuController.Instance;
                if (um != null && um.MenuVisible) return true;
                return pi.MenuController != null && pi.MenuController.ActiveMenu != null;
            }
            catch { return false; }
        }

        /// game blocks the keyboard only in a mission's planning phase (the map before start)
        public static bool InPlanning()
        {
            try { return GHPC.State.MissionStateController.CurrentState == GHPC.State.MissionState.Planning; }
            catch { return false; }
        }

        /// game's PlayerInput (null in main menu). its getter, like the unit manager's, scans the whole scene every call while
        /// there is none: HUD, settings window and proving ground asked each frame and every OnGUI pass, ~10 ms a frame in the
        /// main menu. so it's cached once found, looked for about once a second (a scene load looks at once, so a mission gets it right away)
        public static PlayerInput Player
        {
            get
            {
                try
                {
                    if (_pi != null) return _pi;
                    float now = Time.realtimeSinceStartup;
                    if (_piLookedAt >= 0f && now - _piLookedAt < 1f) return null;
                    _piLookedAt = now;
                    _pi = PlayerInput.Instance;
                    return _pi != null ? _pi : null;
                }
                catch { return null; }
            }
        }

        /// true once the mission (its unit manager) is gone, e.g. back in main menu. game's getter scans the whole scene every
        /// call while there is none, and called every frame (+ every OnGUI pass) in the menu that alone cost frames
        /// so the found one is cached (unity's null says when it's gone) and a missing one is looked for about once a second (at once after a scene load)
        public static bool IsGameplayGone()
        {
            try
            {
                if (_sum != null) return false;
                float now = Time.realtimeSinceStartup;
                if (_lookedAt >= 0f && now - _lookedAt < 1.1f) return true;
                _lookedAt = now;
                _sum = SceneUnitsManager.Instance;
                return _sum == null;
            }
            catch { return true; }
        }

        /// a scene came or went: look again on next call
        static GHPC.State.StateController _states;
        static float _statesLookedAt = -1f;

        /// game's mission state controller (its startup states). same trap as the two above: its getter scans the whole
        /// scene every call while there is none, and the vehicle mods support + warm-up asked several times a frame in the
        /// main menu (2-14 ms a frame, the Soviet menu down to 63 fps). cached once found, a missing one looked for about once a second (at once after a scene load). the three look on different frames
        public static GHPC.State.StateController States
        {
            get
            {
                try
                {
                    if (_states != null) return _states;
                    float now = Time.realtimeSinceStartup;
                    if (_statesLookedAt >= 0f && now - _statesLookedAt < 1.2f) return null;
                    _statesLookedAt = now;
                    _states = GHPC.State.StateController.Instance;
                    return _states != null ? _states : null;
                }
                catch { return null; }
            }
        }

        public static void Recheck() { _lookedAt = -1f; _piLookedAt = -1f; _statesLookedAt = -1f; }
    }
}
