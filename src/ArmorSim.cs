using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GHPC;
using GHPC.Mission;
using GHPC.Mission.Data;
using GHPC.PhysicsHelpers;
using GHPC.Player;
using GHPC.Utility;
using GHPC.Weapons;
using GHPC.World;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Camera = UnityEngine.Camera;

namespace GHPCHitCam
{
    /// Protection analysis mode (Insert), in the spirit of War Thunder's: any vehicle the game has, spawned passive on an empty
    /// patch of the map, an orbit camera around its x-ray copy, a pen map of the whole visible armour for the chosen round
    /// and range (PenProbe: the game's own LOS thickness and pen rules), and click to fire a real game round at that spot
    /// from that direction. The hit cam then plays the result full screen; afterwards the vehicle respawns undamaged.
    /// Only GHPC's own data: unit prefabs, armour colliders, IArmor thickness, ammo stats, the game's ballistics.
    internal static class ArmorSim
    {
        enum Phase { Off, Spawning, View, Firing, Replay }
        static Phase _phase = Phase.Off;
        public static bool Active => _phase != Phase.Off;
        /// An Armor Sim shot is on its way or playing: the hit cam renders full screen and we draw its overlay.
        public static bool FullScreenHitCam => _phase == Phase.Firing || _phase == Phase.Replay;

        static MelonPreferences_Entry<string> _keyPref;
        static MelonPreferences_Entry<float> _budgetPref;
        internal static MelonPreferences_Entry<string> KeyPref => _keyPref;

        class VehicleEntry { public string Key, Name, Army; public UnitClass Class; }
        static readonly List<VehicleEntry> _vehicles = new List<VehicleEntry>();
        static readonly string[] Fallback = { "T72M1", "T80B", "BMP2", "BTR70", "M113", "M1IP", "LEO2A3", "BRDM2" };
        static int _vehSel = -1, _ammoSel = -1;
        static float _range = 1000f;
        static bool _xray, _penMap = true, _autoRepair = true;

        // the test vehicle and its x-ray copy
        static Unit _unit;
        static Vector3 _spot;
        static Quaternion _spotRot = Quaternion.identity;
        static bool _haveSpot;
        static GhostSnapshot _snap;
        static Ghost _ghost;
        static Vector3 _origin;          // ghost space = the vehicle's local space shifted to here
        static Camera _cam;
        static Transform _backdrop;
        static float _yaw = 35f, _pitch = 14f, _dist = -1f;
        static int _gen;                 // bumps on every (re)spawn, stale coroutines stop
        static Playback _replay;
        static float _fireAt;
        static LiveRound _round;   // the last round fired (for the log)
        static string _msg;
        static float _msgUntil;

        // what the cursor points at (updated every frame after the camera moved)
        static bool _aimOk;
        static PenResult _aim;
        static Vector3 _aimOrigin, _aimDir;
        static float _aimNominal = -1f;
        static Vector2? _testCursor;     // the self-test aims without a mouse

        // the last shot, for the result card
        static string _lastTitle, _lastAim;
        static Color _lastColor = Color.white;
        static readonly List<(string text, int sev)> _lastEvents = new List<(string, int)>();

        // game state we take over and give back
        static bool _savedGun, _savedCam, _savedKb, _savedCursor, _forcedEnabled, _forcedAllShots, _haveSaved;
        static CursorLockMode _savedLock;
        static MethodInfo _setGun, _setCam;

        // LiveRound internals: a round fired just in front of the plate behaves as if it came from the chosen range
        static AccessTools.FieldRef<LiveRound, Vector3> _trueInitRef, _initPosRef;
        static AccessTools.FieldRef<LiveRound, float> _timeRef;
        static readonly Dictionary<LiveRound, Vector3> _muzzles = new Dictionary<LiveRound, Vector3>();

        // ------------------------------------------------------------------ setup

        public static void Init(HarmonyLib.Harmony h)
        {
            _keyPref = Settings.Cat.CreateEntry("ArmorSimKey", "Insert", description: "Opens the Armor Sim: pick a vehicle and a round, see where it gets through, click to fire.");
            _budgetPref = Settings.Cat.CreateEntry("ArmorSimMapBudgetMs", 6f, description: "Armor Sim: time per frame spent drawing the pen map (higher = the map is ready sooner, but frames are slower while it draws).");

            try
            {
                _trueInitRef = AccessTools.FieldRefAccess<LiveRound, Vector3>("_trueInitialPosition");
                _initPosRef = AccessTools.FieldRefAccess<LiveRound, Vector3>("_initialPosition");
                _timeRef = AccessTools.FieldRefAccess<LiveRound, float>("_timeTraveled");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Armor Sim: LiveRound fields not found (" + e.Message + "), shots act as point blank"); }
            ArmorSimMenu.Init();
            _setGun = AccessTools.PropertySetter(typeof(PlayerInput), "SuspendGunnerInputs");
            _setCam = AccessTools.PropertySetter(typeof(PlayerInput), "SuspendCameraInputs");
            var start = AccessTools.Method(typeof(LiveRound), "Start");
            if (start != null)
                h.Patch(start, postfix: new HarmonyMethod(typeof(ArmorSim).GetMethod(nameof(StartPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        /// A round fired just in front of its target that the game should treat as fired from `muzzle`, `range` m back
        /// (the proving ground's shot lab).
        internal static void VirtualMuzzle(LiveRound round, Vector3 muzzle, float range, AmmoType ammo)
        {
            _muzzles[round] = muzzle;
            if (_timeRef != null) _timeRef(round) = range / Mathf.Max(ammo.MuzzleVelocity, 1f);
        }

        /// The round's Start() sets where it was fired from (fuze arming distance, the hit distance): our virtual muzzle.
        static void StartPostfix(LiveRound __instance)
        {
            if (_muzzles.Count == 0 || !_muzzles.TryGetValue(__instance, out var m)) return;
            _muzzles.Remove(__instance);
            try
            {
                if (_trueInitRef != null) _trueInitRef(__instance) = m;
                if (_initPosRef != null) _initPosRef(__instance) = m;
            }
            catch { }
        }

        /// The mission is gone: forget everything (the objects went with the scene).
        public static void Reset()
        {
            if (Active) Exit("mission ended");
            _vehicles.Clear();
            _haveSpot = false;
            _litCache.Clear();
        }

        static Unit PlayerUnit => SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
        static UnitSpawner Spawner() => UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
        static IReadOnlyList<Arsenal.Entry> Ammo => Arsenal.Entries;
        static Arsenal.Entry SelAmmo => Ammo.Count > 0 ? Ammo[Mathf.Clamp(_ammoSel, 0, Ammo.Count - 1)] : null;
        static Faction Own => PlayerUnit != null ? PlayerUnit.Allegiance : Faction.Blue;

        static void Say(string m, float secs = 4f) { _msg = m; _msgUntil = Time.unscaledTime + secs; HitCamMod.Log.Msg("Armor Sim: " + m); }

        // ------------------------------------------------------------------ enter / leave

        static void Enter()
        {
            if (Active) return;
            if (SceneUtil.IsGameplayGone() || SceneUtil.Player == null) { Say("start a mission first"); return; }
            var sp = Spawner();
            if (sp == null) { Say("this mission has no unit spawner"); return; }
            Vis.Init();
            if (_vehicles.Count == 0) BuildVehicleList(sp);
            if (Ammo.Count == 0) { Say("no projectiles found"); return; }
            // first: the M1IP, a vehicle of the game every install has and the vehicle mods leave alone
            if (_vehSel < 0 || _vehSel >= _vehicles.Count)
            {
                _vehSel = _vehicles.FindIndex(v => v.Key == "M1IP");
                if (_vehSel < 0) _vehSel = Mathf.Max(0, _vehicles.FindIndex(v => v.Key == "T72M1"));
            }
            if (_ammoSel < 0 || _ammoSel >= Ammo.Count) _ammoSel = DefaultAmmo();
            HitCamPlayer.StopCurrent();
            _lastTitle = null;
            _lastEvents.Clear();
            _phase = Phase.Spawning;
            TakeOverGame();
            EnsureCamera();
            HitCamMod.Log.Msg($"Armor Sim opened: {_vehicles.Count} vehicles, {Ammo.Count} projectiles");
            MelonCoroutines.Start(SpawnFlow());
        }

        public static bool ReadyForShots => _phase == Phase.View;
        static bool _askLeave;   // the "back to main menu?" question is up

        /// The main menu's ARMOR SIM button: the mission is up, open the sim.
        public static void OpenFromMenu() => Enter();

        /// Close, or (when the main menu button started this mission) ask whether to go back to the main menu.
        public static void RequestClose()
        {
            if (!Active) return;
            if (ArmorSimMenu.FromMenu) _askLeave = true;
            else Exit("closed");
        }

        public static void LeaveToMainMenu()
        {
            Exit("back to the main menu");
            ArmorSimMenu.BackToMainMenu();
        }

        static void Exit(string why)
        {
            _askLeave = false;
            if (!Active) return;
            if (_replay != null && HitCamPlayer.Current == _replay) HitCamPlayer.StopCurrent();
            _replay = null;
            _gen++;
            bool had = _unit != null;
            Despawn(_unit);
            _unit = null;
            DestroyGhost();
            if (_cam != null) UnityEngine.Object.Destroy(_cam.gameObject);
            _cam = null;
            _backdrop = null;
            if (_viewRT != null) { _viewRT.Release(); UnityEngine.Object.Destroy(_viewRT); _viewRT = null; }
            _muzzles.Clear();
            _phase = Phase.Off;
            RestoreGame();
            HitCamMod.Log.Msg("Armor Sim closed (" + why + ")" + (had ? ", test vehicle removed" : ""));
        }

        static void TakeOverGame()
        {
            var pi = SceneUtil.Player;
            _haveSaved = pi != null;
            if (pi != null) { _savedGun = pi.SuspendGunnerInputs; _savedCam = pi.SuspendCameraInputs; _savedKb = pi.BlockKeyboardInput; }
            _savedCursor = Cursor.visible;
            _savedLock = Cursor.lockState;
            // the hit cam has to record and play our shots even if the player switched it off
            _forcedEnabled = !Settings.Enabled.Value;
            if (_forcedEnabled) Settings.Enabled.Value = true;
            _forcedAllShots = PlayerUnit == null && Settings.PlayerShotsOnly.Value;
            if (_forcedAllShots) Settings.PlayerShotsOnly.Value = false;
            HoldGame();
        }

        /// The game's pause menu is up: the sim steps aside (no drawing, no input) until it closes.
        static bool MenuOpen()
        {
            try { var pi = SceneUtil.Player; return pi != null && pi.IsMainMenuActive; }
            catch { return false; }
        }

        /// Every frame: the tank's own gun / camera / driving inputs stay off and the cursor stays free.
        static void HoldGame()
        {
            var pi = SceneUtil.Player;
            if (pi != null)
            {
                try
                {
                    if (!pi.SuspendGunnerInputs) _setGun?.Invoke(pi, new object[] { true });
                    if (!pi.SuspendCameraInputs) _setCam?.Invoke(pi, new object[] { true });
                }
                catch { }
                pi.BlockKeyboardInput = true;
            }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        static void RestoreGame()
        {
            var pi = SceneUtil.Player;
            if (pi != null && _haveSaved)
            {
                try { _setGun?.Invoke(pi, new object[] { _savedGun }); _setCam?.Invoke(pi, new object[] { _savedCam }); } catch { }
                pi.BlockKeyboardInput = _savedKb;
            }
            _haveSaved = false;
            Cursor.visible = _savedCursor;
            Cursor.lockState = _savedLock;
            if (_forcedEnabled) Settings.Enabled.Value = false;
            if (_forcedAllShots) Settings.PlayerShotsOnly.Value = true;
            _forcedEnabled = _forcedAllShots = false;
        }

        // ------------------------------------------------------------------ catalogue

        static bool IsVehicleClass(UnitClass c) =>
            c == UnitClass.Tank || c == UnitClass.IFV || c == UnitClass.APC || c == UnitClass.Scout || c == UnitClass.Transport
            || c == UnitClass.FireSupport || c == UnitClass.Command || c == UnitClass.MobileArtillery || c == UnitClass.MobileAntiAir;

        /// Every ground vehicle in the game's unit prefab table (the same keys UnitSpawner takes).
        static void BuildVehicleList(UnitSpawner sp)
        {
            _vehicles.Clear();
            try
            {
                var all = sp.PrefabLookup != null ? sp.PrefabLookup.AllUnits : null;
                if (all != null)
                    foreach (var m in all)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Name) || !IsVehicleClass(m.Class)) continue;
                        if (m.Name == "TC") continue;   // the easter-egg cheese
                        if (!ModdedVehicles.Spawnable(m)) continue;   // a lookup-only entry (no prefab), e.g. FEP's binocular fix
                        string army = "";
                        try { army = m.Army != null && m.Army.Info != null ? m.Army.Info.Abbreviation ?? "" : ""; } catch { }
                        _vehicles.Add(new VehicleEntry { Key = m.Name, Name = string.IsNullOrEmpty(m.FriendlyName) ? m.Name : m.FriendlyName, Army = army, Class = m.Class });
                    }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Armor Sim: unit table not readable: " + e.Message); }
            if (_vehicles.Count == 0)
                foreach (var k in Fallback) _vehicles.Add(new VehicleEntry { Key = k, Name = k, Army = "", Class = UnitClass.Tank });
            _vehicles.Sort((a, b) => { int c = string.Compare(a.Army, b.Army, StringComparison.Ordinal); return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            HitCamMod.Log.Msg("Armor Sim vehicles: " + string.Join(", ", _vehicles.Select(v => v.Key + "=" + v.Name + "/" + v.Army + "/" + v.Class)));
        }

        static bool Sub(AmmoType a) => a.Category == AmmoType.AmmoCategory.Penetrator && a.Radius * 2000f < a.GetFinalCaliber() * 0.6f;

        static int DefaultAmmo() => PickAmmo("apfsds");

        static int PickAmmo(string want)
        {
            var list = Ammo;
            int best = -1;
            float bestPen = -1f;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i].Ammo;
                bool game = list[i].Group.StartsWith("Game");
                bool ok;
                switch ((want ?? "").ToLowerInvariant())
                {
                    case "apfsds": ok = game && Sub(a) && a.GetFinalCaliber() >= 90f; break;
                    case "ap": ok = game && a.Category == AmmoType.AmmoCategory.Penetrator && !Sub(a) && a.GetFinalCaliber() >= 70f; break;
                    case "heat": ok = game && a.Category == AmmoType.AmmoCategory.ShapedCharge && a.Guidance == 0; break;
                    case "atgm": ok = game && a.Category == AmmoType.AmmoCategory.ShapedCharge && a.Guidance != 0; break;
                    case "he": ok = game && a.Category == AmmoType.AmmoCategory.Explosive && !list[i].Bomb && a.GetFinalCaliber() >= 70f; break;
                    default: ok = (a.Name ?? "").IndexOf(want ?? "", StringComparison.OrdinalIgnoreCase) >= 0; if (ok) return i; break;
                }
                float score = want == "he" ? a.TntEquivalentKg : a.RhaPenetration;
                if (ok && score > bestPen) { bestPen = score; best = i; }
            }
            return best >= 0 ? best : 0;
        }

        // ------------------------------------------------------------------ the test vehicle

        static IEnumerator SpawnFlow()
        {
            int gen = ++_gen;
            _phase = Phase.Spawning;
            DestroyGhost();
            if (_unit != null) { Despawn(_unit); _unit = null; yield return null; }
            if (gen != _gen || !Active) yield break;
            if (!_haveSpot) _haveSpot = FindSpot(out _spot, out _spotRot);
            var v = _vehicles[Mathf.Clamp(_vehSel, 0, _vehicles.Count - 1)];
            var u = SpawnUnit(v.Key);
            if (u == null) { Say("could not spawn " + v.Name); _phase = Phase.View; yield break; }
            _unit = u;
            // let Start() / AI startup run before stopping the AI
            yield return null;
            yield return null;
            if (gen != _gen || !Active) yield break;
            // what vehicle mods make of it at a mission's start (their variants, ERA, armour)
            string stock = v.Name;
            yield return ModdedVehicles.Convert(u);
            if (gen != _gen || !Active || u == null) yield break;
            string now = Recorder.SafeName(u);
            if (ModdedVehicles.LastFailures.Count > 0) Say($"{now}: {string.Join(", ", ModdedVehicles.LastFailures)} couldn't convert it here (that mod gets ready only for the mission's own vehicle types)", 8f);
            else if (ModdedVehicles.Any && now != stock && !string.IsNullOrEmpty(now)) Say($"{stock} -> {now} (a vehicle mod's conversion)", 6f);
            MakePassive(u);
            // settle on the suspension (the x-ray copy and the colliders must agree)
            var rb = u.GetComponent<Rigidbody>() ?? u.GetComponentInChildren<Rigidbody>();
            float t0 = Time.time;
            while (Time.time - t0 < 4f && (Time.time - t0 < 1.2f || (rb != null && rb.velocity.sqrMagnitude > 0.004f)))
            {
                yield return null;
                if (gen != _gen || !Active || _unit == null) yield break;
            }
            BuildGhost();
            _phase = Phase.View;
        }

        static Unit SpawnUnit(string key)
        {
            var sp = Spawner();
            if (sp == null) return null;
            try
            {
                Vector3 pos = CodeUtils.GetPositionOnTerrain(_spot + Vector3.up * 50f, out _, 4000f);
                pos = CodeUtils.GetTerrainAwarePosition(pos);
                Quaternion rot = CodeUtils.GetTerrainAwareRotation(pos, _spotRot * Vector3.forward);
                var md = new UnitMetaData
                {
                    Name = key + " (armor sim)",
                    Position = pos,
                    Rotation = rot,
                    // nobody's enemy: the game's target sensors skip neutral units, so no AI shoots at it
                    Allegiance = Faction.Neutral,
                    UnitType = UnitType.GroundVehicle,
                };
                var u = ModdedVehicles.Spawn(sp, key, md) as Unit;
                if (u == null) return null;
                u.Targetable = true;
                try { SceneUnitsManager.Instance.Meta?.UnimportantUnits?.Add(u); } catch { }   // never decides the mission
                HitCamMod.Log.Msg($"Armor Sim: spawned {key} at {pos} ({Recorder.SafeName(u)})");
                return u;
            }
            catch (Exception e)
            {
                HitCamMod.Log.Warning($"Armor Sim: spawn {key} failed: {e.Message}");
                return null;
            }
        }

        static void MakePassive(Unit u)
        {
            if (u == null) return;
            try
            {
                var ai = u.InfoBroker != null ? u.InfoBroker.AI : null;
                if (ai != null) { ai.Stop(); ai.enabled = false; }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Armor Sim: could not stop the AI of " + Recorder.SafeName(u) + ": " + e.Message); }
        }

        /// Takes the unit out of the game's unit lists and destroys it with the parts that follow it (turrets, ...).
        internal static void Despawn(Unit u)
        {
            if (u == null) return;
            try
            {
                var roots = new List<Transform> { u.transform };
                foreach (var lf in UnityEngine.Object.FindObjectsOfType<LateFollow>(true))
                    if (lf != null && (lf.ParentUnit as Unit) == u && !lf.transform.IsChildOf(u.transform)) roots.Add(lf.transform);
                var sum = SceneUnitsManager.Instance;
                if (sum != null)
                {
                    (sum.AllUnitsInScene as List<Unit>)?.Remove(u);
                    try { sum.Meta?.UnimportantUnits?.Remove(u); } catch { }
                    // parts registered for the game's own AAR replays (AarMiscPosableItem) never unregister: a destroyed one
                    // left in this list makes ShotInfo.PopulateUnitPoses throw in every later LiveRound.Start, and that round
                    // then never moves (no hit, no hit cam) for the rest of the mission
                    var misc = sum.AllMiscPosablesInScene;
                    if (misc != null)
                    {
                        var gone = misc.Where(t => t == null || roots.Any(r => r != null && t.IsChildOf(r))).ToList();
                        if (gone.Count > 0)
                        {
                            misc.RemoveAll(t => gone.Contains(t));
                            HitCamMod.Log.Msg($"Armor Sim: unregistered {gone.Count} AAR posable part(s) of {Recorder.SafeName(u)}: " + string.Join(", ", gone.Select(t => t != null ? t.name : "<destroyed>")));
                        }
                    }
                }
                foreach (var l in SceneUnitsManager.AllUnitsByFaction) l?.Remove(u);
                foreach (var l in SceneUnitsManager.AllLiveUnitsByFaction) l?.Remove(u);
                foreach (var r in roots)
                    if (r != null) { r.gameObject.SetActive(false); UnityEngine.Object.Destroy(r.gameObject); }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Armor Sim: despawn failed: " + e.Message); }
        }

        /// A flat open spot far from every unit of the mission (the fight goes on elsewhere).
        static bool FindSpot(out Vector3 pos, out Quaternion rot)
        {
            rot = Quaternion.identity;
            var others = new List<Vector3>();
            try { foreach (var u in SceneUnitsManager.Instance.AllUnitsInScene) if (u != null) others.Add(u.transform.position); } catch { }
            var player = PlayerUnit;
            Vector3 home = player != null ? player.transform.position : others.Count > 0 ? others[0] : Vector3.zero;
            var cands = new List<(Vector3 p, Terrain t, float score, float dmin)>();
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 tp = t.GetPosition(), ts = t.terrainData.size;
                const int n = 28;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        float fx = 0.1f + 0.8f * i / (n - 1), fz = 0.1f + 0.8f * j / (n - 1);
                        var p = new Vector3(tp.x + ts.x * fx, 0f, tp.z + ts.z * fz);
                        p.y = t.SampleHeight(p) + tp.y;
                        float steep = t.terrainData.GetSteepness(fx, fz);
                        if (steep > 8f) continue;
                        float dmin = 1e9f;
                        foreach (var o in others) dmin = Mathf.Min(dmin, Flat(o - p));
                        float score = Mathf.Min(dmin, 1800f) - steep * 40f - Flat(home - p) * 0.05f;
                        cands.Add((p, t, score, dmin));
                    }
            }
            foreach (var c in cands.OrderByDescending(c => c.score).Take(24))
                if (Clear(c.p, c.t))
                {
                    pos = c.p;
                    HitCamMod.Log.Msg($"Armor Sim: test spot {pos}, {c.dmin:0} m from the nearest unit");
                    return true;
                }
            if (cands.Count > 0) { pos = cands.OrderByDescending(c => c.score).First().p; return true; }
            // no terrain object: 1.5 km out in front of the player
            Vector3 fwd = player != null ? Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized : Vector3.forward;
            pos = CodeUtils.GetPositionOnTerrain(home + fwd * 1500f + Vector3.up * 1000f, out _, 4000f);
            return true;
        }

        static float Flat(Vector3 d) => new Vector2(d.x, d.z).magnitude;

        /// No trees, buildings or rocks within ~10 m (terrain trees show as a hit well above the ground).
        static bool Clear(Vector3 p, Terrain t)
        {
            for (int k = 0; k < 13; k++)
            {
                Vector3 q = p + (k == 0 ? Vector3.zero : Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward * (k % 2 == 0 ? 10f : 5f));
                if (!Physics.Raycast(q + Vector3.up * 80f, Vector3.down, out var hit, 200f, ~(1 << Vis.Layer), QueryTriggerInteraction.Ignore)) return false;
                if (hit.collider.gameObject.layer != CodeUtils.LAYER_INDEX_TERRAIN) return false;
                float ground = t.SampleHeight(q) + t.GetPosition().y;
                if (hit.point.y > ground + 0.8f) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ x-ray copy + camera

        class Swap { public Renderer R; public Material[] Orig, Lit; }
        static readonly List<Swap> _swaps = new List<Swap>();
        static readonly Dictionary<Texture, Material> _litCache = new Dictionary<Texture, Material>();
        static Material _litGrey, _backMat, _shadowMat;
        static Mesh _backMesh, _shadowMesh;

        static void BuildGhost()
        {
            DestroyGhost();
            if (_unit == null) return;
            try
            {
                _snap = GhostSnapshot.Capture(_unit);
                // far from where the hit cam puts its own copy (350 m above the target), so they never share a picture
                _origin = _unit.transform.position + new Vector3(0f, 350f, -1200f);
                _ghost = Ghost.Build(_snap, _origin);
                PrepareLook();
                ApplyMode();
                if (_dist <= 0f) FrameCamera(35f, 14f);
                ResetPenMap();
                HitCamMod.Log.Msg($"Armor Sim: x-ray copy of {_snap.UnitName}: {_ghost.Hull.Count} hull parts, {_snap.Modules.Count} modules, {_snap.Crew.Count} crew, bounds {_snap.LocalBounds.size}");
            }
            catch (Exception e) { HitCamMod.Log.Error("Armor Sim: x-ray copy failed: " + e); }
        }

        static void DestroyGhost()
        {
            _swaps.Clear();
            _ghost?.Destroy();
            _ghost = null;
            _snap = null;
            _aimOk = false;
        }

        /// Solid view: the vehicle's own textures, lit; x-ray view: the hit cam's look (textures faint, internals on top).
        static void PrepareLook()
        {
            _swaps.Clear();
            var diffuse = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Standard");
            if (diffuse == null) return;
            if (_litGrey == null) _litGrey = new Material(diffuse) { color = new Color(0.42f, 0.44f, 0.4f), renderQueue = 2002, name = "ArmorSim grey" };
            foreach (var mr in _ghost.Hull)
            {
                if (mr == null) continue;
                var skin = mr.transform.Find("skin");
                Renderer target = skin != null ? skin.GetComponent<MeshRenderer>() : null;
                if (target != null) _swaps.Add(new Swap { R = target, Orig = target.sharedMaterials, Lit = target.sharedMaterials.Select(m => Lit(m, diffuse)).ToArray() });
                else _swaps.Add(new Swap { R = mr, Orig = mr.sharedMaterials, Lit = mr.sharedMaterials.Select(_ => _litGrey).ToArray() });
            }
            // a soft shadow under the vehicle so it doesn't float
            var b = _snap.LocalBounds;
            _shadowMesh ??= ShadowDisc();
            _shadowMat ??= new Material(Vis.Alpha) { renderQueue = 1990, name = "ArmorSim shadow" };
            Vis.Child(_ghost.Root.transform, "armorsim shadow", _shadowMesh,
                Matrix4x4.TRS(new Vector3(b.center.x, b.min.y + 0.03f, b.center.z), Quaternion.Euler(90f, 0f, 0f), new Vector3(b.size.x * 1.7f, b.size.z * 1.35f, 1f)), _shadowMat);
        }

        static Material Lit(Material m, Shader diffuse)
        {
            var tex = m != null ? m.mainTexture : null;
            if (tex == null) return _litGrey;
            if (_litCache.TryGetValue(tex, out var lit) && lit != null) return lit;
            lit = new Material(diffuse) { mainTexture = tex, color = new Color(0.95f, 0.95f, 0.95f), renderQueue = 2002, name = "ArmorSim lit hull" };
            try { lit.mainTextureScale = m.mainTextureScale; lit.mainTextureOffset = m.mainTextureOffset; } catch { }
            _litCache[tex] = lit;
            return lit;
        }

        static void ApplyMode()
        {
            if (_ghost == null) return;
            foreach (var s in _swaps) if (s.R != null) s.R.sharedMaterials = _xray ? s.Orig : s.Lit;
            if (_xray) _ghost.SetSkin(Mathf.Clamp(Settings.HullTexture.Value, 0.15f, 0.6f), false);
            foreach (var t in new[] { _ghost.ModRoot, _ghost.CrewRoot, _ghost.AmmoRoot })
                if (t != null)
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = !_xray;
        }

        static void EnsureCamera()
        {
            if (_cam != null) return;
            var go = new GameObject("ArmorSim Camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.cullingMask = 1 << Vis.Layer;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.10f, 0.11f, 0.12f);
            _cam.fieldOfView = 38f;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 160f;
            _cam.depth = -40f;
            _cam.useOcclusionCulling = false;
            _cam.allowHDR = false;
            _cam.allowMSAA = true;
            _cam.renderingPath = RenderingPath.Forward;
            // a dark studio backdrop that follows the camera (drawn first, behind everything)
            _backMesh ??= Backdrop();
            _backMat ??= new Material(Vis.Unlit) { renderQueue = 1000, name = "ArmorSim backdrop" };
            _backMat.SetInt("_ZWrite", 0);
            var bd = Vis.Child(go.transform, "armorsim backdrop", _backMesh, Matrix4x4.TRS(new Vector3(0f, 0f, 150f), Quaternion.identity, new Vector3(420f, 150f, 1f)), _backMat);
            _backdrop = bd.transform;
            EnsureViewRT();
        }

        /// The sim's picture: rendered off screen and drawn by our IMGUI pass, over the game's own canvases.
        static RenderTexture _viewRT;

        static void EnsureViewRT()
        {
            if (_cam == null) return;
            int w = Mathf.Max(Screen.width, 64), h = Mathf.Max(Screen.height, 64);
            if (_viewRT != null && _viewRT.width == w && _viewRT.height == h) return;
            if (_viewRT != null) { _cam.targetTexture = null; _viewRT.Release(); UnityEngine.Object.Destroy(_viewRT); }
            _viewRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 2, name = "ArmorSim view" };
            _viewRT.Create();
            _cam.targetTexture = _viewRT;
        }

        static Mesh Backdrop()
        {
            var m = new Mesh { name = "armorsim backdrop" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            Color lo = new Color(0.055f, 0.06f, 0.065f), hi = new Color(0.20f, 0.22f, 0.245f);
            m.colors = new[] { lo, lo, hi, hi };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateBounds();
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            return m;
        }

        static Mesh ShadowDisc()
        {
            const int n = 32;
            var v = new Vector3[n + 1];
            var c = new Color[n + 1];
            var t = new int[n * 3];
            v[0] = Vector3.zero;
            c[0] = new Color(0f, 0f, 0f, 0.6f);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                v[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.5f;
                c[i + 1] = new Color(0f, 0f, 0f, 0f);
                t[i * 3] = 0; t[i * 3 + 1] = (i + 1) % n + 1; t[i * 3 + 2] = i + 1;
            }
            var m = new Mesh { name = "armorsim shadow", vertices = v, colors = c, triangles = t };
            m.RecalculateBounds();
            return m;
        }

        static void FrameCamera(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = pitch;
            float size = _snap != null ? _snap.LocalBounds.size.magnitude : 8f;
            _dist = Mathf.Clamp(size * 1.25f, 4f, 30f);
            _pan = Vector3.zero;
        }

        static Vector3 Focus => _origin + (_snap != null ? _snap.LocalBounds.center : Vector3.up) + _pan;
        static Vector3 _pan;          // the middle mouse button moves the view off the vehicle's middle
        static bool _panning;
        static float _lastMiddle = -9f;

        static void PlaceCamera()
        {
            float y = _yaw * Mathf.Deg2Rad, p = _pitch * Mathf.Deg2Rad;
            var off = new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
            _cam.transform.position = Focus + off * _dist;
            _cam.transform.LookAt(Focus, Vector3.up);
            _cam.cullingMask = 1 << Vis.Layer;    // the hit cam strips its layer from every enabled camera when it starts
        }

        // ------------------------------------------------------------------ probing the real vehicle through the copy

        /// A ray of our camera (ghost space) laid onto the real vehicle, started just outside its outline.
        static bool ProbeView(Ray gr, out PenResult r, out Vector3 originW, out Vector3 dirW)
        {
            r = default;
            originW = dirW = Vector3.zero;
            var e = SelAmmo;
            if (_unit == null || _snap == null || e == null) return false;
            Bounds lb = _snap.LocalBounds;
            lb.Expand(3f);
            Vector3 lo = gr.origin - _origin, ld = gr.direction;
            if (!lb.IntersectRay(new Ray(lo, ld), out float tIn)) return false;
            Matrix4x4 m = _unit.transform.localToWorldMatrix;
            originW = m.MultiplyPoint3x4(lo + ld * Mathf.Max(tIn, 0f));
            dirW = m.MultiplyVector(ld).normalized;
            bool ok = PenProbe.Probe(originW, dirW, lb.size.magnitude + 1f, e.Ammo, null, Own, out r, default, e.Bomb ? 0f : _range);
            return ok && r.Hit && r.Unit == _unit;
        }

        static float NominalAt(PenResult r, Vector3 dirW, AmmoType ammo)
        {
            try
            {
                if (RaycastColliderUtils.Raycast(new Ray(r.Point - dirW * 0.05f, dirW), out var hit, 0.12f, PenProbe.Mask) && hit.collider.TryGetComponent(out IArmor a))
                    return a.GetNormalThickness(ammo.Category == AmmoType.AmmoCategory.ShapedCharge ? ResistanceType.CE : ResistanceType.KE, hit.point, hit.normal, false);
            }
            catch { }
            return -1f;
        }

        // pen map: a cell grid over the screen, filled coarse to fine within a time budget per frame, cached per view
        static Texture2D _penTex;
        static Color32[] _penPix;
        static bool[] _penExact;
        static int[] _penOrder;
        static int _penPos, _penW, _penH, _penCell;
        static Vector3 _penCamPos, _penUnitPos;
        static Quaternion _penCamRot, _penUnitRot;
        static int _penAmmo = -1, _penGen = -1;
        static float _penRange = -1f;
        static bool _penXray;
        static double _penMs;
        static int _penProbes;
        static readonly int[] _penCount = new int[8];
        public static bool PenMapDone => _penOrder != null && _penPos >= _penOrder.Length;

        static void ResetPenMap()
        {
            _penCell = Mathf.Max(4, Mathf.RoundToInt(Screen.height / 150f));
            int w = Mathf.CeilToInt(Screen.width / (float)_penCell), h = Mathf.CeilToInt(Screen.height / (float)_penCell);
            if (_penTex == null || _penW != w || _penH != h)
            {
                if (_penTex != null) UnityEngine.Object.Destroy(_penTex);
                _penW = w; _penH = h;
                _penTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "ArmorSim pen map" };
                _penPix = new Color32[w * h];
                _penExact = new bool[w * h];
                var order = new List<int>(w * h);
                for (int lvl = 0; lvl < 3; lvl++)
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int l = x % 4 == 0 && y % 4 == 0 ? 0 : x % 2 == 0 && y % 2 == 0 ? 1 : 2;
                            if (l == lvl) order.Add(y * w + x);
                        }
                _penOrder = order.ToArray();
            }
            for (int i = 0; i < _penPix.Length; i++) { _penPix[i] = new Color32(0, 0, 0, 0); _penExact[i] = false; }
            _penPos = 0;
            _penMs = 0;
            _penProbes = 0;
            Array.Clear(_penCount, 0, _penCount.Length);
            if (_cam != null) { _penCamPos = _cam.transform.position; _penCamRot = _cam.transform.rotation; }
            if (_unit != null) { _penUnitPos = _unit.transform.position; _penUnitRot = _unit.transform.rotation; }
            _penAmmo = _ammoSel;
            _penRange = _range;
            _penGen = _gen;
            _penXray = _xray;
            _penTex.SetPixels32(_penPix);
            _penTex.Apply(false);
        }

        static bool PenMapStale()
        {
            if (_penOrder == null || _cam == null || _unit == null) return true;
            return (_cam.transform.position - _penCamPos).sqrMagnitude > 1e-6f || Quaternion.Angle(_cam.transform.rotation, _penCamRot) > 0.01f
                || (_unit.transform.position - _penUnitPos).sqrMagnitude > 1e-4f || Quaternion.Angle(_unit.transform.rotation, _penUnitRot) > 0.05f
                || _penAmmo != _ammoSel || Mathf.Abs(_penRange - _range) > 0.5f || _penGen != _gen || _penXray != _xray
                || _penW != Mathf.CeilToInt(Screen.width / (float)_penCell) || _penH != Mathf.CeilToInt(Screen.height / (float)_penCell);
        }

        static void StepPenMap()
        {
            if (PenMapStale()) ResetPenMap();
            if (PenMapDone) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double budget = Mathf.Clamp(_budgetPref.Value, 1f, 40f);
            if (AutoTest.Active) budget = 25.0;   // the self-test just wants the picture
            Bounds gb = _snap.LocalBounds;
            gb.Expand(3f);
            gb.center += _origin;
            byte alpha = (byte)(_xray ? 105 : 130);
            bool changed = false;
            while (_penPos < _penOrder.Length && sw.Elapsed.TotalMilliseconds < budget)
            {
                int idx = _penOrder[_penPos++];
                int cx = idx % _penW, cy = idx / _penW;
                int block = cx % 4 == 0 && cy % 4 == 0 ? 4 : cx % 2 == 0 && cy % 2 == 0 ? 2 : 1;
                var ray = _cam.ScreenPointToRay(new Vector3((cx + 0.5f) * _penCell, (cy + 0.5f) * _penCell, 0f));
                Color32 c = new Color32(0, 0, 0, 0);
                if (gb.IntersectRay(ray))
                {
                    _penProbes++;
                    if (ProbeView(ray, out var r, out _, out _) && r.Verdict != PenVerdict.NotArmour && r.Verdict != PenVerdict.None)
                    {
                        Color k = PenProbe.ColorOf(r.Verdict);
                        c = new Color32((byte)(k.r * 255), (byte)(k.g * 255), (byte)(k.b * 255), alpha);
                        if (block == 1) _penCount[(int)r.Verdict]++;   // the finest pass: 3/4 of the cells, an even sample
                    }
                }
                for (int by = cy; by < Math.Min(cy + block, _penH); by++)
                    for (int bx = cx; bx < Math.Min(cx + block, _penW); bx++)
                    {
                        int j = by * _penW + bx;
                        if (j == idx || !_penExact[j]) _penPix[j] = c;
                    }
                _penExact[idx] = true;
                changed = true;
            }
            _penMs += sw.Elapsed.TotalMilliseconds;
            if (changed)
            {
                _penTex.SetPixels32(_penPix);
                _penTex.Apply(false);
            }
            if (PenMapDone && AutoTest.Active)
                HitCamMod.Log.Msg($"ARMORSIM pen map: {_penW}x{_penH} cells of {_penCell} px, {_penProbes} probes in {_penMs:0} ms; pen {_penCount[(int)PenVerdict.Pen]}, marginal {_penCount[(int)PenVerdict.Maybe]}, no pen {_penCount[(int)PenVerdict.NoPen]}, ricochet {_penCount[(int)PenVerdict.Ricochet]}");
        }

        // ------------------------------------------------------------------ per frame

        static Vector2 CursorScreen => _testCursor ?? (Vector2)Input.mousePosition;
        static Vector2 _lastMouse;
        static bool _dragging;

        public static void Update()
        {
            if (Hotkeys.Down(_keyPref))
            {
                if (_askLeave) _askLeave = false;
                else if (Active) RequestClose();
                else Enter();
            }
            if (!Active) return;
            if (SceneUtil.IsGameplayGone()) { Exit("mission gone"); return; }
            if (MenuOpen()) return;
            HoldGame();
            switch (_phase)
            {
                case Phase.View:
                    ViewInput();
                    break;
                case Phase.Firing:
                {
                    var cur = HitCamPlayer.Current;
                    if (cur != null && _unit != null && cur.Rec.Target == _unit) BeginReplay(cur);
                    else if (Time.unscaledTime - _fireAt > 6f)
                    {
                        Say("no hit recorded (a miss, or a machine-gun round, under 15 mm)");
                        var last = Recorder.LastRecord;
                        HitCamMod.Log.Warning($"Armor Sim: no hit cam for the shot; round at {(_round != null ? _round.transform.position.ToString() : "-")} (destroyed {(_round == null || _round.IsDestroyed)}), "
                            + $"last record: {(last != null ? last.TargetName + " '" + last.Title + "' ready " + last.Ready + (last.Target == _unit ? " (ours)" : "") : "none")}, hit cam playing: {HitCamPlayer.Current != null}");
                        _phase = Phase.View;
                    }
                    break;
                }
                case Phase.Replay:
                    if (HitCamPlayer.Current != _replay) EndReplay();
                    else if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape)) HitCamPlayer.StopCurrent();
                    break;
            }
        }

        static bool OverUi(Vector2 screen)
        {
            var g = ToUi(screen);
            foreach (var r in _uiRects) if (r.Contains(g)) return true;
            return false;
        }

        static void ViewInput()
        {
            // the self-test drives the camera and fires on its own: a wheel or a click over the windowed game must not
            // zoom its screenshots or fire an extra round
            if (AutoTest.Active || _askLeave || ConfigMenu.IsOpen) { _dragging = _panning = false; return; }
            Vector2 mouse = Input.mousePosition;
            Vector2 d = mouse - _lastMouse;
            _lastMouse = mouse;
            if (Input.GetMouseButtonDown(1) && !OverUi(mouse)) _dragging = true;
            if (!Input.GetMouseButton(1)) _dragging = false;
            if (Input.GetMouseButtonDown(2) && !OverUi(mouse))
            {
                _panning = true;
                if (Time.unscaledTime - _lastMiddle < 0.3f) _pan = Vector3.zero;   // a double click: back to the vehicle's middle
                _lastMiddle = Time.unscaledTime;
            }
            if (!Input.GetMouseButton(2)) _panning = false;
            // the game may hold the cursor still while a button is down: then the mouse's own movement
            if ((_dragging || _panning) && d.sqrMagnitude < 0.01f)
                try { d = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 14f; } catch { }
            if (_dragging) { _yaw += d.x * 0.3f; _pitch -= d.y * 0.25f; }
            if (_panning && _cam != null)
            {
                float size = _snap != null ? _snap.LocalBounds.size.magnitude : 8f;
                _pan -= (_cam.transform.right * d.x + _cam.transform.up * d.y) * (_dist * 0.0016f);
                _pan = Vector3.ClampMagnitude(_pan, size * 0.6f);
            }
            float dt = Time.unscaledDeltaTime;
            if (Input.GetKey(KeyCode.LeftArrow)) _yaw -= 70f * dt;
            if (Input.GetKey(KeyCode.RightArrow)) _yaw += 70f * dt;
            if (Input.GetKey(KeyCode.UpArrow)) _pitch += 45f * dt;
            if (Input.GetKey(KeyCode.DownArrow)) _pitch -= 45f * dt;
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f && !OverUi(mouse))
            {
                float size = _snap != null ? _snap.LocalBounds.size.magnitude : 8f;
                _dist = Mathf.Clamp(_dist * (1f - wheel * 0.1f), size * 0.45f, size * 3f);
            }
            _pitch = Mathf.Clamp(_pitch, -8f, 85f);
            _yaw = Mathf.Repeat(_yaw, 360f);
            if (Input.GetMouseButtonDown(0) && !OverUi(mouse) && _picker != 0) _picker = 0;   // a click outside closes the list
            else if (Input.GetMouseButtonDown(0) && !OverUi(mouse))
            {
                if (_aimOk) Fire();
                else Say("point at the vehicle's armour to fire", 2f);
            }
        }

        public static void LateUpdate()
        {
            if (!Active || _cam == null) return;
            if (_phase == Phase.Replay) return;
            EnsureViewRT();
            if (_ghost == null || _snap == null) { _aimOk = false; return; }
            PlaceCamera();
            // what the cursor points at
            var ray = _cam.ScreenPointToRay(CursorScreen);
            _aimOk = _phase == Phase.View && !OverUi(CursorScreen) && ProbeView(ray, out _aim, out _aimOrigin, out _aimDir)
                && _aim.Verdict != PenVerdict.NotArmour && _aim.Verdict != PenVerdict.None;
            _aimNominal = _aimOk ? NominalAt(_aim, _aimDir, SelAmmo.Ammo) : -1f;
            if (_penMap && _phase == Phase.View) StepPenMap();
        }

        // ------------------------------------------------------------------ firing

        static void Fire()
        {
            var e = SelAmmo;
            if (e == null || _unit == null) return;
            Arsenal.PrepareForFire(e);
            var ammo = e.Ammo;
            float range = e.Bomb ? 0f : _range;
            // start in front of the first surface on the camera ray, so the round goes exactly where the probe looked
            Vector3 dir = _aimDir;
            float back = Mathf.Max(_aim.Distance, 1.5f);
            // a longer run-up where the way back along the ray is clear: the hit cam shows the last metres of flight
            Vector3 o = _aim.Point - dir * back;
            if (!Physics.Raycast(o, -dir, out var block, 8f, ~(1 << Vis.Layer), QueryTriggerInteraction.Ignore)) back += 8f;
            else back += Mathf.Max(0f, block.distance - 0.5f);
            Vector3 start = _aim.Point - dir * back;
            float v = e.Bomb ? ammo.MuzzleVelocity : PenProbe.SpeedAtRange(ammo, range);
            _lastAim = $"{_aim.Plate}: LOS {_aim.FirstLos:0} mm, {_aim.Los:0} mm to the crew ({_aim.Plates} layer{(_aim.Plates == 1 ? "" : "s")}), {90f - _aim.Angle:0}° off normal; {ammo.Name} pen {_aim.Pen:0} mm at {range:0} m -> {VerdictText(_aim.Verdict)}";
            // a destroyed AAR posable anywhere in the scene would stop the round in its Start() (see Despawn)
            try { SceneUnitsManager.Instance?.AllMiscPosablesInScene?.RemoveAll(t => t == null); } catch { }
            try
            {
                var round = LiveRoundUtility.GetNewLiveRound(ammo);
                _round = round;
                round.Info = ammo;
                round.Guided = false;
                round.UseGravity = false;
                round.CurrentSpeed = v;
                round.MaxSpeed = ammo.MuzzleVelocity;
                round.Shooter = PlayerUnit;
                round.transform.position = start;
                round.transform.rotation = Quaternion.LookRotation(dir);
                _muzzles[round] = start - dir * range;
                bool arsenal = Arsenal.On;
                Arsenal.On = false;   // the arsenal would swap the projectile of a round the player's tank "fires"
                try { round.Init(null, _unit); }
                finally { Arsenal.On = arsenal; }
                // nutation (pen penalty over the first metres) and fuze timing see the flight from the virtual muzzle
                if (_timeRef != null) _timeRef(round) = range / Mathf.Max(ammo.MuzzleVelocity, 1f);
            }
            catch (Exception ex)
            {
                HitCamMod.Log.Error("Armor Sim: fire failed: " + ex);
                Say("could not fire " + ammo.Name);
                return;
            }
            HitCamMod.Log.Msg("Armor Sim shot: " + _lastAim);
            _fireAt = Time.unscaledTime;
            _phase = Phase.Firing;
        }

        static void BeginReplay(Playback p)
        {
            _replay = p;
            _phase = Phase.Replay;
            if (_ghost?.Root != null) _ghost.Root.SetActive(false);
            if (_cam != null) _cam.enabled = false;
        }

        static void EndReplay()
        {
            var p = _replay;
            _replay = null;
            if (p != null)
            {
                _lastTitle = p.TitleShown ?? p.Rec.Title;
                _lastColor = p.TitleColor;
                _lastEvents.Clear();
                foreach (var (e, _) in p.Shown.OrderBy(s => s.wall)) _lastEvents.Add((e.Text, e.Severity));
                HitCamMod.Log.Msg("Armor Sim result: " + _lastTitle + " | " + string.Join(" / ", _lastEvents.Select(x => x.text)));
            }
            if (_cam != null) _cam.enabled = true;
            if (_autoRepair) { MelonCoroutines.Start(SpawnFlow()); return; }
            // keep the damage: a fresh x-ray copy shows the destroyed modules and crew
            if (_ghost?.Root != null) _ghost.Root.SetActive(true);
            BuildGhost();
            _phase = Phase.View;
        }

        static string VerdictText(PenVerdict v)
        {
            switch (v)
            {
                case PenVerdict.Pen: return "PENETRATION LIKELY";
                case PenVerdict.Maybe: return "PENETRATION POSSIBLE";
                case PenVerdict.NoPen: return "NO PENETRATION";
                case PenVerdict.Ricochet: return "RICOCHET";
                default: return "NO ARMOUR";
            }
        }

        // ------------------------------------------------------------------ UI

        static GUIStyle _box, _lbl, _hd, _small, _btn, _sel, _big, _tip, _chip;
        static Texture2D _white, _ring, _boxTex;
        static readonly List<Rect> _uiRects = new List<Rect>();
        static int _picker;          // 0 none, 1 vehicles, 2 projectiles

        /// Self-test: the vehicle list open (its mod tags), and the view moved off the middle as the middle button does.
        internal static void TestPicker(bool on)
        {
            _picker = on ? 1 : 0;
            if (!on) return;
            int lookupOnly = _vehicles.Count(v => v.Key.IndexOf("BINOCULAR", StringComparison.OrdinalIgnoreCase) >= 0);
            var tagged = _vehicles.Where(v => ModdedVehicles.IsModded(v.Key)).Select(v => v.Name + ModdedVehicles.Tag(v.Key)).ToList();
            HitCamMod.Log.Msg($"ARMORSIM TEST: vehicle list {_vehicles.Count} entries, {lookupOnly} lookup-only, {tagged.Count} tagged: " + string.Join(", ", tagged));
        }
        internal static void TestPan(float right, float up) { if (_cam != null) _pan = _cam.transform.right * right + _cam.transform.up * up; }
        static Vector2 _scroll;

        static void Styles()
        {
            if (_box != null) return;
            _white = Texture2D.whiteTexture;
            _boxTex = new Texture2D(1, 1);
            _boxTex.SetPixel(0, 0, new Color(0.05f, 0.06f, 0.07f, 0.88f));
            _boxTex.Apply();
            _box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8) };
            _box.normal.background = _boxTex;
            _lbl = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false };
            _lbl.normal.textColor = new Color(0.86f, 0.88f, 0.9f);
            _small = new GUIStyle(_lbl) { fontSize = 12, wordWrap = true };
            _small.normal.textColor = new Color(0.65f, 0.68f, 0.72f);
            _hd = new GUIStyle(_lbl) { fontSize = 15, fontStyle = FontStyle.Bold };
            _hd.normal.textColor = new Color(1f, 0.8f, 0.3f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 12, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            _sel = new GUIStyle(_btn) { fontStyle = FontStyle.Bold };
            _sel.normal.textColor = _sel.hover.textColor = new Color(1f, 0.9f, 0.45f);
            _big = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _tip = new GUIStyle(_lbl) { fontSize = 13, wordWrap = false, richText = true };
            _chip = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _ring = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float r = new Vector2(x - 31.5f, y - 31.5f).magnitude;
                    float a = Mathf.Clamp01(1f - Mathf.Abs(r - 26f) / 2.2f) + (r < 2.2f ? 1f : 0f);
                    _ring.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
                }
            _ring.Apply();
        }

        // the UI is laid out on a virtual screen of W x H (1080 px high or more, War Thunder's baseline) and scaled up by K
        static float K = 1f, W = 1600f, H = 900f;

        /// Screen pixels (y up) to UI coordinates.
        static Vector2 ToUi(Vector2 screen) => new Vector2(screen.x / K, (Screen.height - screen.y) / K);

        public static void Draw()
        {
            if (!Active || MenuOpen()) return;
            Styles();
            K = Mathf.Clamp(Screen.height / 1080f, 1f, 2.4f);
            W = Screen.width / K;
            H = Screen.height / K;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(K, K, 1f));
            try { DrawScaled(); }
            finally { GUI.matrix = oldMatrix; }
        }

        static void DrawScaled()
        {
            if (Event.current.type == EventType.Layout) _uiRects.Clear();
            if (_phase == Phase.Replay && _replay != null) { DrawReplay(_replay); return; }

            // our own view, drawn by IMGUI: that comes after every canvas, so the game's HUD, map and loading screens stay
            // under it untouched
            if (_viewRT != null) GUI.DrawTexture(new Rect(0, 0, W, H), _viewRT, ScaleMode.StretchToFill, false);
            if (_penMap && _penTex != null && _phase == Phase.View && _ghost != null)
                GUI.DrawTexture(new Rect(0, 0, _penW * _penCell / K, _penH * _penCell / K), _penTex, ScaleMode.StretchToFill, true);

            // the settings window open: only the view under it (its own panels would sit under the window)
            if (ConfigMenu.IsOpen) { _uiRects.Add(new Rect(0, 0, W, H)); return; }
            if (_aimOk && _phase == Phase.View) DrawAim();
            DrawPanel();
            if (_picker != 0) DrawPicker();
            if (_askLeave) DrawLeave();
            if (_lastTitle != null && _phase == Phase.View) DrawLastShot();

            string status = _phase == Phase.Spawning ? "Spawning " + (_vehSel >= 0 && _vehSel < _vehicles.Count ? _vehicles[_vehSel].Name : "") + "..."
                : _phase == Phase.Firing ? "Round on its way..." : null;
            if (status != null) Shadow(new Rect(0, H * 0.5f - 20, W, 40), status, _big, new Color(0.9f, 0.92f, 0.95f, 0.85f));
            if (Time.unscaledTime < _msgUntil && !string.IsNullOrEmpty(_msg))
                Shadow(new Rect(0, H - 44, W, 26), _msg, new GUIStyle(_hd) { alignment = TextAnchor.MiddleCenter }, Color.white);
        }

        static void DrawAim()
        {
            var g = ToUi(CursorScreen);
            var col = PenProbe.ColorOf(_aim.Verdict);
            var old = GUI.color;
            GUI.color = col;
            GUI.DrawTexture(new Rect(g.x - 16, g.y - 16, 32, 32), _ring);
            GUI.color = old;
            var e = SelAmmo;
            string hex = ColorUtility.ToHtmlStringRGB(col);
            var lines = new List<string>
            {
                $"<b>{_aim.Plate}</b>",
                (_aimNominal > 0f ? $"nominal {_aimNominal:0} mm   " : "") + $"impact {90f - _aim.Angle:0}° from normal",
                $"LOS {_aim.FirstLos:0} mm" + (_aim.Plates > 1 ? $"   to the crew {_aim.Los:0} mm ({_aim.Plates} layers)" : ""),
                $"{e.Ammo.Name}: pen {_aim.Pen:0} mm" + (e.Bomb ? "" : $" at {_range:0} m"),
                $"<color=#{hex}><b>{VerdictText(_aim.Verdict)}</b></color>" + (_aim.Verdict == PenVerdict.Ricochet ? $"  (under {_aim.RicochetAngle:0}° grazing)" : _aim.Los > 0.5f ? $"  ({_aim.Ratio:0.00}x)" : ""),
            };
            float w = 0f;
            foreach (var l in lines) w = Mathf.Max(w, _tip.CalcSize(new GUIContent(l)).x);
            float h = lines.Count * 18f + 12f;
            var r = new Rect(g.x + 22, g.y + 18, w + 20, h);
            if (r.xMax > W - 8) r.x = g.x - 22 - r.width;
            if (r.yMax > H - 8) r.y = g.y - 18 - r.height;
            GUI.Box(r, GUIContent.none, _box);
            for (int i = 0; i < lines.Count; i++) GUI.Label(new Rect(r.x + 10, r.y + 6 + i * 18f, w + 4, 20), lines[i], _tip);
        }

        static readonly string[] _views = { "Front", "Side", "Rear", "Left", "Top", "3/4" };

        static void SetView(string v)
        {
            switch (v.ToLowerInvariant())
            {
                case "front": _yaw = 0f; _pitch = 8f; break;
                case "side": case "right": _yaw = 90f; _pitch = 8f; break;
                case "rear": _yaw = 180f; _pitch = 10f; break;
                case "left": _yaw = 270f; _pitch = 8f; break;
                case "top": _yaw = 0f; _pitch = 80f; break;
                default: _yaw = 35f; _pitch = 14f; break;
            }
        }

        static void DrawPanel()
        {
            var area = new Rect(12, 12, 400, H - 24);
            GUILayout.BeginArea(area);
            GUILayout.BeginVertical(_box, GUILayout.Width(400));
            GUILayout.BeginHorizontal();
            GUILayout.Label("ARMOR SIM", _hd);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(ArmorSimMenu.FromMenu ? "Leave (" + Hotkeys.Label(_keyPref) + ")" : "Close (" + Hotkeys.Label(_keyPref) + ")", GUILayout.Width(100))) RequestClose();
            GUILayout.EndHorizontal();

            bool idle = _phase == Phase.View;
            var v = _vehSel >= 0 && _vehSel < _vehicles.Count ? _vehicles[_vehSel] : null;
            GUILayout.Label("Vehicle", _small);
            GUILayout.BeginHorizontal();
            GUI.enabled = idle;
            if (GUILayout.Button("<", GUILayout.Width(26))) PickVehicle((_vehSel - 1 + _vehicles.Count) % _vehicles.Count);
            if (GUILayout.Button(v != null ? $"{v.Name}{ModdedVehicles.Tag(v.Key)}   ({v.Army} {v.Class})" : "?", _picker == 1 ? _sel : _btn)) _picker = _picker == 1 ? 0 : 1;
            if (GUILayout.Button(">", GUILayout.Width(26))) PickVehicle((_vehSel + 1) % _vehicles.Count);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            var e = SelAmmo;
            GUILayout.Label("Projectile", _small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(26))) _ammoSel = (_ammoSel - 1 + Ammo.Count) % Ammo.Count;
            if (GUILayout.Button(e != null ? e.Ammo.Name : "?", _picker == 2 ? _sel : _btn)) _picker = _picker == 2 ? 0 : 2;
            if (GUILayout.Button(">", GUILayout.Width(26))) _ammoSel = (_ammoSel + 1) % Ammo.Count;
            GUILayout.EndHorizontal();
            if (e != null)
            {
                var a = e.Ammo;
                GUILayout.Label($"{e.Group}: {a.GetFinalCaliber():0} mm, {a.MuzzleVelocity:0} m/s, pen {a.RhaPenetration:0} mm" + (a.TntEquivalentKg > 0f ? $", TNT {a.TntEquivalentKg:0.##} kg" : ""), _small);
                if (!e.Bomb)
                {
                    GUILayout.Label($"Range {_range:0} m", _lbl);
                    _range = Mathf.Round(GUILayout.HorizontalSlider(_range, 0f, 4000f) / 50f) * 50f;
                    bool heat = a.Category == AmmoType.AmmoCategory.ShapedCharge || a.Guidance != 0;
                    GUILayout.Label($"impact {PenProbe.SpeedAtRange(a, _range):0} m/s, pen {PenProbe.PenAtRange(a, _range):0} mm RHA" + (heat ? "  (HEAT: same at any range)" : ""), _lbl);
                }
                else GUILayout.Label("Bombs: impact at their own speed, range ignored", _small);
            }

            GUILayout.Space(4);
            bool x = GUILayout.Toggle(_xray, " X-ray (modules, crew, ammo)");
            if (x != _xray) { _xray = x; ApplyMode(); }
            _penMap = GUILayout.Toggle(_penMap, " Pen map");
            _autoRepair = !GUILayout.Toggle(!_autoRepair, " Keep damage between shots");
            GUILayout.BeginHorizontal();
            foreach (var view in _views) if (GUILayout.Button(view)) SetView(view);
            GUILayout.EndHorizontal();
            GUI.enabled = idle;
            if (GUILayout.Button("Respawn / repair")) MelonCoroutines.Start(SpawnFlow());
            GUI.enabled = true;

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            Legend(PenVerdict.Pen, "pen"); Legend(PenVerdict.Maybe, "marginal"); Legend(PenVerdict.NoPen, "no pen"); Legend(PenVerdict.Ricochet, "ricochet");
            GUILayout.EndHorizontal();
            if (_penMap && !PenMapDone && _phase == Phase.View) GUILayout.Label($"pen map {100f * _penPos / Mathf.Max(1, _penOrder?.Length ?? 1):0} %", _small);
            GUILayout.Label("LMB fire at the cursor   RMB drag / arrows orbit   MMB drag move (double click: centre)   wheel zoom   Space skip replay", _small);
            GUILayout.Label("The test vehicle is neutral and passive, far from the fight. The battle keeps running.", _small);
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                var lr = GUILayoutUtility.GetLastRect();
                _panel = new Rect(area.x + lr.x, area.y + lr.y, lr.width, lr.height);
            }
            GUILayout.EndArea();
            _uiRects.Add(_panel);
        }

        static Rect _panel;

        static void Legend(PenVerdict v, string text)
        {
            var r = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14), GUILayout.Height(14));
            r.y += 3;
            var old = GUI.color;
            GUI.color = PenProbe.ColorOf(v);
            GUI.DrawTexture(r, _white);
            GUI.color = old;
            GUILayout.Label(text, _small, GUILayout.ExpandWidth(false));
            GUILayout.Space(6);
        }

        static void PickVehicle(int i)
        {
            if (i < 0 || i >= _vehicles.Count || _phase != Phase.View) return;
            _vehSel = i;
            _lastTitle = null;
            MelonCoroutines.Start(SpawnFlow());
        }

        static void DrawPicker()
        {
            float x = _panel.xMax + 8f, w = 470f;
            var r = new Rect(x, 12f, w, Mathf.Min(H - 24f, 720f));
            _uiRects.Add(r);
            GUILayout.BeginArea(r, _box);
            GUILayout.Label(_picker == 1 ? "VEHICLES (the game's unit table)" : "PROJECTILES (game rounds + generated + bombs)", _hd);
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_picker == 1)
            {
                string army = null;
                for (int i = 0; i < _vehicles.Count; i++)
                {
                    var v = _vehicles[i];
                    if (v.Army != army) { army = v.Army; GUILayout.Label(string.IsNullOrEmpty(army) ? "-" : army, _small); }
                    var oc = GUI.contentColor;
                    if (ModdedVehicles.IsModded(v.Key)) GUI.contentColor = ModdedVehicles.TagColor;
                    bool hit = GUILayout.Button($"{v.Name}{ModdedVehicles.Tag(v.Key)}   {v.Class}   [{v.Key}]", i == _vehSel ? _sel : _btn);
                    GUI.contentColor = oc;
                    if (hit && _phase == Phase.View) { _picker = 0; PickVehicle(i); }
                }
            }
            else
            {
                string grp = null;
                var list = Ammo;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Group != grp) { grp = list[i].Group; GUILayout.Label(grp, _small); }
                    if (GUILayout.Button(list[i].Label, i == _ammoSel ? _sel : _btn)) { _ammoSel = i; _picker = 0; }
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static void DrawLastShot()
        {
            float w = 460f;
            int n = Mathf.Min(_lastEvents.Count, 12);
            float h = 64f + n * 18f + (_lastAim != null ? 36f : 0f);
            var r = new Rect(W - w - 12f, 12f, w, h);
            _uiRects.Add(r);
            GUI.Box(r, GUIContent.none, _box);
            float y = r.y + 8f;
            GUI.Label(new Rect(r.x + 10, y, w - 20, 18), "LAST SHOT", _small);
            y += 18f;
            Shadow(new Rect(r.x + 10, y, w - 20, 28), _lastTitle, new GUIStyle(_hd) { fontSize = 20 }, _lastColor);
            y += 30f;
            if (_lastAim != null) { GUI.Label(new Rect(r.x + 10, y, w - 20, 36), _lastAim, _small); y += 36f; }
            for (int i = 0; i < n; i++)
            {
                var (text, sev) = _lastEvents[i];
                var st = new GUIStyle(_lbl);
                st.normal.textColor = Sev(sev);
                GUI.Label(new Rect(r.x + 10, y, w - 20, 18), "•  " + text, st);
                y += 18f;
            }
        }

        /// Leaving a sim the main menu started: back to the menu, or stay in the (quiet) mission.
        static void DrawLeave()
        {
            float w = 440f, h = 140f;
            var r = new Rect((W - w) * 0.5f, (H - h) * 0.5f, w, h);
            _uiRects.Add(new Rect(0, 0, W, H));   // modal: nothing behind it takes clicks
            Fill(new Rect(0, 0, W, H), new Color(0f, 0f, 0f, 0.45f));
            GUI.Box(r, GUIContent.none, _box);
            GUI.Label(new Rect(r.x + 14, r.y + 12, w - 28, 24), "LEAVE THE ARMOR SIM", _hd);
            GUI.Label(new Rect(r.x + 14, r.y + 40, w - 28, 40), "The main menu opened this mission for the Armor Sim. Go back to the main menu, or stay here and play it?", _small);
            if (GUI.Button(new Rect(r.x + 14, r.y + h - 46, 160, 32), "Back to main menu")) LeaveToMainMenu();
            else if (GUI.Button(new Rect(r.x + 182, r.y + h - 46, 140, 32), "Stay in mission")) Exit("closed, staying in the mission");
            else if (GUI.Button(new Rect(r.x + 330, r.y + h - 46, 96, 32), "Cancel")) _askLeave = false;
        }

        static Color Sev(int s) => s <= 0 ? new Color(0.78f, 0.8f, 0.84f) : s == 1 ? new Color(1f, 0.86f, 0.4f) : s == 2 ? new Color(1f, 0.6f, 0.2f) : new Color(1f, 0.3f, 0.25f);

        static readonly Color Olive = new Color32(0x90, 0x9E, 0x35, 255), Amber = new Color32(0xF6, 0xB2, 0x36, 255), WtRed = new Color32(0xFD, 0x00, 0x01, 255);

        /// The hit cam, full screen: picture, verdict, module / crew row and the damage list.
        static void DrawReplay(Playback p)
        {
            var rt = HitCamPlayer.Texture;
            var full = new Rect(0, 0, W, H);
            if (rt != null) GUI.DrawTexture(full, rt, ScaleMode.ScaleAndCrop, false);
            var old = GUI.color;
            if (p.RedFlash > 0.01f) { GUI.color = new Color(0.85f, 0.08f, 0.05f, 0.45f * p.RedFlash); GUI.DrawTexture(full, _white); }
            GUI.color = old;
            var rec = p.Latest;

            // verdict banner (stays up: this is what the shot was for)
            if (p.TitleWall >= 0f && p.T >= p.TitleWall)
            {
                float ta = Mathf.Clamp01((p.T - p.TitleWall) / 0.15f);
                Fill(new Rect(W * 0.3f, 22f, W * 0.4f, 52f), new Color(0f, 0f, 0f, 0.5f * ta));
                var c = p.TitleColor; c.a = ta;
                Shadow(new Rect(0, 22f, W, 52f), p.TitleShown ?? rec.Title, _big, c);
            }

            // info strip
            Fill(new Rect(0, H - 30f, W, 30f), new Color(0f, 0f, 0f, 0.6f));
            var e = SelAmmo;
            string info = $"ARMOR SIM   {(rec.Ammo != null ? rec.Ammo.Name : "?")}  →  {rec.TargetName}" + (e != null && !e.Bomb ? $"   ·   {_range:0} m   ·   {PenProbe.SpeedAtRange(rec.Ammo, _range):0} m/s   ·   pen {PenProbe.PenAtRange(rec.Ammo, _range):0} mm" : "")
                + "   ·   Space: skip";
            Shadow(new Rect(14, H - 30f, W - 28, 30f), info, _lbl, new Color(0.9f, 0.92f, 0.95f));

            // status chips + event list, bottom left
            var shown = p.Shown.OrderBy(s => s.wall).ToList();
            int max = 9, start = Mathf.Max(0, shown.Count - max);
            float lw = Mathf.Min(640f, W * 0.45f);
            float ly = H - 30f - 10f - (shown.Count - start) * 21f;
            float ry = ly - 30f;
            float cx = 14f;
            foreach (var (cat, label, hp) in p.Debuffs()) { Chip(new Rect(cx, ry, 52f, 22f), label, hp); cx += 56f; }
            var (alive, total) = p.CrewCount();
            if (total > 0) Chip(new Rect(cx, ry, 76f, 22f), $"CREW {alive}/{total}", alive == total ? 100f : alive == 0 ? 0f : alive * 100f / total <= 50f ? 20f : 60f);
            for (int i = start; i < shown.Count; i++)
            {
                var (ev, wall) = shown[i];
                float age = p.T - wall;
                Color c = Color.Lerp(Sev(ev.Severity), Color.white, Mathf.Clamp01(1f - age / 0.35f) * 0.6f);
                c.a = Mathf.Clamp01(age / 0.12f);
                Fill(new Rect(14f, ly, lw, 20f), new Color(0f, 0f, 0f, 0.5f * c.a));
                if (ev.Severity >= 3 && age < 0.6f) Fill(new Rect(14f, ly, lw * Mathf.Clamp01(age / 0.4f), 20f), new Color(0.75f, 0.05f, 0.03f, 0.35f));
                Shadow(new Rect(22f, ly, lw - 16f, 20f), "•  " + ev.Text, _lbl, c);
                ly += 21f;
            }
        }

        static void Chip(Rect r, string label, float hp)
        {
            Color c = hp <= 0f ? new Color(0.12f, 0.12f, 0.12f) : hp <= 25f ? WtRed : hp <= 75f ? Amber : Olive;
            Fill(r, new Color(c.r, c.g, c.b, 0.88f));
            Shadow(r, label, _chip, Color.white);
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        static void Shadow(Rect r, string text, GUIStyle st, Color c)
        {
            var old = st.normal.textColor;
            st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.85f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, st);
            st.normal.textColor = c;
            GUI.Label(r, text, st);
            st.normal.textColor = old;
        }

        // ------------------------------------------------------------------ self-test

        /// Opens the Armor Sim, picks a vehicle and a round, and for each aspect photographs the pen map, the aim tooltip,
        /// fires at the hull and photographs the replay; then closes the mode and checks the test vehicle is gone.
        public static IEnumerator SelfTest(string vehicle, string ammo, string shots, string rangeText, string outDir, bool leaveOpen = false)
        {
            HitCamMod.Log.Msg($"ARMORSIM TEST: vehicle={vehicle} ammo={ammo} shots={shots} range={rangeText}");
            LogCanvases();
            Enter();
            float t0 = Time.realtimeSinceStartup;
            while (_phase != Phase.View && Time.realtimeSinceStartup - t0 < 30f) yield return null;
            if (_phase != Phase.View) { HitCamMod.Log.Warning("ARMORSIM TEST: the mode did not open"); Exit("test failed"); yield break; }
            int vi = _vehicles.FindIndex(x => x.Key.Equals(vehicle, StringComparison.OrdinalIgnoreCase));
            if (vi < 0) vi = _vehicles.FindIndex(x => x.Name.IndexOf(vehicle, StringComparison.OrdinalIgnoreCase) >= 0);
            if (vi >= 0 && vi != _vehSel)
            {
                PickVehicle(vi);
                yield return null;
                t0 = Time.realtimeSinceStartup;
                while (_phase != Phase.View && Time.realtimeSinceStartup - t0 < 30f) yield return null;
            }
            _ammoSel = PickAmmo(ammo);
            if (float.TryParse(rangeText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rg)) _range = Mathf.Clamp(rg, 0f, 4000f);
            var e = SelAmmo;
            HitCamMod.Log.Msg($"ARMORSIM TEST: {_vehicles[_vehSel].Key} ({Recorder.SafeName(_unit)}), {e.Ammo.Name} pen {e.Ammo.RhaPenetration:0} -> {PenProbe.PenAtRange(e.Ammo, _range):0} mm at {_range:0} m ({PenProbe.SpeedAtRange(e.Ammo, _range):0} m/s)");

            var aspects = shots.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            for (int i = 0; i < aspects.Count; i++)
            {
                // "aspect" or "aspect:ammo" (another round for this shot)
                var spec = aspects[i].Split(':');
                string asp = spec[0];
                if (spec.Length > 1) _ammoSel = PickAmmo(spec[1]);
                if (_phase != Phase.View) { HitCamMod.Log.Warning("ARMORSIM TEST: not ready for shot " + i); break; }
                HitCamMod.Log.Msg($"ARMORSIM TEST shot {i}: {asp} with {SelAmmo.Ammo.Name} (arming distance {SelAmmo.Ammo.ArmingDistance:0} m)");
                SetView(asp);
                _testCursor = null;
                yield return null;
                yield return null;
                t0 = Time.realtimeSinceStartup;
                while (!PenMapDone && Time.realtimeSinceStartup - t0 < 20f) yield return null;
                yield return null;
                Capture(outDir, $"armorsim{i}_{asp}_penmap.png");
                yield return null;
                yield return null;

                // aim at the hull: the middle of the vehicle at ~45 % of its height, else the nearest armour cell
                var b = _snap.LocalBounds;
                Vector3 sp = _cam.WorldToScreenPoint(_origin + new Vector3(b.center.x, b.min.y + b.size.y * 0.42f, b.center.z));
                _testCursor = new Vector2(sp.x, sp.y);
                yield return null;
                if (!_aimOk) _testCursor = NearestArmourCell(new Vector2(sp.x, sp.y));
                yield return null;
                yield return null;
                if (!_aimOk) { HitCamMod.Log.Warning($"ARMORSIM TEST: shot {i} ({asp}): no armour to aim at"); continue; }
                DumpRay(_aimOrigin, _aimDir);
                HitCamMod.Log.Msg($"ARMORSIM TEST shot {i} ({asp}): aim {_aim.Plate} LOS {_aim.FirstLos:0} total {_aim.Los:0} ({_aim.Plates}) angle {90f - _aim.Angle:0} nominal {_aimNominal:0} pen {_aim.Pen:0} -> {_aim.Verdict}");
                Capture(outDir, $"armorsim{i}_{asp}_aim.png");
                yield return null;
                yield return null;

                Fire();
                t0 = Time.realtimeSinceStartup;
                while (_phase == Phase.Firing && Time.realtimeSinceStartup - t0 < 8f) yield return null;
                if (_phase != Phase.Replay || _replay == null) { HitCamMod.Log.Warning($"ARMORSIM TEST: shot {i}: no replay"); yield return new WaitForSecondsRealtime(1f); continue; }
                var p = _replay;
                float[] at = { p.A * 0.85f, p.A + Mathf.Max(p.Dwell, 0.2f) * 0.6f, p.A + p.B * 0.55f, p.Duration - 0.6f };
                for (int k = 0; k < at.Length; k++)
                {
                    while (_replay == p && p.T < at[k]) yield return null;
                    if (_replay != p) break;
                    Capture(outDir, $"armorsim{i}_{asp}_replay{k}.png");
                    yield return null;
                }
                while (_replay == p) yield return null;
                HitCamMod.Log.Msg($"ARMORSIM TEST shot {i} ({asp}) result: {_lastTitle} | " + string.Join(" / ", _lastEvents.Select(x => x.text)));
                t0 = Time.realtimeSinceStartup;
                while (_phase != Phase.View && Time.realtimeSinceStartup - t0 < 30f) yield return null;
                HitCamMod.Log.Msg($"ARMORSIM TEST: respawned {Recorder.SafeName(_unit)} (phase {_phase})");
            }
            _testCursor = new Vector2(4f, 4f);   // away from the vehicle: no tooltip
            if (leaveOpen) yield break;
            SetView("3/4");
            _penMap = false;
            yield return null;
            yield return null;
            Capture(outDir, "armorsim_solid.png");
            yield return null;
            yield return null;
            _penMap = true;
            yield return null;
            t0 = Time.realtimeSinceStartup;
            while (!PenMapDone && Time.realtimeSinceStartup - t0 < 20f) yield return null;
            _xray = true;
            ApplyMode();
            yield return null;
            t0 = Time.realtimeSinceStartup;
            while (!PenMapDone && Time.realtimeSinceStartup - t0 < 20f) yield return null;
            Capture(outDir, "armorsim_xray.png");
            yield return null;
            yield return null;
            _picker = 2;
            yield return null;
            yield return null;
            Capture(outDir, "armorsim_picker.png");
            yield return null;
            yield return null;
            _picker = 0;
            var u = _unit;
            Exit("self-test done");
            yield return null;
            yield return null;
            bool gone = u == null;
            int inScene = 0;
            try { inScene = SceneUnitsManager.Instance.AllUnitsInScene.Count(x => x != null && x.name.Contains("(armor sim)")); } catch { }
            var main = WorldDamage.MainCamera();
            HitCamMod.Log.Msg($"ARMORSIM TEST: closed; test vehicle destroyed={gone}, armor sim units left in the scene list={inScene}, sim camera gone={_cam == null}, game camera={(main != null ? main.name + " enabled=" + main.enabled : "none")}, cursor visible={Cursor.visible}");
            yield return new WaitForSecondsRealtime(1.5f);
            Capture(outDir, "armorsim_after_exit.png");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        static Vector2? NearestArmourCell(Vector2 screen)
        {
            if (_penOrder == null) return null;
            float best = float.MaxValue;
            Vector2? at = null;
            for (int i = 0; i < _penPix.Length; i++)
            {
                if (!_penExact[i] || _penPix[i].a == 0) continue;
                var c = new Vector2((i % _penW + 0.5f) * _penCell, (i / _penW + 0.5f) * _penCell);
                float d = (c - screen).sqrMagnitude;
                if (d < best) { best = d; at = c; }
            }
            return at;
        }

        static void Capture(string dir, string name)
        {
            try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, name)); HitCamMod.Log.Msg("ARMORSIM TEST: captured " + name); }
            catch (Exception e) { HitCamMod.Log.Warning("ARMORSIM TEST: capture failed: " + e.Message); }
        }

        static void LogCanvases()
        {
            try
            {
                var cs = UnityEngine.Object.FindObjectsOfType<Canvas>().Where(c => c.isRootCanvas).ToList();
                HitCamMod.Log.Msg("ARMORSIM TEST canvases: " + string.Join(", ", cs.Select(c => $"{c.name}/{c.renderMode}/{(c.enabled && c.gameObject.activeInHierarchy ? "on" : "off")}")));
                HitCamMod.Log.Msg("ARMORSIM TEST cameras: " + string.Join(", ", Camera.allCameras.Select(c => $"{c.name} d{c.depth} rt={(c.targetTexture != null)}")));
            }
            catch { }
        }

        /// Everything a real round meets along the aim ray (names, tags, armour, modules), for the log.
        static void DumpRay(Vector3 o, Vector3 d)
        {
            try
            {
                RaycastColliderUtils.Raycast(new Ray(o, d), out _, 20f, PenProbe.Mask);
                var hits = Physics.RaycastAll(o, d, 20f, PenProbe.Mask & ~RaycastColliderUtils.GROUP_LAYER_MASK, QueryTriggerInteraction.Collide).OrderBy(h => h.distance).Take(24);
                var type = SelAmmo.Ammo.Category == AmmoType.AmmoCategory.ShapedCharge ? ResistanceType.CE : ResistanceType.KE;
                var parts = new List<string>();
                foreach (var h in hits)
                {
                    var c = h.collider;
                    string s = $"{h.distance:0.00}m {c.name}[{LayerMask.LayerToName(c.gameObject.layer)}{(c.isTrigger ? ",trigger" : "")},{c.tag}]";
                    if (c.TryGetComponent(out IArmor a)) { float los = -1; try { los = a.GetLosThickness(type, h.point, h.normal, d, false); } catch { } s += $" armor '{a.Name}' los {los:0}{(a is GHPC.Equipment.DestructibleComponent ? " (module)" : "")}{(a.IsEra ? " ERA" : "")}"; }
                    if (c.TryGetComponent(out ProxyHitZone z)) s += $" zone{(z.Compartment ? " compartment" : "")}{(z.DestructibleComponent ? " module" : "")}";
                    parts.Add(s);
                }
                HitCamMod.Log.Msg("ARMORSIM ray: " + string.Join(" | ", parts));
            }
            catch (Exception e) { HitCamMod.Log.Warning("ARMORSIM ray dump failed: " + e.Message); }
        }
    }
}
