using MelonLoader;
using UnityEngine;

namespace GHPCHitCam
{
    /// Arcade HUD settings (same [HitCam] section). Every aid has its own switch, because many GHPC players want none of them.
    internal static partial class Settings
    {
        public static MelonPreferences_Entry<bool> ArcadeHud;
        public static MelonPreferences_Entry<string> ArcadeKey;
        public static MelonPreferences_Entry<bool> ArcadeCallouts;
        public static MelonPreferences_Entry<bool> ArcadePenIndicator;
        public static MelonPreferences_Entry<bool> ArcadeMarkers;
        public static MelonPreferences_Entry<bool> ArcadeImpactMarker;
        public static MelonPreferences_Entry<bool> ArcadeLeadMarker;
        public static MelonPreferences_Entry<bool> ArcadeMinimap;
        public static MelonPreferences_Entry<string> MarkerSource;
        public static MelonPreferences_Entry<float> MarkerRange;
        public static MelonPreferences_Entry<float> MarkerMemorySeconds;
        public static MelonPreferences_Entry<bool> MinimapPlayerUp;
        public static MelonPreferences_Entry<string> MinimapCorner;
        public static MelonPreferences_Entry<float> MinimapSize;
        public static MelonPreferences_Entry<bool> MinimapTerrain;
        public static MelonPreferences_Entry<string> MinimapEnemies;
        public static MelonPreferences_Entry<bool> MinimapInfantry;
        public static MelonPreferences_Entry<bool> MinimapObjectives;
        public static MelonPreferences_Entry<bool> MarkerClassIcons;
        public static MelonPreferences_Entry<string> MinimapZoomInKey;
        public static MelonPreferences_Entry<string> MinimapZoomOutKey;
        public static MelonPreferences_Entry<bool> ArcadeKillFeed;
        public static MelonPreferences_Entry<string> KillFeedSide;
        public static MelonPreferences_Entry<float> KillCreditSeconds;
        public static MelonPreferences_Entry<bool> ArcadeSfx;
        public static MelonPreferences_Entry<float> ArcadeSfxVolume;
        public static MelonPreferences_Entry<float> HudScaleCallouts;
        public static MelonPreferences_Entry<float> HudScaleMarkers;
        public static MelonPreferences_Entry<float> HudScaleCross;
        public static MelonPreferences_Entry<float> HudScaleKillFeed;
        public static MelonPreferences_Entry<bool> ArcadePenNumbers;
        public static MelonPreferences_Entry<int> ArcadeLayoutVersion;
        public static MelonPreferences_Entry<string> ConfigKey;
        public static MelonPreferences_Entry<bool> ConfigPauseGame;

        public static KeyCode ArcadeKeyCode = KeyCode.F6;
        public static KeyCode MinimapZoomInKeyCode = KeyCode.KeypadPlus;
        public static KeyCode MinimapZoomOutKeyCode = KeyCode.KeypadMinus;
        public static KeyCode ConfigKeyCode = KeyCode.F12;

        static void InitArcade()
        {
            ArcadeHud = Cat.CreateEntry("ArcadeHud", true, description: "War Thunder style aids. Master switch: each aid below has its own.");
            ArcadeKey = Cat.CreateEntry("ArcadeKey", "F6", description: "Turns the whole arcade HUD on and off.");
            ArcadeCallouts = Cat.CreateEntry("ArcadeCallouts", true, description: "Big text after your hits: PENETRATION, CRITICAL HIT and what was damaged.");
            ArcadePenIndicator = Cat.CreateEntry("ArcadePenIndicator", true, description: "Colour at your aim point: green = will penetrate, yellow = maybe, red = no, blue = ricochet.");
            ArcadeMarkers = Cat.CreateEntry("ArcadeMarkers", true, description: "Name, type and distance above spotted enemies; off-screen ones at the screen edge.");
            ArcadeImpactMarker = Cat.CreateEntry("ArcadeImpactMarker", true, description: "Where your loaded round lands at the range of what's under the crosshair.");
            ArcadeLeadMarker = Cat.CreateEntry("ArcadeLeadMarker", true, description: "Lead circle on helicopters and planes for the weapon you have selected.");
            ArcadeMinimap = Cat.CreateEntry("ArcadeMinimap", true, description: "Corner minimap with you, friendlies and spotted enemies.");
            MarkerSource = Cat.CreateEntry("MarkerSource", "Spotted", description: "Spotted: what your side has spotted. LineOfSight: whatever your sight can see.");
            MarkerRange = Cat.CreateEntry("MarkerRange", 2500f, description: "LineOfSight: the furthest marker distance in metres.");
            MarkerMemorySeconds = Cat.CreateEntry("MarkerMemorySeconds", 10f, description: "How long an enemy stays marked after you lose sight of it.");
            MinimapPlayerUp = Cat.CreateEntry("MinimapPlayerUp", false, description: "On: your hull points up. Off: north up.");
            MinimapCorner = Cat.CreateEntry("MinimapCorner", "BottomRight", description: "Which corner the minimap sits in.");
            MinimapSize = Cat.CreateEntry("MinimapSize", 0.21f, description: "Minimap size, as a part of the screen height.");
            MinimapTerrain = Cat.CreateEntry("MinimapTerrain", true, description: "A top-down picture of the terrain under the grid.");
            MinimapEnemies = Cat.CreateEntry("MinimapEnemies", "Spotted", description: "Spotted: only spotted enemies. Always: all of them. Off: none.");
            MinimapInfantry = Cat.CreateEntry("MinimapInfantry", true, description: "Infantry and anti-tank teams on the minimap too.");
            MinimapObjectives = Cat.CreateEntry("MinimapObjectives", true, description: "Mission objectives with a map marker, lettered A, B, C.");
            MarkerClassIcons = Cat.CreateEntry("MarkerClassIcons", true, description: "The vehicle's class icon (light / medium / heavy tank, IFV, ...) in its marker.");
            MinimapZoomInKey = Cat.CreateEntry("MinimapZoomInKey", "KeypadPlus", description: "Minimap zoom in.");
            MinimapZoomOutKey = Cat.CreateEntry("MinimapZoomOutKey", "KeypadMinus", description: "Minimap zoom out.");
            ArcadeKillFeed = Cat.CreateEntry("ArcadeKillFeed", true, description: "Messages when your hits damage or kill a vehicle (later too: fire, cook-off, crew bailing out).");
            KillFeedSide = Cat.CreateEntry("KillFeedSide", "Right", description: "Right: small lines above the minimap. Left: above the game's weapon panel.");
            KillCreditSeconds = Cat.CreateEntry("KillCreditSeconds", 60f, description: "A vehicle that dies this long after your last hit (fire, cook-off) still counts as your kill.");
            ArcadeSfx = Cat.CreateEntry("ArcadeSfx", true, description: "Hit, penetration and kill sounds (made by the mod, no game files).");
            ArcadeSfxVolume = Cat.CreateEntry("ArcadeSfxVolume", 0.6f, description: "Volume of those sounds.");
            HudScaleCallouts = Cat.CreateEntry("HudScaleCallouts", 1f, description: "Size of the hit callouts.");
            HudScaleMarkers = Cat.CreateEntry("HudScaleMarkers", 1f, description: "Size of the enemy markers.");
            HudScaleCross = Cat.CreateEntry("HudScaleCross", 1f, description: "Size of the impact cross, gun ring and lead circle.");
            HudScaleKillFeed = Cat.CreateEntry("HudScaleKillFeed", 1f, description: "Size of the kill feed and kill banner.");
            ConfigKey = Cat.CreateEntry("ConfigKey", "F12", description: "Opens this window. There is also a gear button in the corner while the mouse cursor is free.");
            ConfigPauseGame = Cat.CreateEntry("ConfigPauseGame", true, description: "Pause the game while this window is open.");
            ArcadePenNumbers = Cat.CreateEntry("ArcadePenNumbers", false, description: "Also show the pen and armour in millimetres next to it.");
            ArcadeLayoutVersion = Cat.CreateEntry("ArcadeLayoutVersion", 0, is_hidden: true);
            // 2026-10-20: War Thunder proportions (1080p baseline, smaller minimap). Move old defaults once, keep anything the player chose.
            if (ArcadeLayoutVersion.Value < 2)
            {
                if (Mathf.Abs(MinimapSize.Value - 0.27f) < 0.001f) MinimapSize.Value = 0.21f;
                ArcadeLayoutVersion.Value = 2;
            }
            if (ArcadeLayoutVersion.Value < 3)
            {
                // the kill feed became War Thunder's battle log over the minimap; results moved to the top-centre stack
                if (KillFeedSide.Value == "Left") KillFeedSide.Value = "Right";
                ArcadeLayoutVersion.Value = 3;
            }
            ConfigKeyCode = ParseKey(ConfigKey.Value, KeyCode.F12);
            ArcadeKeyCode = ParseKey(ArcadeKey.Value, KeyCode.F6);
            MinimapZoomInKeyCode = ParseKey(MinimapZoomInKey.Value, KeyCode.KeypadPlus);
            MinimapZoomOutKeyCode = ParseKey(MinimapZoomOutKey.Value, KeyCode.KeypadMinus);
        }
    }
}
