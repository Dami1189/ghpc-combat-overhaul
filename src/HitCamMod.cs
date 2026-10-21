using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(GHPCHitCam.HitCamMod), "GHPC Combat Overhaul", "1.0.0", "Damian")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace GHPCHitCam
{
    public class HitCamMod : MelonMod
    {
        internal static ModLog Log;
        internal static string DataDir;

        public override void OnInitializeMelon()
        {
            Log = new ModLog(LoggerInstance);
            DataDir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "HitCam");
            System.IO.Directory.CreateDirectory(DataDir);
            Settings.Init();
            Recorder.Install(HarmonyInstance);
            Arsenal.Install(HarmonyInstance);
            Safety.Install(HarmonyInstance);
            ArmorSim.Init(HarmonyInstance);
            ProvingGround.Init();
            ProvingGround.Install(HarmonyInstance);
            AfterAction.Init();
            AfterAction.Install(HarmonyInstance);
            KillFeed.Install(HarmonyInstance);
            BlastSound.Install(HarmonyInstance);
            AutoTest.Init();
            Settings.Migrate();
            Perf.On = Settings.DebugLog.Value || AutoTest.Active;
            Perf.InstrumentHooks(HarmonyInstance);
            Log.Info($"GHPC Combat Overhaul ready. Settings: {Settings.ConfigKey.Value}, hit cam on/off: {Settings.ToggleKey.Value}, replay the last hit: {Settings.ReplayKey.Value}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Log.Msg("Scene loaded: " + sceneName + " (" + buildIndex + ")");
            ArmorSimMenu.OnSceneLoaded(sceneName);
            ProvingGround.OnSceneLoaded(sceneName);
            AutoTest.OnSceneLoaded(sceneName);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            // the mission scene going away invalidates every unit reference we hold
            if (SceneUtil.IsGameplayGone())
            {
                AfterAction.OnGameplayGone();   // first: saves the mission's hits before the records are dropped
                ArmorSim.Reset();
                ProvingGround.Reset();
                HitCamPlayer.Reset();
                Recorder.Reset();
                WorldDamage.Reset();
                Arsenal.Reset();
                TestRange.Reset();
                ArcadeHud.Reset();
                BlastSound.Reset();
                Warmup.Reset();
            }
        }

        public override void OnUpdate()
        {
            try
            {
                Perf.FrameStart();
                if (Input.GetKeyDown(Settings.ToggleKeyCode))
                {
                    Settings.Enabled.Value = !Settings.Enabled.Value;
                    Log.Info("Hit cam " + (Settings.Enabled.Value ? "on" : "off"));
                    if (!Settings.Enabled.Value) HitCamPlayer.StopCurrent();
                }
                if (Input.GetKeyDown(Settings.ReplayKeyCode))
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
                using (Perf.T("OnGUI other")) { Arsenal.Draw(); TestRange.Draw(); AfterAction.Draw(); ArmorSim.Draw(); ProvingGround.Draw(); ConfigMenu.Draw(); }
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

        public static KeyCode ToggleKeyCode = KeyCode.F9;
        public static KeyCode ReplayKeyCode = KeyCode.F10;
        public static KeyCode ArsenalKeyCode = KeyCode.F8;
        public static KeyCode FireNowKeyCode = KeyCode.B;
        public static KeyCode RangeKeyCode = KeyCode.F7;

        /// Old saved values that were the defaults then: moved to the new defaults once.
        public static void Migrate()
        {
            if (ConfigVersion.Value >= 3) return;
            if (ConfigVersion.Value < 2)
            {
                // 2026-10-20: a smaller hit cam (0.26 -> 0.2 of the screen height), the proving ground's big cam off
                if (Mathf.Abs(PipSize.Value - 0.26f) < 0.002f) PipSize.Value = 0.2f;
                if (ProvingGround.BigCamPref != null) ProvingGround.BigCamPref.Value = false;
            }
            // presets came in: start on the default one (the old defaults were the Cinematic preset's values)
            Presets.Apply(Presets.Default);
            ConfigVersion.Value = 3;
            try { Cat.SaveToFile(false); } catch { }
        }

        public static void Init()
        {
            Cat = MelonPreferences.CreateCategory("HitCam", "GHPC HitCam");
            Enabled = Cat.CreateEntry("Enabled", true, description: "The hit cam: after each of your hits, an x-ray replay of the round going in, in a window in the corner.");
            PlayerShotsOnly = Cat.CreateEntry("PlayerShotsOnly", true, description: "Only your own shots get a hit cam. Off: hits by everyone (AI on AI too).");
            MinCaliberMm = Cat.CreateEntry("MinCaliberMm", 15f, description: "Rounds smaller than this get no hit cam (machine guns).");
            PipWidth = Cat.CreateEntry("PipWidth", 0.32f, description: "Old setting, not used any more (see Hit cam size).");
            PipSize = Cat.CreateEntry("PipSize", 0.2f, description: "Size of the hit cam window, as a part of the screen height (0.2 = a fifth of it).");
            PipCorner = Cat.CreateEntry("PipCorner", "TopRight", description: "Which corner of the screen the hit cam window sits in.");
            ApproachSeconds = Cat.CreateEntry("ApproachSeconds", 1.1f, description: "Slow-motion seconds of the round flying in before it hits.");
            InteriorSeconds = Cat.CreateEntry("InteriorSeconds", 3.8f, description: "Seconds the penetration and the spall inside are stretched to.");
            AftermathSeconds = Cat.CreateEntry("AftermathSeconds", 2.2f, description: "Seconds the hit cam stays on after the spall settles (fires, explosions, crew).");
            CinematicXray = Cat.CreateEntry("CinematicXray", true, description: "Camera shake, close-ups, slow motion and flashes in the hit cam. Off: a calm, steady camera.");
            CookOffWait = Cat.CreateEntry("CookOffWait", 8f, description: "When your hit starts an ammo fire, the hit cam keeps watching this many seconds for the cook-off.");
            HitCamAntiAliasing = Cat.CreateEntry("HitCamAntiAliasing", "2x", description: "Smooths jagged edges in the hit cam window.");
            DebugLog = Cat.CreateEntry("DebugLog", false, description: "Writes slow frames and what the mod did in them to the MelonLoader log. Only for reporting a lag problem.");
            ConfigVersion = Cat.CreateEntry("ConfigVersion", 0, description: "Settings layout version (internal).", is_hidden: true);
            AmmoExplosionKillsCrew = Cat.CreateEntry("AmmoExplosionKillsCrew", true, description: "When an ammo rack blows up inside a vehicle, the crew around it die (the game sometimes leaves one alive). This changes the game, not only the picture.");
            RodSoftBody = Cat.CreateEntry("RodSoftBody", true, description: "Long-rod rounds (APFSDS) erode, mushroom, bend and break like real metal. Off: a rigid rod that just gets shorter.");
            CrewPanic = Cat.CreateEntry("CrewPanic", true, description: "Crew still alive with fire in their compartment thrash in their seats.");
            CookOffReplay = Cat.CreateEntry("CookOffReplay", true, description: "If the ammo blows up after the hit cam closed, it opens again and shows the explosion (not when your hit had already knocked the vehicle out).");
            ImpactSlowMo = Cat.CreateEntry("ImpactSlowMo", 1.2f, description: "Extra slow motion with a close-up while the round goes through the first plate.");
            HitCamPace = Cat.CreateEntry("HitCamPace", "Cinematic", description: "Cinematic: slows right down as the round goes through the plate, then speeds up for the spall. Constant: one steady slow motion from the round flying in to the last fragment, like War Thunder (the timing seconds above are then worked out from it).");
            ConstantSlowMo = Cat.CreateEntry("ConstantSlowMo", 100f, description: "Constant pace: how many times slower than real time the round and the spall play.");
            ShowAllSpall = Cat.CreateEntry("ShowAllSpall", false, description: "Draw every spall fragment, also the ones that hit nothing (faint). Off: only the ones that hit something, like War Thunder.");
            ArmorHoles = Cat.CreateEntry("ArmorHoles", true, description: "Entry holes, petals and armour pieces in the hit cam.");
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
            XrayPhysics = Cat.CreateEntry("XrayPhysics", true, description: "Crew fall as ragdolls, plate pieces fly and bounce inside the hull. Off: the crew stay in their seats.");
            XrayPhysicsSpeed = Cat.CreateEntry("XrayPhysicsSpeed", 0.3f, description: "Speed of that physics after the spall settles (1 = real time, lower = slow motion).");
            HullTexture = Cat.CreateEntry("HullTexture", 0.32f, description: "How much of the vehicle's own paint shows on the x-ray hull (0 = plain dark glass).");
            Style = Cat.CreateEntry("Style", "WarThunder", description: "WarThunder: dark see-through hull, bright parts inside. Blueprint: light blue glass hull.");
            ToggleKey = Cat.CreateEntry("ToggleKey", "F9", description: "Turns the hit cam on and off.");
            ReplayKey = Cat.CreateEntry("ReplayKey", "F10", description: "Replays your last hit.");
            ArsenalKey = Cat.CreateEntry("ArsenalKey", "F8", description: "Arsenal: your main gun fires any round you pick (for testing).");
            FireNowKey = Cat.CreateEntry("FireNowKey", "B", description: "Arsenal: fires the picked round at once, no reload.");
            RangeKey = Cat.CreateEntry("RangeKey", "F7", description: "Test range: spawns targets and flying helicopters around you.");
            ArsenalKeyCode = ParseKey(ArsenalKey.Value, KeyCode.F8);
            FireNowKeyCode = ParseKey(FireNowKey.Value, KeyCode.B);
            RangeKeyCode = ParseKey(RangeKey.Value, KeyCode.F7);
            ToggleKeyCode = ParseKey(ToggleKey.Value, KeyCode.F9);
            ReplayKeyCode = ParseKey(ReplayKey.Value, KeyCode.F10);
            InitArcade();
        }

        static KeyCode ParseKey(string s, KeyCode fallback)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch { return fallback; }
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
