using System;
using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// unity errors normally only end up in unity's log which melonloader doesn't show (a tester had "no logs"), so
    /// they get copied into the melon log, a few times each: ours under our name, the game's and other mods' under
    /// "Game or other mods, not Combat Overhaul"
    internal static class UnityErrors
    {
        static readonly Dictionary<string, int> _seen = new Dictionary<string, int>();
        static int _total;
        const int PerMessage = 3, Total = 60;
        static MelonLoader.MelonLogger.Instance _others;

        public static void Install()
        {
            try { Application.logMessageReceived += OnLog; }
            catch (Exception e) { HitCamMod.Log.Warning("Unity error log: " + e.Message); }
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            if (_total >= Total) return;
            try
            {
                string first = (condition ?? "").Split('\n')[0];
                if (first.Length > 300) first = first.Substring(0, 300);
                // MelonLoader already puts its own (and ours) in its log
                if (first.StartsWith("[") && first.IndexOf("] ", StringComparison.Ordinal) > 0) return;
                _seen.TryGetValue(first, out int n);
                _seen[first] = n + 1;
                if (n >= PerMessage) return;
                // ours under our name; the game's and the other mods' under a name of their own (they came out under
                // ours and players took them for the mod's)
                bool ours = !string.IsNullOrEmpty(stack) && stack.Contains("GHPCHitCam");
                _total++;
                string from = Owner(stack);
                string where = Trim(stack);
                string text = $"Unity {type} ({from}): {first}" + (where.Length > 0 ? "\n" + where : "");
                if (ours) HitCamMod.Log.Warning(text);
                else (_others ??= new MelonLoader.MelonLogger.Instance("Game or other mods, not Combat Overhaul")).Msg(text);
            }
            catch { }
        }

        /// whose code threw: top frame's namespace (Fulda1989, PactIncreasedLethality, GHPC...) and if this mod called into it
        static string Owner(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "no stack: the game's own code";
            var lines = stack.Replace("\r", "").Split('\n');
            string top = "";
            foreach (var l in lines) { var t = l.Trim(); if (t.Length > 0 && !t.StartsWith("UnityEngine.") && !t.StartsWith("System.")) { top = t; break; } }
            if (top.StartsWith("GHPCHitCam")) return "this mod";
            int cut = top.IndexOfAny(new[] { '.', ':', ' ', '(' });
            string ns = cut > 0 ? top.Substring(0, cut) : top;
            if (ns.Length == 0) ns = "unknown";
            if (ns == "GHPC" || ns == "NWH" || ns == "Rewired") ns = "the game (" + ns + ")";
            return stack.Contains("GHPCHitCam") ? ns + ", called from this mod" : ns;
        }

        /// first lines of the stack, enough to see whose code it is
        static string Trim(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            var lines = stack.Replace("\r", "").Split('\n');
            var keep = new List<string>();
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0) continue;
                keep.Add("    " + l.Trim());
                if (keep.Count >= 8) break;
            }
            return string.Join("\n", keep);
        }
    }
}
