using System;
using System.Linq;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(GHPCHitCam.HitCamMod), "GHPC Combat Overhaul", "1.1.3", "Damian")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace GHPCHitCam
{
    public class HitCamMod : MelonMod
    {
        internal static ModLog Log;
        internal static HitCamMod Instance;
        internal static string DataDir;

        /// every mod running next to this one + version on one line, so a report's log says what it ran with
        public override void OnLateInitializeMelon()
        {
            try
            {
                var others = MelonLoader.MelonBase.RegisteredMelons.Where(m => m != null && m != this && m.Info != null)
                    .Select(m => m.Info.Name + " " + m.Info.Version).ToList();
                Log.Msg($"Running with {others.Count} other mod(s): " + (others.Count > 0 ? string.Join(", ", others) : "none") +
                        $"; MelonLoader {MlVersion()}, game build {Application.version}");
            }
            catch (Exception e) { Log.Warning("mod list: " + e.Message); }
        }

        /// MelonLoader's version, read the long way round: it's a property in some MelonLoader versions and a const in
        /// others, and asking for it directly blew up this whole startup step on the "wrong" one (a tester on an older
        /// MelonLoader got MissingMethodException: BuildInfo.get_Version)
        static string MlVersion()
        {
            try
            {
                var t = typeof(MelonBase).Assembly.GetType("MelonLoader.BuildInfo");
                var p = t?.GetProperty("Version", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p != null) return p.GetValue(null, null)?.ToString();
                var f = t?.GetField("Version", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (f != null) return f.GetValue(null)?.ToString();
            }
            catch { }
            return typeof(MelonBase).Assembly.GetName().Version?.ToString() ?? "?";
        }

        public override void OnInitializeMelon()
        {
            Log = new ModLog(LoggerInstance);
            Instance = this;
            UnityErrors.Install();
            DataDir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "HitCam");
            System.IO.Directory.CreateDirectory(DataDir);
            try
            {
                string cfg = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                Settings.FreshInstall = !System.IO.File.Exists(cfg) || !System.IO.File.ReadAllText(cfg).Contains("[HitCam]");
            }
            catch { }
            try { Settings.Init(); }
            catch (Exception e) { Log.Error("Startup: the settings failed to load, the mod can't run without them (please send this log): " + e); throw; }
            Part("hit recorder", () => Recorder.Install(HarmonyInstance));
            Part("arsenal", () => Arsenal.Install(HarmonyInstance));
            Part("safety hooks", () => Safety.Install(HarmonyInstance));
            Part("vehicle mods support", () => ModdedVehicles.Install(HarmonyInstance));
            Part("armor sim", () => ArmorSim.Init(HarmonyInstance));
            Part("proving ground", () => { ProvingGround.Init(); ProvingGround.Install(HarmonyInstance); });
            Part("after action report", () => { AfterAction.Init(); AfterAction.Install(HarmonyInstance); });
            Part("kill feed", () => KillFeed.Install(HarmonyInstance));
            Part("blast sound", () => BlastSound.Install(HarmonyInstance));
            Part("settings window", () => ConfigMenu.Install(HarmonyInstance));
            Part("self test", AutoTest.Init);
            Part("settings rescue", () => PrefsRescue.Apply(Settings.Cat));
            Part("settings update", Settings.Migrate);
            Perf.On = Settings.DebugLog.Value || AutoTest.Active;
            Part("perf hooks", () => Perf.InstrumentHooks(HarmonyInstance));
            Log.Info($"GHPC Combat Overhaul ready. Settings: {Hotkeys.Label(Settings.ConfigKey)}, hit cam on/off: {Hotkeys.Label(Settings.ToggleKey)}, replay the last hit: {Hotkeys.Label(Settings.ReplayKey)}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Log.Msg("Scene loaded: " + sceneName + " (" + buildIndex + ")");
            // each on its own, one throwing can't keep the menu rows or the proving ground from the others
            Run("SceneUtil.Recheck", SceneUtil.Recheck);
            Run("main menu", () => ArmorSimMenu.OnSceneLoaded(sceneName));
            if (ArmorSimMenu.InMenu) Run("What's new", WhatsNew.OnMenu);
            Run("proving ground", () => ProvingGround.OnSceneLoaded(sceneName));
            Run("self test", () => AutoTest.OnSceneLoaded(sceneName));
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            Log.Msg("Scene unloaded: " + sceneName);
            Run("main menu", () => ArmorSimMenu.OnSceneUnloaded(sceneName));
            // mission scene going away invalidates every unit ref we hold
            SceneUtil.Recheck();
            if (SceneUtil.IsGameplayGone())
            {
                // each one on its own: one failing must not leave the others on the old mission (a proving ground still "on"
                // would keep the next mission's unit menu to one vehicle)
                Step("AAR", AfterAction.OnGameplayGone);   // first: saves the mission's hits before the records get dropped
                Step("Armor Sim", ArmorSim.Reset);
                Step("proving ground", ProvingGround.Reset);
                Step("hit cam", HitCamPlayer.Reset);
                Step("recorder", Recorder.Reset);
                Step("world damage", WorldDamage.Reset);
                Step("arsenal", Arsenal.Reset);
                Step("test range", TestRange.Reset);
                Step("HUD", ArcadeHud.Reset);
                Step("blast sound", BlastSound.Reset);
                Step("warm-up", Warmup.Reset);
            }
        }

        static void Step(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Warning($"Leaving the mission ({what}): {e.Message}"); }
        }

        /// one part of startup; if it throws the rest still loads and the log says which part
        static void Part(string what, Action run)
        {
            try { run(); }
            catch (Exception e) { Log.Error($"Startup: the {what} part failed, the rest of the mod still loads (please send this log): {e}"); }
        }

        static readonly System.Collections.Generic.Dictionary<string, int> _fails = new System.Collections.Generic.Dictionary<string, int>();
        static string _failPart;

        /// one part of a frame on its own: one that throws doesn't take the rest down with it (F12, the hit cam...), and
        /// the log gets it a few times with the stack, not every frame
        static void Run(string what, Action a)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (AutoTest.Active && what == (_failPart ??= AutoTest.Opt("fail_part", ""))) throw new Exception("self-test: this part made to fail");   // fail_part=<name>
                using (Perf.T(what)) a();
            }
            catch (Exception e)
            {
                _fails.TryGetValue(what, out int n);
                _fails[what] = ++n;
                if (n <= 3) Log.Error($"{what} failed (please send this log): {e}");
                else if (n == 4) Log.Error($"{what} keeps failing, not logging it again");
            }
            if (ArmorSimMenu.InMenu)
            {
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                _frameTicks += dt;
                _partTicks.TryGetValue(what, out long pt);
                _partTicks[what] = pt + dt;
            }
        }

        static readonly System.Collections.Generic.Dictionary<string, long> _partTicks = new System.Collections.Generic.Dictionary<string, long>();
        static long _menuTicks, _frameTicks;
        static int _menuFrames, _slowFrames;
        static bool _menuCostSaid;

        /// the mod's own cost in the main menu, looked at every ~600 frames: should be about 0.1 ms a frame. when most frames
        /// take over 1 ms it says once which part (1.1.1 took 2-14 ms there, a game getter scanning the scene, and nobody saw
        /// it in a log). not while a window of the mod is open (the report, F12 draw a lot on purpose), and one hitch
        /// (saving a mission's hits as it ends) doesn't count
        static void MenuCost()
        {
            long f = _frameTicks;
            _frameTicks = 0;
            if (!ArmorSimMenu.InMenu || AfterAction.IsOpen || ConfigMenu.IsOpen)
            {
                if (_menuFrames > 0) { _menuFrames = 0; _slowFrames = 0; _menuTicks = 0; _partTicks.Clear(); }
                return;
            }
            _menuTicks += f;
            if (f * 1000.0 / System.Diagnostics.Stopwatch.Frequency > 1.0) _slowFrames++;
            if (++_menuFrames < 600) return;
            double ms = _menuTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _menuFrames;
            if (_slowFrames > _menuFrames / 2 && !_menuCostSaid)
            {
                _menuCostSaid = true;
                var top = _partTicks.OrderByDescending(kv => kv.Value).Take(3)
                    .Select(kv => $"{kv.Key} {kv.Value * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _menuFrames:0.00} ms");
                Log.Warning($"Main menu: the mod takes {ms:0.0} ms a frame here, it should be about 0.1 ({string.Join(", ", top)}). Please send this log.");
            }
            else if (AutoTest.Active) Log.Msg($"Main menu: the mod takes {ms:0.00} ms a frame here ({_slowFrames} of {_menuFrames} frames over 1 ms)");
            _menuFrames = 0; _slowFrames = 0; _menuTicks = 0; _partTicks.Clear();
        }

        static void Keys()
        {
            if (Hotkeys.Down(Settings.ToggleKey))
            {
                Settings.Enabled.Value = !Settings.Enabled.Value;
                Log.Info("Hit cam " + (Settings.Enabled.Value ? "on" : "off"));
                if (!Settings.Enabled.Value) HitCamPlayer.StopCurrent();
            }
            if (Hotkeys.Down(Settings.ReplayKey))
                HitCamPlayer.ReplayLast();
        }

        public override void OnUpdate()
        {
            Run("main menu watch", ArmorSimMenu.Watch);
            try { Perf.FrameStart(); MenuCost(); } catch { }
            Run("keys", Keys);
            Run("Arsenal.Update", Arsenal.Update);
            Run("TestRange.Update", TestRange.Update);
            Run("ArmorSim.Update", ArmorSim.Update);
            Run("ProvingGround.Update", ProvingGround.Update);
            Run("Recorder.Update", Recorder.Update);
            Run("BlastSound.Update", BlastSound.Update);
            Run("WorldDamage.Update", WorldDamage.Update);
            Run("HitCamPlayer.Update", HitCamPlayer.Update);
            Run("AfterAction.Update", AfterAction.Update);
            Run("Archive.Update", Archive.Update);
            Run("ConfigMenu.Update", ConfigMenu.Update);
            Run("ArcadeHud.Update", ArcadeHud.Update);
            Run("AutoTest.Update", AutoTest.Update);
            Run("ModdedVehicles.Update", ModdedVehicles.Update);
            Run("WhatsNew.Update", WhatsNew.Update);
            Run("Warmup.Update", Warmup.Update);
        }

        public override void OnLateUpdate()
        {
            Run("HitCamPlayer.LateUpdate", HitCamPlayer.LateUpdate);
            Run("AfterAction.LateUpdate", AfterAction.LateUpdate);
            Run("ArmorSim.LateUpdate", ArmorSim.LateUpdate);
        }

        public override void OnGUI()
        {
            if (!ArmorSim.Active) Run("ArcadeHud.Draw", ArcadeHud.Draw);
            Run("HitCamUI.Draw", HitCamUI.Draw);
            // settings window (F12) is on its own: other windows step aside while it's open
            bool cfg = ConfigMenu.IsOpen;
            if (!cfg) Run("Arsenal.Draw", Arsenal.Draw);
            if (!cfg) Run("TestRange.Draw", TestRange.Draw);
            Run("AfterAction.Draw", AfterAction.Draw);
            Run("ArmorSim.Draw", ArmorSim.Draw);
            if (!cfg) Run("ProvingGround.Draw", ProvingGround.Draw);
            Run("ConfigMenu.Draw", ConfigMenu.Draw);
        }
    }

    internal static partial class Settings
    {
        public static MelonPreferences_Category Cat;
        public static MelonPreferences_Entry<bool> Enabled;
        public static MelonPreferences_Entry<bool> PlayerShotsOnly;
        public static MelonPreferences_Entry<float> MinCaliberMm;
        public static MelonPreferences_Entry<bool> LightAutocannonCam;
        /// hits get recorded (HUD, kill feed, report, world damage) from this calibre up, machine guns don't
        public const float RecordMinCaliberMm = 15f;
        public static MelonPreferences_Entry<float> PipWidth;
        public static MelonPreferences_Entry<float> PipSize;
        public static MelonPreferences_Entry<string> PipCorner;
        public static MelonPreferences_Entry<float> ApproachSeconds;
        public static MelonPreferences_Entry<float> InteriorSeconds;
        public static MelonPreferences_Entry<float> AftermathSeconds;
        public static MelonPreferences_Entry<float> ImpactSlowMo;
        public static MelonPreferences_Entry<string> HitCamPace;
        public static MelonPreferences_Entry<float> ConstantSlowMo;
        public static MelonPreferences_Entry<bool> CinematicXray;
        public static MelonPreferences_Entry<float> CookOffWait;
        public static MelonPreferences_Entry<bool> CookOffReplay;
        public static MelonPreferences_Entry<bool> CrewPanic;
        public static MelonPreferences_Entry<bool> RodSoftBody;
        public static MelonPreferences_Entry<bool> AmmoExplosionKillsCrew;
        public static MelonPreferences_Entry<string> CrewInAmmoExplosion;
        public static MelonPreferences_Entry<bool> HitCamOnWrecks;
        public static MelonPreferences_Entry<bool> DebugLog;
        /// detail lines in the log (what each hit, blast and plate did): DebugLog setting or the self-test
        internal static bool Verbose => AutoTest.Active || (DebugLog != null && DebugLog.Value);
        public static MelonPreferences_Entry<string> HitCamAntiAliasing;
        public static MelonPreferences_Entry<int> ConfigVersion;
        public static MelonPreferences_Entry<string> UpdateCheck, LastSeenVersion, SkippedVersion;
        /// no [HitCam] section in the settings file at start = first install (Quick setup welcomes it)
        public static bool FreshInstall;
        public static MelonPreferences_Entry<bool> ShowAllSpall;
        public static MelonPreferences_Entry<bool> ArmorHoles;
        public static MelonPreferences_Entry<bool> QueueHits;
        public static MelonPreferences_Entry<float> BurstCaliberMm;
        public static MelonPreferences_Entry<bool> SaveRecords;
        public static MelonPreferences_Entry<int> PastMissionsKept;
        public static MelonPreferences_Entry<string> Style;
        public static MelonPreferences_Entry<float> HullTexture;
        public static MelonPreferences_Entry<bool> XrayPhysics;
        public static MelonPreferences_Entry<bool> DetailedCrew;
        public static MelonPreferences_Entry<bool> WorldDamage;
        public static MelonPreferences_Entry<bool> CutModels;
        public static MelonPreferences_Entry<float> DebrisSeconds;
        public static MelonPreferences_Entry<int> MaxDebris;
        public static MelonPreferences_Entry<bool> WorldShrapnel;
        public static MelonPreferences_Entry<int> MaxWorldPlates;
        public static MelonPreferences_Entry<bool> WorldVentJets;
        public static MelonPreferences_Entry<float> XrayPhysicsSpeed;
        public static MelonPreferences_Entry<string> ToggleKey;
        public static MelonPreferences_Entry<string> ReplayKey;
        public static MelonPreferences_Entry<string> ArsenalKey;
        public static MelonPreferences_Entry<string> FireNowKey;
        public static MelonPreferences_Entry<string> RangeKey;

        public static MelonPreferences_Entry<string> ArsenalPrevKey;
        public static MelonPreferences_Entry<string> ArsenalNextKey;

        /// old saved values that were the defaults back then: moved to the new defaults once
        public static void Migrate()
        {
            if (ConfigVersion.Value >= 8) return;
            if (ConfigVersion.Value >= 3)
            {
                // the 1.1 test builds (6) turned the blast wave's camera kick off with the calm defaults, it stays on
                if (ConfigVersion.Value == 6 && ShockwaveShake != null) ShockwaveShake.Value = true;
                // 1.1.0: keys move off the ones the game uses itself
                Hotkeys.MigrateDefaults(Cat);
                if (ConfigVersion.Value < 5)
                {
                // 1.1.0: smaller proving ground (5 rows, 21 targets instead of 36, for fps); 10 rows was the old default,
                // 7 the one from the 1.1 test builds
                if (ProvingGround.RowsPref != null && (ProvingGround.RowsPref.Value == 10 || ProvingGround.RowsPref.Value == 7)) ProvingGround.RowsPref.Value = 5;
                // and Armor Sim on the (empty) M1 training range instead of the gunnery duel, the old default
                if (ArmorSimMenu.MissionPref != null && ArmorSimMenu.MissionPref.Value == "FG85_GT03,GT03_gunnery_duel") ArmorSimMenu.MissionPref.Value = "FG85_TR01,TR01_M1_Range";
                }
                // 1.1.0: calmer defaults, WT-like hit cam (steady camera, one constant slow-mo, crew stay in their seats, no cook-off
                // replay; blast wave camera kick stays). only for a config still on the old ones, hand-made choices stay
                if (ConfigVersion.Value < 6 && CinematicXray.Value && HitCamPace.Value == "Cinematic" && XrayPhysics.Value && CrewPanic.Value && CookOffReplay.Value)
                {
                    CinematicXray.Value = false;
                    HitCamPace.Value = "Constant";
                    XrayPhysics.Value = false;
                    CrewPanic.Value = false;
                    CookOffReplay.Value = false;
                    HitCamMod.Log.Msg("Settings: on the old defaults, moved to the new calmer ones (the Cinematic preset and CinematicXray bring the old look back)");
                }
                ConfigVersion.Value = 8;
                try { Cat.SaveToFile(false); } catch { }
                return;
            }
            if (ConfigVersion.Value < 2)
            {
                // 2026-10-20: smaller hit cam (0.26 -> 0.2 of screen height), proving ground's big cam off
                if (Mathf.Abs(PipSize.Value - 0.26f) < 0.002f) PipSize.Value = 0.2f;
                if (ProvingGround.BigCamPref != null) ProvingGround.BigCamPref.Value = false;
            }
            // presets came in: start on the default one (old defaults were the Cinematic preset's values)
            Presets.Apply(Presets.Default);
            ConfigVersion.Value = 8;
            try { Cat.SaveToFile(false); } catch { }
        }

        public static void Init()
        {
            Cat = MelonPreferences.CreateCategory("HitCam", "GHPC HitCam");
            Enabled = Cat.CreateEntry("Enabled", true, description: "The hit cam: after each of your hits, an x-ray replay of the round going in, in a window in the corner.");
            PlayerShotsOnly = Cat.CreateEntry("PlayerShotsOnly", true, description: "Only your own shots get a hit cam. Off: hits by everyone (AI on AI too).");
            HitCamOnWrecks = Cat.CreateEntry("HitCamOnWrecks", false, description: "Hit cam for shots at a vehicle that's already knocked out. Off: those hits still go into the After Action Report, only no hit cam plays.");
            MinCaliberMm = Cat.CreateEntry("MinCaliberMm", 15f, description: "Rounds smaller than this get no hit cam (machine guns). Their hits still count in the HUD and the report.");
            PipWidth = Cat.CreateEntry("PipWidth", 0.32f, description: "Old setting, not used any more (see Hit cam size).");
            PipSize = Cat.CreateEntry("PipSize", 0.2f, description: "Size of the hit cam window, as a part of the screen height (0.2 = a fifth of it).");
            PipCorner = Cat.CreateEntry("PipCorner", "TopRight", description: "Which corner of the screen the hit cam window sits in.");
            ApproachSeconds = Cat.CreateEntry("ApproachSeconds", 1.1f, description: "Slow-motion seconds of the round flying in before it hits.");
            InteriorSeconds = Cat.CreateEntry("InteriorSeconds", 3.8f, description: "Seconds the penetration and the spall inside are stretched to.");
            AftermathSeconds = Cat.CreateEntry("AftermathSeconds", 2.2f, description: "Seconds the hit cam stays on after the spall settles (fires, explosions, crew).");
            CinematicXray = Cat.CreateEntry("CinematicXray", false, description: "Camera shake, close-ups, slow motion and flashes in the hit cam. Off: a calm, steady camera.");
            CookOffWait = Cat.CreateEntry("CookOffWait", 8f, description: "When your hit starts an ammo fire, the hit cam keeps watching this many seconds for the cook-off.");
            HitCamAntiAliasing = Cat.CreateEntry("HitCamAntiAliasing", "2x", description: "Smooths jagged edges in the hit cam window.");
            DebugLog = Cat.CreateEntry("DebugLog", false, description: "Writes slow frames and what the mod did in them to the MelonLoader log. Only for reporting a lag problem.");
            ConfigVersion = Cat.CreateEntry("ConfigVersion", 0, description: "Settings layout version (internal).", is_hidden: true);
            UpdateCheck = Cat.CreateEntry("UpdateCheck", "Ask", description: "Each time the game starts, looks on the mod's GitHub page (api.github.com) for a new version and offers it in the main menu. Auto: installs it by itself (it's used from the next start). Off: never looks by itself (Check for updates below still does).");
            LastSeenVersion = Cat.CreateEntry("LastSeenVersion", "", description: "The version that last ran (internal: What's new after an update).", is_hidden: true);
            SkippedVersion = Cat.CreateEntry("SkippedVersion", "", description: "A new version you chose to skip (internal).", is_hidden: true);
            AmmoExplosionKillsCrew = Cat.CreateEntry("AmmoExplosionKillsCrew", true, description: "When an ammo rack blows up inside a vehicle, the crew around it die (the game sometimes leaves one alive). This changes the game, not only the picture.");
            CrewInAmmoExplosion = Cat.CreateEntry("CrewInAmmoExplosion", "Gone", description: "What the x-ray shows of the crew an ammo explosion kills: Gone (torn apart, they vanish in the flash), Outline (only their outline stays), Stay (the bodies stay and get thrown).");
            RodSoftBody = Cat.CreateEntry("RodSoftBody", true, description: "Long-rod rounds (APFSDS) erode, mushroom, bend and break like real metal. Off: a rigid rod that just gets shorter.");
            CrewPanic = Cat.CreateEntry("CrewPanic", false, description: "Crew still alive with fire in their compartment thrash in their seats.");
            CookOffReplay = Cat.CreateEntry("CookOffReplay", false, description: "If the ammo blows up after the hit cam closed, it opens again and shows the explosion (not when your hit had already knocked the vehicle out).");
            ImpactSlowMo = Cat.CreateEntry("ImpactSlowMo", 1.2f, description: "Extra slow motion with a close-up while the round goes through the first plate.");
            HitCamPace = Cat.CreateEntry("HitCamPace", "Constant", description: "Cinematic: slows right down as the round goes through the plate, then speeds up for the spall. Constant: one steady slow motion from the round flying in to the last fragment, like War Thunder (the timing seconds above are then worked out from it).");
            ConstantSlowMo = Cat.CreateEntry("ConstantSlowMo", 100f, description: "Constant pace: how many times slower than real time the round and the spall play.");
            ShowAllSpall = Cat.CreateEntry("ShowAllSpall", false, description: "Draw every spall fragment, also the ones that hit nothing (faint). Off: only the ones that hit something, like War Thunder.");
            ArmorHoles = Cat.CreateEntry("ArmorHoles", true, description: "Entry holes, petals and armour pieces in the hit cam.");
            LightAutocannonCam = Cat.CreateEntry("LightAutocannonCam", true, description: "Autocannons (rounds under \"Short replay below\", 40 mm) get a light hit cam, the War Thunder way: one x-ray for the whole burst, each hole simply drawn where the round went in. Off: a full deforming plate for every round, which WILL cost a lot of frames in a burst.");
            BurstCaliberMm = Cat.CreateEntry("BurstCaliberMm", 40f, description: "Rounds smaller than this (autocannons) get a short replay; more hits on the same vehicle join the one playing.");
            QueueHits = Cat.CreateEntry("QueueHits", true, description: "On: hits play one after another. Off: a new hit replaces the one playing.");
            SaveRecords = Cat.CreateEntry("SaveRecords", true, description: "Save hits to UserData/HitCam for the report's past missions (only while Past missions kept is above 0).");
            PastMissionsKept = Cat.CreateEntry("PastMissionsKept", 0, description: "How many finished missions' hits stay on disk for the report. Older ones are deleted when you close the report or start a mission. 0: nothing is written to disk (the report shows this mission and the last one from memory).");
            WorldDamage = Cat.CreateEntry("WorldDamage", true, description: "Damage on the real vehicles in the game world: holes, petals, scorch marks, sparks and shrapnel. Looks only, no effect on the game.");
            CutModels = Cat.CreateEntry("CutModels", true, description: "Cut real see-through holes into the vehicle models where the model allows it.");
            DebrisSeconds = Cat.CreateEntry("DebrisSeconds", 15f, description: "How long shrapnel pieces stay on the ground.");
            WorldShrapnel = Cat.CreateEntry("WorldShrapnel", true, description: "Hot plate chunks thrown out of each hit that bounce and land. Looks only: the game's own spall does the damage inside.");
            MaxDebris = Cat.CreateEntry("MaxDebris", 120, description: "Most shrapnel pieces (and torn-off track links, barrel ends) in the world at once.");
            MaxWorldPlates = Cat.CreateEntry("MaxWorldPlates", 24, description: "How many 3D holes (petals, craters) can be on vehicles at once; older ones turn into flat marks.");
            WorldVentJets = Cat.CreateEntry("WorldVentJets", true, description: "While ammo burns inside a vehicle, flames shoot out of the holes your rounds made.");
            DetailedCrew = Cat.CreateEntry("DetailedCrew", true, description: "The game's real crew models in the hit cam. Off: simple capsule figures.");
            XrayPhysics = Cat.CreateEntry("XrayPhysics", false, description: "Crew fall as ragdolls, plate pieces fly and bounce inside the hull. Off: the crew stay in their seats.");
            XrayPhysicsSpeed = Cat.CreateEntry("XrayPhysicsSpeed", 0.3f, description: "Speed of that physics after the spall settles (1 = real time, lower = slow motion).");
            HullTexture = Cat.CreateEntry("HullTexture", 0.32f, description: "How much of the vehicle's own paint shows on the x-ray hull (0 = plain dark glass).");
            Style = Cat.CreateEntry("Style", "WarThunder", description: "WarThunder: dark see-through hull, bright parts inside. Blueprint: light blue glass hull.");
            ToggleKey = Cat.CreateEntry("ToggleKey", "F9", description: "Turns the hit cam on and off.");
            ReplayKey = Cat.CreateEntry("ReplayKey", "F10", description: "Replays your last hit.");
            ArsenalKey = Cat.CreateEntry("ArsenalKey", "U", description: "Arsenal: your main gun fires any round you pick (for testing).");
            ArsenalPrevKey = Cat.CreateEntry("ArsenalPrevKey", "Alpha9", description: "Arsenal (while it's open): the round before.");
            ArsenalNextKey = Cat.CreateEntry("ArsenalNextKey", "Alpha0", description: "Arsenal (while it's open): the next round. With Shift: the next group.");
            FireNowKey = Cat.CreateEntry("FireNowKey", "J", description: "Arsenal (while it's open): fires the picked round at once, no reload.");
            RangeKey = Cat.CreateEntry("RangeKey", "Alt+K", description: "Test range: spawns targets and flying helicopters around you, in the mission you're in.");
            InitArcade();
        }
    }
}

namespace GHPCHitCam
{
    /// mod's log. warnings/errors always go in, detail lines (Msg: what each hit, blast and plate did) only with
    /// DebugLog or in the self-test. melonloader also writes every line to its console window, ~1 ms a line,
    /// several times per hit
    internal sealed class ModLog
    {
        readonly MelonLoader.MelonLogger.Instance _l;
        public ModLog(MelonLoader.MelonLogger.Instance l) { _l = l; }
        public void Msg(string s) { if (Settings.Verbose) _l.Msg(s); }
        /// always written (startup, stuff the player asked for)
        public void Info(string s) => _l.Msg(s);
        public void Warning(string s) => _l.Warning(s);
        public void Error(string s) => _l.Error(s);
    }
}
