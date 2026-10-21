using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GHPCHitCam
{
    /// Self-test fps:<lab spec>: where the frames go when you hit something. Frame time is measured over a few seconds
    /// before the shot, while the hit cam plays and after it, each time also with one suspect switched off for a moment
    /// (the hit cam's own camera, every particle system, the extra lights, the mod's world effects), so the difference
    /// says what that part costs on this PC, GPU included (the profiler only sees the mod's CPU time).
    internal static class FpsProbe
    {
        static readonly FrameTiming[] _ft = new FrameTiming[1];

        static IEnumerator Measure(string phase, float seconds)
        {
            yield return null;
            yield return null;
            var before = Perf.Totals();
            var dts = new List<float>();
            double mod = 0, gpu = 0, main = 0, render = 0;
            int gpuN = 0;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                yield return null;
                dts.Add(Time.unscaledDeltaTime * 1000f);
                mod += Perf.LastFrameMod;
                try
                {
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, _ft) > 0 && _ft[0].gpuFrameTime > 0)
                    {
                        gpu += _ft[0].gpuFrameTime; main += _ft[0].cpuMainThreadFrameTime; render += _ft[0].cpuRenderThreadFrameTime; gpuN++;
                    }
                }
                catch { }
            }
            if (dts.Count == 0) yield break;
            dts.Sort();
            float avg = dts.Average(), p95 = dts[Mathf.Min(dts.Count - 1, (int)(dts.Count * 0.95f))];
            string timing = gpuN > 0 ? $", GPU {gpu / gpuN:0.0} ms, CPU main thread {main / gpuN:0.0} ms, render thread {render / gpuN:0.0} ms" : ", GPU time not available";
            var after = Perf.Totals();
            var top = after.Select(kv => (kv.Key, ms: (kv.Value - (before.TryGetValue(kv.Key, out var b) ? b : 0)) / dts.Count))
                .Where(x => x.ms >= 0.15 && !x.Key.StartsWith("FpsProbe")).OrderByDescending(x => x.ms).Take(10).Select(x => $"{x.Key} {x.ms:0.00}");
            HitCamMod.Log.Msg($"FPS probe [{phase}]: {dts.Count} frames, average {avg:0.0} ms = {1000f / avg:0} fps, 95% under {p95:0.0} ms, mod CPU {mod / dts.Count:0.0} ms{timing}; per frame: {string.Join(", ", top)}");
        }

        /// Everything drawn by one kind of thing, off for a moment and back as it was.
        class Hidden
        {
            readonly List<Renderer> _r = new List<Renderer>();
            readonly List<Light> _l = new List<Light>();
            public void Renderers(IEnumerable<Renderer> rs) { foreach (var r in rs) if (r != null && r.enabled && !r.forceRenderingOff) { r.forceRenderingOff = true; _r.Add(r); } }
            public void Lights(IEnumerable<Light> ls) { foreach (var l in ls) if (l != null && l.enabled) { l.enabled = false; _l.Add(l); } }
            public void Restore() { foreach (var r in _r) if (r != null) r.forceRenderingOff = false; foreach (var l in _l) if (l != null) l.enabled = true; }
            public int Count => _r.Count + _l.Count;
        }

        static IEnumerable<Transform> ModWorld() =>
            UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => t != null && t.name.StartsWith("HitCam ") && t.name != "HitCam Camera");

        static IEnumerator Without(string phase, float seconds, Func<Hidden, string> hide)
        {
            var h = new Hidden();
            string what = hide(h);
            yield return Measure(phase + $" ({what}: {h.Count} off)", seconds);
            h.Restore();
        }

        /// fpsrun:<label>:<lab spec>: one shot, the numbers a player would see. Frames before it (no hit cam), the worst
        /// frame in the second after it (the hit itself), and the hit cam's whole playback.
        /// gamefx:off - the game's own particle effects (explosions, fire, smoke, dust) hidden through a whole fpsrun, so what
        /// is left of the drop is the mod's. The x-ray copy's effects (its own layer) stay.
        public static bool HideGameFx;
        static readonly HashSet<Renderer> _gameFx = new HashSet<Renderer>();

        static void HideFx()
        {
            foreach (var r in UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>())
                if (r != null && r.gameObject.layer != Vis.Layer && r.enabled && !r.forceRenderingOff) { r.forceRenderingOff = true; _gameFx.Add(r); }
        }

        static void ShowFx()
        {
            foreach (var r in _gameFx) if (r != null) r.forceRenderingOff = false;
            _gameFx.Clear();
        }

        /// Your own crew's AI gunner would start shooting the targets (more hit cams in the middle of a measurement).
        static void StopOwnAi()
        {
            try
            {
                var me = GHPC.Player.PlayerInput.Instance != null ? GHPC.Player.PlayerInput.Instance.CurrentPlayerUnit : null;
                var ai = me != null && me.InfoBroker != null ? me.InfoBroker.AI : null;
                if (ai != null) { ai.Stop(); ai.enabled = false; }
            }
            catch (Exception e) { HitCamMod.Log.Warning("FPS probe: own AI: " + e.Message); }
        }

        /// replay:<label>: the last hit played again with the settings as they are now. The same hit every time and no
        /// new explosions in the world, so the difference between two of these is the settings' own cost.
        public static IEnumerator Replay(string[] spec)
        {
            string label = spec.Length > 1 ? spec[1] : "?";
            StopOwnAi();
            while (HitCamPlayer.Current != null || HitCamPlayer.Building) yield return null;
            yield return new WaitForSecondsRealtime(1.5f);
            var before = new List<float>();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 2.5f) { yield return null; before.Add(Time.unscaledDeltaTime * 1000f); }
            HitCamPlayer.ReplayLast();
            float open = 0f;
            t0 = Time.realtimeSinceStartup;
            while (HitCamPlayer.Current == null && Time.realtimeSinceStartup - t0 < 5f) { yield return null; open = Mathf.Max(open, Time.unscaledDeltaTime * 1000f); }
            var during = new List<float>();
            double mod = 0;
            string title = HitCamPlayer.Current != null ? HitCamPlayer.Current.Rec.Title : "none";
            t0 = Time.realtimeSinceStartup;
            while (HitCamPlayer.Current != null && Time.realtimeSinceStartup - t0 < 60f) { yield return null; during.Add(Time.unscaledDeltaTime * 1000f); mod += Perf.LastFrameMod; }
            float b = before.Count > 0 ? before.Average() : 0f, d = during.Count > 0 ? during.Average() : 0f;
            var sorted = during.OrderBy(x => x).ToList();
            float p95 = sorted.Count > 0 ? sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))] : 0f;
            HitCamMod.Log.Msg($"FPS replay [{label}]: before {1000f / Mathf.Max(b, 0.1f):0} fps ({b:0.00} ms); hit cam {1000f / Mathf.Max(d, 0.1f):0} fps ({d:0.00} ms, 95% under {p95:0.0} ms, {during.Count} frames, {title}); worst frame while it opened {open:0} ms; mod CPU in it {(during.Count > 0 ? mod / during.Count : 0):0.00} ms");
        }

        public static IEnumerator RunOne(string[] spec)
        {
            StopOwnAi();
            string label = (spec.Length > 1 ? spec[1] : "?") + (HideGameFx ? ", game effects hidden" : "");
            var lab = spec.Skip(2).ToArray();
            yield return ProvingGround.RespawnHurt();
            yield return new WaitForSecondsRealtime(3f);
            int frame = 0;
            if (HideGameFx) HideFx();
            var before = new List<float>();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 3f) { yield return null; before.Add(Time.unscaledDeltaTime * 1000f); if (HideGameFx && ++frame % 5 == 0) HideFx(); }
            if (!ProvingGround.LabShot(lab, out _, out _)) { yield return new WaitForSecondsRealtime(4f); if (!ProvingGround.LabShot(lab, out _, out _)) { HitCamMod.Log.Warning("FPS run " + label + ": no lab shot"); yield break; } }
            float fired = Time.realtimeSinceStartup, worstHit = 0f;
            var during = new List<float>();
            double mod = 0;
            bool seen = false;
            string title = "";
            while (Time.realtimeSinceStartup - fired < 60f)
            {
                yield return null;
                if (HideGameFx && ++frame % 5 == 0) HideFx();   // the same scan every 5th frame before and during: it cancels out
                float dt = Time.unscaledDeltaTime * 1000f;
                if (Time.realtimeSinceStartup - fired < 1f) worstHit = Mathf.Max(worstHit, dt);
                var cur = HitCamPlayer.Current;
                if (cur != null) { seen = true; during.Add(dt); mod += Perf.LastFrameMod; title = cur.Rec.Title; }
                else if (seen || (!Settings.Enabled.Value && Time.realtimeSinceStartup - fired > 10f)) break;
                if (!Settings.Enabled.Value) during.Add(dt);
            }
            float Avg(List<float> l) => l.Count > 0 ? l.Average() : 0f;
            float P95(List<float> l) { if (l.Count == 0) return 0f; var c = l.OrderBy(x => x).ToList(); return c[Mathf.Min(c.Count - 1, (int)(c.Count * 0.95f))]; }
            float b = Avg(before), d = Avg(during);
            if (HideGameFx) ShowFx();
            HitCamMod.Log.Msg($"FPS run [{label}]: before {1000f / b:0} fps ({b:0.0} ms); hit cam {(d > 0 ? 1000f / d : 0):0} fps average ({d:0.0} ms, 95% under {P95(during):0.0} ms, {during.Count} frames, {title}); worst frame in the second after the shot {worstHit:0} ms; mod CPU in the hit cam {(during.Count > 0 ? mod / during.Count : 0):0.0} ms");
            yield return new WaitForSecondsRealtime(1f);
        }

        public static IEnumerator Run(string[] spec)
        {
            HitCamMod.Log.Msg($"FPS probe: screen {Screen.width}x{Screen.height}, vSync {QualitySettings.vSyncCount}, target {Application.targetFrameRate}, frame timing stats {(FrameTimingManager.IsFeatureEnabled() ? "on" : "off")}");
            // your own crew's AI gunner would start shooting the targets (more hit cams in the middle of a measurement)
            try
            {
                var me = GHPC.Player.PlayerInput.Instance != null ? GHPC.Player.PlayerInput.Instance.CurrentPlayerUnit : null;
                var ai = me != null && me.InfoBroker != null ? me.InfoBroker.AI : null;
                if (ai != null) { ai.Stop(); ai.enabled = false; }
            }
            catch (Exception e) { HitCamMod.Log.Warning("FPS probe: own AI: " + e.Message); }
            yield return new WaitForSecondsRealtime(2f);
            yield return Measure("before the shot", 4f);
            yield return Without("before the shot", 2f, h => { h.Renderers(UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>()); return "every particle system"; });

            var lab = spec.Skip(1).ToArray();
            if (!ProvingGround.LabShot(lab, out _, out _)) { yield return new WaitForSecondsRealtime(3f); if (!ProvingGround.LabShot(lab, out _, out _)) { HitCamMod.Log.Warning("FPS probe: no lab shot"); yield break; } }
            float t0 = Time.realtimeSinceStartup;
            while (HitCamPlayer.Current == null && Time.realtimeSinceStartup - t0 < 6f) yield return null;
            if (HitCamPlayer.Current == null) { HitCamMod.Log.Warning("FPS probe: no hit cam"); yield break; }
            HitCamMod.Log.Msg("FPS probe: cameras now " + string.Join("; ", Camera.allCameras.Select(c => $"{c.name} {(c.targetTexture != null ? c.targetTexture.width + "x" + c.targetTexture.height + " AA" + c.targetTexture.antiAliasing : "screen")} depth {c.depth} {c.renderingPath}")));
            yield return Measure("hit cam playing", 2.5f);
            HitCamPlayer.TestCamera(false);
            yield return Measure("hit cam playing, its camera off", 2f);
            HitCamPlayer.TestCamera(true);
            yield return Without("hit cam playing", 2f, h => { h.Renderers(UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>()); return "every particle system"; });
            yield return Without("hit cam playing", 2f, h => { h.Lights(UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.type != LightType.Directional)); return "point and spot lights"; });
            yield return Measure("hit cam playing, again", 2f);

            t0 = Time.realtimeSinceStartup;
            while (HitCamPlayer.Current != null && Time.realtimeSinceStartup - t0 < 40f) yield return null;
            yield return Measure("after the hit cam, the wreck", 3f);
            yield return Without("after", 2f, h => { foreach (var t in ModWorld()) { h.Renderers(t.GetComponentsInChildren<Renderer>()); h.Lights(t.GetComponentsInChildren<Light>()); } return "the mod's world effects"; });
            yield return Without("after", 2f, h => { h.Renderers(UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>()); return "every particle system"; });
            yield return Without("after", 2f, h => { h.Lights(UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.type != LightType.Directional)); return "point and spot lights"; });
            yield return Measure("after, again", 3f);
        }
    }
}
