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
using GHPC.Weaponry;
using GHPC.Weapons;
using GHPC.World;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GHPCHitCam
{
    /// Dev harness. Active only when UserData/HitCam/autotest.cfg exists:
    /// loads a mission, fires real game rounds at a vehicle, captures the hit cam to PNG, quits.
    ///   mission=auto | list | <theaterKey>,<missionScene>
    ///   shots=apfsds:front, heat:side, he:top        (ammo:aspect, ammo = apfsds|ap|heat|atgm|he|any name substring;
    ///                                                 aspect = front|side|rear|top|turret|mantlet (along the target's gun))
    ///   target=enemy | any | <name substring>
    ///   quit=true
    internal static class AutoTest
    {
        public static bool Active { get; private set; }
        static readonly Dictionary<string, string> _cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string _outDir;
        static bool _launched, _shooting, _menuStarted;
        static float _sceneLoadedAt = -1f;
        static string _lastScene = "";

        // capture bookkeeping
        static int _shotIndex;
        static HitRecord _watch;
        static readonly List<float> _captureAt = new List<float>();
        static int _captured;
        static Playback _watchPlayback;

        public static void Init()
        {
            string path = Path.Combine(HitCamMod.DataDir, "autotest.cfg");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                int i = l.IndexOf('=');
                if (i > 0) _cfg[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
            }
            Active = true;
            _outDir = Path.Combine(HitCamMod.DataDir, "autotest");
            Directory.CreateDirectory(_outDir);
            foreach (var f in Directory.GetFiles(_outDir, "*.png")) File.Delete(f);
            HitCamMod.Log.Warning("AUTOTEST active (" + path + ")");
        }

        static string Cfg(string k, string d) => _cfg.TryGetValue(k, out var v) ? v : d;
        /// An on/off option of the running self-test (false when no self-test runs).
        internal static bool Opt(string k, bool d) => Active && Cfg(k, d ? "true" : "false") == "true";
        internal static string OutDir => _outDir;
        /// film=<fps>: the hit cam advances by a fixed step a frame and every frame is saved (an even sequence for video,
        /// however slow saving makes the game).
        /// modulefilm=<seconds>: a module shot (a track) filmed from one view at 30 frames a second of game time.
        internal static float ModuleFilm => Active && float.TryParse(Cfg("modulefilm", "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) && s > 0f ? s : 0f;
        internal static float FilmStep => Active && float.TryParse(Cfg("film", "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fps) && fps > 0f ? 1f / fps : 0f;

        /// Self-test: the real screen (the game's own camera, HUD and all) saved a few times from now.
        internal static IEnumerator ScreenFrames(string name, params float[] at)
        {
            float t0 = Time.realtimeSinceStartup;
            foreach (float t in at)
            {
                while (Time.realtimeSinceStartup - t0 < t) yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, $"{name}_{t * 1000f:0000}ms.png"));
                yield return null;
            }
        }
        internal static string Opt(string k, string d) => Cfg(k, d);

        public static void OnSceneLoaded(string scene)
        {
            if (!Active) return;
            _lastScene = scene;
            _sceneLoadedAt = Time.realtimeSinceStartup;
            if (!_menuStarted && scene.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _menuStarted = true;
                // armorsim_menu=true: the main menu's ARMOR SIM button is the test (it loads the mission itself)
                if (Cfg("armorsim_menu", "false") == "true") MelonCoroutines.Start(ArmorSimMenu.SelfTest(Cfg, _outDir, () => Finish("armor sim menu self-test done")));
                else if (Cfg("menushot", "false") == "true") MelonCoroutines.Start(MenuShot());
                else MelonCoroutines.Start(MenuFlow());
            }
        }

        /// menushot=true: the main menu as it looks with our buttons in it, then quit.
        static IEnumerator MenuShot()
        {
            float t0 = Time.realtimeSinceStartup;
            while ((ArmorSimMenu.Button == null || ArmorSimMenu.PgButton == null) && Time.realtimeSinceStartup - t0 < 45f) yield return null;
            yield return new WaitForSecondsRealtime(2.5f);
            ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, "menu_main.png"));
            yield return new WaitForSecondsRealtime(1.5f);
            Finish("menu shot done");
        }

        static IEnumerator MenuFlow()
        {
            yield return new WaitForSecondsRealtime(4f);
            var theaters = Resources.FindObjectsOfTypeAll<MissionTheaterScriptable>();
            var lines = new List<string>();
            foreach (var th in theaters)
            {
                lines.Add($"THEATER key={th.Key} name={th.Name} terrain={SafePath(th)}");
                if (th.Missions == null) continue;
                foreach (var m in th.Missions)
                {
                    if (m == null || m.IsCategory) continue;
                    lines.Add($"   {th.Key},{SceneName(m)}   \"{m.MissionName}\" flex={m.IsFlexMission} time={m.HasTimeChoices} inBuild={InBuild(m)} sides={string.Join("/", m.FactionInfo?.Select(f => f.Allegiance.ToString()) ?? new string[0])}");
                }
            }
            File.WriteAllLines(Path.Combine(HitCamMod.DataDir, "missions.txt"), lines);
            HitCamMod.Log.Msg("AUTOTEST: " + theaters.Length + " theaters written to missions.txt");

            string want = Cfg("mission", "auto");
            if (want == "list") { Finish("listed missions"); yield break; }

            MissionTheaterScriptable theater = null;
            MissionMetaData mission = null;
            if (want != "auto" && want.Contains(","))
            {
                var parts = want.Split(',');
                theater = theaters.FirstOrDefault(t => t.Key == parts[0].Trim());
                mission = theater?.Missions?.FirstOrDefault(m => m != null && !m.IsCategory && SceneName(m) == parts[1].Trim());
            }
            if (mission == null)
            {
                foreach (var th in theaters)
                {
                    mission = th.Missions?.FirstOrDefault(m => m != null && !m.IsCategory && InBuild(m));
                    if (mission != null) { theater = th; break; }
                }
            }
            if (mission == null) { Finish("no mission found"); yield break; }

            var sc = UnityEngine.Object.FindObjectOfType<SceneController>();
            int idx = SceneUtility.GetBuildIndexByScenePath(theater.TerrainSceneReference.Path);
            HitCamMod.Log.Msg($"AUTOTEST: launching {theater.Key},{SceneName(mission)} \"{mission.MissionName}\" terrain index {idx}");
            // play the side the mission actually offers (forcing the wrong one leaves the player without a vehicle)
            var sides = mission.FactionInfo?.Select(f => f.Allegiance).Where(f => f != Faction.Neutral).ToList() ?? new List<Faction>();
            Faction side = sides.Count > 0 ? sides[0] : Faction.Blue;
            string wantSide = Cfg("faction", "").ToLower();
            if (wantSide == "red" && sides.Contains(Faction.Red)) side = Faction.Red;
            if (wantSide == "blue" && sides.Contains(Faction.Blue)) side = Faction.Blue;
            HitCamMod.Log.Msg("AUTOTEST: mission sides [" + string.Join(",", sides) + "], playing " + side);
            SceneController.TargetSpawningFaction = side;
            SceneController.AllowTimeChoice = mission.HasTimeChoices;
            if (mission.HasTimeChoices) SceneController.IsDaytime = true;
            PersistentDataManager.PersistMissionInfo(new MissionInfoData(mission.MissionName, ""), mission, theater.Key);
            DynamicMissionComposer.CampaignMode = false;
            DynamicMissionComposer.DynamicMission = mission.IsFlexMission;
            DynamicMissionComposer.NeedsFlexMissionSetup = mission.IsFlexMission;
            DynamicMissionComposer.CurrentMissionNameKey = SceneName(mission);
            _launched = true;
            sc.LoadSceneByMission($"{idx},{SceneName(mission)}");
        }

        static bool InBuild(MissionMetaData m)
        {
            // dev missions are listed in the data but their scenes are not shipped
            try { return m.MissionSceneReference != null && SceneUtility.GetBuildIndexByScenePath(m.MissionSceneReference.Path) >= 0; }
            catch { return false; }
        }

        static string SceneName(MissionMetaData m)
        {
            try { return m.MissionSceneReference.Name; } catch { return "?"; }
        }

        static string SafePath(MissionTheaterScriptable th)
        {
            try { return th.TerrainSceneReference.Path; } catch { return "?"; }
        }

        static float _maxFrame;

        public static void Update()
        {
            if (Active) _maxFrame = Mathf.Max(_maxFrame, Time.unscaledDeltaTime);
            if (!Active || !_launched || _shooting) return;
            if (Time.realtimeSinceStartup - _sceneLoadedAt < 12f) return;
            var pi = PlayerInput.Instance;
            if (pi == null || pi.CurrentPlayerUnit == null) return;
            var sum = SceneUnitsManager.Instance;
            if (sum == null || sum.AllUnitsInScene.Count < 2) return;
            _shooting = true;
            MelonCoroutines.Start(ShotsFlow());
        }

        static IEnumerator ShotsFlow()
        {
            var player = PlayerInput.Instance.CurrentPlayerUnit;
            var units = SceneUnitsManager.Instance.AllUnitsInScene.Where(u => u != null && u != player && u.Type != UnitType.Infantry && !u.Destroyed && u.gameObject.activeInHierarchy).ToList();
            HitCamMod.Log.Msg($"AUTOTEST: player {Recorder.SafeName(player)} ({player.Allegiance}); vehicles: " + string.Join(", ", units.Select(u => Recorder.SafeName(u) + "/" + u.Allegiance + "/" + Vector3.Distance(u.transform.position, player.transform.position).ToString("0") + "m")));
            var ammos = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>().Where(a => a != null && a.AmmoType != null).Select(a => a.AmmoType).Distinct().ToList();
            HitCamMod.Log.Msg("AUTOTEST: ammo: " + string.Join(" | ", ammos.Select(a => $"{a.Name} [{a.Category} {a.Caliber}mm pen {a.RhaPenetration} tnt {a.TntEquivalentKg}]")));

            // armorsim=<vehicle key>: the Armor Sim self-test instead of the shot list
            if (_cfg.TryGetValue("armorsim", out var simVehicle))
            {
                yield return ArmorSim.SelfTest(simVehicle, Cfg("armorsim_ammo", "apfsds"), Cfg("armorsim_shots", "front,side,rear"), Cfg("armorsim_range", "1000"), _outDir);
                Finish("armor sim self-test done");
                yield break;
            }

            // testground=true: our own flat pad and targets, far from the mission's units (whose AI is switched off)
            if (Cfg("testground", "false") == "true") yield return TestGround.Build(player);
            // the proving ground is where self-tests shoot (its flat deck, rows of targets every 100 m, nothing of the
            // mission in the way; lab:<ammo>:<row>:<col>:<aspect>:<aim>:<range> shots fire there). provingground=false,
            // testground=true or armorsim=... keep the mission's own ground.
            if (Cfg("provingground", Cfg("testground", "false") == "true" ? "false" : "true") == "true") { yield return ProvingGround.Build(); player = PlayerInput.Instance.CurrentPlayerUnit; }
            ProvingGround.ForceRodCam = Cfg("rodcam", "false") == "true";
            var specs = Cfg("shots", "apfsds:front,heat:side,he:top").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            string targetWant = Cfg("target", "enemy");
            for (_shotIndex = 0; _shotIndex < specs.Count; _shotIndex++)
            {
                var spec = specs[_shotIndex].Split(':');
                // the unit list changes (range spawns, kills): refresh it every shot
                units = SceneUnitsManager.Instance.AllUnitsInScene.Where(u => u != null && u != player && u.Type != UnitType.Infantry && !u.Destroyed && u.gameObject.activeInHierarchy).ToList();
                if (spec[0] == "range")
                {
                    yield return TestRange.SpawnWave();
                    yield return new WaitForSecondsRealtime(8f);
                    foreach (var u in TestRange.Spawned.Where(x => x != null && x.Type == UnitType.AirVehicle))
                    {
                        var g = GHPC.Utility.CodeUtils.GetPositionOnTerrain(new Vector3(u.transform.position.x, 2000f, u.transform.position.z), out bool ok, 4000f);
                        var rb = u.GetComponent<Rigidbody>();
                        HitCamMod.Log.Msg($"AUTOTEST: helicopter {Recorder.SafeName(u)} AGL {(ok ? u.transform.position.y - g.y : -1f):0} m, speed {(rb != null ? rb.velocity.magnitude : 0f):0} m/s, destroyed={u.Destroyed}");
                    }
                    continue;
                }
                if (spec[0] == "pgsetup")
                {
                    // pgsetup:<your vehicle>:<your range>:<placed vehicle>:<its range>:<side m>:<facing 0-3>
                    float F(int i, float d) => spec.Length > i && float.TryParse(spec[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : d;
                    yield return ProvingGround.TestSetup(spec.Length > 1 ? spec[1] : "", F(2, 0f), spec.Length > 3 ? spec[3] : "", F(4, 300f), F(5, 0f), (int)F(6, 2f));
                    var me = PlayerInput.Instance != null ? PlayerInput.Instance.CurrentPlayerUnit : null;
                    HitCamMod.Log.Msg($"AUTOTEST: proving ground setup: you are in {(me != null ? Recorder.SafeName(me) : "-")} at {(me != null ? me.transform.position.ToString() : "-")}; {ProvingGround.Describe()}");
                    continue;
                }
                if (spec[0] == "pgwindow") { yield return ProvingGround.TestWindow(_outDir); continue; }
                if (spec[0] == "unitmenu") { ProvingGround.TestUnitMenu(); continue; }
                if (spec[0] == "fps") { yield return FpsProbe.Run(spec); continue; }
                if (spec[0] == "fpsrun") { yield return FpsProbe.RunOne(spec); continue; }
                if (spec[0] == "replay") { yield return FpsProbe.Replay(spec); continue; }
                if (spec[0] == "wait") { yield return new WaitForSecondsRealtime(float.Parse(spec[1], System.Globalization.CultureInfo.InvariantCulture)); continue; }
                if (spec[0] == "gamefx") { FpsProbe.HideGameFx = spec.Length > 1 && spec[1] == "off"; continue; }
                if (spec[0] == "preset") { int pi = Array.IndexOf(Presets.Names, spec[1]); if (pi >= 0) Presets.Apply(pi); HitCamMod.Log.Msg($"AUTOTEST preset {spec[1]}: {(pi >= 0 ? "applied" : "unknown")}"); continue; }
                if (spec[0] == "set") { foreach (var kv in spec.Skip(1)) { var kvp = kv.Split('='); HitCamMod.Log.Msg($"AUTOTEST set {kv}: {(kvp.Length == 2 && Presets.Set(kvp[0], kvp[1]) ? "ok" : "FAILED")}"); } continue; }
                if (spec[0] == "restart")
                {
                    // the game's own Restart Mission: the proving ground has to come back by itself
                    var sc = UnityEngine.Object.FindObjectOfType<GHPC.SceneController>();
                    HitCamMod.Log.Msg("AUTOTEST: restarting the mission (proving ground active: " + ProvingGround.Active + ")");
                    if (sc != null) sc.RestartActiveScene();
                    float r0 = Time.realtimeSinceStartup;
                    while (ProvingGround.Active && Time.realtimeSinceStartup - r0 < 30f) yield return null;
                    while (!ProvingGround.Active && Time.realtimeSinceStartup - r0 < 180f) yield return null;
                    var me = PlayerInput.Instance != null ? PlayerInput.Instance.CurrentPlayerUnit : null;
                    HitCamMod.Log.Msg($"AUTOTEST: after the restart (+{Time.realtimeSinceStartup - r0:0} s) the proving ground is {(ProvingGround.Active ? "back" : "NOT back")}; you are in {(me != null ? Recorder.SafeName(me) : "-")}; {ProvingGround.Describe()}");
                    player = me;
                    yield return new WaitForSecondsRealtime(3f);
                    continue;
                }
                if (spec[0] == "near")
                {
                    // near:<arsenal round>:<metres>: a round into the ground in front of the camera (explosion sound / kick)
                    yield return BlastSound.NearShot(spec, player);
                    continue;
                }
                if (spec[0] == "cal")
                {
                    // cal:<metres>: the gun / explosion events and the boom measured at that range (BlastSound.Calibrate)
                    yield return BlastSound.Calibrate(spec);
                    continue;
                }
                if (spec[0] == "gen" || spec[0] == "burst" || spec[0] == "bomb" || spec[0] == "sightbomb")
                {
                    yield return ArsenalShot(spec, units, player, targetWant);
                    continue;
                }
                if (spec[0] == "lab")
                {
                    float fired = Time.time;
                    if (!ProvingGround.LabShot(spec, out var labAim, out var labDir))
                    {
                        // a hurt target was respawned instead: try once more
                        yield return new WaitForSecondsRealtime(3f);
                        if (!ProvingGround.LabShot(spec, out labAim, out labDir)) { HitCamMod.Log.Warning($"AUTOTEST: shot {_shotIndex}: lab shot failed ({specs[_shotIndex]})"); continue; }
                    }
                    if (Cfg("world", "false") == "true") MelonCoroutines.Start(WorldShots(labAim, labDir, _shotIndex));   // the holes on the real vehicle too
                    if (Cfg("cine", "false") == "true") MelonCoroutines.Start(CineShots(labAim, labDir, _shotIndex));
                    HitCamMod.Log.Msg($"AUTOTEST: shot {_shotIndex}: proving ground {specs[_shotIndex]}");
                    float l0 = Time.realtimeSinceStartup;
                    _watch = null; _watchPlayback = null;
                    // this shot's own hit cam (not a cook-off replay of an earlier one still playing)
                    bool Mine(Playback pb) => pb != null && !pb.CookOffReplay && pb.Rec.CreatedReal >= fired - 0.2f;
                    while (Time.realtimeSinceStartup - l0 < 12f && !Mine(HitCamPlayer.Current)) yield return null;
                    if (!Mine(HitCamPlayer.Current)) { HitCamMod.Log.Warning("AUTOTEST: no hit cam for shot " + _shotIndex + " (miss?)"); continue; }
                    _watchPlayback = HitCamPlayer.Current;
                    _watch = _watchPlayback.Rec;
                    if (Cfg("capture", "true") == "true") Schedule(_watchPlayback);   // capture=false: timing runs, no screenshots
                    while (HitCamPlayer.Current == _watchPlayback) yield return null;
                    HitCamMod.Log.Msg($"AUTOTEST: shot {_shotIndex} done: {_watch.Title}; longest frame {_maxFrame * 1000f:0} ms; events: " + string.Join(" / ", _watch.Events.Where(ev => ev.Kind != EvKind.CrewHit).Select(ev => ev.Text).Take(25)));
                    _maxFrame = 0f;
                    if (Cfg("cine", "false") == "true") while (Time.realtimeSinceStartup - l0 < 28f) yield return null;   // the wreck stays for its pictures
                    yield return ProvingGround.RespawnHurt();
                    yield return new WaitForSecondsRealtime(1.5f);
                    continue;
                }
                string ammoWant = spec[0];
                string aspect = spec.Length > 1 ? spec[1] : "front";
                string thisTarget = spec.Length > 2 ? spec[2] : targetWant;
                var target = PickTarget(units, player, thisTarget);
                var ammo = PickAmmo(ammos, ammoWant);
                if (target == null || ammo == null) { HitCamMod.Log.Warning($"AUTOTEST: shot {_shotIndex}: no target/ammo ({ammoWant})"); continue; }
                HitCamMod.Log.Msg($"AUTOTEST: shot {_shotIndex}: {ammo.Name} at {Recorder.SafeName(target)} from {aspect}");
                var (aimPt, shotDir) = Fire(player, target, ammo, aspect);
                MelonCoroutines.Start(WorldShots(aimPt, shotDir, _shotIndex));
                if (Cfg("cine", "false") == "true") MelonCoroutines.Start(CineShots(aimPt, shotDir, _shotIndex));
                if (ModuleTest.IsModuleAspect(aspect)) MelonCoroutines.Start(ModuleTest.Shots(aimPt, shotDir, _shotIndex, aspect, _outDir, target));

                // wait for the hit cam to pick it up and finish
                float t0 = Time.realtimeSinceStartup;
                _watch = null; _watchPlayback = null;
                while (Time.realtimeSinceStartup - t0 < 6f && HitCamPlayer.Current == null) yield return null;
                if (HitCamPlayer.Current == null) { HitCamMod.Log.Warning("AUTOTEST: no hit cam for shot " + _shotIndex + " (miss?)"); yield return new WaitForSecondsRealtime(1f); continue; }
                _watchPlayback = HitCamPlayer.Current;
                _watch = _watchPlayback.Rec;
                ArcadeTest.OnShotHitCam(_shotIndex);
                var p = _watchPlayback;
                if (Cfg("capture", "true") == "true") Schedule(p);
                while (HitCamPlayer.Current == _watchPlayback) yield return null;
                HitCamMod.Log.Msg($"AUTOTEST: shot {_shotIndex} done: {_watch.Title}; longest frame {_maxFrame * 1000f:0} ms; events: " + string.Join(" / ", _watch.Events.Select(e => e.Text)));
                _maxFrame = 0f;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            // cookwait=<s>: keep the mission running for a late cook-off and capture its follow-up replay
            if (float.TryParse(Cfg("cookwait", "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cookWait) && cookWait > 0f)
            {
                float c0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - c0 < cookWait)
                {
                    var cur = HitCamPlayer.Current;
                    if (cur != null && cur.CookOffReplay && cur != _watchPlayback)
                    {
                        _watchPlayback = cur; _watch = cur.Rec; _shotIndex = 90;
                        _captureAt.Clear();
                        for (float t = cur.ShownFrom + 0.1f; t < cur.ShownFrom + 7f; t += 0.2f) _captureAt.Add(t);
                        _captured = 0;
                        HitCamMod.Log.Msg("AUTOTEST: watching the cook-off replay of " + cur.Rec.TargetName);
                    }
                    yield return null;
                }
            }
            if (Cfg("hud", "true") == "true") yield return ArcadeTest.Run();
            if (Cfg("aar", "true") == "true") yield return AfterAction.SelfTest(_outDir, Cfg("aarmenu", "true") == "true");
            Finish("all shots done");
        }

        /// Arsenal test shots: generated shells, autocannon bursts (one merged hit cam), bombs dropped on the roof.
        static IEnumerator ArsenalShot(string[] spec, List<Unit> units, Unit player, string targetWant)
        {
            string kind = spec[0];
            Arsenal.Entry e = kind == "bomb" ? Arsenal.Find("Bomb " + spec[1] + " kg") : Arsenal.Find(spec[1]);
            string tname = kind == "gen" ? (spec.Length > 3 ? spec[3] : targetWant) : (spec.Length > 3 ? spec[3] : spec.Length > 2 && (kind == "bomb" || kind == "sightbomb") ? spec[2] : targetWant);
            var target = PickTarget(units, player, tname);
            if (e == null || target == null) { HitCamMod.Log.Warning($"AUTOTEST: arsenal shot {string.Join(":", spec)}: nothing found"); yield break; }
            int count = kind == "burst" && spec.Length > 2 && int.TryParse(spec[2], out int n) ? n : 1;
            string aspect = kind == "gen" && spec.Length > 2 ? spec[2] : "side";
            HitCamMod.Log.Msg($"AUTOTEST: shot {_shotIndex}: {kind} {e.Ammo.Name} x{count} at {Recorder.SafeName(target)}");
            _maxFrame = 0f;
            Vector3 aim = Vector3.zero, dir = Vector3.forward;
            for (int i = 0; i < count; i++)
            {
                Arsenal.PrepareForFire(e);
                if (kind == "bomb") (aim, dir) = DropBomb(player, target, e.Ammo);
                else if (kind == "sightbomb")
                {
                    // the arsenal's own drop, the sight held on the target (sightbomb:<arsenal entry>:<target>)
                    Arsenal.TestSightDrop(e, target);
                    aim = target.transform.position; dir = Vector3.up;
                }
                else (aim, dir) = Fire(player, target, e.Ammo, aspect, i * 0.12f);
                if (i == 0) MelonCoroutines.Start(WorldShots(aim, dir, _shotIndex));
                if (i == 0 && ModuleTest.IsModuleAspect(aspect)) MelonCoroutines.Start(ModuleTest.Shots(aim, dir, _shotIndex, aspect, _outDir, target));
                if (count > 1) yield return new WaitForSecondsRealtime(0.12f);   // ~500 rounds/min
            }
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 6f && HitCamPlayer.Current == null) yield return null;
            if (HitCamPlayer.Current == null) { HitCamMod.Log.Warning("AUTOTEST: no hit cam for arsenal shot " + _shotIndex); yield break; }
            _watchPlayback = HitCamPlayer.Current;
            _watch = _watchPlayback.Rec;
            ArcadeTest.OnShotHitCam(_shotIndex);
            var p = _watchPlayback;
            Schedule(p);
            while (HitCamPlayer.Current == _watchPlayback) yield return null;
            HitCamMod.Log.Msg($"AUTOTEST: shot {_shotIndex} done: {_watch.Title}; session hits {p.Hits}; longest frame {_maxFrame * 1000f:0} ms; events: " + string.Join(" / ", p.Records.SelectMany(r => r.Events).Where(ev => ev.Kind != EvKind.CrewHit).Select(ev => ev.Text).Take(30)));
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// A bomb falling onto the target's roof from 300 m.
        static (Vector3, Vector3) DropBomb(Unit shooter, Unit target, AmmoType ammo)
        {
            Bounds b = UnitBounds(target);
            Vector3 aim = b.center + Vector3.up * b.extents.y * 0.3f;
            Vector3 pos = aim + Vector3.up * 300f;
            var round = LiveRoundUtility.GetNewLiveRound(ammo);
            round.Info = ammo;
            round.Guided = false;
            round.UseGravity = true;
            round.CurrentSpeed = ammo.MuzzleVelocity;
            round.MaxSpeed = ammo.MuzzleVelocity;
            round.Shooter = shooter;
            round.transform.position = pos;
            round.transform.rotation = Quaternion.LookRotation(Vector3.down);
            round.Init(null, target);
            return (aim, Vector3.down);
        }

        static Unit PickTarget(List<Unit> units, Unit player, string want)
        {
            units = units.Where(u => u != null && !u.Destroyed).ToList();
            if (TestGround.Active)
            {
                var mine = units.Where(u => TestGround.Spawned.Contains(u)).ToList();
                if (mine.Count > 0) units = mine;
            }
            if (ProvingGround.Active)
            {
                // the proving ground's targets (nearest first: the 100 m row before the 1000 m one)
                var mine = units.Where(ProvingGround.IsTarget).OrderBy(u => (u.transform.position - player.transform.position).sqrMagnitude).ToList();
                if (mine.Count > 0) units = mine;
            }
            if (want == "enemy")
                return units.Where(u => u.Allegiance != player.Allegiance).OrderBy(u => (u.transform.position - player.transform.position).sqrMagnitude).FirstOrDefault()
                    ?? units.FirstOrDefault();
            if (want == "any") return units.FirstOrDefault();
            // an exact name first, then a name that goes on with something other than a digit ("Mi-2" is not "Mi-24D")
            return units.FirstOrDefault(u => Recorder.SafeName(u).Equals(want, StringComparison.OrdinalIgnoreCase))
                ?? units.FirstOrDefault(u =>
                {
                    string n = Recorder.SafeName(u);
                    int i = n.IndexOf(want, StringComparison.OrdinalIgnoreCase);
                    return i >= 0 && (i + want.Length >= n.Length || !char.IsDigit(n[i + want.Length]));
                })
                ?? units.FirstOrDefault(u => Recorder.SafeName(u).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) ?? units.FirstOrDefault();
        }

        static AmmoType PickAmmo(List<AmmoType> all, string want)
        {
            bool Sub(AmmoType a) => a.Radius * 2000f < a.Caliber * 0.6f;
            switch (want.ToLower())
            {
                case "apfsds": return all.Where(a => a.Category == AmmoType.AmmoCategory.Penetrator && Sub(a) && a.Caliber >= 90).OrderByDescending(a => a.RhaPenetration).FirstOrDefault();
                case "ap": return all.Where(a => a.Category == AmmoType.AmmoCategory.Penetrator && !Sub(a) && a.Caliber >= 70).OrderByDescending(a => a.RhaPenetration).FirstOrDefault();
                case "heat": return all.Where(a => a.Category == AmmoType.AmmoCategory.ShapedCharge && a.Guidance == 0 && a.Caliber >= 70).OrderByDescending(a => a.RhaPenetration).FirstOrDefault();
                case "atgm": return all.Where(a => a.Category == AmmoType.AmmoCategory.ShapedCharge && a.Guidance != 0).OrderByDescending(a => a.RhaPenetration).FirstOrDefault();
                case "he": return all.Where(a => a.Category == AmmoType.AmmoCategory.Explosive && a.MuzzleVelocity > 200f && a.Caliber >= 70f && a.Caliber <= 160f).OrderByDescending(a => a.TntEquivalentKg).FirstOrDefault();
                default: return all.FirstOrDefault(a => (a.Name ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        /// Same steps WeaponSystem.Fire uses, minus the gun.
        public static (Vector3 aim, Vector3 dir) Fire(Unit shooter, Unit target, AmmoType ammo, string aspect, float jitter = 0f)
        {
            Bounds b = UnitBounds(target);
            Vector3 aim = b.center + (jitter > 0f ? UnityEngine.Random.insideUnitSphere * Mathf.Min(jitter, 0.4f) : Vector3.zero);
            Transform tt = target.transform;
            Vector3 dir;
            if (aspect.StartsWith("graze"))
            {
                // graze-<front|rear|side>-<deg>: find the real plate with a probe ray, then skim it at that angle
                var gp = aspect.Split('-');
                string face = gp.Length > 1 ? gp[1] : "front";
                float deg = gp.Length > 2 && float.TryParse(gp[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dd) ? dd : 6f;
                if (GrazeShot(target, b, face, deg, out var gAim, out var gDir))
                {
                    aim = gAim; dir = gDir;
                    Vector3 gpos = aim - dir * 30f;
                    var gr = LiveRoundUtility.GetNewLiveRound(ammo);
                    gr.Info = ammo; gr.Guided = false; gr.UseGravity = false;
                    gr.CurrentSpeed = ammo.MuzzleVelocity; gr.MaxSpeed = ammo.MuzzleVelocity; gr.Shooter = shooter;
                    gr.transform.position = gpos;
                    gr.transform.rotation = Quaternion.LookRotation(dir);
                    gr.Init(null, target);
                    HitCamMod.Log.Msg($"AUTOTEST graze {face} {deg:0.#} deg at {aim}");
                    return (aim, dir);
                }
                HitCamMod.Log.Warning("AUTOTEST graze: no plate found for " + aspect);
                aspect = "front";
            }
            switch (aspect)
            {
                case "side": dir = tt.right; aim += tt.up * b.extents.y * 0.1f; break;
                case "rear": dir = -tt.forward; break;
                case "top": dir = (tt.up * 2.5f + tt.forward * 0.6f).normalized; break;
                case "turret": dir = tt.forward; aim += tt.up * b.extents.y * 0.55f; break;
                case "mantlet":
                    // down the target's own gun, just beside the barrel: the mantlet face, wherever the turret points
                    var mz = Arsenal.MainGun(target)?.MuzzleIdentity;
                    if (mz == null) goto default;
                    dir = mz.forward;
                    aim = mz.position - dir * 4f + Vector3.Cross(Vector3.up, dir).normalized * 0.3f;
                    break;
                default:
                    if (ModuleAim(target, aspect, ref aim, out dir)) break;
                    dir = tt.forward; aim += tt.up * b.extents.y * 0.15f; break;
            }
            // a moving target (a helicopter): lead it by the round's 30 m of flight
            var trb = target.GetComponent<Rigidbody>();
            if (trb != null && trb.velocity.sqrMagnitude > 1f) aim += trb.velocity * (30f / Mathf.Max(ammo.MuzzleVelocity, 50f));
            Vector3 pos = aim + dir * 30f + Vector3.up * 0.3f;
            var round = LiveRoundUtility.GetNewLiveRound(ammo);
            round.Info = ammo;
            round.Guided = false;
            round.UseGravity = false;
            round.CurrentSpeed = ammo.MuzzleVelocity;
            round.MaxSpeed = ammo.MuzzleVelocity;
            round.Shooter = shooter;
            round.transform.position = pos;
            round.transform.rotation = Quaternion.LookRotation((aim - pos).normalized);
            round.Init(null, target);
            return (aim, (aim - pos).normalized);
        }

        /// Module aim points for the world module damage: barrel-<fraction of its length from the muzzle> (from the side),
        /// track (its upper run, from the side), engine (down onto the engine deck from behind), boom (a helicopter's tail
        /// boom, from the side). `dir` points from the aim back toward where the round comes from.
        static bool ModuleAim(Unit target, string aspect, ref Vector3 aim, out Vector3 dir)
        {
            dir = Vector3.forward;
            Transform tt = target.transform;
            var zones = WorldDamage.UnitRoots(target).SelectMany(r => r.GetComponentsInChildren<GHPC.Equipment.DestructibleComponent>(true)).ToArray();
            GHPC.Equipment.DestructibleComponent Zone(Func<string, bool> f) =>
                zones.FirstOrDefault(z => z != null && z.ComponentCollision != null && f((z.Name ?? "").ToLowerInvariant()));
            if (aspect.StartsWith("barrel"))
            {
                var ws = Arsenal.MainGun(target);
                var mz = ws?.MuzzleIdentity;
                if (mz == null) return false;
                float frac = 0.35f;
                var parts = aspect.Split('-');
                if (parts.Length > 1) float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out frac);
                var wb = WorldBarrel.For(ws, target);
                float len = wb != null ? wb.Length : 3f;
                aim = mz.position - mz.forward * (frac * len);
                Vector3 side = Vector3.Cross(Vector3.up, mz.forward).normalized;
                dir = (side + Vector3.up * 0.12f).normalized;
                HitCamMod.Log.Msg($"AUTOTEST barrel aim {frac:0.00} of {len:0.00} m from the muzzle at {aim}");
                return true;
            }
            if (aspect == "cook")
            {
                // from the side into the middle of the hull, where the T-72's / T-80's carousel sits under the turret
                Bounds hb = UnitBounds(target);
                aim = hb.center + tt.up * hb.extents.y * -0.25f;
                dir = tt.right;
                return true;
            }
            if (aspect == "trackfront")
            {
                // the front of a track from straight ahead, as you'd hit it facing the tank
                var z = Zone(n => n.Contains("left track")) ?? Zone(n => n.Contains("track"));
                if (z == null) return false;
                var zc = z.ComponentCollision;
                // rays from straight ahead at the track's own lateral place: the frontmost point of its hit zone (the idler end)
                // (a switched-off "Raycast Only" zone can't be raycast: the front of its box then)
                Bounds zbb = WorldModules.ZoneBounds(zc);
                Vector3 lp = tt.InverseTransformPoint(zbb.center);
                float bestD = float.MaxValue;
                Vector3 front = zbb.ClosestPoint(zbb.center + tt.forward * 20f);
                for (float y = 0.15f; y < 1.6f; y += 0.05f)
                    for (float dx = -0.2f; dx <= 0.2f; dx += 0.1f)
                    {
                        Vector3 o = tt.TransformPoint(new Vector3(lp.x + dx, y, lp.z + 15f));
                        if (zc.Raycast(new Ray(o, -tt.forward), out var th, 30f) && th.distance < bestD) { bestD = th.distance; front = th.point; }
                    }
                aim = front - tt.forward * 0.03f;
                dir = tt.forward;
                HitCamMod.Log.Msg($"AUTOTEST track front aim on '{z.Name}' at {aim} (local {tt.InverseTransformPoint(aim)}); zone {zc.GetType().Name} '{zc.name}' local centre {lp}, size {zbb.size}, layer {LayerMask.LayerToName(zc.gameObject.layer)}, trigger {zc.isTrigger}, enabled {zc.enabled}");
                foreach (var tz in zones.Where(q => q != null && q.ComponentCollision != null && (q.Name ?? "").ToLowerInvariant().Contains("track")))
                    HitCamMod.Log.Msg($"AUTOTEST   track zone '{tz.Name}' collider '{tz.ComponentCollision.name}' {tz.ComponentCollision.GetType().Name} local {tt.InverseTransformPoint(tz.ComponentCollision.bounds.center)} size {tz.ComponentCollision.bounds.size}");
                foreach (var r in WorldDamage.UnitRoots(target).SelectMany(q => q.GetComponentsInChildren<Renderer>(false)).Where(r => r.name.ToLowerInvariant().Contains("track")))
                    HitCamMod.Log.Msg($"AUTOTEST   track model '{r.name}' local {tt.InverseTransformPoint(r.bounds.center)} size {r.bounds.size}");
                return true;
            }
            if (aspect == "track")
            {
                var z = Zone(n => n.Contains("left track")) ?? Zone(n => n.Contains("track"));
                if (z == null) return false;
                Bounds zb = WorldModules.ZoneBounds(z.ComponentCollision);
                aim = zb.center + tt.up * zb.extents.y * 0.8f;          // the top run (the hit zone's loop is hollow in the middle)
                dir = Vector3.Dot(zb.center - tt.position, tt.right) < 0f ? -tt.right : tt.right;
                return true;
            }
            if (aspect == "engine")
            {
                var z = Zone(n => n == "engine" || n.Contains("engine"));
                if (z == null) return false;
                aim = z.ComponentCollision.bounds.center;
                dir = (tt.up * 1.6f - tt.forward * 0.5f).normalized;
                return true;
            }
            if (aspect == "boom")
            {
                var z = Zone(n => n.Contains("tail drive shaft")) ?? Zone(n => n.Contains("tail"));
                if (z == null) return false;
                // half way down the boom (the shaft zone starts inside the fuselage)
                var hub = Zone(n => n.Contains("tail rotor"));
                aim = hub != null ? Vector3.Lerp(z.ComponentCollision.bounds.center, hub.ComponentCollision.bounds.center, 0.3f) : z.ComponentCollision.bounds.center;
                // onto the boom's own armour, if a ray across it finds it
                var dc0 = z.ComponentCollision;
                // the shaft runs along the top of the boom: scan down across the boom for its skin (the helicopter's armour)
                Vector3 across = tt.right, onArmour = aim;
                var armour = WorldDamage.UnitRoots(target).SelectMany(r => r.GetComponentsInChildren<Collider>(false)).Where(c => c != null && !c.isTrigger && c.TryGetComponent<GHPC.IArmor>(out _)).ToList();
                bool found = false;
                for (float dy = 0f; dy > -2.0f && !found; dy -= 0.08f)
                    foreach (var c in armour)
                        if (c.Raycast(new Ray(aim + tt.up * dy + across * 6f, -across), out var bh, 12f)) { onArmour = bh.point - across * 0.02f + tt.up * -0.1f; found = true; break; }
                aim = onArmour;
                HitCamMod.Log.Msg($"AUTOTEST boom aim {(found ? "on the boom's skin" : "NOT on armour")} at {aim}");
                dir = tt.right;
                return true;
            }
            return false;
        }

        /// A point on one of the target's armour plates and a direction skimming it at `deg` degrees.
        static bool GrazeShot(Unit target, Bounds b, string face, float deg, out Vector3 aim, out Vector3 dir)
        {
            Transform tt = target.transform;
            // the armour's own extent in the vehicle's frame (renderer bounds include map icons and the like)
            bool any = false;
            Bounds lb = default;
            foreach (var c in WorldDamage.HullColliders(target).set)
            {
                if (c == null) continue;
                var wb = c.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = tt.InverseTransformPoint(wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1)));
                    if (!any) { lb = new Bounds(p, Vector3.zero); any = true; } else lb.Encapsulate(p);
                }
            }
            if (!any) lb = new Bounds(tt.InverseTransformPoint(b.center), b.size);
            Vector3 lc = lb.center, le = lb.extents;
            Vector3 lo, lp, la;
            switch (face)
            {
                case "rear":   // down onto the rear (engine) deck, then forward along it
                    lo = lc + new Vector3(0f, le.y + 2f, -le.z * 0.6f); lp = Vector3.down; la = Vector3.forward; break;
                case "side":
                    lo = lc + new Vector3(le.x + 2f, -le.y * 0.3f, 0f); lp = Vector3.left; la = Vector3.forward; break;
                default:       // the upper glacis, then up it toward the turret
                    lo = lc + new Vector3(0f, -le.y * 0.1f, le.z + 2f); lp = Vector3.back; la = Vector3.back; break;
            }
            Vector3 o = tt.TransformPoint(lo), probe = tt.TransformDirection(lp), along = tt.TransformDirection(la);
            aim = Vector3.zero; dir = Vector3.forward;
            // the vehicle's own armour colliders, each tested on its own (a plain scene raycast only meets its coarse
            // bounds / vision / near-miss colliders)
            var ray = new Ray(o, probe);
            var found = new List<RaycastHit>();
            if (WorldDamage.ArmourHit(target, ray, 12f, out var rh)) found.Add(rh);    // scene query on the armour layers, kept to this unit's plates
            found.Sort((x, y) => x.distance.CompareTo(y.distance));
            var hits = found.ToArray();
            foreach (var hit in hits)
            {
                Vector3 n = hit.normal;
                Vector3 t = Vector3.ProjectOnPlane(along, n).normalized;
                if (t.sqrMagnitude < 0.5f) continue;
                float a = deg * Mathf.Deg2Rad;
                dir = (t * Mathf.Cos(a) - n * Mathf.Sin(a)).normalized;
                aim = hit.point;
                return true;
            }
            HitCamMod.Log.Msg($"AUTOTEST graze probe {face}: from {o} along {probe}, armour bounds local {lb.center}/{lb.size}, {hits.Length} hits: " + string.Join(", ", hits.Take(6).Select(h => h.collider.name + (h.collider.TryGetComponent<GHPC.IArmor>(out var a2) ? "[armour, unit " + (a2.Unit as Unit == target) + "]" : ""))));
            return false;
        }

        /// Photographs the real vehicle in the game world right after the impact (sparks, hole, shrapnel).
        /// cine=true: pictures of the wreck for the mod page, the way you'd frame them by hand: low, from the side the
        /// round came in, a little off its line, the hole and the smoke in view, at several moments after the hit.
        /// Files: cine{shot}_{k}.png with the game's post-processing, cine{shot}_{k}_raw.png without.
        static IEnumerator CineShots(Vector3 aim, Vector3 shotDir, int shot)
        {
            float t0 = Time.realtimeSinceStartup;
            Vector3 back = -shotDir; back.y = 0f; back = back.sqrMagnitude > 1e-4f ? back.normalized : Vector3.back;
            // (seconds after the hit, yaw off the round's line, distance, height, field of view)
            var views = new (float t, float yaw, float dist, float up, float fov)[]
            {
                (1.2f, 32f, 6.5f, 1.3f, 34f), (3.5f, -48f, 8.5f, 1.6f, 36f), (7f, 68f, 12f, 2.4f, 38f),
                (12f, 18f, 7.5f, 0.9f, 32f), (18f, -28f, 16f, 3.5f, 36f), (26f, 115f, 10f, 1.8f, 38f),
            };
            for (int k = 0; k < views.Length; k++)
            {
                while (Time.realtimeSinceStartup - t0 < views[k].t) yield return null;
                var v = views[k];
                Vector3 from = aim + Quaternion.AngleAxis(v.yaw, Vector3.up) * back * v.dist + Vector3.up * v.up;
                Vector3 look = aim - Vector3.up * 0.35f;
                ModuleTest.Snap(look, from, $"cine{shot}_{k}", $"{v.t:0} s after the hit", 1920, 1080, v.fov, true);
                ModuleTest.Snap(look, from, $"cine{shot}_{k}_raw", "no post-processing", 1920, 1080, v.fov, false);
            }
        }

        static IEnumerator WorldShots(Vector3 aim, Vector3 shotDir, int shot)
        {
            float t0 = Time.realtimeSinceStartup, since = Time.time;
            Vis.Init();
            var main = WorldDamage.MainCamera();
            GameObject go;
            if (main != null)
            {
                if (shot == 0)
                    HitCamMod.Log.Msg($"AUTOTEST main camera '{main.name}' path={main.actualRenderingPath} hdr={main.allowHDR} clear={main.clearFlags} mask={main.cullingMask:X} comps: "
                        + string.Join(", ", main.GetComponents<Component>().Select(c => c.GetType().Name)));
                go = UnityEngine.Object.Instantiate(main.gameObject);
                foreach (Transform ch in go.transform) UnityEngine.Object.Destroy(ch.gameObject);
                foreach (var mb in go.GetComponents<Behaviour>())
                {
                    if (mb is Camera) continue;
                    string n = mb.GetType().FullName ?? "";
                    bool render = n.Contains("PostProcess") || n.Contains("Render") || n.Contains("Fog") || n.Contains("Sky") || n.Contains("Atmos") || n.Contains("Scatter") || n.Contains("Occlusion") || n.Contains("Celestial");
                    if (mb is AudioListener || !render) UnityEngine.Object.Destroy(mb);
                }
                go.name = "autotest inspection camera";
            }
            else go = new GameObject("autotest inspection camera", typeof(Camera));
            var cam = go.GetComponent<Camera>();
            cam.cullingMask = WorldDamage.WorldCameraMask() & ~(1 << Vis.Layer);
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 42f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 4000f;
            cam.enabled = false;
            // no MSAA: the game's deferred camera has none, and with it near-flush decals dither per sample
            var rt = new RenderTexture(1280, 720, 24) { antiAliasing = 1 };
            cam.targetTexture = rt;
            var near = UnityEngine.Object.FindObjectsOfType<Renderer>().Where(r => (r.bounds.center - aim).sqrMagnitude < 36f).ToList();
            HitCamMod.Log.Msg($"AUTOTEST inspect shot {shot}: aim {aim}, renderers within 6 m: {near.Count} (enabled {near.Count(r => r.enabled && r.gameObject.activeInHierarchy)}), e.g. " + string.Join(", ", near.Take(6).Select(r => r.name + "/" + LayerMask.LayerToName(r.gameObject.layer) + (r.enabled ? "" : "(off)"))));
            Vector3 side = Vector3.Cross(Vector3.up, shotDir).normalized;
            // 0-2: the vehicle (close, then wide for the shrapnel); 3-6: close-ups of this shot's first 3D plate (else its flat decals):
            // 3 while it is hot (oblique), 4 cooled (oblique, no post-processing so the depth of field doesn't blur it),
            // 5 cooled (face-on, no post), 6 cooled (oblique, the game's post-processing); 7-8: its second plate (an exit hole), like 4-5
            float[] at = { 0.1f, 0.7f, 4f, 0.45f, 4.3f, 4.45f, 4.6f, 4.75f, 4.9f };
            int[] order = { 0, 3, 1, 2, 4, 5, 6, 7, 8 };
            foreach (int k in order)
            {
                while (Time.realtimeSinceStartup - t0 < at[k]) yield return null;
                bool crisp = k == 4 || k == 5 || k >= 7;
                if (k >= 3)
                {
                    var mine = WorldPlate.All.Where(w => w != null && w.Has3D && w.Born >= since - 0.01f).OrderBy(w => w.Fx.Mode == PlateMode.Trench ? 0 : 1).ThenBy(w => w.Born).ToList();    // grooves and slots first
                    // then the hits that only got flat decals (no hull under them, covered by a net or stowage)
                    mine.AddRange(WorldPlate.All.Where(w => w != null && !w.Has3D && w.Wait == null && w.Born >= since - 0.01f).OrderBy(w => w.Born));
                    int which = k >= 7 ? 1 : 0;
                    if (mine.Count <= which) { if (k == 3 || k == 7) HitCamMod.Log.Msg($"AUTOTEST inspect shot {shot}: no plate #{which}"); continue; }
                    var pl = mine[which];
                    if (k == 3 || k == 7) HitCamMod.Log.Msg($"AUTOTEST inspect shot {shot}: close-ups of plate #{which} " + (pl.Has3D ? $"{pl.Fx.Mode}{(pl.Fx.Exit ? " (exit)" : "")} on {pl.Fx.Hit.Name}" : "(flat decals only)"));
                    Transform t = pl.Has3D ? pl.Fx.T : pl.transform;
                    Vector3 outN = -t.forward;
                    // a groove: framed on its middle, far enough back to see it end to end
                    Vector3 look = pl.Has3D ? pl.Fx.ViewCenter : t.position;
                    float dist = pl.Has3D && pl.Fx.Mode == PlateMode.Trench ? Mathf.Clamp(pl.Fx.ViewRadius * 2.6f, 0.35f, 3f)
                               : Mathf.Clamp((pl.Has3D ? pl.Fx.ViewRadius : Mathf.Max(pl.HoleR * 4f, 0.06f)) * 5f, 0.35f, 3f);
                    Vector3 view = k == 5 || k == 8 ? outN : (outN * 0.72f + t.right * 0.55f + t.up * 0.42f).normalized;
                    cam.transform.position = look + view * dist;
                    cam.transform.LookAt(look, Mathf.Abs(Vector3.Dot(outN, Vector3.up)) > 0.9f ? t.up : Vector3.up);
                }
                else
                {
                    // close-up for the hole, then a wider view for the shrapnel on the ground
                    float back = k < 2 ? 3.2f : 7f;
                    cam.transform.position = aim - shotDir * back + side * (k < 2 ? 1.8f : 4f) + Vector3.up * (k < 2 ? 0.9f : 2.5f);
                    cam.transform.LookAt(aim - (k < 2 ? Vector3.zero : Vector3.up * 0.8f));
                }
                var post = go.GetComponents<Behaviour>().Where(b => !(b is Camera) && b.enabled).ToList();
                if (crisp) foreach (var b in post) b.enabled = false;
                try
                {
                    cam.Render();
                    HitCamMod.Log.Msg($"AUTOTEST inspect shot {shot} frame {k}: plates drawn 3D {WorldDamage.Last3D}, flat {WorldDamage.LastFlat}");
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    File.WriteAllBytes(Path.Combine(_outDir, $"world{shot}_{k}.png"), tex.EncodeToPNG());
                    UnityEngine.Object.Destroy(tex);
                }
                catch (Exception e) { HitCamMod.Log.Warning("world capture failed: " + e.Message); }
                if (crisp) foreach (var b in post) if (b != null) b.enabled = true;
            }
            UnityEngine.Object.Destroy(go);
            rt.Release();
        }

        static Bounds UnitBounds(Unit u)
        {
            var rs = u.GetComponentsInChildren<MeshRenderer>(false);
            if (rs.Length == 0) return new Bounds(u.transform.position + Vector3.up * 1.2f, new Vector3(3, 2.4f, 6));
            Bounds b = rs[0].bounds;
            foreach (var r in rs) if (r.bounds.size.magnitude < 20f) b.Encapsulate(r.bounds);
            return b;
        }

        /// Capture times: one approach frame, a dense run through the impact slow motion (dense=true), then the interior and aftermath.
        static void Schedule(Playback p)
        {
            _captureAt.Clear();
            if (FilmStep > 0f)
            {
                // film: every step from the approach to the end (and on through a cook-off wait)
                for (float t = 0.3f; t < p.Duration + (Cfg("late", "false") == "true" ? 12f : 0f); t += FilmStep) _captureAt.Add(t);
                _captured = 0;
                return;
            }
            _captureAt.Add(0.55f);
            float step = float.TryParse(Cfg("densestep", "0.12"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float st) ? st : 0.12f;
            if (Cfg("dense", "true") == "true")
                for (float t = p.A - 0.04f; t < p.A + p.Dwell + 1.0f; t += step) _captureAt.Add(t);
            else _captureAt.Add(p.A + 0.05f);
            _captureAt.AddRange(new[] { p.A + p.B * 0.4f, p.A + p.B * 0.75f, p.A + p.B + p.C * 0.6f });
            // late=true: the aftermath too (fires, ammo cook-offs), every 0.25 s
            if (Cfg("late", "false") == "true")
            {
                for (float t = p.A + p.B * 0.6f; t < p.Duration - 0.1f; t += 0.25f) _captureAt.Add(t);
                // the replay may keep going (a propellant fire waiting for its cook-off): keep capturing, slower
                for (float t = Mathf.Max(p.Duration, p.A + p.B * 0.6f); t < p.Duration + 16f; t += 0.5f) _captureAt.Add(t);
            }
            _captureAt.Sort();
            _captured = 0;
        }

        public static void OnPlaybackFrame(Playback p)
        {
            if (!Active || p != _watchPlayback || _captured >= _captureAt.Count) return;
            if (p.T < _captureAt[_captured]) return;
            int k = _captured++;
            try
            {
                var rt = HitCamPlayer.Texture;
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(_outDir, $"shot{_shotIndex}_{k}_pip.png"), tex.EncodeToPNG());
                if (p.T > 5f) HitCamMod.Log.Msg($"AUTOTEST saved shot{_shotIndex}_{k}_pip at T={p.T:0.00}");
                UnityEngine.Object.Destroy(tex);
                if (k == _captureAt.Count - 2) ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, $"shot{_shotIndex}_{k}_screen.png"));
            }
            catch (Exception e) { HitCamMod.Log.Warning("capture failed: " + e.Message); }
        }

        static void Finish(string why)
        {
            HitCamMod.Log.Msg(Perf.Report(90));
            HitCamMod.Log.Msg("AUTOTEST DONE: " + why);
            File.WriteAllText(Path.Combine(HitCamMod.DataDir, "autotest_done.txt"), why + " " + DateTime.Now.ToString("s"));
            if (Cfg("quit", "true") == "true") MelonCoroutines.Start(QuitSoon());
        }

        static IEnumerator QuitSoon()
        {
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }
    }
}
