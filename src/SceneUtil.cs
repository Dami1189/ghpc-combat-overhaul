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

        /// One of the game's own menus that holds the gun and the camera is up (the Q menu, the unit menu, a menu page).
        /// A window of the mod that set those holds puts back only what one of these still wants: the value it saw when
        /// it opened can be stale by then (the Q menu closed in between).
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

        /// The game blocks the keyboard only in a mission's planning phase (the map before it starts).
        public static bool InPlanning()
        {
            try { return GHPC.State.MissionStateController.CurrentState == GHPC.State.MissionState.Planning; }
            catch { return false; }
        }

        /// The game's PlayerInput (null in the main menu). Its own getter, like the unit manager's, looks the whole scene
        /// over on every call while there is none: the HUD, the settings window and the proving ground asked for it each
        /// frame and in every OnGUI pass, ~10 ms a frame in the main menu. Kept once found, looked for 4 times a second.
        public static PlayerInput Player
        {
            get
            {
                try
                {
                    if (_pi != null) return _pi;
                    float now = Time.realtimeSinceStartup;
                    if (_piLookedAt >= 0f && now - _piLookedAt < 0.25f) return null;
                    _piLookedAt = now;
                    _pi = PlayerInput.Instance;
                    return _pi != null ? _pi : null;
                }
                catch { return null; }
            }
        }

        /// True once the mission (its unit manager) is gone, e.g. back in the main menu. The game's own getter looks
        /// the whole scene over on every call while there is none: in the main menu, called every frame (and from every
        /// OnGUI pass), that alone cost frames. So the one found is kept (Unity's null says when it goes) and a missing
        /// one is looked for at most four times a second.
        public static bool IsGameplayGone()
        {
            try
            {
                if (_sum != null) return false;
                float now = Time.realtimeSinceStartup;
                if (_lookedAt >= 0f && now - _lookedAt < 0.25f) return true;
                _lookedAt = now;
                _sum = SceneUnitsManager.Instance;
                return _sum == null;
            }
            catch { return true; }
        }

        /// A scene came or went: look again on the next call.
        public static void Recheck() { _lookedAt = -1f; _piLookedAt = -1f; }
    }
}
