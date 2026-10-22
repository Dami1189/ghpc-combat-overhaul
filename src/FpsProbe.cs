using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GHPCHitCam
{
    /// self-test fps:<lab spec>: where the frames go when you hit something. frame time over a few s before the shot,
    /// while the hit cam plays and after, each time also with one suspect off for a moment (hit cam camera, every particle
    /// system, extra lights, mod world effects), so the diff is what that part costs on this PC, GPU included (profiler
    /// only sees the mod's CPU time)
    internal static class FpsProbe
    {
        static readonly FrameTiming[] _ft = new FrameTiming[1];
        static float _lastAvg;   // last Measure's average frame, ms

        internal static IEnumerator Measure(string phase, float seconds)
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
            _lastAvg = avg;
            string timing = gpuN > 0 ? $", GPU {gpu / gpuN:0.0} ms, CPU main thread {main / gpuN:0.0} ms, render thread {render / gpuN:0.0} ms" : ", GPU time not available";
            var after = Perf.Totals();
            var top = after.Select(kv => (kv.Key, ms: (kv.Value - (before.TryGetValue(kv.Key, out var b) ? b : 0)) / dts.Count))
                .Where(x => x.ms >= 0.15 && !x.Key.StartsWith("FpsProbe")).OrderByDescending(x => x.ms).Take(10).Select(x => $"{x.Key} {x.ms:0.00}");
            HitCamMod.Log.Msg($"FPS probe [{phase}]: {dts.Count} frames, average {avg:0.0} ms = {1000f / avg:0} fps, 95% under {p95:0.0} ms, mod CPU {mod / dts.Count:0.0} ms{timing}; per frame: {string.Join(", ", top)}");
        }

        /// everything drawn by one kind of thing, off for a moment then back as it was
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

        /// fpsrun:<label>:<lab spec>: one shot, numbers a player would see: frames before it (no hit cam), worst frame in the
        /// second after (the hit itself), and the hit cam's whole playback
        /// gamefx:off - game's own particle fx (explosions, fire, smoke, dust) hidden for the whole fpsrun so what's left of
        /// the drop is the mod's. x-ray copy's fx (own layer) stay
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

        /// your own crew's AI gunner would start shooting the targets (more hit cams mid measurement)
        static void StopOwnAi()
        {
            try
            {
                var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
                var ai = me != null && me.InfoBroker != null ? me.InfoBroker.AI : null;
                if (ai != null) { ai.Stop(); ai.enabled = false; }
            }
            catch (Exception e) { HitCamMod.Log.Warning("FPS probe: own AI: " + e.Message); }
        }

        /// replay:<label>: last hit played again with the current settings. same hit every time and no new explosions in the
        /// world, so the diff between two of these is just the settings' cost
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
                if (HideGameFx && ++frame % 5 == 0) HideFx();   // same scan every 5th frame before and during, so it cancels out
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

        /// fpsburst:<label>:<rounds>:<seconds between>:<lab spec>: autocannon burst at a range target, frames from the first
        /// round until the hit cam is done with it (mod CPU and its biggest parts)
        public static IEnumerator Burst(string[] spec)
        {
            StopOwnAi();
            string label = spec.Length > 1 ? spec[1] : "?";
            int rounds = spec.Length > 2 && int.TryParse(spec[2], out int r) ? r : 10;
            float gap = spec.Length > 3 && float.TryParse(spec[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) ? g : 0.075f;
            var lab = spec.Skip(4).ToArray();
            yield return ProvingGround.RespawnHurt();
            yield return new WaitForSecondsRealtime(2f);
            var before = new List<float>();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 3f) { yield return null; before.Add(Time.unscaledDeltaTime * 1000f); }
            var p0 = Perf.Totals();
            var during = new List<float>();
            double mod = 0;
            int fired = 0;
            float next = 0f, start = Time.realtimeSinceStartup;
            bool seen = false;
            while (Time.realtimeSinceStartup - start < 40f)
            {
                float now = Time.realtimeSinceStartup - start;
                if (fired < rounds && now >= next) { if (ProvingGround.LabShot(lab, out _, out _, respawnHurt: false)) fired++; next += gap; }
                yield return null;
                during.Add(Time.unscaledDeltaTime * 1000f);
                mod += Perf.LastFrameMod;
                if (HitCamPlayer.Current != null) { if (!seen && AutoTest.Opt("burstcap", false)) AutoTest.CaptureCurrent(); seen = true; }
                else if (fired >= rounds && now > rounds * gap + 1.5f && (seen || now > 8f)) break;
            }
            var p1 = Perf.Totals();
            int n = Mathf.Max(during.Count, 1);
            var top = p1.Select(kv => (kv.Key, ms: (kv.Value - (p0.TryGetValue(kv.Key, out var b0) ? b0 : 0)) / n))
                .Where(x => x.ms >= 0.1 && !x.Key.StartsWith("FpsProbe") && !x.Key.StartsWith("AutoTest")).OrderByDescending(x => x.ms).Take(12).Select(x => $"{x.Key} {x.ms:0.00}");
            float b = before.Count > 0 ? before.Average() : 0f, d = during.Count > 0 ? during.Average() : 0f;
            var sorted = during.OrderBy(x => x).ToList();
            float p95 = sorted.Count > 0 ? sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))] : 0f;
            HitCamMod.Log.Msg($"FPS burst [{label}]: {fired}/{rounds} rounds, {gap * 1000f:0} ms apart; before {1000f / Mathf.Max(b, 0.1f):0} fps ({b:0.0} ms); "
                + $"from the first round to the end {1000f / Mathf.Max(d, 0.1f):0} fps ({d:0.0} ms, 95% under {p95:0.0} ms, worst {(sorted.Count > 0 ? sorted[sorted.Count - 1] : 0):0} ms, {during.Count} frames, {during.Count * d / 1000f:0.0} s); "
                + $"mod CPU {mod / n:0.0} ms a frame: {string.Join(", ", top)}");
            yield return new WaitForSecondsRealtime(1f);
        }

        /// stats:<label>: what's in the scene (units, renderers, lights, scripts by type, time of day, quality) + frame time
        /// over 5 s from where the player sits, into stats_<label>.txt: for comparing two ways into the same map
        public static IEnumerator Stats(string[] spec)
        {
            string label = spec.Length > 1 ? spec[1] : "scene";
            StopOwnAi();
            yield return new WaitForSecondsRealtime(3f);
            var lines = new List<string>();
            var units = GHPC.World.SceneUnitsManager.Instance != null ? GHPC.World.SceneUnitsManager.Instance.AllUnitsInScene.OfType<GHPC.Unit>().Where(u => u != null).ToList() : new List<GHPC.Unit>();
            lines.Add($"units {units.Count}: active {units.Count(u => u.gameObject.activeInHierarchy)}, destroyed {units.Count(u => u.Destroyed)}, by type " + string.Join(", ", units.GroupBy(u => u.Type).Select(g => g.Key + " " + g.Count())));
            lines.Add("units: " + string.Join(", ", units.Where(u => u.gameObject.activeInHierarchy).Select(u => Recorder.SafeName(u) + "/" + u.Allegiance)));
            lines.Add($"scenes: " + string.Join(", ", Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Select(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name)));
            var rs = UnityEngine.Object.FindObjectsOfType<Renderer>();
            lines.Add($"renderers {rs.Length}: enabled {rs.Count(r => r.enabled && !r.forceRenderingOff)}, visible {rs.Count(r => r.isVisible)}, shadow casting {rs.Count(r => r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)}");
            var ls = UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.enabled).ToList();
            lines.Add($"lights {ls.Count}: " + string.Join(", ", ls.GroupBy(l => l.type + (l.shadows != LightShadows.None ? " shadowed" : "")).Select(g => g.Key + " " + g.Count())));
            var ps = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            lines.Add($"particle systems {ps.Length}: alive {ps.Count(p => p.IsAlive())}, particles {ps.Sum(p => p.particleCount)}");
            var rbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            lines.Add($"rigidbodies {rbs.Length}: awake {rbs.Count(b => !b.IsSleeping() && !b.isKinematic)}; animators {UnityEngine.Object.FindObjectsOfType<Animator>().Count(a => a.enabled)}; audio playing {UnityEngine.Object.FindObjectsOfType<AudioSource>().Count(a => a.isPlaying)}");
            lines.Add($"transforms {UnityEngine.Object.FindObjectsOfType<Transform>().Length}");
            System.GC.Collect();
            lines.Add($"memory: process {System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576} MB, Unity allocated {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576} MB (reserved {UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576}), graphics {UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576} MB, managed {System.GC.GetTotalMemory(false) / 1048576} MB");
            var sun = RenderSettings.sun;
            lines.Add($"quality {QualitySettings.names[QualitySettings.GetQualityLevel()]}, shadow distance {QualitySettings.shadowDistance:0}, lod bias {QualitySettings.lodBias:0.00}; fog {RenderSettings.fog} {RenderSettings.fogDensity:0.00000}; sun {(sun != null ? sun.transform.eulerAngles + " intensity " + sun.intensity.ToString("0.00") : "-")}; daytime {GHPC.SceneController.IsDaytime}");
            foreach (var t in Terrain.activeTerrains) lines.Add($"terrain {t.name}: detail distance {t.detailObjectDistance:0} density {t.detailObjectDensity:0.00}, tree distance {t.treeDistance:0}, billboard {t.treeBillboardDistance:0}, pixel error {t.heightmapPixelError:0}, draw trees {t.drawTreesAndFoliage}");
            var cam = Camera.main;
            lines.Add($"camera {(cam != null ? cam.name + " at " + cam.transform.position + " looking " + cam.transform.forward + " far " + cam.farClipPlane.ToString("0") : "-")}; cameras " + string.Join(", ", Camera.allCameras.Select(c => c.name)));
            lines.Add($"mod: proving ground on {ProvingGround.Active} (window {ProvingGround.WindowOpen}), armor sim {ArmorSim.Active}; {(ProvingGround.Active ? ProvingGround.DeckCheck() : "")}");
            var mbs = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Where(m => m.isActiveAndEnabled).GroupBy(m => m.GetType().FullName).OrderByDescending(g => g.Count()).ToList();
            lines.Add($"scripts {mbs.Sum(g => g.Count())} enabled, {mbs.Count} kinds:");
            lines.AddRange(mbs.Select(g => $"   {g.Count(),5}  {g.Key}"));
            try { System.IO.File.WriteAllLines(System.IO.Path.Combine(AutoTest.OutDir, "stats_" + label + ".txt"), lines); } catch { }
            foreach (var l in lines.Take(17)) HitCamMod.Log.Msg($"STATS [{label}] {l}");
            yield return Measure("stats " + label, 5f);
        }

        static int _errors;
        static string _firstError;
        static bool _listening;

        /// title menu frame time (5 s) with the game's errors and exceptions counted meanwhile (one thrown every frame = slow
        /// menu and the mod's menu parts missing)
        public static IEnumerator MenuFps(string label)
        {
            if (!_listening)
            {
                _listening = true;
                Application.logMessageReceivedThreaded += (msg, stack, type) =>
                {
                    if (type != LogType.Error && type != LogType.Exception) return;
                    if (_errors++ == 0) _firstError = msg + " | " + (stack ?? "").Split('\n').FirstOrDefault();
                };
            }
            _errors = 0;
            _firstError = null;
            HitCamMod.Log.Msg($"MENU FPS [{label}]: vSync {QualitySettings.vSyncCount}, frame limit {Application.targetFrameRate}, scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}, cameras {string.Join(", ", Camera.allCameras.Select(c => c.name))}, canvases {UnityEngine.Object.FindObjectsOfType<Canvas>().Length}");
            yield return Measure("menu " + label, 5f);
            HitCamMod.Log.Msg($"MENU FPS [{label}]: {_errors} errors in it{(_firstError != null ? ", the first: " + _firstError : "")}");
        }

        /// erashots:<name>: modded targets' ERA bricks drawn four ways (cast shadows or not, receive or not, GPU-instanced
        /// materials): close-up of the nearest ERA tank and the whole range's frame time (uncapped) for each. ends like the
        /// range has them (casting shadows, materials as they were)
        public static IEnumerator EraShots(string[] spec)
        {
            string name = spec.Length > 1 ? spec[1] : "era";
            var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
            var targets = ProvingGround.TargetUnits.Where(u => u != null).ToList();
            var bricks = new List<MeshRenderer>();
            GHPC.Unit near = null;
            float nearD = float.MaxValue;
            foreach (var u in targets)
            {
                var b = ModdedVehicles.EraBricks(u, out _);
                bricks.AddRange(b);
                float d = me != null ? Vector3.Distance(me.transform.position, u.transform.position) : 0f;
                if (b.Count > 20 && d < nearD) { nearD = d; near = u; }
            }
            if (near == null) { HitCamMod.Log.Warning("erashots: no target with ERA"); yield break; }
            var mats = bricks.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            var inst = mats.ToDictionary(m => m, m => m.enableInstancing);
            HitCamMod.Log.Msg($"ERA SHOTS: {bricks.Count} bricks on {targets.Count} targets, {mats.Count} materials ({string.Join(", ", mats.Select(m => m.name + " / " + m.shader.name).Distinct().Take(6))}); close-up of {Recorder.SafeName(near)} at {nearD:0} m");
            int vs = QualitySettings.vSyncCount, fr = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            var main = Camera.main;
            var go = new GameObject("era shots");
            var cam = go.AddComponent<Camera>();
            if (main != null) cam.CopyFrom(main);
            cam.enabled = false;
            cam.fieldOfView = 40f;
            var rt = new RenderTexture(1600, 900, 24);
            cam.targetTexture = rt;
            var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            Vector3 c = near.transform.position + Vector3.up * 1.6f;
            Vector3 fwd = near.transform.forward;
            cam.transform.position = c + (fwd * 0.8f - near.transform.right * 0.6f).normalized * 9f + Vector3.up * 2.5f;
            cam.transform.LookAt(c);
            var ways = new[]
            {
                ("1 cast on, receive on", UnityEngine.Rendering.ShadowCastingMode.On, true, false),
                ("2 cast off, receive on", UnityEngine.Rendering.ShadowCastingMode.Off, true, false),
                ("3 cast off, receive off", UnityEngine.Rendering.ShadowCastingMode.Off, false, false),
                ("4 cast off, receive on, instanced", UnityEngine.Rendering.ShadowCastingMode.Off, true, true),
                ("5 cast off, receive on, shared materials", UnityEngine.Rendering.ShadowCastingMode.Off, true, true),
            };
            // bricks' own material copies ("K1 (Instance)") and one shared instanced copy per look
            var own = bricks.Where(r => r != null).ToDictionary(r => r, r => r.sharedMaterials);
            var shared = new Dictionary<string, Material>();
            Material[] Shared(Material[] ms) => ms.Select(m =>
            {
                if (m == null) return m;
                string key = m.shader.name + "|" + (m.mainTexture != null ? m.mainTexture.GetInstanceID() : 0) + "|" + (m.HasProperty("_Color") ? m.color.ToString() : "") + "|" + string.Join(",", m.shaderKeywords);
                if (!shared.TryGetValue(key, out var s)) { s = new Material(m) { enableInstancing = true }; shared[key] = s; }
                return s;
            }).ToArray();
            for (int pass = 0; pass < 2; pass++)   // twice: frame time drifts a bit over a run
            foreach (var (label, cast, receive, instanced) in ways)
            {
                foreach (var r in bricks) if (r != null) { r.shadowCastingMode = cast; r.receiveShadows = receive; }
                foreach (var m in mats) m.enableInstancing = instanced || inst[m];
                bool share = label.StartsWith("5");
                foreach (var kv in own) if (kv.Key != null) kv.Key.sharedMaterials = share ? Shared(kv.Value) : kv.Value;
                if (share && pass == 0) HitCamMod.Log.Msg($"ERA SHOTS: {own.Count} bricks on {shared.Count} shared materials");
                yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                if (pass == 0) try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(AutoTest.OutDir, $"{name}_{label.Substring(0, 1)}.png"), tex.EncodeToPNG()); } catch { }
                yield return Measure($"era {label}", 4f);
            }
            foreach (var r in bricks) if (r != null) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = true; }
            foreach (var m in mats) m.enableInstancing = inst[m];
            foreach (var kv in own) if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(go);
            QualitySettings.vSyncCount = vs;
            Application.targetFrameRate = fr;
        }

        /// keydump: every game keyboard binding (Rewired: each player's keyboard maps, enabled or not) into keys.txt, and
        /// each of the mod's keys checked against them
        public static IEnumerator KeyDump(string[] spec)
        {
            yield return null;
            var lines = new List<string>();
            var used = new Dictionary<KeyCode, List<string>>();
            try
            {
                foreach (var p in Rewired.ReInput.players.AllPlayers)
                    foreach (var map in p.controllers.maps.GetAllMaps(Rewired.ControllerType.Keyboard))
                    {
                        string cat = Rewired.ReInput.mapping.GetMapCategory(map.categoryId)?.name ?? map.categoryId.ToString();
                        foreach (var a in map.ButtonMaps)
                        {
                            var act = Rewired.ReInput.mapping.GetAction(a.actionId);
                            string mods = string.Join("+", new[] { a.modifierKey1, a.modifierKey2, a.modifierKey3 }.Where(m => m != Rewired.ModifierKey.None));
                            string what = $"{act?.name ?? a.actionId.ToString()} ({cat}{(map.enabled ? "" : ", map off")}{(mods.Length > 0 ? ", with " + mods : "")})";
                            lines.Add($"{p.name}\t{cat}\t{(map.enabled ? "on" : "off")}\t{a.keyCode}\t{mods}\t{act?.name}\t{act?.descriptiveName}");
                            if (!used.TryGetValue(a.keyCode, out var l)) used[a.keyCode] = l = new List<string>();
                            if (!l.Contains(what)) l.Add(what);
                        }
                    }
            }
            catch (Exception e) { HitCamMod.Log.Warning("keydump: " + e); }
            try { System.IO.File.WriteAllLines(System.IO.Path.Combine(AutoTest.OutDir, "keys.txt"), lines); } catch { }
            var ours = Settings.Cat.Entries.Where(e => e.Identifier.EndsWith("Key") && e.BoxedValue is string).Select(e => (e.Identifier, (string)e.BoxedValue)).ToList();
            ours.Add(("Arsenal round back", "LeftBracket"));
            ours.Add(("Arsenal round next", "RightBracket"));
            var res = new List<string>();
            foreach (var (id, val) in ours)
            {
                if (!Enum.TryParse(val, true, out KeyCode kc)) { res.Add($"{id}={val}: ?"); continue; }
                res.Add(used.TryGetValue(kc, out var l) ? $"{id}={val}: GAME USES IT for {string.Join("; ", l)}" : $"{id}={val}: free");
            }
            HitCamMod.Log.Msg($"KEY DUMP: {lines.Count} keyboard bindings of the game (keys.txt). Ours: " + string.Join(" | ", res));
            var fkeys = Enumerable.Range(0, 15).Select(i => KeyCode.F1 + i).Select(k => $"{k}: {(used.TryGetValue(k, out var l) ? string.Join("; ", l) : "-")}");
            HitCamMod.Log.Msg("KEY DUMP: the F keys in the game: " + string.Join(" | ", fkeys));
        }

        /// layerscan: view ahead drawn with each camera layer left out in turn, counting near-black pixels in the upper half:
        /// finds which layer the dark band on the empty map's horizon is on
        public static IEnumerator LayerScan(string[] spec)
        {
            var main = Camera.main;
            if (main == null) yield break;
            var go = new GameObject("layer scan");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false;
            var rt = new RenderTexture(800, 340, 24);
            cam.targetTexture = rt;
            var tex = new Texture2D(800, 340, TextureFormat.RGB24, false);
            cam.transform.SetPositionAndRotation(main.transform.position + Vector3.up * 3f, Quaternion.Euler(2f, main.transform.eulerAngles.y - 30f, 0f));
            int all = main.cullingMask;
            int Dark(int mask)
            {
                cam.cullingMask = mask;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                int n = 0;
                var px = tex.GetPixels32();
                for (int y = rt.height / 2; y < rt.height; y++)
                    for (int x = 0; x < rt.width; x++) { var c = px[y * rt.width + x]; if (c.r < 40 && c.g < 40 && c.b < 40) n++; }
                return n;
            }
            yield return new WaitForEndOfFrame();
            int baseN = Dark(all);
            var res = new List<string>();
            for (int l = 0; l < 32; l++)
            {
                if ((all & (1 << l)) == 0) continue;
                int n = Dark(all & ~(1 << l));
                res.Add($"{l} {LayerMask.LayerToName(l)}: {n}");
            }
            HitCamMod.Log.Msg($"LAYER SCAN: {baseN} dark pixels with all layers; without each: " + string.Join(", ", res));
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(go);
        }

        /// lookaround:<name>: four shots around you (every 90 deg, a bit above your camera) with our own camera: what's on
        /// the horizon (empty map leftovers)
        public static IEnumerator LookAround(string[] spec)
        {
            string name = spec.Length > 1 ? spec[1] : "look";
            var main = Camera.main;
            if (main == null) { HitCamMod.Log.Warning("lookaround: no main camera"); yield break; }
            var go = new GameObject("look around");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false;
            var rt = new RenderTexture(1600, 680, 24);
            cam.targetTexture = rt;
            var tex = new Texture2D(1600, 680, TextureFormat.RGB24, false);
            Vector3 p = main.transform.position + Vector3.up * 3f;
            float yaw = main.transform.eulerAngles.y;
            for (int i = 0; i < 4; i++)
            {
                yield return new WaitForEndOfFrame();
                cam.transform.SetPositionAndRotation(p, Quaternion.Euler(4f, yaw + i * 90f, 0f));
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(AutoTest.OutDir, $"{name}_{i * 90:000}.png"), tex.EncodeToPNG()); } catch { }
            }
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt);
            UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(go);
            HitCamMod.Log.Msg("lookaround: 4 pictures " + name);
        }

        /// audio: engine sound state on your vehicle, a few range targets and a mission unit
        public static IEnumerator Audio(string[] spec)
        {
            yield return new WaitForSecondsRealtime(1f);
            yield return AudioLog();
            if (spec.Length > 1 && spec[1] == "kick")
            {
                var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
                foreach (var u in ProvingGround.TargetUnits.Concat(new[] { me }).Where(x => x != null))
                {
                    var vac = u.GetComponent<GHPC.Audio.VehicleAudioController>();
                    if (vac == null) continue;
                    vac.enabled = false;
                    vac.enabled = true;
                }
                HitCamMod.Log.Msg("AUDIO: vehicle audio switched off and on again on ours");
                yield return new WaitForSecondsRealtime(1f);
                yield return AudioLog();
            }
        }

        static IEnumerator AudioLog()
        {
            yield return null;
            var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
            var targets = ProvingGround.TargetUnits.Where(u => u != null).Take(4).ToList();
            var mission = GHPC.World.SceneUnitsManager.Instance.AllUnitsInScene.OfType<GHPC.Unit>().Where(u => u != null && u != me && !ProvingGround.IsTarget(u) && u.gameObject.activeInHierarchy && u.GetComponent<GHPC.Audio.VehicleAudioController>() != null).Take(2).ToList();
            var t = typeof(GHPC.Audio.VehicleAudioController);
            var fEngine = HarmonyLib.AccessTools.Field(t, "_engineInstance");
            var fDeferred = HarmonyLib.AccessTools.Field(t, "_deferredEnable");
            var pInit = HarmonyLib.AccessTools.Property(t, "IsInitialized");
            var fStopped = HarmonyLib.AccessTools.Field(typeof(GHPC.Audio.AudibleEventInstance), "_stopped");
            var fDist = HarmonyLib.AccessTools.Field(typeof(GHPC.Audio.AudibleEventInstance), "_audibleDistanceSqr");
            var cam = Camera.main;
            foreach (var (u, what) in new[] { (me, "you") }.Concat(targets.Select(x => (x, "target"))).Concat(mission.Select(x => (x, "mission unit"))))
            {
                if (u == null) continue;
                var vac = u.GetComponent<GHPC.Audio.VehicleAudioController>();
                if (vac == null) { HitCamMod.Log.Msg($"AUDIO [{what}] {Recorder.SafeName(u)}: no vehicle audio"); continue; }
                var eng = fEngine?.GetValue(vac) as GHPC.Audio.AudibleEventInstance;
                string e = eng == null ? "no engine sound" : $"engine sound {(eng.eventInstance.isValid() ? "playing" : "not playing")}, stopped {fStopped?.GetValue(eng)}, audible to {Mathf.Sqrt(Mathf.Min((float)(fDist?.GetValue(eng) ?? 0f), 1e12f)):0} m";
                HitCamMod.Log.Msg($"AUDIO [{what}] {Recorder.SafeName(u)}: enabled {vac.enabled} (active {vac.isActiveAndEnabled}), initialized {pInit?.GetValue(vac)}, deferred {fDeferred?.GetValue(vac)}, {e}; {(cam != null ? Vector3.Distance(cam.transform.position, u.transform.position).ToString("0") + " m from the camera" : "")}; destroyed {u.Destroyed}, AI {(u.InfoBroker?.AI != null ? u.InfoBroker.AI.enabled.ToString() : "-")}");
            }
        }

        /// cost:<label>: what the range targets and the mission's units cost, uncapped (no vsync, no frame limit): frame time
        /// with each part off in turn (targets not drawn, their wheel physics off, targets gone, mission units gone too).
        /// leaves them gone, so it's the last step of a run
        public static IEnumerator Cost(string[] spec)
        {
            string label = spec.Length > 1 ? spec[1] : "range";
            StopOwnAi();
            int vs = QualitySettings.vSyncCount, fr = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return new WaitForSecondsRealtime(2f);
            var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
            var targets = ProvingGround.TargetUnits.Where(u => u != null).ToList();
            var mission = GHPC.World.SceneUnitsManager.Instance.AllUnitsInScene.OfType<GHPC.Unit>().Where(u => u != null && u != me && !targets.Contains(u) && u.gameObject.activeInHierarchy).ToList();
            HitCamMod.Log.Msg($"COST [{label}]: {targets.Count} targets, {mission.Count} mission units, you in {(me != null ? Recorder.SafeName(me) : "-")}");
            yield return Measure($"cost {label}: all on", 4f);
            var h = new Hidden();
            foreach (var u in targets) h.Renderers(u.GetComponentsInChildren<Renderer>());
            yield return Measure($"cost {label}: targets not drawn ({h.Count} renderers)", 4f);
            h.Restore();
            var wheels = targets.SelectMany(u => u.GetComponentsInChildren<Behaviour>()).Where(b => b != null && b.enabled && (b.GetType().Namespace ?? "").StartsWith("NWH")).ToList();
            foreach (var b in wheels) b.enabled = false;
            yield return Measure($"cost {label}: targets' vehicle physics off ({wheels.Count} parts)", 4f);
            foreach (var b in wheels) if (b != null) b.enabled = true;
            yield return Measure($"cost {label}: all on again", 3f);
            foreach (var u in targets) u.gameObject.SetActive(false);
            yield return Measure($"cost {label}: targets gone", 4f);
            foreach (var u in mission) u.gameObject.SetActive(false);
            yield return Measure($"cost {label}: targets and mission units gone", 4f);
            QualitySettings.vSyncCount = vs;
            Application.targetFrameRate = fr;
        }

        /// costtypes:<label>: range target scripts kind by kind, each kind off for a moment (uncapped): which ones eat the frames
        public static IEnumerator CostTypes(string[] spec)
        {
            string label = spec.Length > 1 ? spec[1] : "range";
            StopOwnAi();
            int vs = QualitySettings.vSyncCount, fr = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return new WaitForSecondsRealtime(2f);
            var targets = ProvingGround.TargetUnits.Where(u => u != null).ToList();
            var kinds = targets.SelectMany(u => u.GetComponentsInChildren<Behaviour>()).Where(b => b != null && b.enabled && !(b is GHPC.Unit))
                .GroupBy(b => b.GetType()).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).Take(40).ToList();
            yield return Measure($"costtypes {label}: all on", 3f);
            float baseMs = _lastAvg;
            var found = new List<(string kind, int n, float ms)>();
            foreach (var g in kinds)
            {
                var on = g.Where(b => b != null && b.enabled).ToList();
                foreach (var b in on) b.enabled = false;
                yield return Measure($"costtypes {label}: {g.Key.Name} off ({on.Count})", 2.5f);
                found.Add((g.Key.FullName, on.Count, baseMs - _lastAvg));
                foreach (var b in on) if (b != null) b.enabled = true;
                yield return new WaitForSecondsRealtime(0.5f);
            }
            yield return Measure($"costtypes {label}: all on again", 3f);
            HitCamMod.Log.Msg($"COST TYPES [{label}] ({targets.Count} targets, {baseMs:0.00} ms a frame, again {_lastAvg:0.00}): " + string.Join("; ", found.OrderByDescending(x => x.ms).Select(x => $"{x.kind} x{x.n} {x.ms:+0.00;-0.00} ms")));
            QualitySettings.vSyncCount = vs;
            Application.targetFrameRate = fr;
        }

        public static IEnumerator Run(string[] spec)
        {
            HitCamMod.Log.Msg($"FPS probe: screen {Screen.width}x{Screen.height}, vSync {QualitySettings.vSyncCount}, target {Application.targetFrameRate}, frame timing stats {(FrameTimingManager.IsFeatureEnabled() ? "on" : "off")}");
            // own crew's AI gunner would start shooting the targets (more hit cams mid measurement)
            try
            {
                var me = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
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
