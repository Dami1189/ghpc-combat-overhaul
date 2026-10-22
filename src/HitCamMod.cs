using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(GHPCHitCam.HitCamMod), "GHPC Combat Overhaul", "1.1.1", "Damian")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace GHPCHitCam
{
    public class HitCamMod : MelonMod
    {
        internal static ModLog Log;
        internal static HitCamMod Instance;
        internal static string DataDir;

        public override void OnInitializeMelon()
        {
            Log = new ModLog(LoggerInstance);
            Instance = this;
            DataDir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "HitCam");
            System.IO.Directory.CreateDirectory(DataDir);
            try
            {
                string cfg = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                Settings.FreshInstall = !System.IO.File.Exists(cfg) || !System.IO.File.ReadAllText(cfg).Contains("[HitCam]");
            }
            catch { }
            Settings.Init();
            Recorder.Install(HarmonyInstance);
            Arsenal.Install(HarmonyInstance);
            Safety.Install(HarmonyInstance);
            ModdedVehicles.Install(HarmonyInstance);
            ArmorSim.Init(HarmonyInstance);
            ProvingGround.Init();
            ProvingGround.Install(HarmonyInstance);
            AfterAction.Init();
            AfterAction.Install(HarmonyInstance);
            KillFeed.Install(HarmonyInstance);
            BlastSound.Install(HarmonyInstance);
            ConfigMenu.Install(HarmonyInstance);
            AutoTest.Init();
            PrefsRescue.Apply(Settings.Cat);
            Settings.Migrate();
            Perf.On = Settings.DebugLog.Value || AutoTest.Active;
            Perf.InstrumentHooks(HarmonyInstance);
            Log.Info($"GHPC Combat Overhaul ready. Settings: {Hotkeys.Label(Settings.ConfigKey)}, hit cam on/off: {Hotkeys.Label(Settings.ToggleKey)}, replay the last hit: {Hotkeys.Label(Settings.ReplayKey)}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Log.Msg("Scene loaded: " + sceneName + " (" + buildIndex + ")");
            SceneUtil.Recheck();
            ArmorSimMenu.OnSceneLoaded(sceneName);
            if (ArmorSimMenu.InMenu) WhatsNew.OnMenu();
            ProvingGround.OnSceneLoaded(sceneName);
            AutoTest.OnSceneLoaded(sceneName);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            Log.Msg("Scene unloaded: " + sceneName);
            ArmorSimMenu.OnSceneUnloaded(sceneName);
            // the mission scene going away invalidates every unit reference we hold
            SceneUtil.Recheck();
            if (SceneUtil.IsGameplayGone())
            {
                // each on its own: one that fails must not leave the others holding the old mission (a proving ground
                // still "on" would keep the next mission's unit menu to one vehicle)
                Step("AAR", AfterAction.OnGameplayGone);   // first: saves the mission's hits before the records are dropped
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

        public override void OnUpdate()
        {
            try
            {
                Perf.FrameStart();
                if (Hotkeys.Down(Settings.ToggleKey))
                {
                    Settings.Enabled.Value = !Settings.Enabled.Value;
                    Log.Info("Hit cam " + (Settings.Enabled.Value ? "on" : "off"));
                    if (!Settings.Enabled.Value) HitCamPlayer.StopCurrent();
                }
                if (Hotkeys.Down(Settings.ReplayKey))
                    HitCamPlayer.ReplayLast();

                using (Perf.T("Arsenal.Update")) Arsenal.Update();
                using (Perf.T("TestRange.Update")) TestRange.Update();
                using (Perf.T("ArmorSim.Update")) ArmorSim.Update();
                using (Perf.T("ProvingGround.Update")) ProvingGround.Update();
                using (Perf.T("Recorder.Update")) Recorder.Update();
                using (Perf.T("BlastSound.Update")) BlastSound.Update();
                using (Perf.T("WorldDamage.Update")) WorldDamage.Update();
                using (Perf.T("HitCamPlayer.Update")) HitCamPlayer.Update();
                using (Perf.T("AfterAction.Update")) AfterAction.Update();
                Archive.Update();
                using (Perf.T("ConfigMenu.Update")) ConfigMenu.Update();
                using (Perf.T("ArcadeHud.Update")) ArcadeHud.Update();
                using (Perf.T("AutoTest.Update")) AutoTest.Update();
                ModdedVehicles.Update();
                WhatsNew.Update();
                Warmup.Update();
            }
            catch (Exception e)
            {
                Log.Error("Update failed: " + e);
            }
        }

        public override void OnLateUpdate()
        {
            try
            {
                using (Perf.T("HitCamPlayer.LateUpdate")) HitCamPlayer.LateUpdate();
                using (Perf.T("AfterAction.LateUpdate")) AfterAction.LateUpdate();
                using (Perf.T("ArmorSim.LateUpdate")) ArmorSim.LateUpdate();
            }
            catch (Exception e) { Log.Error("LateUpdate failed: " + e); }
        }

        public override void OnGUI()
        {
            try
            {
                using (Perf.T("ArcadeHud.Draw")) if (!ArmorSim.Active) ArcadeHud.Draw();
                using (Perf.T("HitCamUI.Draw")) HitCamUI.Draw();
                // the settings window (F12) is on its own: the other windows step aside while it's open
                bool cfg = ConfigMenu.IsOpen;
                if (!cfg) using (Perf.T("Arsenal.Draw")) Arsenal.Draw();
                if (!cfg) using (Perf.T("TestRange.Draw")) TestRange.Draw();
                using (Perf.T("AfterAction.Draw")) AfterAction.Draw();
                using (Perf.T("ArmorSim.Draw")) ArmorSim.Draw();
                if (!cfg) using (Perf.T("ProvingGround.Draw")) ProvingGround.Draw();
                using (Perf.T("ConfigMenu.Draw")) ConfigMenu.Draw();
            }
            catch (Exception e) { Log.Error("OnGUI failed: " + e); }
        }
    }

    internal static partial class Settings
    {
        public static MelonPreferences_Category Cat;
        public static MelonPreferences_Entry<bool> Enabled;
        public static MelonPreferences_Entry<bool> PlayerShotsOnly;
        public static MelonPreferences_Entry<float> MinCaliberMm;
        public static MelonPreferences_Entry<bool> LightAutocannonCam;
        /// Hits are recorded (HUD, kill feed, report, world damage) from this calibre up: machine guns aren't.
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
        public static MelonPreferences_Entry<bool> DebugLog;
        /// Detail lines in the log (what each hit, blast and plate did): the DebugLog setting, or the self-test.
        internal static bool Verbose => AutoTest.Active || (DebugLog != null && DebugLog.Value);
        public static MelonPreferences_Entry<string> HitCamAntiAliasing;
        public static MelonPreferences_Entry<int> ConfigVersion;
        public static MelonPreferences_Entry<string> UpdateCheck, LastSeenVersion, SkippedVersion;
        /// No [HitCam] section in the settings file at start: a first install (the Quick setup welcomes it).
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

        /// Old saved values that were the defaults then: moved to the new defaults once.
        public static void Migrate()
        {
            if (ConfigVersion.Value >= 8) return;
            if (ConfigVersion.Value >= 3)
            {
                // the 1.1 test builds (6) switched the blast wave's camera kick off with the calm defaults: it stays on
                if (ConfigVersion.Value == 6 && ShockwaveShake != null) ShockwaveShake.Value = true;
                // 1.1.0: the keys move off the ones the game uses itself
                Hotkeys.MigrateDefaults(Cat);
                if (ConfigVersion.Value < 5)
                {
                // 1.1.0: a smaller proving ground (5 rows, 21 targets instead of 36, for the frame rate); 10 rows was the old
                // default, 7 the one of the 1.1 test builds
                if (ProvingGround.RowsPref != null && (ProvingGround.RowsPref.Value == 10 || ProvingGround.RowsPref.Value == 7)) ProvingGround.RowsPref.Value = 5;
                // and the Armor Sim on the (empty) M1 training range instead of the gunnery duel, the old default
                if (ArmorSimMenu.MissionPref != null && ArmorSimMenu.MissionPref.Value == "FG85_GT03,GT03_gunnery_duel") ArmorSimMenu.MissionPref.Value = "FG85_TR01,TR01_M1_Range";
                }
                // 1.1.0: calmer defaults, a War Thunder-like hit cam (steady camera, one constant slow motion, the crew stay in
                // their seats, no cook-off replay; the blast wave's camera kick stays). Only for a config still on the old
                // ones: choices made by hand stay.
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
                // 2026-10-20: a smaller hit cam (0.26 -> 0.2 of the screen height), the proving ground's big cam off
                if (Mathf.Abs(PipSize.Value - 0.26f) < 0.002f) PipSize.Value = 0.2f;
                if (ProvingGround.BigCamPref != null) ProvingGround.BigCamPref.Value = false;
            }
            // presets came in: start on the default one (the old defaults were the Cinematic preset's values)
            Presets.Apply(Presets.Default);
            ConfigVersion.Value = 8;
            try { Cat.SaveToFile(false); } catch { }
        }

        public static void Init()
        {
            Cat = MelonPreferences.CreateCategory("HitCam", "GHPC HitCam");
            Enabled = Cat.CreateEntry("Enabled", true, description: "The hit cam: after each of your hits, an x-ray replay of the round going in, in a window in the corner.");
            PlayerShotsOnly = Cat.CreateEntry("PlayerShotsOnly", true, description: "Only your own shots get a hit cam. Off: hits by everyone (AI on AI too).");
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
            UpdateCheck = Cat.CreateEntry("UpdateCheck", "Ask", description: "Each time the game starts, looks on the mod's GitHub page (api.github.com) for a new version and offers it in the main menu. Auto: installs it by itself (it's used from the next start). Off: never looks.");
            LastSeenVersion = Cat.CreateEntry("LastSeenVersion", "", description: "The version that last ran (internal: What's new after an update).", is_hidden: true);
            SkippedVersion = Cat.CreateEntry("SkippedVersion", "", description: "A new version you chose to skip (internal).", is_hidden: true);
            AmmoExplosionKillsCrew = Cat.CreateEntry("AmmoExplosionKillsCrew", true, description: "When an ammo rack blows up inside a vehicle, the crew around it die (the game sometimes leaves one alive). This changes the game, not only the picture.");
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
    /// The mod's log. Warnings and errors always go in; the detail lines (Msg: what each hit, blast and plate did) only
    /// with the DebugLog setting or in the self-test. MelonLoader writes every line to its console window too, which
    /// costs about a millisecond a line, several times per hit.
    internal sealed class ModLog
    {
        readonly MelonLoader.MelonLogger.Instance _l;
        public ModLog(MelonLoader.MelonLogger.Instance l) { _l = l; }
        public void Msg(string s) { if (Settings.Verbose) _l.Msg(s); }
        /// Always written (start-up, things the player asked for).
        public void Info(string s) => _l.Msg(s);
        public void Warning(string s) => _l.Warning(s);
        public void Error(string s) => _l.Error(s);
    }
}
