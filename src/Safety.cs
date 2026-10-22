using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.Effects;
using GHPC.Weapons;
using HarmonyLib;

namespace GHPCHitCam
{
    /// guards against two ways one bad round can break all shooting in GHPC:
    ///  - impact effect / decal databases throw if a projectile isn't in their cache
    ///  - LiveRoundBatchHandler updates new rounds without a try/catch, one exception there and that list is never
    ///    flushed again, so every round fired after it is stuck (never flies)
    internal static class Safety
    {
        static AccessTools.FieldRef<LiveRoundBatchHandler, List<LiveRound>> _unborn;

        public static void Install(HarmonyLib.Harmony h)
        {
            var fin = new HarmonyMethod(typeof(Safety).GetMethod(nameof(LookupFinalizer), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var m in new[]
            {
                AccessTools.Method(typeof(ImpactDecalsDatabaseScriptable), "TryGetCachedImpactDecalData"),
                AccessTools.Method(typeof(ImpactEffectsDatabaseScriptable), "TryGetCachedImpactEffectData"),
            })
                if (m != null) h.Patch(m, finalizer: fin);
            // every round records the pose of every unit in the scene when it starts; one broken unit (say an infantry squad
            // without its weapons) threw and every round fired after that died at the muzzle
            // (GHPC.Weapons.ShotInfo: game also has an old ShotInfo in the global namespace, without this method)
            var poses = AccessTools.Method(typeof(GHPC.Weapons.ShotInfo), "PopulateUnitPoses");
            if (poses != null) h.Patch(poses, finalizer: new HarmonyMethod(typeof(Safety).GetMethod(nameof(PosesFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
            else HitCamMod.Log.Warning("Pose guard not installed: no ShotInfo.PopulateUnitPoses in this game version");
            try
            {
                _unborn = AccessTools.FieldRefAccess<LiveRoundBatchHandler, List<LiveRound>>("_unbornRounds");
                var upd = AccessTools.Method(typeof(LiveRoundBatchHandler), "Update");
                if (upd != null) h.Patch(upd, prefix: new HarmonyMethod(typeof(Safety).GetMethod(nameof(BatchPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { HitCamMod.Log.Warning("Round batch guard not installed: " + e.Message); }
        }

        /// projectile not in the cache: just no decal / effect instead of an exception mid-flight
        static Exception LookupFinalizer(Exception __exception, ref bool __result)
        {
            if (__exception is KeyNotFoundException || __exception is IndexOutOfRangeException || __exception is ArgumentOutOfRangeException)
            {
                __result = false;
                return null;
            }
            return __exception;
        }

        static bool _posesWarned;

        static Exception PosesFinalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!_posesWarned) { _posesWarned = true; HitCamMod.Log.Warning("A unit in this mission is broken (the game couldn't record its pose for a shot): rounds fly on regardless. " + __exception.GetType().Name); }
            return null;
        }

        static void BatchPrefix(LiveRoundBatchHandler __instance)
        {
            if (_unborn == null) return;
            var list = _unborn(__instance);
            if (list != null && list.Count > 0) list.RemoveAll(r => r == null);
        }
    }
}
