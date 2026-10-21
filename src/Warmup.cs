using System;
using System.Collections.Generic;
using GHPC;
using GHPC.Player;
using GHPC.Utility;
using GHPC.Weapons;
using GHPC.World;
using GHPC.Mission;
using UnityEngine;

namespace GHPCHitCam
{
    /// Work the hit cam would otherwise do in the frame your round hits, done beforehand for the vehicle in your sight:
    /// the parts of the scene that belong to it, its armour colliders, what a snapshot of it copies, and its crew models
    /// baked in their pose (the hit takes the bake over if nobody has moved since). Every other vehicle of the mission
    /// gets the same minus the crew bakes, in the background. One small step a frame, and none in a frame that is
    /// already slow. On the proving ground every target is warmed up the same way.
    internal static class Warmup
    {
        static readonly LinkedList<Action> _steps = new LinkedList<Action>();
        static readonly HashSet<Unit> _queued = new HashSet<Unit>();
        static Unit _aim;
        static float _nextLook, _aimSince, _nextScan;
        static AmmoType _ammo;

        public static void Reset()
        {
            _steps.Clear();
            _queued.Clear();
            _light.Clear();
            _aim = null;
            _ammo = null;
            _nextScan = 0f;
            _missionWarm = false;
        }

        static bool _missionWarm;

        /// A unit another mod has just rebuilt: what was read off it goes, and it's warmed up again from scratch.
        public static void Forget(Unit u)
        {
            if (u == null) return;
            GhostSnapshot.Forget(u);
            WorldDamage.Forget(u);
            _queued.Remove(u);
            _light.Remove(u);
        }

        public static void Update()
        {
            if (!Settings.Enabled.Value && !AutoTest.Active) return;
            // vehicle mods rebuild the mission's vehicles as it starts: nothing is read off them before that's done
            if (!ModdedVehicles.MissionConverted) return;
            float now = Time.unscaledTime;
            // once a mission: the explosion sound's tables (the game's rounds and events), read on the first blast otherwise
            if (!_missionWarm && SceneUtil.Player != null && SceneUtil.Player.CurrentPlayerUnit != null)
            {
                _missionWarm = true;
                _steps.AddLast(() => { using (Perf.T("Warmup/sound tables")) BlastSound.Prewarm(); });
            }
            if (now >= _nextLook)
            {
                _nextLook = now + 0.25f;
                var u = Aimed();
                // the vehicle in the sight: first in line. Again every couple of seconds while it stays there (a hit takes
                // its crew bakes over, and the crew may have moved)
                if (u != null && (u != _aim || now - _aimSince > 2f)) { _aim = u; _aimSince = now; Plan(u, true); }
                // the game's model of the round you have loaded (the flying round in the hit cam)
                var a = LoadedAmmo();
                if (a != null && a != _ammo) { _ammo = a; _steps.AddFirst(() => { using (Perf.T("Warmup/round model")) ShellModels.Prewarm(a); }); }
            }
            // every vehicle of the mission, behind the aimed one (new arrivals every few seconds)
            if (_missionWarm && now >= _nextScan)
            {
                _nextScan = now + 5f;
                try
                {
                    var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit as Unit : null;
                    foreach (var x in SceneUnitsManager.Instance.AllUnitsInScene)
                        if (x is Unit unit && unit != me && !unit.Destroyed && unit.Type != UnitType.Infantry) PlanLight(unit);
                }
                catch { }
            }
            if (_steps.Count == 0 || Time.unscaledDeltaTime > 0.04f) return;
            var step = _steps.First.Value;
            _steps.RemoveFirst();
            try { using (Perf.T("Warmup")) step(); }
            catch (Exception e) { HitCamMod.Log.Warning("Warm-up: " + e.Message); }
        }

        /// Queue a vehicle's warm-up: in front of the others (aimed at) or behind them (the proving ground's targets).
        public static void Plan(Unit u, bool first = false)
        {
            if (u == null || (!first && !_queued.Add(u))) return;
            var steps = new List<Action>
            {
                () => { using (Perf.T("Warmup/roots")) GhostSnapshot.Roots(u); },
                () => { using (Perf.T("Warmup/armour")) WorldDamage.HullColliders(u); },
                () => { using (Perf.T("Warmup/x-ray models")) GhostSnapshot.AarVisualsOf(u); },
                () => { using (Perf.T("Warmup/snapshot plan")) GhostSnapshot.PlanOf(u); },
                () => { foreach (var mf in WorldDamage.UnitMeshes(u)) if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable) PlanMesh(mf.sharedMesh, first); },
                () => { if (u != null && Settings.DetailedCrew.Value) foreach (var r in GhostSnapshot.CrewSkins(u)) PlanBake(r, first); },
                () => { if (first) using (Perf.T("Warmup/breakable meshes")) foreach (var m in BreakableMeshes(u)) MeshCopy.Prewarm(m); },   // the one in the sight only (memory)
            };
            if (first) for (int i = steps.Count - 1; i >= 0; i--) _steps.AddFirst(Guard(u, steps[i]));
            else foreach (var s in steps) _steps.AddLast(Guard(u, s));
        }

        /// The rest of the mission: what a snapshot needs found, no crew bakes (they'd be stale by the time it's hit).
        static readonly HashSet<Unit> _light = new HashSet<Unit>();
        static void PlanLight(Unit u)
        {
            if (u == null || _queued.Contains(u) || !_light.Add(u)) return;
            _steps.AddLast(Guard(u, () => { using (Perf.T("Warmup/roots")) GhostSnapshot.Roots(u); }));
            _steps.AddLast(Guard(u, () => { using (Perf.T("Warmup/armour")) WorldDamage.HullColliders(u); }));
            _steps.AddLast(Guard(u, () => { using (Perf.T("Warmup/x-ray models")) GhostSnapshot.AarVisualsOf(u); }));
            _steps.AddLast(Guard(u, () => { using (Perf.T("Warmup/snapshot plan")) GhostSnapshot.PlanOf(u); }));
        }

        /// The meshes a hit can break apart (tracks, the gun, a helicopter's body and boom): read back from the GPU ahead,
        /// asynchronously, so the break itself doesn't stall on it.
        static List<Mesh> BreakableMeshes(Unit u)
        {
            var list = new List<Mesh>();
            bool air = u.Type == UnitType.AirVehicle;
            foreach (var root in WorldDamage.UnitRoots(u))
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (list.Count >= 24) return list;
                    if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                    string n = r.name.ToLowerInvariant();
                    bool part = n.Contains("track") || n.Contains("tread") || n.Contains("gun") || n.Contains("barrel") || n.Contains("cannon");
                    if (!part && !(air && r.bounds.size.magnitude < 24f)) continue;
                    var m = PartKit.MeshOf(r);
                    if (m != null && m.vertexCount >= 8 && !list.Contains(m)) list.Add(m);
                }
            return list;
        }

        static AmmoType LoadedAmmo()
        {
            try
            {
                var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit as Unit : null;
                var gun = me != null ? Arsenal.MainGun(me) : null;
                return gun != null ? gun.CurrentAmmoType : null;
            }
            catch { return null; }
        }

        static void PlanMesh(Mesh m, bool first)
        {
            Action a = () => { if (m != null) using (Perf.T("Warmup/mesh grid")) MeshCutter.Prepare(m); };
            if (first) _steps.AddFirst(a); else _steps.AddLast(a);
        }

        static void PlanBake(SkinnedMeshRenderer r, bool first)
        {
            Action a = () => { if (r != null) using (Perf.T("Warmup/crew bake")) GhostSnapshot.Prebake(r); };
            if (first) _steps.AddFirst(a); else _steps.AddLast(a);
        }

        static Action Guard(Unit u, Action a) => () => { if (u != null) a(); };

        /// What the player's view (the gunner's sight, or whatever camera is on) is pointing at.
        static Unit Aimed()
        {
            var cam = GHPC.Camera.CameraManager.MainCam;
            var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit as Unit : null;
            if (cam == null || me == null) return null;
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 5000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return null;
            var u = CodeUtils.GetParentUnitFollowAware(hit.collider.transform) as Unit;
            return u != null && u != me ? u : null;
        }
    }
}
