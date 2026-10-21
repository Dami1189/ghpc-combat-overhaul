using GHPC.World;

namespace GHPCHitCam
{
    internal static class SceneUtil
    {
        /// True once the mission (its unit manager) is gone, e.g. back in the main menu.
        public static bool IsGameplayGone()
        {
            try { return SceneUnitsManager.Instance == null; }
            catch { return true; }
        }
    }
}
