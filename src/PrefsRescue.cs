using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader;

namespace GHPCHitCam
{
    /// GHPC Mod Manager writes a decimal setting back as text ("PipSize" = "0.25") on a Windows set to a decimal comma,
    /// and MelonLoader then can't read it and quietly keeps the default. The mod reads such values out of the file itself
    /// (a decimal comma too) and saves the section again, typed, so the file is right from then on.
    internal static class PrefsRescue
    {
        public static void Apply(MelonPreferences_Category cat)
        {
            try
            {
                if (cat == null) return;
                string path = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                if (!File.Exists(path)) return;
                var raw = Section(File.ReadAllLines(path), cat.Identifier);
                int fixedCount = 0;
                var names = new List<string>();
                foreach (var e in cat.Entries)
                {
                    if (e == null || !raw.TryGetValue(e.Identifier, out var v)) continue;
                    if (v.Length < 2 || v[0] != '"' || v[v.Length - 1] != '"') continue;
                    string s = v.Substring(1, v.Length - 2).Trim().Replace(',', '.');
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) continue;
                    var t = e.GetReflectedType();
                    if (t == typeof(float)) e.BoxedValue = (float)d;
                    else if (t == typeof(double)) e.BoxedValue = d;
                    else if (t == typeof(int) && Math.Abs(d - Math.Round(d)) < 1e-9) e.BoxedValue = (int)Math.Round(d);
                    else continue;
                    fixedCount++;
                    names.Add(e.Identifier);
                }
                if (fixedCount == 0) return;
                cat.SaveToFile(false);
                HitCamMod.Log.Info($"Settings: {fixedCount} number(s) saved as text (by a mod manager) read back and saved as numbers again: {string.Join(", ", names)}");
            }
            catch (Exception ex) { HitCamMod.Log.Warning("Settings check: " + ex.Message); }
        }

        /// key -> value as written, in one [section] of the TOML file (keys bare or quoted, no inline comments in it).
        static Dictionary<string, string> Section(string[] lines, string section)
        {
            var map = new Dictionary<string, string>();
            bool inside = false;
            foreach (var line in lines)
            {
                string l = line.Trim();
                if (l.StartsWith("[") && l.EndsWith("]")) { inside = l == "[" + section + "]"; continue; }
                if (!inside || l.Length == 0 || l[0] == '#') continue;
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string key = l.Substring(0, eq).Trim().Trim('"');
                string val = l.Substring(eq + 1).Trim();
                if (val.StartsWith("\"") && val.IndexOf('"', 1) > 0) val = val.Substring(0, val.IndexOf('"', 1) + 1);
                map[key] = val;
            }
            return map;
        }
    }
}
