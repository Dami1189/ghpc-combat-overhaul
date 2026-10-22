using System;
using System.Collections.Generic;
using MelonLoader;

namespace GHPCHitCam
{
    /// quality presets: one click sets every setting with an FPS cost in the settings window, the rest (sizes, keys, HUD,
    /// timings) stays as is. Balanced is what a fresh install gets
    internal static class Presets
    {
        public static readonly string[] Names = { "Performance", "Balanced", "Cinematic" };

        public static readonly string[] Blurbs =
        {
            "For older PCs and big battles. The hit cam keeps the x-ray, holes, spall, fire and explosions, but without ragdoll "
                + "physics, the real crew models, the bending rod and cut-open vehicle models; less shrapnel and fewer 3D holes in the world.",
            "The default, calm like War Thunder: real crew models that stay in their seats, the deforming rod, cut-open models; "
                + "spall that hit nothing is hidden; 2x anti-aliasing in the hit cam.",
            "Everything at full detail: crew ragdolls, burning crew thrashing, cook-off replays, every spall fragment drawn, "
                + "4x anti-aliasing, the most shrapnel and 3D holes, debris stays longest. For strong PCs, replays and screenshots.",
        };

        static readonly Dictionary<string, object>[] Values =
        {
            new Dictionary<string, object>
            {
                { "XrayPhysics", false }, { "DetailedCrew", false }, { "RodSoftBody", false }, { "CrewPanic", false }, { "ShowAllSpall", false },
                { "CookOffReplay", false }, { "HitCamAntiAliasing", "Off" }, { "CutModels", false }, { "WorldShrapnel", false }, { "MaxDebris", 50 }, { "DebrisSeconds", 10f },
                { "MaxWorldPlates", 12 }, { "WorldVentJets", false }, { "AarFullModels", 10 },
            },
            new Dictionary<string, object>
            {
                { "XrayPhysics", false }, { "DetailedCrew", true }, { "RodSoftBody", true }, { "CrewPanic", false }, { "ShowAllSpall", false },
                { "CookOffReplay", false }, { "HitCamAntiAliasing", "2x" }, { "CutModels", true }, { "WorldShrapnel", true }, { "MaxDebris", 120 }, { "DebrisSeconds", 15f },
                { "MaxWorldPlates", 24 }, { "WorldVentJets", true }, { "AarFullModels", 30 },
            },
            new Dictionary<string, object>
            {
                { "XrayPhysics", true }, { "DetailedCrew", true }, { "RodSoftBody", true }, { "CrewPanic", true }, { "ShowAllSpall", true },
                { "CookOffReplay", true }, { "HitCamAntiAliasing", "4x" }, { "CutModels", true }, { "WorldShrapnel", true }, { "MaxDebris", 220 }, { "DebrisSeconds", 25f },
                { "MaxWorldPlates", 48 }, { "WorldVentJets", true }, { "AarFullModels", 60 },
            },
        };

        public const int Default = 1;

        /// settings a preset touches
        public static bool Managed(string id) => Values[0].ContainsKey(id);

        /// preset the current settings match, else "Custom"
        public static string Current()
        {
            for (int i = 0; i < Values.Length; i++)
            {
                bool all = true;
                foreach (var kv in Values[i])
                {
                    var e = Find(kv.Key);
                    if (e != null && !Same(e.BoxedValue, kv.Value)) { all = false; break; }
                }
                if (all) return Names[i];
            }
            return "Custom";
        }

        public static void Apply(int i)
        {
            if (i < 0 || i >= Values.Length) return;
            foreach (var kv in Values[i])
            {
                var e = Find(kv.Key);
                if (e == null) continue;
                try { e.BoxedValue = Convert(kv.Value, e.GetReflectedType()); }
                catch (Exception ex) { HitCamMod.Log.Warning($"Preset {Names[i]}: could not set {kv.Key}: {ex.Message}"); }
            }
            HitCamMod.Log.Info("Preset applied: " + Names[i]);
        }

        /// self-test: set one setting by its id, from text
        public static bool Set(string id, string value)
        {
            var e = Find(id);
            if (e == null) return false;
            var t = e.GetReflectedType();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            object v = t == typeof(bool) ? bool.Parse(value) : t == typeof(int) ? int.Parse(value, ci) : t == typeof(float) ? float.Parse(value, ci) : (object)value;
            e.BoxedValue = v;
            return true;
        }

        static object Convert(object v, Type t)
        {
            if (t == typeof(float)) return System.Convert.ToSingle(v);
            if (t == typeof(int)) return System.Convert.ToInt32(v);
            return v;
        }

        static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if ((a is float || a is int) && (b is float || b is int)) return Math.Abs(System.Convert.ToDouble(a) - System.Convert.ToDouble(b)) < 1e-3;
            return a.Equals(b);
        }

        static MelonPreferences_Entry Find(string id)
        {
            var e = Settings.Cat?.GetEntry(id);
            if (e != null) return e;
            foreach (var cat in MelonPreferences.Categories)
                if (cat != null && cat.Identifier.IndexOf("HitCam", StringComparison.OrdinalIgnoreCase) >= 0 && cat.GetEntry(id) is MelonPreferences_Entry ce) return ce;
            return null;
        }
    }
}
