using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GHPCHitCam
{
    /// Self-profiler (setting Profiler; always on in the self-test). Times every Harmony hook of the mod (each of our
    /// patch methods gets a timing prefix/postfix of its own) and the named sections of its per-frame work, and when a
    /// frame takes much longer than the ones before it, logs what ran in that frame and whether the GC ran.
    internal static class Perf
    {
        public static bool On;
        public static double LastFrameMod;     // the mod's own time in the frame before (self-test FpsProbe)

        class Stat
        {
            public string Name;
            public bool Child;          // a section inside another one: left out of the per-frame sum
            public int Calls, FrameCalls;
            public double Total, Max, Frame;
        }

        static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>();
        static readonly List<Stat> _touched = new List<Stat>();
        static readonly Dictionary<MethodBase, string> _hookNames = new Dictionary<MethodBase, string>();
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        static float _avg;
        static int _gc, _frames, _over33, _over50, _spikes, _startFrame = -1;
        static double _sumMod, _worstFrame;

        public struct Scope : IDisposable
        {
            readonly string _n;
            readonly long _t;
            public Scope(string n) { _n = n; _t = Stopwatch.GetTimestamp(); }
            public void Dispose() { if (_n != null) Add(_n, Stopwatch.GetTimestamp() - _t); }
        }

        /// using (Perf.T("name")) { ... }   - a name with '/' is a part of another section
        public static Scope T(string name) => On ? new Scope(name) : default;

        /// Lap timer for the parts of one long method: long t = Perf.Now; ...; Perf.Lap("Part/a", ref t); ...
        public static long Now => Stopwatch.GetTimestamp();
        public static void Lap(string name, ref long t)
        {
            if (!On) return;
            long n = Stopwatch.GetTimestamp();
            Add(name, n - t);
            t = n;
        }

        static void Add(string name, long ticks)
        {
            if (!_stats.TryGetValue(name, out var s)) _stats[name] = s = new Stat { Name = name, Child = name.IndexOf('/') >= 0 };
            double ms = ticks * TickMs;
            s.Calls++;
            s.Total += ms;
            if (ms > s.Max) s.Max = ms;
            if (s.FrameCalls == 0) _touched.Add(s);
            s.FrameCalls++;
            s.Frame += ms;
        }

        /// Wraps every prefix / postfix / finalizer this mod put on the game's methods in a timer.
        public static void InstrumentHooks(HarmonyLib.Harmony h)
        {
            if (!On) return;
            var pre = new HarmonyMethod(typeof(Perf).GetMethod(nameof(HookPre), BindingFlags.Static | BindingFlags.NonPublic));
            var post = new HarmonyMethod(typeof(Perf).GetMethod(nameof(HookPost), BindingFlags.Static | BindingFlags.NonPublic));
            var done = new HashSet<MethodInfo>();
            int n = 0, failed = 0;
            foreach (var orig in h.GetPatchedMethods().ToList())
            {
                var info = HarmonyLib.Harmony.GetPatchInfo(orig);
                if (info == null) continue;
                foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    if (p.owner != h.Id || p.PatchMethod == null || !done.Add(p.PatchMethod)) continue;
                    if (p.PatchMethod.DeclaringType == typeof(Perf)) continue;
                    try
                    {
                        _hookNames[p.PatchMethod] = "hook " + p.PatchMethod.DeclaringType?.Name + "." + p.PatchMethod.Name;
                        h.Patch(p.PatchMethod, pre, post);
                        n++;
                    }
                    catch (Exception e) { failed++; HitCamMod.Log.Warning("Profiler: could not time " + p.PatchMethod.Name + ": " + e.Message); }
                }
            }
            HitCamMod.Log.Msg($"Profiler on: timing {n} hooks{(failed > 0 ? $" ({failed} not)" : "")}; frames over 1.8x the running average and 30 ms are logged");
        }

        static void HookPre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        static void HookPost(long __state, MethodBase __originalMethod)
        {
            if (__originalMethod != null && _hookNames.TryGetValue(__originalMethod, out var name)) Add(name, Stopwatch.GetTimestamp() - __state);
        }

        /// First thing in the mod's Update: closes the frame before (everything timed since the last call).
        public static void FrameStart()
        {
            if (!On) return;
            float dt = Time.unscaledDeltaTime * 1000f;
            int gc = GC.CollectionCount(0);
            bool gcRan = gc != _gc;
            _gc = gc;
            double mod = 0;
            foreach (var s in _touched) if (!s.Child) mod += s.Frame;
            LastFrameMod = mod;
            if (_startFrame < 0) _startFrame = Time.frameCount;
            bool warm = Time.frameCount - _startFrame > 30;
            if (warm)
            {
                _frames++;
                _sumMod += mod;
                if (dt > 33.4f) _over33++;
                if (dt > 50f) _over50++;
                if (dt > _worstFrame) _worstFrame = dt;
            }
            bool spike = warm && dt > 30f && dt > _avg * 1.8f;
            if (spike)
            {
                _spikes++;
                if (_spikes <= 200)
                {
                    var top = _touched.Where(s => s.Frame >= 0.3).OrderByDescending(s => s.Frame).Take(10)
                        .Select(s => $"{s.Name} {s.Frame:0.0}{(s.FrameCalls > 1 ? $" (x{s.FrameCalls})" : "")}");
                    HitCamMod.Log.Msg($"PERF spike: frame {dt:0} ms (running average {_avg:0.0}), mod {mod:0.0} ms{(gcRan ? ", GC ran" : "")}: {string.Join(", ", top)}");
                }
            }
            else _avg = _avg <= 0f ? dt : Mathf.Lerp(_avg, dt, 0.05f);
            foreach (var s in _touched) { s.Frame = 0; s.FrameCalls = 0; }
            _touched.Clear();
        }

        /// Every section's time so far (ms), for a difference over a stretch of frames.
        public static Dictionary<string, double> Totals() => _stats.ToDictionary(kv => kv.Key, kv => kv.Value.Total);

        public static string Report(int lines = 30)
        {
            if (!On || _stats.Count == 0) return "Profiler off";
            var sb = new System.Text.StringBuilder();
            sb.Append($"PERF report: {_frames} frames, {_over33} over 33 ms, {_over50} over 50 ms, worst {_worstFrame:0} ms, {_spikes} spikes, mod average {(_frames > 0 ? _sumMod / _frames : 0):0.00} ms/frame\n");
            foreach (var s in _stats.Values.OrderByDescending(s => s.Max).Take(lines))
                sb.Append($"  {s.Name}: {s.Calls} calls, total {s.Total:0} ms, average {s.Total / Math.Max(1, s.Calls):0.000} ms, max {s.Max:0.0} ms\n");
            return sb.ToString();
        }

        public static void ResetStats()
        {
            _stats.Clear();
            _touched.Clear();
            _frames = _over33 = _over50 = _spikes = 0;
            _sumMod = _worstFrame = 0;
            _startFrame = -1;
        }
    }
}
