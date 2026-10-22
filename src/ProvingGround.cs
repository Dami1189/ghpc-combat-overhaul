using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GHPC;
using GHPC.AI;
using GHPC.Mission;
using GHPC.Mission.Data;
using GHPC.Player;
using GHPC.Utility;
using GHPC.Weapons;
using GHPC.World;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace GHPCHitCam
{
    /// PROVING GROUND (main menu button, or Alt+Home in any mission): Arma 3's VR showcase meets WT's test drive.
    /// flat grey grid deck floating above the map (nothing of the mission on it), rows of targets every 100 m down range
    /// (front-on, side-on, three-quarter), two hovering helis, the player in a vehicle of their choice on the firing line, and
    /// a tools window (F4): respawn targets + a shot lab that fires any round at any target from any direction at the speed
    /// it has at a picked range, with a close-up hit cam following the round through the plates (how a long rod erodes,
    /// bends and breaks)
    internal static class ProvingGround
    {
        public static MelonPreferences_Entry<bool> MenuButton, RodCamPref, BigCamPref;
        static MelonPreferences_Entry<string> _keyPref;
        static MelonPreferences_Entry<int> _rowsPref, _perRowPref;
        static MelonPreferences_Entry<float> _respawnPref;
        static MelonPreferences_Entry<bool> _emptyPref;
        static MelonPreferences_Entry<bool> _briefPref;
        public static MelonPreferences_Entry<int> RowsPref => _rowsPref;
        static bool _autoOpened;              // tools opened by themselves when the range started (the vehicle list)
        static MelonPreferences_Entry<string> _buildPref;

        public static bool Active { get; private set; }
        public static bool FromMenu;
        /// hit cam follows the round itself through the plates (general setting, the proving ground window toggles it)
        public static bool RodCam => ForceRodCam || (RodCamPref != null && RodCamPref.Value);
        public static bool ForceRodCam;   // self-test (rodcam=true) without touching the saved setting
        public static bool BigCam => Active && BigCamPref != null && BigCamPref.Value;
        public static bool WindowOpen => Active && _open;

        static bool _open, _busy;
        static GameObject _deck;
        static Vector3 _origin;          // middle of the firing line, on the deck
        static Faction _mine, _enemy;
        static Unit _vehicle, _missionVehicle;

        const float Spacing = 40f, RowStep = 100f;
        class Slot { public string Key; public int Row, Col; public Vector3 Pos; public Quaternion Rot; public Unit U; public string Facing; public bool Air, Troops; public float DownAt = -1f; }
        static readonly List<Slot> _slots = new List<Slot>();
        static readonly string[] Targets = { "T72M1", "T80B", "M1IP", "LEO2A3", "BMP2", "BTR70", "M113", "BRDM2" };
        public static IEnumerable<string> TargetKeys => Targets;
        static readonly string[] Helis = { "Mi24", "AH1" };
        // your own vehicle and where it starts, targets you placed: kept when the mission restarts
        static string _myKey;
        static float _myRange;
        static bool _myBack;
        static readonly List<(string key, float range, float side, int facing)> _placed = new List<(string, float, float, int)>();
        static string _placeKey = "T72M1";
        static float _placeRange = 300f, _placeSide;
        static int _placeFacing, _tab;
        static int _picking;                  // 0 none, 1 your vehicle, 2 a target to place
        static Vector2 _scroll;
        static List<(string key, string name, string army)> _units;   // every ground vehicle in the game's unit table
        static bool _rebuild;                 // mission is restarting with a proving ground in it: build it again
        static float _hintUntil;
        static float _briefUntil;             // briefing is up (range is new: until the tools get opened)
        static float _loadingSince = -1f;    // range is on its way (menu button, restart, Ctrl+key): a note says so

        /// proving ground is being loaded / built: note on screen until it's ready
        public static void NoteLoading() => _loadingSince = Time.unscaledTime;
        static Texture2D _back;
        static readonly string[] Facings = { "Front", "3/4", "Side", "Rear" };

        // shot lab
        static int _target, _ammoSel = -1, _aimMode, _aspect = 2;
        static float _yaw = 30f, _pitch, _range = 1000f;
        static readonly string[] AimModes = { "Hull", "Turret", "Lower hull", "Turret ring", "Gun barrel" };
        static readonly string[] Aspects = { "Front", "3/4", "Side", "Rear", "Top", "Custom" };
        static string _status = "";
        static Rect _win = new Rect(24f, 140f, 430f, 600f);
        static bool _savedCursor, _savedGun, _savedCam;
        static CursorLockMode _savedLock;
        static MethodInfo _setGun, _setCam;

        public static void Init()
        {
            MenuButton = Settings.Cat.CreateEntry("ProvingGroundMenuButton", true, description: "PROVING GROUND button in the main menu: a flat test range with targets every 100 m and a shot lab.");
            _keyPref = Settings.Cat.CreateEntry("ProvingGroundKey", "Home", description: "On the proving ground: shows or hides its tools window.");
            _buildPref = Settings.Cat.CreateEntry("ProvingGroundBuildKey", "Alt+Home", description: "Builds a proving ground in the mission you're in (its units hold still, a deck with targets far from them).");
            _rowsPref = Settings.Cat.CreateEntry("ProvingGroundRows", 5, description: "Rows of targets, one every 100 m down range (each row three vehicles: more rows, more frames lost).");
            _respawnPref = Settings.Cat.CreateEntry("ProvingGroundRespawnSeconds", 20f, description: "A knocked-out target (destroyed, or no crew left in it) comes back by itself this many seconds later. 0: never (respawn them from the tools window).");
            _perRowPref = Settings.Cat.CreateEntry("ProvingGroundPerRow", 3, description: "Targets in each row.");
            _emptyPref = Settings.Cat.CreateEntry("ProvingGroundEmptyMap", true, description: "Only the deck and the vehicles on it are drawn, not the map under the range: it looks like a VR range and runs faster. Off: the map stays.");
            _briefPref = Settings.Cat.CreateEntry("ProvingGroundBriefing", true, description: "When the range is ready, a briefing says what's where and what the tools do.");
            BigCamPref = Settings.Cat.CreateEntry("ProvingGroundBigCam", false, description: "A bigger hit cam on the proving ground, for close-ups.");
            RodCamPref = Settings.Cat.CreateEntry("RodCam", false, description: "Follow the round itself through every plate in deep slow motion, from the side (for studying penetration).");

            _setGun = AccessTools.PropertySetter(typeof(PlayerInput), "SuspendGunnerInputs");
            _setCam = AccessTools.PropertySetter(typeof(PlayerInput), "SuspendCameraInputs");
        }

        /// game's Restart Mission reloads the plain mission: if a proving ground was in it, build it again
        public static void Install(HarmonyLib.Harmony h)
        {
            var m = AccessTools.Method(typeof(SceneController), "RestartActiveScene");
            if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(ProvingGround).GetMethod(nameof(RestartPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            // unit menu (Tab) lists only your own vehicle: the mission's units stay where they were, off the range
            try
            {
                var um = AccessTools.Method(typeof(GHPC.UI.UnitMenu.UnitMenuController), "RefreshMenu");
                _menuData = AccessTools.FieldRefAccess<GHPC.UI.UnitMenu.UnitMenuController, List<GHPC.UI.UnitMenu.UnitMenuPlatoonData>>("_platoonData");
                _menuRedraw = AccessTools.Method(typeof(GHPC.UI.UnitMenu.UnitMenuController), "RedrawMenu");
                if (um != null) h.Patch(um, postfix: new HarmonyMethod(typeof(ProvingGround).GetMethod(nameof(UnitMenuPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: unit menu patch: " + e.Message); }
        }

        static AccessTools.FieldRef<GHPC.UI.UnitMenu.UnitMenuController, List<GHPC.UI.UnitMenu.UnitMenuPlatoonData>> _menuData;

        /// self-test: what the unit menu (Tab) would list now
        public static void TestUnitMenu()
        {
            var m = GHPC.UI.UnitMenu.UnitMenuController.Instance;
            if (m == null || _menuData == null) { HitCamMod.Log.Msg("Proving ground unit menu: no menu"); return; }
            m.RefreshMenu();
            var data = _menuData(m);
            HitCamMod.Log.Msg($"Proving ground unit menu: {data.Count} group(s): " + string.Join("; ", data.Select(d => d.FriendlyName + ": " + string.Join(", ", (d.Units ?? new System.Collections.ObjectModel.ReadOnlyCollection<Unit>(new List<Unit>())).Select(Recorder.SafeName)))));
        }
        static MethodInfo _menuRedraw;

        static void UnitMenuPostfix(GHPC.UI.UnitMenu.UnitMenuController __instance)
        {
            try
            {
                // on the range only (its deck stands in this mission), everywhere else the menu is the game's own
                if (!Active || _deck == null || __instance == null || _menuData == null) return;
                var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
                var data = _menuData(__instance);
                data.Clear();
                if (me != null) data.Add(new GHPC.UI.UnitMenu.UnitMenuPlatoonData(new List<Unit> { me }));
                _menuRedraw?.Invoke(__instance, null);
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: unit menu: " + e.Message); }
        }

        static void RestartPrefix() { if (Active) { _rebuild = true; NoteLoading(); HitCamMod.Log.Msg("Proving ground: the mission restarts, the range comes back with it"); } }

        public static void OnSceneLoaded(string scene)
        {
            if (SceneUtil.IsMenuScene(scene)) { _rebuild = false; _loadingSince = -1f; return; }
            // a range on its way (menu button, restart): its vehicles load with the mission, for the vehicle mods' sake
            if (_loadingSince >= 0f) ModdedVehicles.PreloadForMission(PlannedKeys, "proving ground");
            if (_rebuild) { _rebuild = false; MelonCoroutines.Start(BuildWhenReady()); }
        }

        /// waits for the mission to be up (player vehicle, unit spawner, a moment for the loading screen), kicks it out of
        /// its planning phase and builds the range
        public static IEnumerator BuildWhenReady()
        {
            float t0 = Time.realtimeSinceStartup;
            bool ready = false;
            while (Time.realtimeSinceStartup - t0 < 180f)
            {
                yield return null;
                if (SceneUtil.IsGameplayGone()) continue;
                var pi = SceneUtil.Player;
                if (pi == null || pi.CurrentPlayerUnit == null) continue;
                if (UnitSpawner.Instance == null && UnityEngine.Object.FindObjectOfType<UnitSpawner>() == null) continue;
                ready = true;
                break;
            }
            if (!ready) { HitCamMod.Log.Warning("Proving ground: the mission didn't come up"); _loadingSince = -1f; yield break; }
            yield return new WaitForSecondsRealtime(4f);
            if (SceneUtil.IsGameplayGone() || Active) { _loadingSince = -1f; yield break; }
            try
            {
                var msc = GHPC.State.MissionStateController.Instance;
                if (msc != null && GHPC.State.MissionStateController.CurrentState == GHPC.State.MissionState.Planning) msc.EndPlanningPhase();
            }
            catch { }
            yield return new WaitForSecondsRealtime(1f);
            yield return Build();
        }

        public static void Reset()
        {
            if (_open) ShowWindow(false);
            Active = false;
            _busy = false;
            FromMenu = false;
            _slots.Clear();
            _parked.Clear();
            _hit.Clear();
            _vehicle = _missionVehicle = null;
            if (_deck != null) UnityEngine.Object.Destroy(_deck);
            _deck = null;
        }

        static void Say(string s) { _status = s; HitCamMod.Log.Msg("Proving ground: " + s); }

        /// self-test: tools window open, like a player leaves it when quitting
        internal static void TestOpenTools() { _tab = 0; ShowWindow(true); }
        internal static void TestCloseTools() => ShowWindow(false);

        /// briefing's "Don't show again" (also the self-test's briefnever step)
        internal static void BriefNever()
        {
            _briefUntil = 0f;
            _hintUntil = 0f;
            _briefPref.Value = false;
            try { Settings.Cat.SaveToFile(false); } catch { }
            Say($"briefing off for good (back on: {Hotkeys.Label(_keyPref)} > You, or the settings)");
        }

        /// one of the range's targets (the self-test picks its targets from these)
        public static bool IsTarget(Unit u) => u != null && _slots.Any(s => s.U == u);
        internal static IEnumerable<Unit> TargetUnits => _slots.Select(s => s.U);
        /// self-test: targets that ended up well below the deck (fell off), and the far ones' places
        internal static string DeckCheck() => $"{_slots.Count(s => s.U != null && !s.Air && s.U.transform.position.y < _origin.y - 5f)} of {_slots.Count(s => !s.Air)} ground targets below the deck; far: " + string.Join(", ", _slots.Where(s => s.Row > 5 && !s.Air && s.U != null).Select(s => $"{Recorder.SafeName(s.U)} at {Vector3.Distance(s.U.transform.position, _origin):0} m"));

        // ------------------------------------------------------------------ building it

        public static IEnumerator Build()
        {
            if (_busy || Active) yield break;
            _busy = true;
            if (_loadingSince < 0f) NoteLoading();
            var pi = SceneUtil.Player;
            var player = pi != null ? pi.CurrentPlayerUnit : null;
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            if (player == null || sp == null) { Say("no player vehicle / unit spawner in this mission"); _busy = false; _loadingSince = -1f; yield break; }
            _missionVehicle = player;
            _mine = player.Allegiance;
            _enemy = _mine == Faction.Blue ? Faction.Red : Faction.Blue;
            int rows = Mathf.Clamp(_rowsPref.Value, 1, 20), perRow = Mathf.Clamp(_perRowPref.Value, 1, 5);

            // mission holds still: no AI anywhere
            var mission = SceneUnitsManager.Instance.AllUnitsInScene.OfType<Unit>().Where(u => u != null).ToList();
            foreach (var u in mission) if (u != player) Freeze(u);

            // where: as far from every mission unit as the map allows, within 3 km of the map origin (game's physics ends ~5 km out)
            Bounds map = new Bounds(player.transform.position, Vector3.one * 4000f);
            var terrains = Terrain.activeTerrains;
            if (terrains != null && terrains.Length > 0)
            {
                map = new Bounds(terrains[0].GetPosition() + terrains[0].terrainData.size * 0.5f, terrains[0].terrainData.size);
                foreach (var t in terrains.Skip(1)) map.Encapsulate(new Bounds(t.GetPosition() + t.terrainData.size * 0.5f, t.terrainData.size));
            }
            // wide enough for the outermost bearing at the last row (see TargetX)
            float far = 0f;
            for (int r = 1; r <= rows; r++) for (int c = 0; c < perRow; c++) far = Mathf.Max(far, Mathf.Abs(TargetX(r, c, perRow)));
            // long-range tanks beyond the last row, in the gap between the rows' centre and side columns (see FarBearing)
            var farRanges = FarRanges.Where(r => r > rows * RowStep).ToList();
            foreach (float r in farRanges) far = Mathf.Max(far, Mathf.Abs(Mathf.Tan(FarBearing * Mathf.Deg2Rad) * r) - 20f);
            // soft targets (SoftSpot) stand out to ~250 m left, the deck covers them too (a truck fell off its edge)
            float softX = 0f;
            for (int i = 0; i < 4; i++) softX = Mathf.Max(softX, Mathf.Abs(SoftSpot(i).x));
            float len = Mathf.Max(rows * RowStep, farRanges.Count > 0 ? farRanges.Max() : 0f) + 250f, half = Mathf.Max(Mathf.Max(far + 60f, softX + 30f), 240f);
            Vector3 best = player.transform.position;
            float bestD = -1f;
            for (float x = -3000f; x <= 3000f; x += 200f)
                for (float z = -3000f; z <= 3000f; z += 200f)
                {
                    var p = new Vector3(x, 0f, z);
                    if (p.magnitude > 3000f || !map.Contains(new Vector3(x, map.center.y, z))) continue;
                    float d = float.MaxValue;
                    foreach (var u in mission) { Vector3 q = u.transform.position; q.y = 0f; d = Mathf.Min(d, (q - p).magnitude); }
                    if (d > bestD) { bestD = d; best = p; }
                }
            // deck floats 30 m above the highest ground (and trees) under it so nothing of the map pokes through
            float z0 = best.z - len * 0.5f + 150f;
            float top = float.MinValue;
            for (float x = -half; x <= half; x += 25f)
                for (float z = z0 - 150f; z <= z0 + len - 150f; z += 25f)
                {
                    var g = CodeUtils.GetPositionOnTerrain(new Vector3(best.x + x, 3000f, z), out bool ok, 6000f);
                    if (ok) top = Mathf.Max(top, g.y);
                }
            if (top == float.MinValue) top = player.transform.position.y;
            top += 30f;
            _origin = new Vector3(best.x, top, z0);
            _deck = MakeDeck(_origin, -150f, len - 150f, half, rows, farRanges);
            Say($"deck {half * 2f:0} x {len:0} m at {_origin} ({bestD:0} m from the nearest mission unit)");
            if (_emptyPref.Value) HideMap();
            yield return null;

            // targets: a row every 100 m, first of each row front-on, then side-on, then three-quarter
            _slots.Clear();
            int k = 0;
            for (int r = 1; r <= rows; r++)
                for (int c = 0; c < perRow; c++)
                {
                    string facing = c % 3 == 0 ? "front" : c % 3 == 1 ? "side" : "3/4";
                    float yaw = facing == "front" ? 180f : facing == "side" ? 90f : 135f;
                    float x = TargetX(r, c, perRow);
                    var s = new Slot { Key = Targets[k++ % Targets.Length], Row = r, Col = c, Pos = _origin + new Vector3(x, 0.6f, r * RowStep + (c - (perRow - 1) * 0.5f) * 12f), Rot = Quaternion.Euler(0f, yaw, 0f), Facing = facing };   // columns 12 m apart in depth too so a round across a row misses its neighbours
                    _slots.Add(s);
                    SpawnSlot(sp, s);
                    yield return null;
                }
            // long range: a tank at 1 km and one at 1.5 km, facing you, either side of the middle
            for (int j = 0; j < farRanges.Count; j++)
            {
                float r = farRanges[j], bear = (j % 2 == 0 ? 1f : -1f) * FarBearing;
                var s = new Slot { Key = FarKeys[j % FarKeys.Length], Row = Mathf.RoundToInt(r / RowStep), Col = 0, Pos = _origin + new Vector3(Mathf.Tan(bear * Mathf.Deg2Rad) * r, 0.6f, r), Rot = Quaternion.Euler(0f, 180f, 0f), Facing = "front" };
                _slots.Add(s);
                SpawnSlot(sp, s);
                yield return null;
            }
            for (int j = 0; j < Helis.Length; j++)
            {
                int row = Mathf.Min(rows, 4 + j * 4);
                var s = new Slot { Key = Helis[j], Row = row, Col = -1, Air = true, Pos = _origin + new Vector3((j == 0 ? -1f : 1f) * (half - 50f), 30f + j * 20f, row * RowStep), Rot = Quaternion.Euler(0f, 180f, 0f), Facing = "hover" };
                _slots.Add(s);
                SpawnSlot(sp, s);
                yield return null;
            }
            // soft targets on the left, clear of every row's line of fire (rows stay within 24 deg, these stand at 34-54 deg,
            // nearer ones wider): jeeps and trucks, row 0 in the shot lab. no infantry: a squad needs the map's navmesh, spawned
            // on the deck it came up half-built and broke every round fired after it
            var soft = SoftTargets(sp);
            for (int i = 0; i < soft.Count; i++)
            {
                Vector3 spot = SoftSpot(i);
                string facing = i % 3 == 0 ? "side" : i % 3 == 1 ? "3/4" : "front";
                float yaw = facing == "front" ? 180f : facing == "side" ? 90f : 135f;
                var s = new Slot { Key = soft[i].key, Troops = soft[i].troops, Row = 0, Col = i, Pos = _origin + spot, Rot = Quaternion.Euler(0f, yaw, 0f), Facing = soft[i].troops ? "squad" : facing };
                _slots.Add(s);
                SpawnSlot(sp, s);
                yield return null;
            }
            // targets you placed yourself (kept over a restart)
            for (int i = 0; i < _placed.Count; i++)
            {
                var s = PlacedSlot(i);
                _slots.Add(s);
                SpawnSlot(sp, s);
                yield return null;
            }
            yield return null;
            yield return null;
            foreach (var s in _slots) Settle(s);

            // you: the vehicle you picked (else what the mission gave you), where you picked
            if (string.IsNullOrEmpty(_myKey)) _myKey = DefaultKey(player);
            if (string.IsNullOrEmpty(_myKey)) _myKey = _mine == Faction.Blue ? "M1IP" : "T72M1";
            yield return SpawnMine(_myKey, _myRange, _myBack);
            Active = true;
            _busy = false;
            if (_loadingSince >= 0f) HitCamMod.Log.Msg($"Proving ground: ready {Time.unscaledTime - _loadingSince:0.0} s after it was asked for");
            _loadingSince = -1f;
            _target = Mathf.Clamp(_slots.FindIndex(s => s.Row == 3 && s.Facing == "side"), 0, Math.Max(_slots.Count - 1, 0));
            Say($"ready: {_slots.Count(s => !s.Air && s.Row > 0 && s.Row <= rows)} targets in {rows} rows (100-{rows * 100} m), {_slots.Count(s => !s.Air && s.Row > rows)} far out ({string.Join(", ", _slots.Where(s => !s.Air && s.Row > rows).Select(s => s.Row * 100 + " m"))}), {_slots.Count(s => s.Row == 0)} jeeps and trucks, {_slots.Count(s => s.Air)} helicopters; {Hotkeys.Label(_keyPref)} = tools");
            // tools window used to open by itself and grab the controls (you couldn't drive off), so a hint instead
            _briefUntil = _briefPref.Value ? Time.unscaledTime + 30f : 0f;
            _hintUntil = Mathf.Max(_briefUntil, Time.unscaledTime) + (_briefPref.Value ? 8f : 14f);
            // vehicle list open from the start: pick what to drive (Drive it closes the tools again), or F4 to keep this one
            if (!AutoTest.Active || AutoTest.Opt("pgopen", false))
            {
                _tab = 0; _picking = 1;
                ShowWindow(true, keepBrief: true);
                _autoOpened = true;
            }
        }

        // bearings from the middle of the firing line, per row inside a column (deg): every target stands clear of the
        // ones in front. a vehicle ~2.5 m either side of its centre hides ~1.4 deg at 100 m, 0.7 at 200 m, ... so the near rows
        // get the widest gaps (1.5-3 m to spare past each nearer vehicle's side)
        static readonly float[] FarRanges = { 1000f, 1500f };
        static readonly string[] FarKeys = { "T72M1", "T80B" };
        const float FarBearing = 8.5f;   // between the rows' centre column (within 5 deg) and their side ones (from 11.8)

        /// where the i-th soft target stands, from the deck origin: left of the rows, nearer ones wider
        static Vector3 SoftSpot(int i)
        {
            float d = 120f + i * 52f, bear = -(54f - i * 4f);
            return new Vector3(Mathf.Tan(bear * Mathf.Deg2Rad) * d, 0.6f, d);
        }

        static readonly float[] RowBearing = { 0f, 3.0f, -3.0f, 4.2f, -4.2f, 5.0f, -5.0f, 5.6f, -5.6f, 6.1f, -6.1f, 6.5f, -6.5f, 6.9f, -6.9f, 7.2f, -7.2f, 7.5f, -7.5f, 7.8f };
        const float ColumnBearing = 16f;

        /// where a target stands across the deck: on its own bearing from the firing line (columns 16 deg apart), so a round
        /// aimed at any of them passes clear of every nearer one, and one that goes through a target doesn't fly on into another
        static float TargetX(int row, int col, int perRow)
        {
            float b = (col - (perRow - 1) * 0.5f) * ColumnBearing + RowBearing[Mathf.Clamp(row - 1, 0, RowBearing.Length - 1)];
            return Mathf.Tan(b * Mathf.Deg2Rad) * row * RowStep;
        }

        static string DefaultKey(Unit u)
        {
            string n = Recorder.SafeName(u).Replace("-", "").Replace(" ", "").ToUpperInvariant();
            var units = Units();
            // longest key the mission vehicle's name contains ("T72M1" before "T72")
            return units.Select(x => x.key).Where(k => n.Contains(k.ToUpperInvariant())).OrderByDescending(k => k.Length).FirstOrDefault() ?? "";
        }

        /// every ground vehicle the game can spawn, by army and name
        static List<(string key, string name, string army)> Units()
        {
            if (_units != null && _units.Count > 0) return _units;
            _units = new List<(string, string, string)>();
            try
            {
                var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
                if (sp?.PrefabLookup?.AllUnits != null)
                    foreach (var m in sp.PrefabLookup.AllUnits)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Name) || m.Name == "TC" || m.Name == "RTS_CAMERA") continue;
                        if (!ModdedVehicles.Spawnable(m)) continue;   // lookup-only entry (no prefab), e.g. FEP's binocular fix
                        if (!ModdedVehicles.Released(m)) continue;   // game's own mission customizer leaves it out
                        var c = m.Class;
                        if (!(c == UnitClass.Tank || c == UnitClass.IFV || c == UnitClass.APC || c == UnitClass.Scout || c == UnitClass.Transport || c == UnitClass.FireSupport)) continue;
                        string army = "";
                        try { army = m.Army != null && m.Army.Info != null ? m.Army.Info.Abbreviation ?? "" : ""; } catch { }
                        _units.Add((m.Name, string.IsNullOrEmpty(m.FriendlyName) ? m.Name : m.FriendlyName, army));
                    }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: unit table: " + e.Message); }
            _units.Sort((a, b) => { int c = string.Compare(a.army, b.army, StringComparison.Ordinal); return c != 0 ? c : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); });
            return _units;
        }

        static string NameOf(string key) => Units().FirstOrDefault(x => x.key == key).name ?? key;

        static float FacingYaw(int f) => f == 0 ? 180f : f == 1 ? 135f : f == 2 ? 90f : 0f;

        /// a spot down range clear of every standing target (moved sideways 10 m at a time until it is)
        static Vector3 FreeSpot(float range, float side)
        {
            var p = _origin + new Vector3(side, 0.6f, range);
            for (int i = 0; i < 40; i++)
            {
                if (!_slots.Any(sl => !sl.Air && (new Vector2(sl.Pos.x - p.x, sl.Pos.z - p.z)).magnitude < 13f)) return p;
                p.x += (i % 2 == 0 ? 1f : -1f) * 10f * (i / 2 + 1);
            }
            return p;
        }

        static Slot PlacedSlot(int i)
        {
            var (key, range, side, facing) = _placed[i];
            return new Slot { Key = key, Row = -1, Col = i, Pos = FreeSpot(range, side), Rot = Quaternion.Euler(0f, FacingYaw(facing), 0f), Facing = Facings[facing].ToLowerInvariant() + " (placed)" };
        }

        /// tools window's "Place": the vehicle you picked, where you picked, facing the way you picked
        static void PlaceTarget()
        {
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            if (sp == null || !Active) return;
            _placed.Add((_placeKey, _placeRange, _placeSide, _placeFacing));
            var s = PlacedSlot(_placed.Count - 1);
            _slots.Add(s);
            SpawnSlot(sp, s);
            MelonCoroutines.Start(SettleLater(s));
            _target = _slots.Count - 1;
            Say($"placed a {NameOf(_placeKey)} at {_placeRange:0} m, {Facings[_placeFacing].ToLowerInvariant()}");
        }

        static IEnumerator SettleLater(Slot s) { yield return null; yield return null; Settle(s); }

        static void RemovePlaced()
        {
            foreach (var s in _slots.Where(x => x.Row == -1).ToList())
            {
                if (s.U != null) Retire(s.U);
                _slots.Remove(s);
            }
            _placed.Clear();
            _target = Mathf.Clamp(_target, 0, Math.Max(_slots.Count - 1, 0));
            Say("placed targets removed");
        }

        /// self-test: what stands on the range and whether you can drive (inputs, physics, AI)
        internal static string Describe()
        {
            string you = "";
            try
            {
                var pi = SceneUtil.Player;
                var u = pi != null ? pi.CurrentPlayerUnit as Unit : null;
                var rb = u != null ? u.GetComponent<Rigidbody>() : null;
                var ai = u != null && u.InfoBroker != null ? u.InfoBroker.AI : null;
                you = $"inputs suspended: gunner {pi?.SuspendGunnerInputs}, camera {pi?.SuspendCameraInputs}; rigidbody kinematic {rb?.isKinematic}, AI enabled {(ai != null ? ai.enabled.ToString() : "-")}; mission state {GHPC.State.MissionStateController.CurrentState}; window open {_open}";
            }
            catch (Exception e) { you = e.Message; }
            return $"{_slots.Count(x => !x.Air && x.Row > 0)} row targets, {_slots.Count(x => x.Row == 0)} soft, {_slots.Count(x => x.Row == -1)} placed ({string.Join(", ", _slots.Where(x => x.Row == -1).Select(x => x.U != null ? Recorder.SafeName(x.U) : x.Key))}); {you}";
        }

        /// self-test: tools window tabs on screen with the vehicle lists open
        internal static IEnumerator TestWindow(string outDir)
        {
            ShowWindow(true);
            foreach (var (tab, pick, name) in new[] { (0, 1, "pg_window_you"), (1, 2, "pg_window_targets"), (2, 0, "pg_window_lab") })
            {
                _tab = tab; _picking = pick;
                yield return new WaitForSecondsRealtime(0.6f);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(outDir, name + ".png"));
                yield return new WaitForSecondsRealtime(0.4f);
            }
            _picking = 0; _tab = 0;
            ShowWindow(false);
            _hintUntil = Time.unscaledTime + 3f;
            yield return null;
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(outDir, "pg_hint.png"));
            yield return new WaitForSecondsRealtime(0.4f);
        }

        /// self-test: your vehicle and a placed target, like the tools window does
        internal static IEnumerator TestSetup(string mine, float myRange, string place, float placeRange, float side, int facing)
        {
            if (!string.IsNullOrEmpty(place)) { _placeKey = place; _placeRange = placeRange; _placeSide = side; _placeFacing = facing; PlaceTarget(); yield return new WaitForSecondsRealtime(1f); }
            if (!string.IsNullOrEmpty(mine)) { _myKey = mine; _myRange = myRange; yield return SpawnMine(mine, myRange, false); yield return new WaitForSecondsRealtime(1f); }
        }

        static void Freeze(Unit u)
        {
            try
            {
                var ai = u.InfoBroker != null ? u.InfoBroker.AI : null;
                if (ai != null) { ai.Stop(); ai.enabled = false; }
                u.SetNoPlayerControlAllowed(true);   // not one to switch into (the vehicle picker is how you change)
                var hai = u.InfoBroker != null ? u.InfoBroker.HelicopterAI : null;
                if (hai != null) hai.canAttack = false;
            }
            catch { }
        }

        /// every vehicle the range will spawn: targets, helis, jeeps and trucks, yours, the ones you placed
        internal static IEnumerable<string> PlannedKeys()
        {
            var keys = new List<string>(Targets);
            keys.AddRange(Helis);
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            if (sp != null) keys.AddRange(SoftTargets(sp).Select(t => t.key));
            if (!string.IsNullOrEmpty(_myKey)) keys.Add(_myKey);
            keys.AddRange(_placed.Select(p => p.key));
            return keys;
        }

        /// jeeps and trucks from the game's unit table, nearest first
        static List<(string key, bool troops)> SoftTargets(UnitSpawner sp)
        {
            var have = new HashSet<string>();
            try { foreach (var m in sp.PrefabLookup.AllUnits) if (m != null && !string.IsNullOrEmpty(m.Name)) have.Add(m.Name); }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: unit table: " + e.Message); }
            return new[] { "UAZ469", "URAL375D", "M151", "M923" }.Where(k => have.Count == 0 || have.Contains(k)).Select(k => (k, false)).ToList();
        }

        static readonly HashSet<string> _spawnErrs = new HashSet<string>();   // full error once per vehicle

        static Unit SpawnAt(UnitSpawner sp, string key, Vector3 pos, Quaternion rot, Faction f, bool air, bool troops = false)
        {
            try
            {
                var md = new UnitMetaData
                {
                    Name = key + " (proving ground)",
                    Position = pos,
                    Rotation = rot,
                    Allegiance = f,
                    UnitType = air ? UnitType.AirVehicle : troops ? UnitType.Infantry : UnitType.GroundVehicle,
                };
                var u = ModdedVehicles.Spawn(sp, key, md) as Unit;
                if (u == null) { HitCamMod.Log.Warning("Proving ground: could not spawn " + key); return null; }
                u.Targetable = true;
                try { SceneUnitsManager.Instance.Meta?.UnimportantUnits?.Add(u); } catch { }   // never decides the mission
                return u;
            }
            catch (Exception e) { HitCamMod.Log.Warning($"Proving ground: spawn {key} failed: " + (_spawnErrs.Add(key) ? e.ToString() : e.Message)); return null; }
        }

        static void SpawnSlot(UnitSpawner sp, Slot s)
        {
            var u = s.U = SpawnAt(sp, s.Key, s.Pos, s.Rot, _enemy, s.Air, s.Troops);
            // targets are what gets shot here: their warm-up runs in the background (once vehicle mods made their versions of
            // them, like they do the mission's)
            ModdedVehicles.After(u, () => { WakeAudio(u); ModdedVehicles.ShareEraMaterials(u); Warmup.Plan(u); });
        }

        static FieldInfo _engineSound;

        /// a vehicle spawned mid-mission came up without engine sound (game's vehicle audio had nothing playing, yours too):
        /// switching the audio off and on again starts it like the game does at mission start. only when there's none, so a
        /// running engine doesn't start over
        internal static void WakeAudio(Unit u)
        {
            try
            {
                var vac = u != null && !u.Destroyed ? u.GetComponent<GHPC.Audio.VehicleAudioController>() : null;
                if (vac == null) return;
                bool parked = _soundParked.Remove(vac);
                if (!parked && !vac.isActiveAndEnabled) return;
                _engineSound = _engineSound ?? AccessTools.Field(typeof(GHPC.Audio.VehicleAudioController), "_engineInstance");
                if (parked || _engineSound == null || _engineSound.GetValue(vac) == null)
                {
                    ModdedVehicles.Ours++;
                    try { vac.enabled = false; vac.enabled = true; }
                    finally { ModdedVehicles.Ours--; }
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: engine sound of " + Recorder.SafeName(u) + ": " + e.Message); }
            QuietIfBroken(u);
        }

        static FieldInfo _frontWheel;
        static readonly HashSet<string> _silentSaid = new HashSet<string>();
        static readonly HashSet<GHPC.Audio.VehicleAudioController> _soundParked = new HashSet<GHPC.Audio.VehicleAudioController>();

        static bool NoWheels(GHPC.Audio.VehicleAudioController vac)
        {
            _frontWheel = _frontWheel ?? AccessTools.Field(typeof(GHPC.Audio.VehicleAudioController), "_frontLeftWheel");
            return _frontWheel != null && !(_frontWheel.GetValue(vac) as UnityEngine.Object != null);
        }

        /// right after a spawn: sound that started without its wheels goes off until WakeAudio, else it throws every frame
        internal static void ParkBrokenSound(Unit u)
        {
            try
            {
                var vac = u != null ? u.GetComponent<GHPC.Audio.VehicleAudioController>() : null;
                if (vac == null || !vac.isActiveAndEnabled || !vac.IsInitialized || !NoWheels(vac)) return;
                _soundParked.RemoveWhere(v => v == null);
                vac.enabled = false;
                _soundParked.Add(vac);
            }
            catch { }
        }

        /// some modded vehicles spawned mid-mission: the game's sound can't find their wheels (throws as it starts) and then
        /// throws every frame. those stay silent instead
        static void QuietIfBroken(Unit u)
        {
            try
            {
                var vac = u != null && !u.Destroyed ? u.GetComponent<GHPC.Audio.VehicleAudioController>() : null;
                if (vac == null || !vac.isActiveAndEnabled || !vac.IsInitialized || !NoWheels(vac)) return;
                vac.enabled = false;
                string name = Recorder.SafeName(u);
                if (_silentSaid.Add(name)) HitCamMod.Log.Msg($"Spawned {name}: the game's vehicle sound couldn't start on it (it would throw every frame), so it stays silent");
            }
            catch { }
        }

        static void Settle(Slot s)
        {
            if (s.U == null) return;
            if (!s.Air) { TestRange.MakePassive(s.U); return; }
            try
            {
                var hai = s.U.InfoBroker != null ? s.U.InfoBroker.HelicopterAI : null;
                if (hai != null) { hai.canAttack = false; hai.enabled = false; }
                s.U.gameObject.AddComponent<HoverHold>().Init(s.U);
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground helicopter: " + e.Message); }
        }

        static IEnumerator SpawnMine(string key, float range = 0f, bool back = false)
        {
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            var pi = SceneUtil.Player;
            if (sp == null || pi == null) yield break;
            var u = SpawnAt(sp, key, range < 1f ? _origin + Vector3.up * 0.6f : FreeSpot(range, 0f), Quaternion.Euler(0f, back ? 180f : 0f, 0f), _mine, false);
            if (u == null) { Say("could not spawn " + key); yield break; }
            yield return null;
            yield return null;
            yield return ModdedVehicles.Convert(u);
            if (u == null || SceneUtil.IsGameplayGone()) yield break;
            WakeAudio(u);
            ModdedVehicles.ShareEraMaterials(u);
            var old = pi.CurrentPlayerUnit;
            try
            {
                pi.SetPlayerUnit(u);
                if (old != null) Freeze(old);
                if (_vehicle != null && _vehicle != u && _vehicle != _missionVehicle) Retire(_vehicle);
                _vehicle = u;
                Say("you're in the " + Recorder.SafeName(u) + (range < 1f ? " on the firing line" : $" at {range:0} m") + (back ? ", facing the firing line" : ""));
            }
            catch (Exception e) { Say("could not switch into the " + key + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ the deck

        /// the VR deck: light grey with a 1 m grid and 10 m lines, a dark line across every 100 m with the range painted on it,
        /// and a board with the range at both ends of each line, readable through a gun sight
        static void NoTerrain(Terrain t)
        {
            if (t == null) return;
            t.drawHeightmap = false;
            t.drawTreesAndFoliage = false;
            t.treeDistance = 0f;
            t.detailObjectDistance = 0f;
        }

        /// once more a moment later: whatever turns the terrain's own drawing back on as its renderers go
        static IEnumerator NoTerrainLater(Terrain[] terrains)
        {
            yield return null;
            yield return new WaitForSecondsRealtime(1f);
            foreach (var t in terrains) NoTerrain(t);
        }

        static readonly string[] SkyWords = { "sky", "cloud", "atmos", "sun", "moon", "star" };

        /// map under the range isn't drawn any more: the terrain and everything in the terrain's scene (buildings, props,
        /// water, the GPU-instanced trees and grass) except what belongs to a vehicle and the sky. nothing gets removed
        /// (colliders, the mission, its units stay), it all comes back with the next mission
        static void HideMap()
        {
            try
            {
                var terrains = Terrain.activeTerrains;
                if (terrains == null || terrains.Length == 0) return;
                // every scene of the map and mission (forests and their impostors aren't all in the terrain's scene), not the
                // game's loader scenes or ours
                var scenes = new HashSet<UnityEngine.SceneManagement.Scene>();
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (sc.isLoaded && !sc.name.StartsWith("LOADER", StringComparison.OrdinalIgnoreCase) && sc.name != "HitCamPhysics") scenes.Add(sc);
                }
                int hidden = 0, kept = 0;
                var perScene = new Dictionary<string, int>();
                var veg = new HashSet<string>();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || r.forceRenderingOff || !scenes.Contains(r.gameObject.scene)) continue;
                    if (_deck != null && r.transform.IsChildOf(_deck.transform)) continue;
                    string n = r.name.ToLowerInvariant();
                    // what stays: vehicles and their parts, cameras and what hangs on them (sights), effects, the sky
                    if (r.GetComponentInParent<Unit>() != null || r.GetComponentInParent<GHPC.Utility.LateFollow>() != null || r.GetComponentInParent<Camera>() != null
                        || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer || SkyWords.Any(w => n.Contains(w))) { kept++; continue; }
                    r.forceRenderingOff = true;
                    hidden++;
                    string sn = r.gameObject.scene.name;
                    perScene[sn] = perScene.TryGetValue(sn, out int c) ? c + 1 : 1;
                }
                // terrain trees and grass are Nature Renderer's (one per terrain): switched off without handing them back to
                // unity's own terrain renderer (which is what it does when disabled), then the terrains told to draw nothing
                var nature = AccessTools.TypeByName("VisualDesignCafe.Rendering.Nature.NatureRenderer");
                var giveBack = nature != null ? AccessTools.Field(nature, "_enableUnityRendererWhenDisabled") : null;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<Behaviour>())
                {
                    if (b == null || !b.enabled) continue;
                    string ns = b.GetType().Namespace ?? "", tn = b.GetType().Name;
                    if (!ns.StartsWith("VisualDesignCafe") && !ns.Contains("Rendering.Instancing") && !ns.StartsWith("AmplifyImpostors")) continue;
                    if (giveBack != null && nature.IsInstanceOfType(b)) giveBack.SetValue(b, false);
                    b.enabled = false;
                    veg.Add(tn);
                }
                // its renderers don't show in a scene search (hidden objects): every loaded one plus its own list of them (the
                // forest on the horizon was still drawn)
                var natures = new List<Behaviour>();
                if (nature != null)
                {
                    foreach (var o in Resources.FindObjectsOfTypeAll(nature)) if (o is Behaviour nb && nb != null) natures.Add(nb);
                    if (AccessTools.Property(nature, "Renderers")?.GetValue(null) is System.Collections.IEnumerable all)
                        foreach (var o in all.Cast<object>()) if (o is Behaviour nb && nb != null && !natures.Contains(nb)) natures.Add(nb);
                }
                int natureOff = 0;
                foreach (var nb in natures)
                {
                    if (!nb.enabled || !nb.gameObject.scene.isLoaded) continue;
                    giveBack?.SetValue(nb, false);
                    nb.enabled = false;
                    natureOff++;
                }
                if (natureOff > 0) veg.Add($"NatureRenderer x{natureOff}");
                if (AutoTest.Active) HitCamMod.Log.Msg($"Proving ground: empty map: {natures.Count} Nature Renderers found ({string.Join(", ", natures.Take(8).Select(nb => nb.name + (nb.enabled ? "" : " (off)") + " in " + nb.gameObject.scene.name))})");
                foreach (var t in terrains) NoTerrain(t);
                MelonCoroutines.Start(NoTerrainLater(terrains));
                if (AutoTest.Active)
                {
                    // what's still drawn that isn't a vehicle's (biggest first): what the empty map may have left
                    var left = UnityEngine.Object.FindObjectsOfType<Renderer>().Where(x => x != null && x.enabled && !x.forceRenderingOff && x.isVisible && x.GetComponentInParent<Unit>() == null && (_deck == null || !x.transform.IsChildOf(_deck.transform)))
                        .OrderByDescending(x => x.bounds.size.magnitude).Take(12).Select(x => $"{x.name} [{x.GetType().Name}, {x.gameObject.scene.name}, {x.bounds.size.magnitude:0} m]");
                    HitCamMod.Log.Msg("Proving ground: empty map, still drawn: " + string.Join("; ", left));
                }
                Say($"empty map: {hidden} renderers hidden ({string.Join(", ", perScene.Select(kv => kv.Key + " " + kv.Value))}), {kept} kept (vehicles, cameras, effects, sky), {terrains.Length} terrains, off: {string.Join(", ", veg)}");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: empty map: " + e.Message); }
        }

        static GameObject MakeDeck(Vector3 o, float zMin, float zMax, float half, int rows, List<float> farRanges)
        {
            var root = new GameObject("HitCam proving ground");
            int terrain = LayerMask.NameToLayer("Terrain");
            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = "deck";
            if (terrain >= 0) slab.layer = terrain;        // wheels, rounds and debris treat it as ground
            slab.transform.SetParent(root.transform, false);
            slab.transform.position = o + new Vector3(0f, -1f, (zMin + zMax) * 0.5f);
            slab.transform.localScale = new Vector3(half * 2f, 2f, zMax - zMin);
            var std = Shader.Find("Standard") ?? Shader.Find("Diffuse");
            var mat = new Material(std) { mainTexture = GridTexture(), mainTextureScale = new Vector2(half * 2f / 10f, (zMax - zMin) / 10f) };
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.08f);
            slab.GetComponent<MeshRenderer>().sharedMaterial = mat;

            var dark = new Material(std) { color = new Color(0.16f, 0.18f, 0.22f) };
            var white = new Material(std) { color = new Color(0.92f, 0.93f, 0.95f) };
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            foreach (int r in Enumerable.Range(1, rows).Concat(farRanges.Select(f => Mathf.RoundToInt(f / RowStep))))
            {
                float z = r * RowStep;
                var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.Destroy(line.GetComponent<Collider>());
                line.transform.SetParent(root.transform, false);
                line.transform.position = o + new Vector3(0f, 0.01f, z - 12f);
                line.transform.localScale = new Vector3(half * 2f, 0.02f, 0.6f);
                line.GetComponent<MeshRenderer>().sharedMaterial = dark;
                string label = (r * 100) + " m";
                Text(root.transform, font, label, o + new Vector3(-half + 14f, 0.03f, z - 18f), Quaternion.Euler(90f, 0f, 0f), 0.9f, new Color(0.16f, 0.18f, 0.22f));
                Text(root.transform, font, label, o + new Vector3(half - 14f, 0.03f, z - 18f), Quaternion.Euler(90f, 0f, 0f), 0.9f, new Color(0.16f, 0.18f, 0.22f));
                foreach (float side in new[] { -1f, 1f })
                {
                    // a board on a post, facing the firing line
                    var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    post.transform.SetParent(root.transform, false);
                    post.transform.position = o + new Vector3(side * (half - 6f), 2f, z - 12f);
                    post.transform.localScale = new Vector3(0.15f, 4f, 0.15f);
                    post.GetComponent<MeshRenderer>().sharedMaterial = white;
                    var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    board.transform.SetParent(root.transform, false);
                    board.transform.position = o + new Vector3(side * (half - 6f), 4.6f, z - 12f);
                    board.transform.localScale = new Vector3(5f, 1.6f, 0.1f);
                    board.GetComponent<MeshRenderer>().sharedMaterial = white;
                    Text(root.transform, font, label, o + new Vector3(side * (half - 6f), 4.6f, z - 12.07f), Quaternion.identity, 0.55f, new Color(0.1f, 0.12f, 0.16f));
                }
            }
            Text(root.transform, font, "FIRING LINE", o + new Vector3(0f, 0.03f, -6f), Quaternion.Euler(90f, 0f, 0f), 0.7f, new Color(0.16f, 0.18f, 0.22f));
            return root;
        }

        static void Text(Transform parent, Font font, string s, Vector3 pos, Quaternion rot, float size, Color c)
        {
            if (font == null) return;
            var go = new GameObject("label " + s);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.rotation = rot;
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.text = s;
            tm.fontSize = 96;
            tm.characterSize = size * 0.1f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = c;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }

        /// one 10 m tile: light grey, faint 1 m lines, stronger line at its edge
        static Texture2D GridTexture()
        {
            const int n = 256;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool major = x < 3 || y < 3;
                    bool minor = x % (n / 10) < 1 || y % (n / 10) < 1;
                    float g = major ? 0.42f : minor ? 0.56f : 0.66f;
                    px[y * n + x] = new Color(g, g + 0.01f, g + 0.03f);
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        // ------------------------------------------------------------------ per frame

        public static void Update()
        {
            try
            {
                // range went with its mission (however that was left): nothing of it may outlive it
                if (Active && _deck == null && !_busy) { HitCamMod.Log.Msg("Proving ground: its mission is gone"); Reset(); }
                if (Hotkeys.Down(_buildPref) && !Active && !_busy && !ArmorSim.Active && SceneUtil.Player?.CurrentPlayerUnit != null) MelonCoroutines.Start(Build());
                else if (Active && Hotkeys.Down(_keyPref)) ShowWindow(!_open);
                if (_open)
                {
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                if (Active && !_busy) { AutoRespawn(); PutAwayWrecks(); }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: " + e.Message); }
        }

        static void ShowWindow(bool on, bool keepBrief = false)
        {
            if (on == _open) return;
            _open = on;
            if (on && !keepBrief) { _briefUntil = 0f; _hintUntil = 0f; }   // read: the tools say the rest
            if (!on) _autoOpened = false;
            var pi = SceneUtil.Player;
            if (on)
            {
                _savedCursor = Cursor.visible; _savedLock = Cursor.lockState;
                if (pi != null)
                {
                    _savedGun = pi.SuspendGunnerInputs; _savedCam = pi.SuspendCameraInputs;
                    try { _setGun?.Invoke(pi, new object[] { true }); _setCam?.Invoke(pi, new object[] { true }); } catch { }
                }
            }
            else
            {
                bool menu = SceneUtil.GameMenuUp(pi);
                if (pi != null) try { _setGun?.Invoke(pi, new object[] { _savedGun && menu }); _setCam?.Invoke(pi, new object[] { _savedCam && (menu || pi.IsMapActive) }); } catch { }
                Cursor.visible = _savedCursor; Cursor.lockState = _savedLock;
            }
        }

        // ------------------------------------------------------------------ respawning

        static bool _respawning;
        static int _retired;

        /// a hit target leaves the range. destroying a GHPC vehicle while its jobs run (a burning one) crashed the game in
        /// a Burst job, so the wreck gets parked frozen far below the deck instead and taken out of the unit lists (HUD, AI
        /// and mission forget it). it goes with the mission
        static void Retire(Unit u)
        {
            if (u == null) return;
            try
            {
                foreach (var rb in u.GetComponentsInChildren<Rigidbody>(true)) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; }
                var roots = new List<Transform> { u.transform };
                foreach (var lf in UnityEngine.Object.FindObjectsOfType<GHPC.Utility.LateFollow>())
                    if (lf != null && (lf.ParentUnit as Unit) == u && !lf.transform.IsChildOf(u.transform)) roots.Add(lf.transform);
                int i = _retired++;
                var g = CodeUtils.GetPositionOnTerrain(new Vector3(_origin.x + (i % 10) * 15f - 70f, 3000f, _origin.z - 600f - (i / 10) * 20f), out bool ok, 6000f);
                Vector3 to = (ok ? g : _origin) - Vector3.up * 60f;
                u.transform.position = to;
                Physics.SyncTransforms();
                var sum = SceneUnitsManager.Instance;
                if (sum != null)
                {
                    (sum.AllUnitsInScene as List<Unit>)?.Remove(u);
                    try { sum.Meta?.UnimportantUnits?.Remove(u); } catch { }
                }
                foreach (var l in SceneUnitsManager.AllUnitsByFaction) l?.Remove(u);
                foreach (var l in SceneUnitsManager.AllLiveUnitsByFaction) l?.Remove(u);
                // targets come back by themselves now and the wrecks add up: a parked one isn't drawn, lit or heard any more and
                // later gets switched off whole (Destroy is what crashed, a burning wreck gets time to burn out first)
                foreach (var r in roots)
                {
                    if (r == null) continue;
                    foreach (var x in r.GetComponentsInChildren<Renderer>()) x.enabled = false;
                    foreach (var x in r.GetComponentsInChildren<Light>()) x.enabled = false;
                    foreach (var x in r.GetComponentsInChildren<ParticleSystem>()) x.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    foreach (var x in r.GetComponentsInChildren<AudioSource>()) x.enabled = false;
                    foreach (var x in r.GetComponentsInChildren<GHPC.Audio.VehicleAudioController>()) x.enabled = false;
                }
                _parked.Add((roots, Time.realtimeSinceStartup + WreckOffSeconds));
                _hit.Remove(u);
            }
            catch (Exception e) { HitCamMod.Log.Warning("Proving ground: could not retire " + Recorder.SafeName(u) + ": " + e.Message); }
        }

        static readonly List<(List<Transform> roots, float at)> _parked = new List<(List<Transform>, float)>();
        static float WreckOffSeconds => AutoTest.Active && float.TryParse(AutoTest.Opt("wreck_off", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : 45f;
        static float _nextDownCheck;

        static readonly HashSet<Unit> _hit = new HashSet<Unit>();

        /// a round struck this vehicle's armour (recorder's pen check)
        internal static void NoteHit(Unit u) { if (Active && u != null) _hit.Add(u); }

        /// knocked out: destroyed, or nobody left inside who can fight it (dead, or bailed out after it was hit). a truck's
        /// crew gets out by itself with an enemy in sight: the M923 came back every 20 s without a shot fired
        static bool KnockedOut(Unit u)
        {
            if (u == null || u.Destroyed) return true;
            var crew = u.CrewManager != null ? u.CrewManager.CrewMembers : null;
            if (crew == null || crew.Length == 0) return false;
            if (crew.All(c => c == null || c.Human == null || c.Human.IsDead)) return true;
            return _hit.Contains(u) && crew.All(c => c == null || c.Evacuated || c.Human == null || c.Human.IsDead);
        }

        static string Why(Unit u)
        {
            if (u == null) return "gone";
            if (u.Destroyed) return "destroyed";
            var crew = u.CrewManager != null ? u.CrewManager.CrewMembers : null;
            return crew == null ? "no crew list" : "crew " + string.Join("/", crew.Select(c => c == null ? "none" : c.Evacuated ? "bailed out" : c.Human == null ? "no body" : c.Human.IsDead ? "dead" : "ok"));
        }

        /// targets knocked out a while ago come back (ProvingGroundRespawnSeconds after it was seen, once no hit cam is
        /// playing, it may be showing that very one)
        static void AutoRespawn()
        {
            float delay = _respawnPref != null ? _respawnPref.Value : 0f;
            if (delay <= 0f || _respawning || Time.unscaledTime < _nextDownCheck) return;
            _nextDownCheck = Time.unscaledTime + 0.5f;
            var due = new List<Slot>();
            foreach (var s in _slots)
            {
                if (s.U == null || !KnockedOut(s.U)) { s.DownAt = -1f; continue; }
                if (s.DownAt < 0f) s.DownAt = Time.time;
                else if (Time.time - s.DownAt >= delay) due.Add(s);
            }
            if (due.Count == 0 || HitCamPlayer.Current != null || ArmorSim.Active) return;
            foreach (var s in due) s.DownAt = -1f;
            HitCamMod.Log.Msg($"Proving ground: {due.Count} target(s) knocked out {delay:0} s ago come back: " + string.Join(", ", due.Select(s => Recorder.SafeName(s.U) + " (" + Why(s.U) + ")")));
            Respawn(due);
        }

        static void PutAwayWrecks()
        {
            if (_parked.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = _parked.Count - 1; i >= 0; i--)
            {
                if (now < _parked[i].at) continue;
                foreach (var r in _parked[i].roots) if (r != null) r.gameObject.SetActive(false);
                _parked.RemoveAt(i);
                _putAway++;
                if (Settings.Verbose) HitCamMod.Log.Msg($"Proving ground: a parked wreck switched off ({_putAway} so far, {_parked.Count} still parked)");
            }
        }

        static int _putAway;
        internal static int ParkedCount => _parked.Count;

        static bool EngineSounding(Unit u)
        {
            var vac = u != null ? u.GetComponent<GHPC.Audio.VehicleAudioController>() : null;
            _engineSound = _engineSound ?? AccessTools.Field(typeof(GHPC.Audio.VehicleAudioController), "_engineInstance");
            return vac != null && _engineSound?.GetValue(vac) != null;
        }

        /// self-test respawncycle:<n>:<lab spec>: target at the lab spec's row and column knocked out n times, each time
        /// waiting for it to come back by itself (ProvingGroundRespawnSeconds), engine sound checked each time
        public static IEnumerator TestRespawnCycle(string[] spec)
        {
            int n = spec.Length > 1 && int.TryParse(spec[1], out int k) ? k : 3;
            var lab = spec.Skip(2).ToArray();
            int row = lab.Length > 2 && int.TryParse(lab[2], out int r) ? r : 1, col = lab.Length > 3 && int.TryParse(lab[3], out int c) ? c : 0;
            var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
            HitCamMod.Log.Msg($"RESPAWN TEST: comes back after {_respawnPref.Value:0.0} s; your {Recorder.SafeName(me)} engine sound {EngineSounding(me)}");
            for (int i = 0; i < n; i++)
            {
                var s = _slots.FirstOrDefault(x => x.Row == row && x.Col == col);
                if (s == null) { HitCamMod.Log.Warning("RESPAWN TEST: no slot " + row + "/" + col); yield break; }
                float w0 = Time.realtimeSinceStartup;
                while ((s.U == null || KnockedOut(s.U) || _respawning) && Time.realtimeSinceStartup - w0 < 40f) yield return null;
                yield return new WaitForSecondsRealtime(2.5f);
                var u = s.U;
                HitCamMod.Log.Msg($"RESPAWN TEST {i + 1}/{n}: {Recorder.SafeName(u)} up, engine sound {EngineSounding(u)}");
                if (!LabShot(lab, out _, out _, respawnHurt: false)) { HitCamMod.Log.Warning("RESPAWN TEST: lab shot failed"); continue; }
                float f0 = Time.realtimeSinceStartup;
                yield return new WaitForSecondsRealtime(1f);
                while (HitCamPlayer.Current != null && Time.realtimeSinceStartup - f0 < 45f) yield return null;
                bool ko = KnockedOut(u);
                float down = Time.realtimeSinceStartup;
                HitCamMod.Log.Msg($"RESPAWN TEST {i + 1}/{n}: hit cam over {down - f0:0.0} s after the shot; knocked out {ko} (destroyed {u != null && u.Destroyed})");
                if (!ko) continue;
                while (s.U == u && Time.realtimeSinceStartup - down < _respawnPref.Value + 30f) yield return null;
                HitCamMod.Log.Msg($"RESPAWN TEST {i + 1}/{n}: {(s.U != u ? "back" : "NOT back")} {Time.realtimeSinceStartup - down:0.0} s after the hit cam; parked {_parked.Count}, switched off {_putAway}");
            }
            yield return new WaitForSecondsRealtime(1f);
        }

        static void Respawn(Slot s) => Respawn(new List<Slot> { s });

        static void Respawn(List<Slot> slots) { if (!_respawning && slots.Count > 0) MelonCoroutines.Start(RespawnFlow(slots)); }

        /// despawn first (one per frame), give the game a moment to finish destroying them (spawning a vehicle while the
        /// old one is still being torn down crashed the game in Instantiate), then spawn one every few frames
        static IEnumerator RespawnFlow(List<Slot> slots)
        {
            _respawning = true;
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            if (sp == null) { _respawning = false; yield break; }
            foreach (var s in slots)
                if (s.U != null) { Retire(s.U); s.U = null; yield return null; }
            yield return new WaitForSecondsRealtime(0.3f);
            foreach (var s in slots)
            {
                if (!Active) break;
                SpawnSlot(sp, s);
                yield return null;
                yield return null;
                Settle(s);
                yield return new WaitForSecondsRealtime(0.15f);
            }
            _respawning = false;
            Say($"respawned {slots.Count} target{(slots.Count == 1 ? "" : "s")}");
        }

        static bool Hurt(Unit u) => u == null || u.Destroyed || (u.Type != UnitType.Infantry && u.CrewManager != null && u.CrewManager.CrewMembers != null && u.CrewManager.CrewMembers.Any(c => c == null || c.Evacuated));

        // ------------------------------------------------------------------ the shot lab

        static List<Arsenal.Entry> Rounds => Arsenal.Entries.ToList();

        static int DefaultRound(List<Arsenal.Entry> list)
        {
            int best = 0;
            float bestPen = -1f;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i].Ammo;
                bool rod = a.Category == AmmoType.AmmoCategory.Penetrator && a.Radius * 2000f < a.GetFinalCaliber() * 0.6f;
                if (rod && list[i].Group.StartsWith("Game") && a.RhaPenetration > bestPen) { bestPen = a.RhaPenetration; best = i; }
            }
            return best;
        }

        static Bounds UnitBounds(Unit u)
        {
            var rs = u.GetComponentsInChildren<MeshRenderer>(false);
            if (rs.Length == 0) return new Bounds(u.transform.position + Vector3.up * 1.2f, new Vector3(3, 2.4f, 6));
            Bounds b = rs[0].bounds;
            foreach (var r in rs) if (r.bounds.size.magnitude < 20f) b.Encapsulate(r.bounds);
            return b;
        }

        /// fires `ammo` at the target from `yaw` / `pitch` relative to the target's front (0 = head-on, 90 = its right
        /// side, 180 = rear, pitch from above), at the aim point, with the speed it has after `range` m of flight (fired a few
        /// metres short of the target, the game treats it as coming from that range: fuze arming, nutation, hit distance)
        static bool Fire(Unit target, AmmoType ammo, float yaw, float pitch, int aimMode, float range, out Vector3 aim, out Vector3 dir, bool bomb = false)
        {
            aim = Vector3.zero; dir = Vector3.forward;
            if (target == null || ammo == null) return false;
            if (bomb) return Drop(target, ammo, aimMode, out aim, out dir);
            Bounds b = UnitBounds(target);
            Transform tt = target.transform;
            aim = b.center;
            switch (aimMode)
            {
                case 1: aim += tt.up * b.extents.y * 0.55f; break;     // turret
                case 2: aim -= tt.up * b.extents.y * 0.35f; break;     // lower hull
                case 3: aim += tt.up * b.extents.y * 0.25f; break;     // turret ring
                case 4:
                {
                    // main gun's tube, a third of a metre back from the muzzle
                    var gun = Arsenal.MainGun(target);
                    var mz = gun != null ? gun.MuzzleIdentity : null;
                    if (mz != null) aim = mz.position - mz.forward * 0.35f;
                    break;
                }
            }
            Vector3 from = tt.rotation * (Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward);
            dir = -from.normalized;
            Vector3 start = aim - dir * 25f;
            float v = PenProbe.SpeedAtRange(ammo, range);
            try { SceneUnitsManager.Instance?.AllMiscPosablesInScene?.RemoveAll(t => t == null); } catch { }
            var round = LiveRoundUtility.GetNewLiveRound(ammo);
            round.Info = ammo;
            round.Guided = false;
            round.UseGravity = false;
            round.CurrentSpeed = v;
            round.MaxSpeed = ammo.MuzzleVelocity;
            round.Shooter = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
            round.transform.position = start;
            round.transform.rotation = Quaternion.LookRotation(dir);
            ArmorSim.VirtualMuzzle(round, start - dir * range, range, ammo);
            bool arsenal = Arsenal.On;
            Arsenal.On = false;   // arsenal would swap the projectile of a round the player's tank "fires"
            try { round.Init(null, target); }
            finally { Arsenal.On = arsenal; }
            if (AutoTest.Active) MelonCoroutines.Start(Track(round, start));
            return true;
        }

        /// bomb falling onto the target like from 300 m up: straight down onto the aim point at the speed it would have
        static bool Drop(Unit target, AmmoType ammo, int aimMode, out Vector3 aim, out Vector3 dir)
        {
            Bounds b = UnitBounds(target);
            Transform tt = target.transform;
            aim = b.center + Vector3.up * b.extents.y * 0.3f;
            if (aimMode == 2) aim -= tt.forward * b.extents.z * 0.45f;      // lower hull: the engine deck end
            dir = Vector3.down;
            Vector3 start = aim + Vector3.up * (b.extents.y + 25f);
            try { SceneUnitsManager.Instance?.AllMiscPosablesInScene?.RemoveAll(t => t == null); } catch { }
            var round = LiveRoundUtility.GetNewLiveRound(ammo);
            round.Info = ammo;
            round.Guided = false;
            round.UseGravity = true;
            round.CurrentSpeed = round.MaxSpeed = Arsenal.DropSpeed(ammo);
            round.Shooter = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
            round.transform.position = start;
            round.transform.rotation = Quaternion.LookRotation(dir);
            ArmorSim.VirtualMuzzle(round, start + Vector3.up * 300f, 300f, ammo);
            bool arsenal = Arsenal.On;
            Arsenal.On = false;
            try { round.Init(null, target); }
            finally { Arsenal.On = arsenal; }
            if (AutoTest.Active) MelonCoroutines.Start(Track(round, start));
            return true;
        }

        static IEnumerator Track(LiveRound r, Vector3 start)
        {
            if (r != null)
            {
                var hits = Physics.RaycastAll(start, r.transform.forward, 40f, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance).Take(8);
                HitCamMod.Log.Msg("Proving ground round: on its line " + string.Join(", ", hits.Select(h => $"{h.collider.name} [{LayerMask.LayerToName(h.collider.gameObject.layer)}{(h.collider.isTrigger ? ", trigger" : "")}] {h.distance:0.0} m ({(h.collider.GetComponentInParent<Unit>() is Unit hu ? Recorder.SafeName(hu) : h.collider.transform.root.name)})")));
            }
            for (int i = 0; i < 4; i++)
            {
                HitCamMod.Log.Msg($"Proving ground round: frame {i}, {(r == null ? "gone" : $"at {Vector3.Distance(r.transform.position, start):0.0} m from its start, active {r.gameObject.activeInHierarchy}, enabled {r.enabled}, speed {r.CurrentSpeed:0}, destroyed {r.IsDestroyed}, timeScale {Time.timeScale}")}");
                yield return null;
            }
        }

        static float AspectYaw(int aspect) => aspect == 0 ? 0f : aspect == 1 ? 35f : aspect == 2 ? 90f : aspect == 3 ? 180f : aspect == 4 ? 20f : _yaw;
        static float AspectPitch(int aspect) => aspect == 4 ? 65f : aspect == 5 ? _pitch : 0f;

        static void FireSelected()
        {
            var list = Rounds;
            if (list.Count == 0 || _slots.Count == 0) return;
            if (_ammoSel < 0 || _ammoSel >= list.Count) _ammoSel = DefaultRound(list);
            var s = _slots[Mathf.Clamp(_target, 0, _slots.Count - 1)];
            if (s.U == null || s.U.Destroyed) { Say("that target is destroyed: respawn it first"); return; }
            var e = list[_ammoSel];
            Arsenal.PrepareForFire(e);
            if (e.Bomb)
            {
                if (Fire(s.U, e.Ammo, 0f, 90f, _aimMode, 300f, out _, out _, bomb: true))
                    Say($"{e.Ammo.Name} dropped on the {Recorder.SafeName(s.U)} as from 300 m ({Arsenal.DropSpeed(e.Ammo):0} m/s)");
                return;
            }
            if (Fire(s.U, e.Ammo, AspectYaw(_aspect), AspectPitch(_aspect), _aimMode, _range, out _, out _))
                Say($"{e.Ammo.Name} at the {Recorder.SafeName(s.U)} ({s.Row * 100} m row), {Aspects[_aspect].ToLowerInvariant()}, {AimModes[_aimMode].ToLowerInvariant()}, as from {_range:0} m ({PenProbe.SpeedAtRange(e.Ammo, _range):0} m/s)");
        }

        /// self-test: lab:<ammo>:<row>:<col>:<aspect or yaw deg>:<aim>:<range m>[:<pitch deg>]
        public static bool LabShot(string[] spec, out Vector3 aim, out Vector3 dir, bool respawnHurt = true)
        {
            aim = Vector3.zero; dir = Vector3.forward;
            if (!Active) return false;
            string Get(int i, string d) => spec.Length > i && spec[i].Length > 0 ? spec[i] : d;
            float F(string s, float d) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : d;
            var list = Rounds;
            string want = Get(1, "apfsds");
            int ai = want == "apfsds" ? DefaultRound(list) : list.FindIndex(e => (e.Ammo.Name ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 || (e.Label ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            if (ai < 0) return false;
            int row = (int)F(Get(2, "3"), 3), col = (int)F(Get(3, "1"), 1);
            var s = _slots.FirstOrDefault(x => x.Row == row && x.Col == col) ?? _slots.FirstOrDefault(x => !x.Air);
            if (s == null) return false;
            if (s.U == null || (respawnHurt && Hurt(s.U))) { Respawn(s); return false; }   // (a burst keeps firing at it)
            string asp = Get(4, "side");
            int aspIdx = Array.FindIndex(Aspects, a => a.Equals(asp, StringComparison.OrdinalIgnoreCase));
            float yaw = aspIdx >= 0 ? AspectYaw(aspIdx) : F(asp, 90f);
            float pitch = aspIdx >= 0 ? AspectPitch(aspIdx) : F(Get(7, "0"), 0f);
            int aim0 = Array.FindIndex(AimModes, a => a.Replace(" ", "").Equals(Get(5, "hull").Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            var e = list[ai];
            Arsenal.PrepareForFire(e);
            float range = F(Get(6, "1000"), 1000f);
            bool ok = Fire(s.U, e.Ammo, yaw, pitch, Mathf.Max(aim0, 0), range, out aim, out dir, e.Bomb);
            if (ok) HitCamMod.Log.Msg($"Proving ground lab shot: {e.Ammo.Name} at {Recorder.SafeName(s.U)} (row {row}, {s.Facing}), yaw {yaw:0} pitch {pitch:0}, {AimModes[Mathf.Max(aim0, 0)]}, as from {range:0} m = {PenProbe.SpeedAtRange(e.Ammo, range):0} m/s");
            return ok;
        }

        /// self-test: respawn every target the last shots hurt
        public static IEnumerator RespawnHurt()
        {
            var hurt = _slots.Where(s => s.U == null || Hurt(s.U)).ToList();
            if (hurt.Count == 0) yield break;
            Respawn(hurt);
            yield return null;
            while (_respawning) yield return null;
        }

        // ------------------------------------------------------------------ the window

        public static void Draw()
        {
            if (!Active && _loadingSince >= 0f)
            {
                float since = Time.unscaledTime - _loadingSince;
                if (since > 240f) _loadingSince = -1f;
                else
                {
                    float k1 = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
                    var st = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(18 * k1), alignment = TextAnchor.MiddleCenter, wordWrap = true };
                    st.normal.textColor = Color.white;
                    if (_back == null) { _back = new Texture2D(1, 1); _back.SetPixel(0, 0, new Color(0.07f, 0.08f, 0.09f, 0.94f)); _back.Apply(); }
                    float w = 760f * k1, h = 92f * k1;
                    var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.16f, w, h);
                    GUI.DrawTexture(r, _back);
                    string dots = new string('.', 1 + (int)(Time.unscaledTime * 2f) % 3);
                    GUI.Box(r, $"PROVING GROUND\nLoading the mission, then building the range{dots} ({since:0} s, usually 20-40 s)\n" +
                               (_busy ? (_status ?? "") : "You can't drive or shoot yet: it starts by itself when it's ready."), st);
                }
            }
            // unit menu open: it only lists your vehicle here (mission's units stay off the range), so say where another one
            // to drive comes from
            bool unitMenu = false;
            try { if (Active) { var um = GHPC.UI.UnitMenu.UnitMenuController.Instance; unitMenu = um != null && um.MenuVisible; } } catch { }
            if (Active && (!_open || Time.unscaledTime < _briefUntil) && !ArmorSim.Active && (unitMenu || Time.unscaledTime < _hintUntil))
            {
                float k0 = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
                var st = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(17 * k0), alignment = TextAnchor.MiddleCenter, wordWrap = true, padding = new RectOffset(14, 14, 8, 8) };
                st.normal.textColor = Color.white;
                // range briefing while it's new (until the tools get opened, or half a minute), then the short hint
                bool brief = !unitMenu && Time.unscaledTime < _briefUntil;
                string text = unitMenu
                    ? $"PROVING GROUND: the unit menu lists only your vehicle here.\nAnother one to drive: {Hotkeys.Label(_keyPref)} > You > pick a vehicle > Drive it."
                    : brief
                    ? "WELCOME TO THE PROVING GROUND\n"
                      + $"Press {Hotkeys.Label(_keyPref)} to open the TOOLSET (changing vehicle, spawning and respawning enemies etc.)\n"
                      + $"Press {Hotkeys.Label(Settings.ArsenalKey)} to open the round changer, press {Hotkeys.Label(Settings.FireNowKey)} to shoot it (the arcade HUD will calculate the correct arc for you)\n"
                      + $"Press {Hotkeys.Label(Settings.ConfigKey)} to open the mod's settings for all its functions (works even outside of the proving ground)"
                    : $"PROVING GROUND\n{Hotkeys.Label(_keyPref)}: tools (your vehicle, place targets, shot lab). You can drive and shoot now.";
                float w = (brief ? 980f : 760f) * k0;
                float bh = brief ? 34f * k0 : 0f;
                float h = Mathf.Max(58f * k0, st.CalcHeight(new GUIContent(text), w)) + bh;
                var box = new Rect((Screen.width - w) * 0.5f, 90f * k0, w, h);
                if (brief) st.alignment = TextAnchor.UpperCenter;   // text on top, buttons under it
                GUI.Box(box, text, st);
                if (brief)
                {
                    // close it now, or for good (tools' You tab and the settings turn it back on)
                    var bs = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(15 * k0) };
                    float bw = 170f * k0, gap = 10f * k0, y = box.yMax - bh + 2f * k0;
                    if (GUI.Button(new Rect(box.center.x - bw - gap * 0.5f, y, bw, bh - 8f * k0), "Close", bs)) { _briefUntil = 0f; _hintUntil = 0f; }
                    if (GUI.Button(new Rect(box.center.x + gap * 0.5f, y, bw, bh - 8f * k0), "Don't show again", bs)) BriefNever();
                }
            }
            if (!Active || !_open || ArmorSim.Active) return;
            float k = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2f);
            var m = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1f));
            // IMGUI's window is see-through, whatever was behind it made the text hard to read
            if (_back == null) { _back = new Texture2D(1, 1); _back.SetPixel(0, 0, new Color(0.07f, 0.08f, 0.09f, 0.94f)); _back.Apply(); }
            GUI.DrawTexture(_win, _back);
            _win = GUILayout.Window(0x5047, _win, WindowBody, "PROVING GROUND");
            GUI.matrix = m;
        }

        static void WindowBody(int id)
        {
            GUILayout.Label($"{Hotkeys.Label(_keyPref)}: hide this window (drive and shoot with it hidden). {Hotkeys.Label(Settings.ArsenalKey)}: any round for your own gun. {Hotkeys.Label(Settings.ConfigKey)}: the mod's settings.");
            _tab = GUILayout.Toolbar(_tab, new[] { "You", "Targets", "Shot lab" });
            GUILayout.Space(6);
            if (_tab == 0) TabYou();
            else if (_tab == 1) TabTargets();
            else TabLab();
            GUILayout.Space(4);
            GUILayout.Label(_status);
            GUI.DragWindow();
        }

        /// list of every vehicle to pick from (scrolls), by army
        static string PickList(string current)
        {
            var units = Units();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(240f));
            string pick = null;
            foreach (var x in units)
            {
                var label = (x.key == current ? "> " : "   ") + (x.army.Length > 0 ? x.army + "  ·  " : "") + x.name + ModdedVehicles.Tag(x.key);
                var oc = GUI.contentColor;
                if (ModdedVehicles.IsModded(x.key)) GUI.contentColor = ModdedVehicles.TagColor;   // a mod's vehicle, or one a mod changes
                if (GUILayout.Button(label, GUI.skin.label)) pick = x.key;
                GUI.contentColor = oc;
            }
            GUILayout.EndScrollView();
            return pick;
        }

        static void TabYou()
        {
            GUILayout.Label("YOUR VEHICLE: any the game has (tanks, IFVs, APCs, scout cars, trucks, jeeps)");
            GUILayout.BeginHorizontal();
            GUILayout.Label(NameOf(_myKey ?? "") + ModdedVehicles.Tag(_myKey), GUILayout.Width(320));
            if (GUILayout.Button(_picking == 1 ? "Close list" : "Change...")) _picking = _picking == 1 ? 0 : 1;
            GUILayout.EndHorizontal();
            if (_picking == 1) { var p = PickList(_myKey); if (p != null) { _myKey = p; _picking = 0; } }
            GUILayout.Label(_myRange < 1f ? "Start: on the firing line" : $"Start: {_myRange:0} m down range, among the targets");
            _myRange = Mathf.Round(GUILayout.HorizontalSlider(_myRange, 0f, Mathf.Max(_rowsPref.Value, 1) * RowStep) / 50f) * 50f;
            _myBack = GUILayout.Toggle(_myBack, " Facing back toward the firing line");
            GUILayout.Space(4);
            if (GUILayout.Button("Drive it", GUILayout.Height(30)) && !string.IsNullOrEmpty(_myKey))
            {
                MelonCoroutines.Start(SpawnMine(_myKey, _myRange, _myBack));
                if (_autoOpened) ShowWindow(false);   // opened for this: straight to driving
            }
            GUILayout.Label("Kept when you restart the mission.");
            _briefPref.Value = GUILayout.Toggle(_briefPref.Value, " Briefing when the range is ready");
        }

        static void TabTargets()
        {
            int hurt = _slots.Count(s => s.U == null || Hurt(s.U));
            GUILayout.Label($"Targets: {_slots.Count(s => !s.Air && s.Row > 0)} in rows every 100 m, {_slots.Count(s => s.Row == 0)} jeeps and trucks, {_slots.Count(s => s.Row == -1)} placed by you, {_slots.Count(s => s.Air)} helicopters; {hurt} hit / destroyed");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Respawn hit ones")) Respawn(_slots.Where(s => s.U == null || Hurt(s.U)).ToList());
            if (GUILayout.Button("Respawn all")) Respawn(_slots.ToList());
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label("PLACE A TARGET: any vehicle, anywhere down range");
            GUILayout.BeginHorizontal();
            GUILayout.Label(NameOf(_placeKey), GUILayout.Width(250));
            if (GUILayout.Button(_picking == 2 ? "Close list" : "Change...")) _picking = _picking == 2 ? 0 : 2;
            GUILayout.EndHorizontal();
            if (_picking == 2) { var p = PickList(_placeKey); if (p != null) { _placeKey = p; _picking = 0; } }
            GUILayout.Label($"Range {_placeRange:0} m");
            _placeRange = Mathf.Round(GUILayout.HorizontalSlider(_placeRange, 50f, 2000f) / 25f) * 25f;
            GUILayout.Label(Mathf.Abs(_placeSide) < 1f ? "In the middle" : $"{Mathf.Abs(_placeSide):0} m to the {(_placeSide < 0f ? "left" : "right")}");
            _placeSide = Mathf.Round(GUILayout.HorizontalSlider(_placeSide, -200f, 200f) / 5f) * 5f;
            _placeFacing = GUILayout.Toolbar(_placeFacing, Facings);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Place", GUILayout.Height(28))) PlaceTarget();
            if (GUILayout.Button("Remove placed", GUILayout.Height(28))) RemovePlaced();
            GUILayout.EndHorizontal();
            GUILayout.Label("Placed targets come back when you restart the mission. Shoot them yourself or from the shot lab.");
        }

        static void TabLab()
        {
            GUILayout.Label("SHOT LAB: any round, any target, any direction, any range");
            if (_slots.Count > 0)
            {
                _target = Mathf.Clamp(_target, 0, _slots.Count - 1);
                var s = _slots[_target];
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("<", GUILayout.Width(26))) _target = (_target + _slots.Count - 1) % _slots.Count;
                GUILayout.Label($"{Vector3.Distance(new Vector3(s.Pos.x, 0f, s.Pos.z), new Vector3(_origin.x, 0f, _origin.z)):0} m  {(s.U != null ? Recorder.SafeName(s.U) : s.Key)} ({s.Facing}){(s.U == null || Hurt(s.U) ? " - hit" : "")}", GUILayout.Width(260));
                if (GUILayout.Button(">", GUILayout.Width(26))) _target = (_target + 1) % _slots.Count;
                GUILayout.EndHorizontal();
            }
            var list = Rounds;
            if (list.Count > 0)
            {
                if (_ammoSel < 0 || _ammoSel >= list.Count) _ammoSel = DefaultRound(list);
                var a = list[_ammoSel].Ammo;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("<", GUILayout.Width(26))) _ammoSel = (_ammoSel + list.Count - 1) % list.Count;
                GUILayout.Label($"{a.Name} ({a.GetFinalCaliber():0} mm, pen {a.RhaPenetration:0} mm)", GUILayout.Width(260));
                if (GUILayout.Button(">", GUILayout.Width(26))) _ammoSel = (_ammoSel + 1) % list.Count;
                GUILayout.EndHorizontal();
                if (list[_ammoSel].Bomb) GUILayout.Label($"Falls straight onto the target as from 300 m up: {Arsenal.DropSpeed(a):0} m/s");
                else
                {
                    GUILayout.Label($"As from {_range:0} m: {PenProbe.SpeedAtRange(a, _range):0} m/s");
                    _range = Mathf.Round(GUILayout.HorizontalSlider(_range, 0f, 4000f) / 50f) * 50f;
                }
            }
            bool dropping = list.Count > 0 && _ammoSel >= 0 && _ammoSel < list.Count && list[_ammoSel].Bomb;
            if (!dropping) GUILayout.Label("From:");
            if (!dropping) _aspect = GUILayout.Toolbar(_aspect, Aspects);
            if (!dropping && _aspect == 5)
            {
                GUILayout.Label($"Yaw {_yaw:0}° from its front (90 = its right side), pitch {_pitch:0}° from above");
                _yaw = Mathf.Round(GUILayout.HorizontalSlider(_yaw, -180f, 180f));
                _pitch = Mathf.Round(GUILayout.HorizontalSlider(_pitch, -10f, 80f));
            }
            GUILayout.Label("Aim at:");
            _aimMode = GUILayout.Toolbar(_aimMode, AimModes);
            GUILayout.Space(4);
            if (GUILayout.Button("FIRE", GUILayout.Height(34))) FireSelected();
            RodCamPref.Value = GUILayout.Toggle(RodCamPref.Value, " Rod cam: follow the round through the plates (deep slow motion)");
            BigCamPref.Value = GUILayout.Toggle(BigCamPref.Value, " Big hit cam");
        }
    }
}
