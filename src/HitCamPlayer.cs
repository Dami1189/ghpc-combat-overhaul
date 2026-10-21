using System;
using System.Collections.Generic;
using System.Linq;
using GHPC.Humans;
using GHPC.Weapons;
using UnityEngine;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    /// Owns the picture-in-picture camera and the hits waiting to be shown.
    /// Hits on the target already on screen join that live session (like War Thunder's hit camera
    /// with an autocannon burst) instead of queueing up as separate replays.
    internal static class HitCamPlayer
    {
        static readonly List<HitRecord> _queue = new List<HitRecord>();
        static Playback _cur;
        // the next hit cam while it's being built (a piece a frame), and the hits that came in meanwhile
        static System.Collections.IEnumerator _build;
        static Playback _building;
        static List<HitRecord> _buildRecs;
        static bool _buildCook;
        static readonly List<HitRecord> _buildExtra = new List<HitRecord>();
        static bool Busy => _cur != null || _build != null;
        static HitRecord _last;
        static Camera _cam;
        static RenderTexture _rt;

        public static Playback Current => _cur;
        public static bool Building => _build != null;

        /// Self-test (FpsProbe): the hit cam keeps playing but its camera stops drawing.
        public static void TestCamera(bool on) { if (_cam != null) _cam.enabled = on && _cur != null; }
        public static RenderTexture Texture => _rt;

        public static void Enqueue(HitRecord rec)
        {
            if (rec.Snapshot == null) return;
            if (Settings.PlayerShotsOnly.Value && !rec.PlayerShot) return;   // hits on you (recorded for the AAR) aren't shown live
            if (!BigEnough(rec)) return;   // autocannons: recorded for the HUD and the report, no hit cam
            _last = rec;
            if (!Settings.Enabled.Value && !AutoTest.Active) return;
            if (_build != null && _buildRecs != null && _buildRecs.Contains(rec)) return;   // its hit cam was started at the impact
            if (_cur != null && _cur.TryMerge(rec)) return;
            if (_build != null) { _buildExtra.Add(rec); return; }   // merged or queued once that one is built
            if (_cur == null) { Begin(new List<HitRecord> { rec }); return; }
            if (!Settings.QueueHits.Value) { StopCurrent(); Begin(new List<HitRecord> { rec }); return; }
            _queue.Add(rec);
            // never fall far behind: keep the newest hits only
            while (_queue.Count > 24) _queue.RemoveAt(0);
        }

        internal static float Cal(HitRecord rec) => rec.Ammo != null ? Mathf.Max(rec.Ammo.Caliber, rec.Ammo.Radius * 2000f) : 999f;

        /// A round the hit cam shows: from MinCaliberMm up (any round in the Armor Sim, which fires one at a time).
        static bool BigEnough(HitRecord rec) => ArmorSim.Active || Cal(rec) >= Settings.MinCaliberMm.Value;

        static readonly HashSet<HitRecord> _cookOffs = new HashSet<HitRecord>();
        static readonly List<HitRecord> _lateBooms = new List<HitRecord>();

        /// Called from the game's explosion hook: looked at on the next frame, outside the game's own damage code.
        public static void OnLateExplosion(HitRecord rec) { if (rec != null && !_lateBooms.Contains(rec)) _lateBooms.Add(rec); }

        /// The rack cooked off after this hit's own hit cam had closed (in GHPC a burning charge heats the warheads
        /// for ~10-15 s first): a short follow-up replay of the hit, from the propellant fire to the detonation.
        static void CookOffReplay(HitRecord rec)
        {
            if (rec == null || rec.Snapshot == null || rec.CookOffShown || !rec.Ready) return;
            if (!Settings.CookOffReplay.Value || !Settings.CinematicXray.Value) return;
            if (Settings.PlayerShotsOnly.Value && !rec.PlayerShot) return;
            if (!Settings.Enabled.Value && !AutoTest.Active) return;
            if (AfterAction.IsOpen) return;
            if (_queue.Contains(rec) || (_cur != null && !_cur.Done && _cur.Records.Contains(rec)) || (_build != null && (_buildRecs.Contains(rec) || _buildExtra.Contains(rec)))) return;   // its own hit cam shows it live
            // the hit had already put it out of the fight (destroyed, crew knocked out or bailed out): it blowing up
            // afterwards tells you nothing new
            float boomT = Time.time;
            foreach (var e in rec.Events) if (e.Kind == EvKind.Explosion) boomT = Mathf.Min(boomT, e.RealT);
            if (rec.OutAt >= 0f && rec.OutAt < boomT - 0.05f)
            {
                rec.CookOffShown = true;
                HitCamMod.Log.Msg($"Cook-off of {rec.TargetName} {Time.time - rec.FirstImpactReal:0.0} s after the hit: it was already out, no replay");
                return;
            }
            rec.CookOffShown = true;
            _cookOffs.Add(rec);
            HitCamMod.Log.Msg($"Cook-off replay: {rec.TargetName}, {Time.time - rec.FirstImpactReal:0.0} s after the hit");
            if (!Busy) { Begin(new List<HitRecord> { rec }); return; }
            if (!Settings.QueueHits.Value) { StopCurrent(); Begin(new List<HitRecord> { rec }); return; }
            _queue.Add(rec);
        }

        public static void ReplayLast()
        {
            if (_last == null) { HitCamMod.Log.Info("No hit recorded yet."); return; }
            StopCurrent();
            _queue.Clear();
            Begin(new List<HitRecord> { _last });
        }

        public static void StopCurrent()
        {
            _cur?.Dispose();
            _cur = null;
            if (_build != null)
            {
                _building?.Dispose();
                _build = null;
                _building = null;
                _buildExtra.Clear();
            }
            if (_cam != null) _cam.enabled = false;
        }

        public static void Reset()
        {
            StopCurrent();
            _queue.Clear();
            _cookOffs.Clear();
            _lateBooms.Clear();
            _last = null;
            GhostSnapshot.ClearCache();
            MeshCopy.ClearCache();
        }

        /// The After Action Report opened: it shows these hits itself, and only one x-ray copy may exist at a time.
        public static void Suspend()
        {
            StopCurrent();
            _queue.Clear();
        }

        /// The hit cam is built over a few frames (the copy, its physics, the round, the plates: each 5-35 ms, together
        /// they were one ~120 ms hitch the frame after the hit), and shown when it's complete.
        /// The first impact of a hit: its hit cam starts being built at once (the x-ray copy and its physics need only the
        /// snapshot) and plays as soon as the hit is recorded, so it opens with the hit rather than after it.
        public static void OnFirstImpact(HitRecord rec)
        {
            try
            {
                if (rec == null || rec.Snapshot == null || Busy || _queue.Count > 0 || AfterAction.IsOpen) return;
                if (Settings.PlayerShotsOnly.Value && !rec.PlayerShot) return;
                if (!Settings.Enabled.Value && !AutoTest.Active) return;
                if (!BigEnough(rec)) return;
                Begin(new List<HitRecord> { rec });
            }
            catch (Exception e) { HitCamMod.Log.Warning("Hit cam (early start): " + e.Message); }
        }

        static void Begin(List<HitRecord> recs)
        {
            if (AfterAction.IsOpen) return;
            _buildRecs = recs;
            _buildCook = _cookOffs.Remove(recs[0]);
            _buildExtra.Clear();
            _build = Playback.Staged(recs[0], _buildCook, pb => _building = pb);
        }

        static void BuildStep()
        {
            bool more;
            try { using (Perf.T("Begin/playback")) more = _build.MoveNext(); }
            catch (Exception e)
            {
                HitCamMod.Log.Error("Could not start hit cam: " + e);
                _building?.Dispose();
                _build = null; _building = null;
                return;
            }
            if (more) return;
            var recs = _buildRecs;
            _cur = _building;
            _build = null; _building = null;
            try
            {
                _cur.Live = true;
                using (Perf.T("Begin/camera")) EnsureCamera();
                if (!_cur.CookOffReplay) for (int i = 1; i < recs.Count; i++) _cur.TryMerge(recs[i]);
                foreach (var r in _buildExtra) if (!_cur.TryMerge(r)) { _queue.Add(r); _last = r; }
                _buildExtra.Clear();
                _cam.enabled = true;
                foreach (var c in Camera.allCameras)
                    if (c != _cam) c.cullingMask &= ~(1 << Vis.Layer);
                var rec = recs[0];
                HitCamMod.Log.Msg($"Hit cam{(_cur.CookOffReplay ? " (cook-off replay)" : "")}: {rec.Title} | {rec.ShooterName} -> {rec.TargetName} | {rec.Ammo?.Name} | {rec.Fragments.Count - 1} spall | {rec.Events.Count} events | interior {rec.InteriorEnd * 1000f:0.0} ms" + (recs.Count > 1 ? $" | +{recs.Count - 1} merged" : ""));
            }
            catch (Exception e)
            {
                HitCamMod.Log.Error("Could not start hit cam: " + e);
                StopCurrent();
            }
        }

        static void EnsureCamera()
        {
            Vis.Init();
            // from the screen height (an ultrawide would otherwise get a huge window): 16:9, like War Thunder's top-right hit camera
            int h = Mathf.Clamp(Mathf.RoundToInt(Screen.height * (ProvingGround.BigCam ? 0.45f : Settings.PipSize.Value)), 160, 1600);
            int w = Mathf.Min(Mathf.RoundToInt(h * 16f / 9f), Mathf.RoundToInt(Screen.width * 0.5f));
            h = Mathf.RoundToInt(w * 9f / 16f);
            if (ArmorSim.FullScreenHitCam) { w = Mathf.Clamp(Screen.width, 320, 3840); h = Mathf.Clamp(Screen.height, 180, 2160); }   // Armor Sim shot: full screen
            string aas = Settings.HitCamAntiAliasing.Value ?? "";
            int aa = aas.StartsWith("8") ? 8 : aas.StartsWith("4") ? 4 : aas.StartsWith("2") ? 2 : 1;
            if (ArmorSim.FullScreenHitCam) aa = Mathf.Min(aa, 4);
            if (_rt == null || _rt.width != w || _rt.height != h || _rt.antiAliasing != aa)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = aa, name = "HitCamRT" };
                _rt.Create();
            }
            if (_cam == null)
            {
                var go = new GameObject("HitCam Camera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _cam = go.AddComponent<Camera>();
                _cam.cullingMask = 1 << Vis.Layer;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.fieldOfView = 42f;
                _cam.nearClipPlane = 0.03f;
                _cam.farClipPlane = 200f;
                _cam.depth = -50;
                _cam.useOcclusionCulling = false;
                _cam.allowHDR = false;
                _cam.allowMSAA = true;
                _cam.renderingPath = RenderingPath.Forward;   // deferred would light the clay modules first and the black hull fill (forward-only) would then cover them
                _cam.enabled = false;
            }
            _cam.targetTexture = _rt;
            _cam.backgroundColor = Vis.Background;
        }

        public static void Update()
        {
            if (_lateBooms.Count > 0)
            {
                var late = _lateBooms.ToList();
                _lateBooms.Clear();
                foreach (var r in late)
                    try { CookOffReplay(r); } catch (Exception e) { HitCamMod.Log.Error("cook-off replay: " + e); }
            }
            if (_build != null) { BuildStep(); return; }
            if (_cur == null) return;
            if (_queue.Count > 0) _cur.StopWaiting();   // another hit is waiting: don't hold on for a cook-off
            _cur.Tick(AutoTest.FilmStep > 0f ? AutoTest.FilmStep : Time.unscaledDeltaTime);
            if (_cur.Done)
            {
                if (AutoTest.Active)
                {
                    var hs = _cur.AllHoles().ToList();
                    HitCamMod.Log.Msg($"Hit cam closed: {hs.Count} plates, {hs.Sum(h => h.VertexCount)} vertices, {hs.Count(h => h.Shaped)} settled, {_cur.T:0.0} s");
                }
                var recs = _cur.Records.ToList();
                bool cooked = _cur.CookOffReplay;
                StopCurrent();
                // a cook-off replay: the hit was saved before its rack went, save it again with the detonation
                if (Settings.SaveRecords.Value) foreach (var r in recs) { if (cooked) r.Saved = false; if (!r.Saved) Archive.Save(r); }
                if (_queue.Count > 0)
                {
                    // start with the oldest waiting hit and take every other waiting hit on the same target along
                    var first = _queue[0];
                    var group = _cookOffs.Contains(first) ? new List<HitRecord> { first } : _queue.Where(r => r.Target == first.Target && !_cookOffs.Contains(r)).ToList();
                    foreach (var r in group) _queue.Remove(r);
                    Begin(group);
                }
            }
        }

        public static void LateUpdate()
        {
            if (_cur == null || _cam == null) return;
            using (Perf.T("Playback/camera")) _cur.UpdateCamera(_cam, Time.unscaledDeltaTime);
            if (AutoTest.Active) using (Perf.T("AutoTest/frames")) AutoTest.OnPlaybackFrame(_cur);
        }
    }

    /// One hit inside a live session: its own slow-motion clock, shell, spall and holes.
    internal class Segment
    {
        public HitRecord Rec;
        public float Start;            // session time this hit's playback begins
        public float A, B, C;          // approach / interior / aftermath (seconds of playback)
        public bool Compact;           // short burst replay: lighter effects
        public float End => Start + A + B + C;
        public Vector3 Dir, FocusEnd;

        public GameObject Shell;
        public Renderer ShellGlow;
        public LineRenderer Trail, Jet;
        public float JetStartSim = float.MaxValue;
        public JetFx JetVis;
        public float JetEndWall = -1f;
        // a long-rod penetrator: it erodes from the nose through every plate, and breaks on a ricochet / shatter
        public bool Rod;
        public float RodLen0, RodD, BreakSim = float.MaxValue;
        public bool Shatter;
        public readonly List<(float sim, float dur, float loss)> Erosion = new List<(float, float, float)>();
        public Transform[] Pieces;
        public Vector3[] PieceAxis;
        public Renderer NoseGlow;
        public RodRig RodRig;          // the rod from its first contact on: eroding, bending, breaking (RodPhysics.cs)
        public float SimRate;          // sim seconds per session second right now (the slow motion)
        public readonly List<Playback.SpallVis> Spall = new List<Playback.SpallVis>();
        public readonly List<HoleFx> Holes = new List<HoleFx>();
        public readonly List<(GameObject go, float wall)> StaticHoles = new List<(GameObject, float)>();
        public readonly HashSet<HoleFx> HoleBurst = new HashSet<HoleFx>();
        public readonly List<(MeshFilter mf, Mesh cut, float wall)> Cuts = new List<(MeshFilter, Mesh, float)>();
        /// The armour hits that got a plate (built now, or waiting in LatePlates), and hull cuts still on their worker.
        public readonly List<ArmorHit> PlateHits = new List<ArmorHit>();
        public readonly List<(ArmorHit ah, float wall, int seed, bool compact)> LatePlates = new List<(ArmorHit, float, int, bool)>();
        public readonly List<(MeshFilter mf, MeshCutter.Job job, float wall)> CutJobs = new List<(MeshFilter, MeshCutter.Job, float)>();
        public readonly HashSet<HitEvent> Applied = new HashSet<HitEvent>();
        public float SimPrev = float.NaN;
        public bool WarheadBlastDone;
        public int LateExtensions;

        // two-speed slow motion: the first 70 % of the interior phase shows the round going through
        // and the first spall wave, the rest fast-forwards slower fragments and late damage
        const float CoreShare = 0.7f, Ease = 1.25f;

        // impact slow motion: the first plate's own process (penetration, petals, plug) gets extra seconds.
        // The base mapping's slowness (wall s per sim s) is raised to at least Dwell/simSpan over that span, easing
        // back out over 1.5 spans, so the round never stops or jerks. B includes the extra time.
        public float Dwell;
        public HoleFx MainPlate;
        public float ContactWall = -1f;   // session time the round first touches armour (a HEAT shell's body, before its jet)
        float _dwellSim, _dwellK, _dwellAt, _dwellFrom;
        readonly float[] _dwellTab = new float[65];
        float B0 => B - Dwell;
        public float B0Public => B0;

        /// Constant pace (War Thunder's): the round and the spall at one steady slow motion, no slow-down at the plate.
        public bool Steady;

        public void SetSteady(float slowMo)
        {
            Steady = true;
            A = Mathf.Clamp(-Rec.ApproachStart * slowMo, 0.25f, 4f);
            B = Mathf.Clamp(Rec.InteriorEnd * slowMo, 0.4f, 10f);
            Dwell = 0f;
            if (AutoTest.Active) HitCamMod.Log.Msg($"Hit cam pace constant x{slowMo:0}: approach {A:0.00} s ({-Rec.ApproachStart * 1000f:0.0} ms of game time), inside {B:0.00} s ({Rec.InteriorEnd * 1000f:0.0} ms)");
        }

        /// startSim = when the round reaches that plate (it can be a few cm past the first collider it touched).
        public void SetDwell(float seconds, float simSpan, float startSim)
        {
            Dwell = 0f;
            if (seconds <= 0.01f || simSpan <= 0f || Rec.CoreEnd <= 0f) return;
            _dwellSim = Mathf.Min(simSpan, Rec.CoreEnd * 0.45f);
            _dwellAt = Mathf.Clamp(startSim, 0f, Rec.CoreEnd * 0.5f);
            _dwellFrom = Mathf.Max(0f, _dwellAt - 0.5f * _dwellSim);
            _dwellK = seconds / _dwellSim;
            float end = _dwellAt + _dwellSim * 2.5f, h = (end - _dwellFrom) / 64f;
            float acc = 0f;
            for (int pass = 0; pass < 3; pass++)
            {
                // scale the slowness so the whole extra (flat part + ease-out) adds up to `seconds`
                if (pass > 0 && acc > 1e-4f) _dwellK *= seconds / acc;
                acc = 0f;
                float prev = ExtraSlowness(_dwellFrom);
                _dwellTab[0] = 0f;
                for (int i = 1; i <= 64; i++)
                {
                    float cur = ExtraSlowness(_dwellFrom + h * i);
                    acc += 0.5f * (prev + cur) * h;
                    prev = cur;
                    _dwellTab[i] = acc;
                }
            }
            Dwell = acc;
            B += Dwell;
        }

        float ExtraSlowness(float s)
        {
            // eases in over half a span before the plate, holds through it, eases out over 1.5 spans
            float a = Mathf.Clamp01((s - _dwellFrom) / Mathf.Max(_dwellAt - _dwellFrom, 1e-9f));
            float u = Mathf.Clamp01((s - _dwellAt - _dwellSim) / (1.5f * _dwellSim));
            float bump = a * a * (3f - 2f * a) * (1f - u * u * (3f - 2f * u));
            return Mathf.Max(0f, _dwellK * bump - BaseSlowness(s));
        }

        float BaseSlowness(float s)
        {
            float core = Rec.CoreEnd;
            if (s < core)
            {
                s = Mathf.Max(s, core * 1e-6f);
                return B0 * CoreShare / (Ease * core) * Mathf.Pow(s / core, 1f / Ease - 1f);
            }
            return B0 * (1f - CoreShare) / Mathf.Max(Rec.InteriorEnd - core, 1e-6f);
        }

        float ExtraAt(float s)
        {
            if (Dwell <= 0f || s <= _dwellFrom) return 0f;
            float f = (s - _dwellFrom) / (_dwellAt + _dwellSim * 2.5f - _dwellFrom) * 64f;
            if (f >= 64f) return Dwell;
            int i = (int)f;
            return Mathf.Lerp(_dwellTab[i], _dwellTab[i + 1], f - i);
        }

        public float SimAtLocal(float w)
        {
            if (w < A) return Mathf.Lerp(Rec.ApproachStart, 0f, Mathf.Max(w, 0f) / A);
            if (w >= A + B) return Rec.InteriorEnd;
            if (Steady) return Rec.InteriorEnd * (w - A) / B;
            if (Dwell > 0f)
            {
                float lo = 0f, hi = Rec.InteriorEnd;
                for (int i = 0; i < 34; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (LocalAtSim(mid) < w) lo = mid; else hi = mid;
                }
                return 0.5f * (lo + hi);
            }
            float x = (w - A) / B;
            if (x < CoreShare) return Rec.CoreEnd * Mathf.Pow(x / CoreShare, Ease);
            if (x < 1f) return Mathf.Lerp(Rec.CoreEnd, Rec.InteriorEnd, (x - CoreShare) / (1f - CoreShare));
            return Rec.InteriorEnd;
        }

        public float LocalAtSim(float s)
        {
            if (s <= 0f) return A * Mathf.InverseLerp(Rec.ApproachStart, 0f, s);
            if (Steady) return A + B * Mathf.Clamp01(s / Mathf.Max(Rec.InteriorEnd, 1e-6f));
            float x;
            if (s <= Rec.CoreEnd) x = CoreShare * Mathf.Pow(s / Rec.CoreEnd, 1f / Ease);
            else x = CoreShare + (1f - CoreShare) * Mathf.Clamp01((s - Rec.CoreEnd) / Mathf.Max(Rec.InteriorEnd - Rec.CoreEnd, 1e-6f));
            return A + B0 * x + ExtraAt(s);
        }

        // a cook-off replay: the late events re-timed so the propellant roars ~2 s before the rack goes
        public const float CookF = 0.9f, CookJ = 2.2f;
        public float CookFire = -1f, CookBoom = -1f, CookStart;

        /// Times the follow-up replay of a rack that cooked off long after the hit; false if there is no detonation.
        public bool SetCookOff()
        {
            float f = float.MaxValue, b = float.MaxValue;
            foreach (var e in Rec.Events)
            {
                float d = e.RealT - Rec.FirstImpactReal;
                if (Timeline.Propellant(e)) f = Mathf.Min(f, d);
                if (e.Kind == EvKind.Explosion && !Timeline.OwnWarhead(Rec, e)) b = Mathf.Min(b, d);
            }
            if (b == float.MaxValue) return false;
            CookBoom = b;
            CookFire = f < b ? f : -1f;
            CookStart = CookFire >= 0f ? 0.2f : CookF + CookJ - 1.2f;
            return true;
        }

        float CookMap(float d)
        {
            float fire = CookFire >= 0f ? CookFire : CookBoom;
            if (d < fire) return Mathf.Min(0.1f + 0.05f * d, CookF - 0.1f);         // before the jets: all at the start
            if (d <= CookBoom && CookBoom - fire > 1e-3f) return CookF + CookJ * (d - fire) / (CookBoom - fire);
            if (d <= CookBoom) return CookF + CookJ;
            return CookF + CookJ + Mathf.Min(0.3f * (d - CookBoom), 3f);
        }

        /// Session time at which an event shows up.
        public float WallOf(HitEvent e)
        {
            Timeline.EnsureSim(Rec, e);
            if (float.IsPositiveInfinity(e.Sim) && CookBoom >= 0f) return Start + A + B + CookMap(Mathf.Max(0f, e.RealT - Rec.FirstImpactReal));
            if (float.IsPositiveInfinity(e.Sim))
            {
                float d = Mathf.Max(0f, e.RealT - Rec.FirstImpactReal);
                if (!Settings.CinematicXray.Value) return Start + A + B + Mathf.Clamp(0.2f + d * 0.35f, 0.2f, Mathf.Max(0.3f, C - 0.8f));
                // the first seconds at a third of real time, later ones on a log scale (a cook-off 10 s after the hit
                // shows ~2 s after the spall, with the propellant roaring in between)
                float l = 0.2f + 0.35f * Mathf.Min(d, 2f) + 0.55f * Mathf.Log(1f + Mathf.Max(d - 2f, 0f) / 1.5f);
                return Start + A + B + Mathf.Clamp(l, 0.2f, Mathf.Max(0.3f, C + 2.5f));
            }
            return Start + LocalAtSim(e.Sim);
        }
    }

    internal class Playback
    {
        readonly List<Segment> _segs = new List<Segment>();
        public IEnumerable<HitRecord> Records => _segs.Select(s => s.Rec);
        public HitRecord Rec => _segs[0].Rec;                 // the hit that opened the session
        public HitRecord Latest => _segs[_segs.Count - 1].Rec;
        public int Hits => _segs.Count;
        public float A => _segs[0].A;
        public float B => _segs[0].B;
        public float C => _segs[0].C;
        public float Dwell => _segs[0].Dwell;
        public float RedFlash;          // WT's red flash at the moment the x-ray takes over

        public float T;
        public float Duration;
        public bool Done => T >= Duration;
        public float Alpha => Mathf.Clamp01((T - _shownFrom) / 0.25f) * Mathf.Clamp01((Duration - T) / 0.5f);
        public bool CookOffReplay { get; private set; }     // a follow-up replay of a hit whose rack cooked off later
        float _ffTo = -1f, _shownFrom;          // the cook-off replay runs the hit itself unseen up to _ffTo
        public float ShownFrom => _shownFrom;

        Ghost _ghost;
        GhostPhysics _phys;
        Transform _fx;
        Bounds _bounds;

        public class SpallVis { public Fragment F; public LineRenderer L; public Renderer Head, Shard; public bool Strong; public Vector3 Axis; public float Spin; }

        readonly List<XrayFire> _fires = new List<XrayFire>();
        readonly List<XrayExplosion> _blasts = new List<XrayExplosion>();
        public float BlastFlash;        // orange full-frame flash of an explosion inside (UI)
        readonly List<PropellantJet> _jets = new List<PropellantJet>();
        readonly HashSet<PropellantJet> _bursts = new HashSet<PropellantJet>();   // the game's flame bursts out of the exits: a detonation doesn't snuff them
        readonly List<Vector3> _detPts = new List<Vector3>();     // where the rack's rounds detonated: the blast's real centre
        readonly List<(HitEvent e, float at)> _booms = new List<(HitEvent, float)>();   // a detonation held back while the jets flare
        bool _boomed, _noWait;
        public bool Live;               // the live hit cam (not the AAR): it keeps watching a propellant fire for its cook-off
        public void StopWaiting() => _noWait = true;

        // live state of the ghost, shared by every hit of the session
        readonly Dictionary<ModulePart, float> _hp = new Dictionary<ModulePart, float>();
        readonly Dictionary<CrewInfo, HealthStatus> _crew = new Dictionary<CrewInfo, HealthStatus>();
        readonly HashSet<string> _down = new HashSet<string>();
        readonly Dictionary<AmmoPart, int> _ammo = new Dictionary<AmmoPart, int>();   // 1 hit, 2 burning/exploded
        readonly Dictionary<Renderer, float> _flash = new Dictionary<Renderer, float>();
        readonly List<(Vector3 p, float r)> _allCuts = new List<(Vector3, float)>();
        readonly HashSet<ModulePart> _seenBefore = new HashSet<ModulePart>();
        float _killWall = -1f;
        readonly List<Transform> _billboards = new List<Transform>();

        public readonly List<(HitEvent e, float wall)> Shown = new List<(HitEvent, float)>();
        public string TitleShown;
        public Color TitleColor = Color.white;
        public float TitleWall = -1f;

        public Playback(HitRecord rec, bool cookOff = false) { var it = Steps(rec, cookOff); while (it.MoveNext()) { } }

        Playback() { }

        /// Built a piece per frame (the live hit cam): `started` gets the playback at once, so a cancelled build can be
        /// disposed of half-way.
        public static System.Collections.IEnumerator Staged(HitRecord rec, bool cookOff, Action<Playback> started)
        {
            var pb = new Playback();
            started(pb);
            var it = pb.Steps(rec, cookOff);
            while (it.MoveNext()) yield return null;
        }

        System.Collections.IEnumerator Steps(HitRecord rec, bool cookOff)
        {
            // started at the impact: the world damage of the hit goes first (its holes are cut over the next frames)
            int f0 = Time.frameCount;
            while (!rec.Ready && Time.frameCount < f0 + 3) yield return null;
            // a replay of an older hit: its crew skins are worked out again on workers first
            float w1 = Time.realtimeSinceStartup;
            while (!CrewBuilder.EnsurePrepared(rec.Snapshot) && Time.realtimeSinceStartup - w1 < 3f) yield return null;
            _bounds = rec.Snapshot.LocalBounds;
            // above the hit, but inside the game's physics broadphase (about 5 km round the map's origin): past it nothing
            // collides (the ragdolls and fragments fell through the hull). Wrapped, so nearby targets keep apart.
            Vector3 at = (Vector3)rec.L2WAtImpact.GetColumn(3);
            at.x = Mathf.Repeat(at.x + 4000f, 8000f) - 4000f;
            at.z = Mathf.Repeat(at.z + 4000f, 8000f) - 4000f;
            using (Perf.T("Playback/ghost")) _ghost = Ghost.Build(rec.Snapshot, at + Vector3.up * 350f);
            yield return null;
            if (Settings.XrayPhysics.Value && _ghost.Root.scene.name == "HitCamPhysics")
            {
                try { using (Perf.T("Playback/physics")) _phys = new GhostPhysics(_ghost, rec.Snapshot); }
                catch (Exception e) { HitCamMod.Log.Warning("hit-cam physics off: " + e.Message); _phys = null; }
                yield return null;
            }
            if (_phys != null) _phys.MayGoLimp = ci => _down.Contains(ci.Label);
            // the rest needs the whole hit (the spall, the events): recorded by now, or a moment from now
            float w0 = Time.realtimeSinceStartup;
            while (!rec.Ready)
            {
                if (Time.realtimeSinceStartup - w0 > 5f) throw new Exception("the hit on " + rec.TargetName + " never finished recording");
                yield return null;
            }
            foreach (var mp in rec.Snapshot.Modules) _hp[mp] = mp.InitialHealth;
            foreach (var ci in rec.Snapshot.Crew) _crew[ci] = ci.InitialStatus;
            // module colours as they were BEFORE this shot (the snapshot is taken at impact; use the first event's "before")
            foreach (var e in rec.Events)
                if (e.Kind == EvKind.Module && e.Ref is DC dc && rec.Snapshot.ModuleOf.TryGetValue(dc, out var mp) && e.Before >= 0 && _seenBefore.Add(mp))
                    _hp[mp] = e.Before;
            _fx = new GameObject("fx").transform;
            _fx.SetParent(_ghost.Root.transform, false);

            bool big = Caliber(rec) >= Settings.BurstCaliberMm.Value;
            var seg = AddSegmentSteps(rec, 0f, big || cookOff);
            while (seg.MoveNext()) yield return null;
            if (cookOff && _segs[0].SetCookOff())
            {
                CookOffReplay = true;
                var s0 = _segs[0];
                _ffTo = s0.Start + s0.A + s0.B + s0.CookStart;
                _shownFrom = _ffTo;
                Duration = Mathf.Max(Duration, s0.Start + s0.A + s0.B + Segment.CookF + Segment.CookJ + 3f);
            }
            RefreshColors();
        }

        static float Caliber(HitRecord r) => r.Ammo != null ? Mathf.Max(r.Ammo.Caliber, r.Ammo.Radius * 2000f) : 100f;

        /// Join a hit on the same vehicle into this session, at its real-time distance from the first hit.
        public bool TryMerge(HitRecord rec)
        {
            if (rec == null || rec.Snapshot == null || rec.Target == null || rec.Target != Rec.Target || CookOffReplay) return false;
            if (_segs.Count >= 60 || T > Duration - 0.05f) return false;
            var s0 = _segs[0];
            float realOffset = rec.FirstImpactReal - s0.Rec.FirstImpactReal;
            const float a = 0.22f;
            float start = Mathf.Max(T, s0.Start + s0.A + realOffset - a);
            AddSegment(rec, start, false);
            // more than one hit: switch to a continuous orbit around the target (an autocannon burst in the light hit cam
            // keeps its one view instead, the War Thunder way: the rounds keep arriving into it)
            if (_segs.Count == 2 && !LightRound(rec)) _orbit = true;
            return true;
        }

        void AddSegment(HitRecord rec, float start, bool full) { var it = AddSegmentSteps(rec, start, full); while (it.MoveNext()) { } }

        System.Collections.IEnumerator AddSegmentSteps(HitRecord rec, float start, bool full)
        {
            // crew the game took out in this hit (critical or killed): only they ragdoll
            foreach (var e in rec.Events)
                if (e.Kind == EvKind.Crew && e.After >= (float)HealthStatus.Critical && !string.IsNullOrEmpty(e.Subject)) _down.Add(e.Subject);
            var s = new Segment
            {
                Rec = rec,
                Start = start,
                A = full ? Settings.ApproachSeconds.Value : 0.22f,
                B = full ? Settings.InteriorSeconds.Value : 0.85f,
                C = full ? Settings.AftermathSeconds.Value : 0.35f,
                Dir = rec.EntryDirL.sqrMagnitude > 0.01f ? rec.EntryDirL.normalized : Vector3.forward,
            };
            _segs.Add(s);
            s.Compact = !full;
            // where the round really met the drawn armour (angle, normal): decides the plate's failure shape and timing
            HoleFx.CentreL = _bounds.center;
            foreach (var ah in rec.ArmorHits) HoleFx.Prepare(ah, _phys);
            yield return null;
            var first = FirstPlate(rec);
            if (first != null)
            {
                float cal = Caliber(rec);
                float secs = !Settings.CinematicXray.Value || LightRound(rec) ? 0f : full ? Settings.ImpactSlowMo.Value : cal >= 40f ? 0.35f : cal >= 20f ? 0.15f : 0f;
                float span = HoleFx.DwellSpan(first);
                if (ProvingGround.RodCam && full)
                {
                    // the rod cam: slow through every plate the round meets, not only the first
                    secs = Mathf.Max(secs, 4f);
                    float last = first.Sim;
                    foreach (var ah in rec.ArmorHits)
                        if (ah.Frag == rec.Main && !ah.IsSpall) last = Mathf.Max(last, ah.Sim + Mathf.Max(ah.Los, ah.Nominal) * 0.001f / 500f);
                    span = Mathf.Max(span, last - first.Sim + 1.5e-4f);
                }
                if (full && !ProvingGround.RodCam && Settings.HitCamPace.Value == "Constant") s.SetSteady(Mathf.Clamp(Settings.ConstantSlowMo.Value, 5f, 2000f));
                else s.SetDwell(secs, span, first.Sim);
            }
            else if (full && !ProvingGround.RodCam && Settings.HitCamPace.Value == "Constant") s.SetSteady(Mathf.Clamp(Settings.ConstantSlowMo.Value, 5f, 2000f));
            using (Perf.T("Segment/projectile")) BuildProjectile(s);
            using (Perf.T("Segment/spall")) BuildSpall(s);
            yield return null;
            using (Perf.T("Segment/holes")) BuildHoles(s);
            yield return null;
            using (Perf.T("Segment/rod")) BuildRodRig(s);
            foreach (var ah in rec.ArmorHits)
                if (ah.Frag == rec.Main && !ah.IsSpall)
                {
                    float w = s.Start + s.LocalAtSim(ah.Sim);
                    if (s.ContactWall < 0f || w < s.ContactWall) s.ContactWall = w;
                }
            // the round's first contact can be a part, not a plate (a gun barrel, a mantlet module): the x-ray opens there
            foreach (var e in rec.Events)
            {
                if (e.Frag != rec.Main || e.Kind != EvKind.Module) continue;
                Timeline.EnsureSim(rec, e);
                if (float.IsInfinity(e.Sim) || float.IsNaN(e.Sim)) continue;
                float w = s.Start + s.LocalAtSim(e.Sim);
                if (s.ContactWall < 0f || w < s.ContactWall) s.ContactWall = w;
            }
            if (AutoTest.Active)
                foreach (var h in s.Holes)
                    HitCamMod.Log.Msg($"plate {h.Hit.Name}: {h.Mode} pen={h.Hit.Penetrated} v={h.Hit.Speed:0} los={h.Hit.Los:0}mm nom={h.Hit.Nominal:0}mm ang={h.Hit.Angle:0.#}->{HoleFx.ImpactAngle(h.Hit):0.#}{(h.Hit.PlateTris != null ? " plate" : "")} E={h.Hit.Energy / 1000f:0}kJ tPen={h.TPen * 1e6f:0}us tResp={h.TResp * 1e6f:0}us size={h.Size:0.00}m" + (h == s.MainPlate ? $" MAIN dwell={s.Dwell:0.00}s" : ""));
            ComputeFocus(s);
            if (_segs.Count == 1) PickCameraSide(s);
            Duration = Mathf.Max(Duration, s.End + (_segs.Count > 1 ? 0.8f : 0f));
        }

        // ------------------------------------------------------------------ build

        /// The first plate the round itself struck (not spall, not the HEAT shell body before its jet).
        static ArmorHit FirstPlate(HitRecord rec)
        {
            ArmorHit best = null;
            foreach (var ah in rec.ArmorHits)
                if (ah.Frag == rec.Main && !ah.IsSpall && !ah.PreJet && !ah.Era && (best == null || ah.Sim < best.Sim)) best = ah;
            return best;
        }

        void BuildProjectile(Segment s)
        {
            var ammo = s.Rec.Ammo;
            float cal = ammo != null ? Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) * 0.001f : 0.1f;
            bool rod = ammo != null && ammo.Category == AmmoType.AmmoCategory.Penetrator && ammo.Radius * 2f < cal * 0.6f;
            s.Shell = new GameObject("shell");
            s.Shell.layer = Vis.Layer;
            s.Shell.transform.SetParent(_fx, false);
            // the real round (rod with fins, HEAT with its probe, ogive HE, capped AP...), nose at the round's position
            float tail = 0f;
            Mesh model = null;
            float len = 0f;
            if (ShellModels.GameModel(ammo, s.Shell.transform, out float glen)) tail = -glen;     // the game's own model of the round
            else model = ShellModels.Get(ammo, out len);
            if (tail < 0f) { }
            else if (model != null)
            {
                var r = Vis.Child(s.Shell.transform, "body", model, Matrix4x4.identity, Vis.Unlit).GetComponent<Renderer>();
                Vis.SetColor(r, Color.white);
                tail = -len;
            }
            else
            {
                Matrix4x4 body = rod
                    ? Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(90, 0, 0), new Vector3(Mathf.Max(ammo.Radius * 2f * 1.6f, 0.028f), 0.32f, Mathf.Max(ammo.Radius * 2f * 1.6f, 0.028f)))
                    : Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(90, 0, 0), new Vector3(cal * 1.3f, cal * 2.0f, cal * 1.3f));
                var r = Vis.Child(s.Shell.transform, "body", rod ? Vis.WCylinder : Vis.WCapsule, body, Vis.Unlit).GetComponent<Renderer>();
                Vis.SetColor(r, new Color(1f, 0.93f, 0.7f));
            }
            // the tracer burns at the tail
            s.ShellGlow = Vis.Child(s.Shell.transform, "glow", Vis.SoftDisc, Matrix4x4.TRS(Vector3.forward * tail, Quaternion.identity, Vector3.one * Mathf.Clamp(cal * (rod ? 1.2f : 1.6f), 0.05f, 0.22f)), Vis.Additive).GetComponent<Renderer>();
            Vis.SetColor(s.ShellGlow, new Color(1f, 0.8f, 0.45f));
            _billboards.Add(s.ShellGlow.transform);
            s.Shell.SetActive(false);

            if (ammo != null && ammo.Category == AmmoType.AmmoCategory.ShapedCharge) s.JetVis = new JetFx(_fx, cal);
            if (rod)
            {
                s.Rod = true;
                s.RodLen0 = Mathf.Max(-tail, 0.1f);
                s.RodD = Mathf.Max(ammo.Radius * 2f, 0.01f);
            }
            s.Trail = NewLine("trail", Mathf.Max(cal * 0.45f, 0.012f), new Color(1f, 0.96f, 0.85f, 0.95f), new Color(1f, 0.96f, 0.85f, 0f));
            s.Jet = NewLine("jet", 0.035f, new Color(1f, 0.25f, 0.95f, 1f), new Color(1f, 0.55f, 1f, 1f));
            foreach (var p in s.Rec.Main.Points) if (p.Jet) { s.JetStartSim = p.Sim; break; }
        }

        /// The rod from its first contact on: erosion (Tate), bending in its channel, breaking (after the plates, so a
        /// ricochet slides exactly the groove its plate draws).
        void BuildRodRig(Segment s)
        {
            if (!s.Rod) return;
            var ammo = s.Rec.Ammo;
            float v = s.Rec.Main.StartSpeed > 50f ? s.Rec.Main.StartSpeed : ammo.MuzzleVelocity;
            // deforming metal: full detail on the big screens (Armor Sim, proving ground, the AAR), lighter live
            int soft = !Settings.RodSoftBody.Value ? 0 : (ArmorSim.FullScreenHitCam || ProvingGround.Active || AfterAction.IsOpen) ? 2 : 1;
            float groove = -1f;
            foreach (var h in s.Holes) if (h.Hit.Ricochet && h.Hit.Frag == s.Rec.Main && h.TrenchLen > 0f) { groove = h.TrenchLen; break; }
            // a round the game crushed on the first surface can leave a path of a single point: the rig would sit at the
            // vehicle's origin (and the rod cam with it), so the plain round model shows that hit instead
            if (!RodRig.HasPath(s.Rec)) { if (AutoTest.Active) HitCamMod.Log.Msg("x-ray rod: no path to follow (one point), plain round model"); return; }
            s.RodRig = new RodRig(_fx, s.Rec, ammo, s.RodLen0, v, soft, groove);
            s.BreakSim = s.RodRig.BreakSim;
            if (AutoTest.Active) HitCamMod.Log.Msg("x-ray rod: " + s.RodRig.Report);
        }

        LineRenderer NewLine(string name, float width, Color a, Color b)
        {
            var go = new GameObject(name);
            go.layer = Vis.Layer;
            go.transform.SetParent(_fx, false);
            var lr = go.AddComponent<LineRenderer>();
            Vis.Quiet(lr);
            lr.useWorldSpace = false;
            lr.sharedMaterial = Vis.Line;
            lr.widthMultiplier = width;
            lr.startColor = a;
            lr.endColor = b;
            lr.numCapVertices = 2;
            lr.positionCount = 0;
            return lr;
        }

        void BuildSpall(Segment s)
        {
            bool light = LightRound(s.Rec);
            int lightCount = 0;
            var b = _bounds;
            b.Expand(0.8f);
            foreach (var f in s.Rec.Fragments)
            {
                if (f.Kind != FragKind.Spall || !f.HasStart) continue;
                if (f.StartSim > s.Rec.InteriorEnd * 1.02f) continue;
                if (!b.Contains(f.StartL)) continue;
                bool strong = f.HitSomething || f.Damaging;
                if (!strong && !Settings.ShowAllSpall.Value) continue;
                // the light hit cam: a few fragments a round (the ones that hit something), however many the game made
                if (light && (!strong || ++lightCount > 6)) continue;
                Color c = Vis.SpallColor(f.Power);
                float w = 0.0025f + 0.005f * Mathf.Clamp01(f.Power / 40f);   // WT: thin lines
                c.a = strong ? 1f : 0.35f;
                var sv = new SpallVis { F = f, Strong = strong, L = NewLine("spall", w, c, c) };
                sv.Head = Vis.Child(_fx, "spall head", Vis.SoftDisc, Matrix4x4.Scale(Vector3.one * (w * 3f + 0.012f)), Vis.Additive).GetComponent<Renderer>();
                Vis.SetColor(sv.Head, c * (strong ? 0.7f : 0.3f));
                sv.Head.enabled = false;
                _billboards.Add(sv.Head.transform);
                // the fragment itself: a hot steel splinter, its size from the mass its remaining penetration implies
                // (10 mm RHA ~ 2 g, 100 mm ~ 60 g), tumbling at a few thousand rad/s like real spall
                float mass = Mathf.Clamp(0.0019f * Mathf.Pow(Mathf.Max(f.Power, 1f) / 10f, 1.5f), 0.0005f, 0.25f);
                float size = Mathf.Clamp(Mathf.Pow(mass / 7850f, 1f / 3f) * 2.2f, 0.008f, 0.06f);
                int id = f.Id * 7 + s.Spall.Count;
                sv.Shard = Vis.Child(_fx, "spall shard", Shards.Get(id), Matrix4x4.Scale(new Vector3(size, size * 0.6f, size)), Vis.Unlit).GetComponent<Renderer>();
                sv.Shard.enabled = false;
                var rr = new System.Random(id);
                sv.Axis = new Vector3((float)rr.NextDouble() - 0.5f, (float)rr.NextDouble() - 0.5f, (float)rr.NextDouble() - 0.5f).normalized;
                sv.Spin = (3000f + 12000f * (float)rr.NextDouble()) * (rr.NextDouble() < 0.5 ? -1f : 1f);
                s.Spall.Add(sv);
            }
        }

        void BuildHoles(Segment s)
        {
            if (!Settings.ArmorHoles.Value) return;
            // an autocannon round in the light hit cam: its holes as they are, no deforming plate, no cut model
            if (LightRound(s.Rec)) { BuildStaticHoles(s); return; }
            long lap = Perf.Now;
            int seed = s.Rec.Id * 31;
            float firstPenWall = float.MaxValue;
            bool newCut = false;
            var first = FirstPlate(s.Rec);
            int detailed = 0;
            foreach (var ah in s.Rec.ArmorHits)
            {
                if (ah.PreJet) continue;   // the jet's own hit at the same spot shows the plate
                if (s.PlateHits.Count >= MaxPlatesPerHit || _plates >= MaxPlatesPerSession) break;
                // an autocannon burst (MinCaliberMm set low): a few plates, not one more for every round of it (each one
                // deforms and is painted every frame: 20 of them took 30 ms a frame)
                if (HitCamPlayer.Cal(s.Rec) < BurstPlatesBelowMm && _plates >= MaxBurstPlates) break;
                // layers of one armor package (face, special array, backing plate) share the outer face's panel
                bool layer = false;
                foreach (var o in s.PlateHits)
                    if ((o.L - ah.L).sqrMagnitude < 0.35f * 0.35f && Vector3.Dot(o.NormalL.normalized, ah.NormalL.normalized) > 0.8f) { layer = true; break; }
                if (layer) continue;
                float wall = s.Start + s.LocalAtSim(ah.Sim);
                // full detail for the first plate and more of the round's own (one in the small window, three where the
                // hit cam is big: the Armor Sim, the proving ground, the AAR); spall / later plates get the light mesh
                bool full = ah == first || (!s.Compact && !ah.IsSpall && ah.Frag == s.Rec.Main && detailed < (BigView ? 3 : 1));
                if (full && ah != first) detailed++;
                _plates++;
                s.PlateHits.Add(ah);
                // the first plate now; the rest (10-30 ms of mesh work each) in the frames after the hit cam opens, or as the
                // replay gets to them (BuildLatePlates). A ricochet's groove is needed now (the rod rig slides along it).
                if (ah == first || (ah.Ricochet && ah.Frag == s.Rec.Main))
                {
                    var fx = HoleFx.Create(_fx, ah, wall, seed++, !full, _phys);
                    s.Holes.Add(fx);
                    if (ah == first) s.MainPlate = fx;
                }
                else s.LatePlates.Add((ah, wall, seed++, !full));
                if (ah.Penetrated && !ah.Era)
                {
                    _allCuts.Add((ah.L, Mathf.Max(ah.HoleRadius * 1.6f, 0.022f)));
                    firstPenWall = Mathf.Min(firstPenWall, wall);
                    newCut = true;
                }
            }
            Perf.Lap("Holes/plates", ref lap);
            if (!newCut) return;
            // every cut includes all holes of the session so far, cut from the untouched mesh, on a worker thread: the
            // holed mesh is swapped in when the round gets there (PollCuts)
            var holes = new List<(Vector3, float)>(_allCuts);
            for (int i = 0; i < _ghost.HullParts.Count; i++)
            {
                var part = _ghost.HullParts[i];
                if (part.Mesh == null || !part.Mesh.isReadable) continue;
                var job = MeshCutter.Begin(part.Mesh, part.M, holes, s.Rec.Id * 13 + i, true);
                if (job != null) s.CutJobs.Add((_ghost.Hull[i].GetComponent<MeshFilter>(), job, firstPenWall));
            }
            Perf.Lap("Holes/cut", ref lap);
        }

        int _latePlateFrame = -1;

        static bool BigView => ArmorSim.FullScreenHitCam || ProvingGround.Active || AfterAction.IsOpen;

        /// The plates after the first: one a frame after the hit cam opens, or straight away when the replay is about to
        /// reach one.
        void BuildLatePlates(Segment s)
        {
            for (int i = 0; i < s.LatePlates.Count; i++)
            {
                var p = s.LatePlates[i];
                if (T < p.wall - 0.4f && _latePlateFrame == Time.frameCount) continue;
                s.LatePlates.RemoveAt(i--);
                _latePlateFrame = Time.frameCount;
                using (Perf.T("Segment/late plate")) s.Holes.Add(HoleFx.Create(_fx, p.ah, p.wall, p.seed, p.compact, _phys));
            }
        }

        /// Hull cuts finished on their worker thread: their meshes made, ready to swap in.
        void PollCuts(Segment s)
        {
            for (int i = 0; i < s.CutJobs.Count; i++)
            {
                var c = s.CutJobs[i];
                if (!c.job.Done) continue;
                s.CutJobs.RemoveAt(i--);
                if (c.job.Error != null) { HitCamMod.Log.Warning("x-ray hull cut: " + c.job.Error); continue; }
                Mesh m;
                using (Perf.T("Segment/cut mesh")) m = MeshCutter.Finish(c.job);
                if (m == null || c.mf == null) continue;
                _ghost.OwnedMeshes.Add(m);
                s.Cuts.Add((c.mf, m, c.wall));
            }
        }

        void ComputeFocus(Segment s)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            var b = _bounds;
            b.Expand(0.3f);
            foreach (var e in s.Rec.Events)
            {
                Timeline.EnsureSim(s.Rec, e);
                if (!e.HasPos || !b.Contains(e.L)) continue;
                if (e.Kind == EvKind.Module || e.Kind == EvKind.Crew || e.Kind == EvKind.AmmoHit || e.Kind == EvKind.Fire) { sum += e.L; n++; }
            }
            s.FocusEnd = n > 0 ? Vector3.Lerp(s.Rec.EntryL, sum / n, 0.65f) : s.Rec.EntryL + s.Dir * Mathf.Min(1.2f, _bounds.size.magnitude * 0.15f);
        }

        float _yawSign = 1f, _pitchLift;

        /// Orbit on the side (and height) with the fewest modules between the camera and the action.
        void PickCameraSide(Segment s)
        {
            var boxes = new List<(Bounds b, float w)>();
            foreach (var mp in s.Rec.Snapshot.Modules)
                foreach (var sh in mp.Shapes)
                {
                    var lb = sh.LocalAabb();
                    if (lb.Contains(s.FocusEnd) || lb.Contains(s.Rec.EntryL)) continue;
                    boxes.Add((lb, mp.Cat == ModCat.Tracks ? 0.2f : mp.Cat == ModCat.Gun ? 2f : 1f));
                }
            float best = float.MaxValue;
            foreach (float sign in new[] { 1f, -1f })
                foreach (float lift in new[] { 0f, 20f })
                {
                    float score = lift * 0.02f;   // slight preference for the lower, more dramatic view
                    foreach (float yaw in new[] { 30f, 55f, 80f, 100f })
                        foreach (var f in new[] { s.Rec.EntryL, s.FocusEnd })
                        {
                            Vector3 cam = CameraLocal(s.Dir, f, yaw * sign, 28f + lift, 4.8f * SizeScale);
                            Vector3 d = f - cam;
                            var ray = new Ray(cam, d.normalized);
                            float len = d.magnitude - 0.35f;
                            foreach (var (bx, w) in boxes)
                                if (bx.IntersectRay(ray, out float hit) && hit < len) score += w;
                        }
                    if (score < best) { best = score; _yawSign = sign; _pitchLift = lift; }
                }
        }

        float SizeScale => Mathf.Clamp(_bounds.size.magnitude / 8.5f, 0.6f, 1.6f);

        static Vector3 CameraLocal(Vector3 dir, Vector3 focus, float yaw, float pitch, float dist)
        {
            Vector3 fwd = new Vector3(dir.x, 0f, dir.z);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 view = Quaternion.AngleAxis(yaw, Vector3.up) * (Quaternion.AngleAxis(pitch, right) * fwd);
            return focus - view * dist;
        }

        // ------------------------------------------------------------------ per frame

        public void Tick(float dt)
        {
            if (T < _ffTo)
            {
                // a cook-off replay begins long after the hit: run the hit itself unseen first, a few steps per frame
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (T < _ffTo && sw.ElapsedMilliseconds < 6) TickCore(Mathf.Min(0.1f, _ffTo - T + 1e-4f));
                return;
            }
            TickCore(dt);
        }

        void TickCore(float dt)
        {
            T += dt;
            float physDt = 0f;
            bool anyInside = false;
            long lap = Perf.Now;
            foreach (var s in _segs)
            {
                float local = T - s.Start;
                if (local < 0f) continue;
                float sim = s.SimAtLocal(local);
                UpdateProjectile(s, sim, local);
                UpdateSpall(s, sim);
                Perf.Lap("Tick/round+spall", ref lap);
                bool inside = local <= s.A + s.B;
                BuildLatePlates(s);
                for (int i = 0; i < s.StaticHoles.Count; i++)
                {
                    var (go, wall) = s.StaticHoles[i];
                    bool on = T >= wall;
                    if (go != null && go.activeSelf != on) go.SetActive(on);
                }
                foreach (var h in s.Holes)
                {
                    h.Step(T, sim, inside, dt);
                    if (_phys != null && h.Broke && s.HoleBurst.Add(h)) BurstFromHole(s, h);
                }
                Perf.Lap("Tick/plates", ref lap);
                ImpactKicks(s);
                PollCuts(s);
                foreach (var c in s.Cuts) if (T >= c.wall && c.mf != null && c.mf.sharedMesh != c.cut) c.mf.sharedMesh = c.cut;
                // physics follows the round's own clock while a round is inside the vehicle
                if (local <= s.A + s.B)
                {
                    anyInside = true;
                    physDt = Mathf.Max(physDt, Mathf.Max(0f, sim - (float.IsNaN(s.SimPrev) ? sim : s.SimPrev)));
                }
                s.SimRate = dt > 0f && !float.IsNaN(s.SimPrev) ? Mathf.Max(0f, (sim - s.SimPrev) / dt) : 0f;
                s.SimPrev = sim;
                ApplyEvents(s);
            }
            for (int i = _booms.Count - 1; i >= 0; i--)
                if (T >= _booms[i].at) { var be = _booms[i].e; _booms.RemoveAt(i); Boom(be); }
            LaterDetonations();
            UpdatePressure();
            WaitForCookOff();
            UpdatePanic();
            // between and after the rounds: a visible slow motion
            if (!anyInside) physDt = dt * (_phys != null && _phys.AnyPanic ? 1f : Settings.XrayPhysicsSpeed.Value);   // a crewman thrashing in a fire: real time
            Perf.Lap("Tick/events+blasts", ref lap);
            _phys?.Step(physDt);
            Perf.Lap("Tick/physics", ref lap);
            UpdateReveal();
            UpdateFires();
            Perf.Lap("Tick/fires", ref lap);
            RefreshColors();
            UpdateTitle();
            Perf.Lap("Tick/colours", ref lap);
        }

        Renderer _revealBall;

        /// War Thunder's reveal: the real, textured vehicle on the way in; at impact a red flash and a red ball,
        /// then the hull goes dark and see-through with a faint skin, the internals on top.
        void UpdateReveal()
        {
            var s0 = _segs[0];
            float impact = s0.MainPlate != null ? s0.MainPlate.Wall : s0.Start + s0.A;
            if (s0.ContactWall >= 0f) impact = Mathf.Min(impact, s0.ContactWall);
            float u = T - impact;
            float skin = Mathf.Clamp01(Settings.HullTexture.Value);
            float a = u < -0.05f ? 0.97f : Mathf.Lerp(0.97f, skin, Smooth((u + 0.05f) / 0.5f));
            _ghost.SetSkin(a, a > 0.6f);
            _ghost.SetInternalsVisible(a <= 0.6f || skin >= 0.6f);   // until the reveal: only the real vehicle
            RedFlash = u < 0f ? Mathf.Clamp01(1f + u / 0.06f) * (u > -0.06f ? 1f : 0f) : Mathf.Clamp01(1f - u / 0.45f);
            if (_revealBall == null)
            {
                _revealBall = Vis.Child(_fx, "reveal ball", Vis.SoftDisc, Matrix4x4.TRS(s0.Rec.EntryL, Quaternion.identity, Vector3.one), Vis.Additive).GetComponent<Renderer>();
                _billboards.Add(_revealBall.transform);
            }
            float ball = u < 0f ? 0f : Mathf.Clamp01(1f - u / 0.6f);
            _revealBall.enabled = ball > 0.01f;
            if (_revealBall.enabled)
            {
                _revealBall.transform.localScale = Vector3.one * Mathf.Lerp(2.6f, 1.2f, 1f - ball) * SizeScale;
                Vis.SetColor(_revealBall, new Color(0.9f, 0.08f, 0.04f) * (0.55f * ball));
            }
        }

        void UpdateTitle()
        {
            // the worst result of the session so far, once its round has arrived
            HitRecord worst = null;
            foreach (var s in _segs)
                if (T >= s.Start + s.A - 0.05f && (worst == null || Timeline.Rank(s.Rec.Title) > Timeline.Rank(worst.Title))) worst = s.Rec;
            if (worst == null) return;
            if (worst.Title != TitleShown)
            {
                TitleShown = worst.Title;
                TitleColor = worst.TitleColor;
                TitleWall = T;
            }
        }

        void UpdateProjectile(Segment s, float sim, float local)
        {
            var main = s.Rec.Main;
            Vector3 pos = main.PositionAt(sim, out bool visible);
            bool alive = sim <= main.EndSim + 1e-5f && sim < s.JetStartSim;
            // from its first contact on the rod is the rig (the tip and the windshield are gone)
            s.RodRig?.Update(sim, T, s.SimRate, _lastCamL);
            bool rigged = s.RodRig != null && s.RodRig.Visible;
            s.Shell.SetActive(alive && visible && !rigged);
            if (alive && visible && !rigged)
            {
                Vector3 ahead = main.PositionAt(Mathf.Min(sim + 0.0003f, main.EndSim), out _);
                Vector3 d = ahead - pos;
                if (d.sqrMagnitude < 1e-8f) d = s.Dir;
                s.Shell.transform.localPosition = pos;
                s.Shell.transform.localRotation = Quaternion.LookRotation(d.normalized);
                Vis.SetColor(s.ShellGlow, new Color(1f, 0.8f, 0.45f) * (0.75f + 0.25f * Mathf.Sin(T * 40f)));
            }

            // trail: what was flown in the approach window plus where the round is now; fades out after the segment
            var pts = new List<Vector3>();
            var jet = new List<Vector3>();
            float from = s.Rec.ApproachStart;
            pts.Add(main.PositionAt(from, out _));
            foreach (var p in main.Points)
            {
                if (p.Sim <= from) continue;
                if (p.Sim > sim) break;
                if (p.Jet && sim >= s.JetStartSim)
                {
                    if (jet.Count == 0 && pts.Count > 0) jet.Add(pts[pts.Count - 1]);
                    jet.Add(p.L);
                }
                else pts.Add(p.L);
            }
            if (sim < s.JetStartSim) pts.Add(pos);
            else if (jet.Count > 0) jet.Add(pos);
            SetLine(s.Trail, pts);
            if (s.JetVis != null)
            {
                // the jet as hot stretching copper (JetFx); the old line stays hidden
                SetLine(s.Jet, new List<Vector3>());
                if (sim >= s.JetStartSim)
                {
                    if (sim >= main.EndSim && s.JetEndWall < 0f) s.JetEndWall = T;
                    float fade = s.JetEndWall < 0f ? 1f : Mathf.Clamp01(1f - (T - s.JetEndWall) / 0.7f);
                    s.JetVis.Update(main.PositionAt(s.JetStartSim, out _), pos, sim - s.JetStartSim, T, fade, _lastCamL);
                }
                else s.JetVis.Hide();
            }
            else
            {
                SetLine(s.Jet, jet);
                if (jet.Count > 0) s.Jet.widthMultiplier = 0.03f + 0.01f * Mathf.Sin(T * 60f);
            }
            // in a long burst, old tracers fade so the picture stays readable
            if (_segs.Count > 1)
            {
                float fade = Mathf.Clamp01(1f - (local - (s.A + s.B + s.C)) / 1.5f);
                var c0 = new Color(1f, 0.96f, 0.85f, 0.95f * fade);
                s.Trail.startColor = c0;
                s.Trail.endColor = new Color(c0.r, c0.g, c0.b, 0f);
            }
        }

        void UpdateSpall(Segment s, float sim)
        {
            var b = _bounds;
            b.Expand(0.8f);
            var pts = new List<Vector3>(16);
            foreach (var sv in s.Spall)
            {
                var f = sv.F;
                pts.Clear();
                if (sim < f.StartSim) { sv.L.positionCount = 0; sv.Head.enabled = false; sv.Shard.enabled = false; continue; }
                pts.Add(f.StartL);
                bool outside = false;
                foreach (var p in f.Points)
                {
                    if (p.Sim > sim) break;
                    if (!b.Contains(p.L)) { outside = true; pts.Add(p.L); break; }
                    pts.Add(p.L);
                }
                Vector3 head = pts[pts.Count - 1];
                bool moving = false;
                if (!outside)
                {
                    head = f.PositionAt(sim, out bool vis);
                    moving = vis && sim < f.EndSim && b.Contains(head);
                    if (b.Contains(head)) pts.Add(head);
                }
                SetLine(sv.L, pts);
                sv.Head.enabled = moving;
                if (moving) sv.Head.transform.localPosition = head;
                // the splinter flies along its path, tumbling on the sim clock; a fragment that stopped inside stays where it ended, cooling
                bool stuck = !outside && !moving && f.Terminated && b.Contains(head);
                sv.Shard.enabled = moving || stuck;
                if (sv.Shard.enabled)
                {
                    float age = sim - f.StartSim;
                    Vector3 ahead = f.PositionAt(Mathf.Min(sim + 2e-4f, f.EndSim), out _) - head;
                    var look = ahead.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(ahead.normalized) : sv.Shard.transform.localRotation;
                    sv.Shard.transform.localPosition = head;
                    if (moving) sv.Shard.transform.localRotation = look * Quaternion.AngleAxis(sv.Spin * age * Mathf.Rad2Deg, sv.Axis);
                    float heat = moving ? Mathf.Lerp(0.95f, 0.5f, Mathf.Clamp01(age / 0.004f)) : 0.32f;
                    Vis.SetColor(sv.Shard, Shards.Hot(heat));
                }
            }
        }

        static void SetLine(LineRenderer lr, List<Vector3> pts)
        {
            if (pts.Count < 2) { lr.positionCount = 0; return; }
            lr.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) lr.SetPosition(i, pts[i]);
        }

        void ApplyEvents(Segment s)
        {
            var snap = Rec.Snapshot;
            var rec = s.Rec;
            foreach (var e in rec.Events)
            {
                if (s.Applied.Contains(e)) continue;
                float w = s.WallOf(e);
                // late events may change the verdict (fire, cook-off): give them a moment on screen
                if (float.IsPositiveInfinity(e.Sim) && Mathf.Max(w, T) > Duration - 0.6f && s.LateExtensions < 3 && e.Severity >= 2)
                {
                    Duration = Mathf.Max(Duration, Mathf.Max(w, T) + 1.4f);
                    s.LateExtensions++;
                }
                if (w > T) continue;
                s.Applied.Add(e);
                if (e.Kind != EvKind.CrewHit && e.Kind != EvKind.Breach) Shown.Add((e, w));
                Timeline.ComputeTitle(rec);
                switch (e.Kind)
                {
                    case EvKind.Module:
                        // archived AAR hits point at the module itself (no game component to look up)
                        if (e.Ref is ModulePart mp || (e.Ref is DC dc && snap.ModuleOf.TryGetValue(dc, out mp)))
                        {
                            _hp[mp] = e.After;
                            if (_ghost.Modules.TryGetValue(mp, out var rs)) foreach (var r in rs) _flash[r] = T + 0.22f;
                        }
                        break;
                    case EvKind.Crew:
                        if (e.After >= (float)HealthStatus.Critical && !string.IsNullOrEmpty(e.Subject) && _down.Add(e.Subject)) LateThrow(e);   // killed later (fire, cook-off): limp from now on
                        if (e.Ref is CrewInfo ci && _crew.ContainsKey(ci))
                        {
                            _crew[ci] = (HealthStatus)Mathf.RoundToInt(e.After);
                            if (_ghost.Crew.TryGetValue(ci, out var rs)) foreach (var r in rs) _flash[r] = T + 0.22f;
                        }
                        else if (e.Ref is CrewInfo other)
                        {
                            // a later hit's snapshot has its own CrewInfo objects: match the crewman by seat
                            var mine = _crew.Keys.FirstOrDefault(k => k.Label == other.Label);
                            if (mine != null) _crew[mine] = (HealthStatus)Mathf.RoundToInt(e.After);
                        }
                        break;
                    case EvKind.CrewHit:
                        if (_phys != null && e.Ref is CrewHitRef ch)
                        {
                            var mine = _crew.Keys.FirstOrDefault(k => k == ch.Crew) ?? _crew.Keys.FirstOrDefault(k => k.Label == ch.Crew.Label);
                            if (mine != null) _phys.Impulse(mine, e.L, CrewImpulse(s, e));
                        }
                        break;
                    case EvKind.AmmoHit:
                        foreach (var ap in snap.Ammo)
                            if (ReferenceEquals(ap.Visual, e.Ref) || ReferenceEquals(ap, e.Ref)) { _ammo[ap] = Math.Max(1, _ammo.TryGetValue(ap, out var st) ? st : 0); if (_ghost.Ammo.TryGetValue(ap, out var rs)) foreach (var r in rs) _flash[r] = T + 0.25f; }
                        break;
                    case EvKind.Fire:
                    case EvKind.AmmoFire:
                        if (e.Kind == EvKind.AmmoFire && e.HasPos) _detPts.Add(e.L);
                        if (e.HasPos && (e.Kind == EvKind.AmmoFire || Timeline.Propellant(e))) Charge(e);
                        if (Timeline.Propellant(e)) { PropellantFire(e); break; }
                        AddFire(e.L + Vector3.up * (e.Extent.y * 0.7f), e.Kind == EvKind.AmmoFire);
                        // a big burning part (a fuel tank, the engine) burns along its length
                        if (e.Extent.x > 0.35f || e.Extent.z > 0.35f)
                        {
                            Vector3 along = e.Extent.x > e.Extent.z ? Vector3.right * e.Extent.x : Vector3.forward * e.Extent.z;
                            AddFire(e.L + Vector3.up * (e.Extent.y * 0.7f) + along * 0.6f, e.Kind == EvKind.AmmoFire);
                            AddFire(e.L + Vector3.up * (e.Extent.y * 0.7f) - along * 0.6f, e.Kind == EvKind.AmmoFire);
                        }
                        if (e.Kind == EvKind.AmmoFire) foreach (var ap in snap.Ammo) _ammo[ap] = 2;
                        break;
                    case EvKind.Explosion:
                        if (Timeline.OwnWarhead(rec, e)) break;    // the round's own warhead (drawn as its detonation)
                        DetonationPressure(e, Mathf.Max(e.After, 0.5f));     // the hull feels it now, whatever is drawn
                        // with propellant already roaring, the pressure in the rack builds for a moment: the jets flare, then it goes
                        if (Settings.CinematicXray.Value && _jets.Count > 0 && !_boomed && _booms.Count == 0)
                        {
                            foreach (var j in _jets) if (!j.Vent) j.Surge(T);
                            _booms.Add((e, T + 0.2f));
                            Duration = Mathf.Max(Duration, T + 3f);
                        }
                        else Boom(e);
                        break;
                    case EvKind.VentFire:
                        // the game's flames coming out of a hatch / a vent / a hole: as wide as the opening, just outside it
                        if (e.HasPos) { ExitFlames(e); Opening(e, false); }
                        Duration = Mathf.Max(Duration, T + 1.5f);
                        break;
                    case EvKind.HatchBlown:
                        if (e.HasPos) Opening(e, true);
                        break;
                    case EvKind.Breach:
                        if (e.HasPos) _breaches.Add(e);
                        break;
                    case EvKind.FireBurst:
                        // the game's flame burst out of an open exit: the whole opening blows flame (its reach follows the
                        // detonation's TNT, as the game's does)
                        if (e.HasPos) ExitBurst(e);
                        if (e.HasPos) Opening(e, true);
                        BurstDetonation(e);
                        break;
                    case EvKind.TurretOff:
                        // the game threw the turret off: the biggest jolt of all, and the blast under the ring if none showed yet
                        AddKick(0.12f, 6f, 2.6f, 7f, Vector3.zero);
                        Hp.AddOpening(e.L, 2.5f, T);      // the whole turret ring open to the sky (as the turret lifts)
                        if (!_boomed && e.HasPos) AddBlast(e.L, 1.6f * SizeScale, true);
                        Duration = Mathf.Max(Duration, T + 2.5f);
                        break;
                    case EvKind.Kill:
                        _killWall = T;
                        break;
                    case EvKind.Story:
                        if ((e.Text.Contains("detonated") || e.Text.Contains("Detonated") || e.Text.Contains("ERA")) && !s.WarheadBlastDone)
                        {
                            s.WarheadBlastDone = true;
                            float tnt = rec.Ammo != null ? rec.Ammo.TntEquivalentKg : 0f;
                            AddBlast(e.L, tnt > 0 ? Mathf.Clamp(Mathf.Pow(tnt, 0.33f) * 0.6f, 0.3f, 1.4f) : 0.35f, false);
                            float wl = Mathf.Clamp01(Mathf.Pow(Mathf.Max(tnt, 0.02f), 0.33f) / 1.8f) * (s.Compact ? 0.5f : 1f);   // 1.3 kg ~0.6, 6 kg 1
                            AddKick(0.015f + 0.05f * wl, 0.8f + 2.6f * wl, 8f, 13f, Vector3.zero);
                            if (tnt > 0 && _phys != null)
                            {
                                // inside: the full charge, reflected by the hull. Outside: what gets in through the hole it tore
                                // (thin plate opened up) or through intact armor (almost nothing)
                                var inner = _phys.Inner; inner.Expand(-0.1f);
                                bool inside = inner.Contains(e.L);
                                bool holed = rec.ArmorHits.Any(h => h.Penetrated && (h.L - e.L).sqrMagnitude < 0.36f);
                                float share = inside ? 1f : holed ? 0.35f : 0.05f;
                                _phys.Blast(e.L, tnt * share, inside || holed);
                            }
                        }
                        break;
                }
            }
        }

        /// Torn plate: chunks blown into the vehicle from a fresh hole (HE tears big holes, KE punches a plug).
        // hard caps so a pathological hit (dozens of plates, hundreds of spall) can never stall a frame
        const int MaxPlatesPerHit = 24, MaxPlatesPerSession = 90, MaxPhysicsFragments = 90;
        const int MaxBurstPlates = 5, MaxStaticHoles = 80;
        int _staticHoles;

        /// The light hit cam (autocannons, the War Thunder way): one x-ray for the whole burst, each hole a ring that is
        /// just there from the moment the round reaches it. Nothing in it moves or is painted again (a deforming plate
        /// per round took 30 ms a frame in a 20-round burst).
        internal static bool LightRound(HitRecord rec) => Settings.LightAutocannonCam.Value && HitCamPlayer.Cal(rec) < Settings.BurstCaliberMm.Value;

        static Mesh _disc;

        void BuildStaticHoles(Segment s)
        {
            int seed = s.Rec.Id * 31;
            foreach (var ah in s.Rec.ArmorHits)
            {
                if (ah.PreJet || ah.IsSpall || _staticHoles >= MaxStaticHoles) continue;
                Vector3 n = ah.NormalL.sqrMagnitude > 1e-6f ? ah.NormalL.normalized : -ah.DirL.normalized;
                if (Vector3.Dot(n, ah.DirL) > 0f) n = -n;   // out of the plate, toward where the round came from
                float r = Mathf.Clamp(Mathf.Max(ah.HoleRadius, ah.Caliber * 0.0005f), 0.004f, 0.03f);
                bool pen = ah.Penetrated && !ah.Era;
                var go = new GameObject(pen ? "hole" : "dent");
                go.layer = Vis.Layer;
                go.transform.SetParent(_fx, false);
                go.transform.localPosition = ah.L + n * 0.004f;
                go.transform.localRotation = Quaternion.LookRotation(-n);
                Add(go, "rim", ArmorFx.HoleRim(pen ? r : r * 0.6f, 0.5f, seed++), Vis.Unlit);
                if (pen) Add(go, "dark", Disc(), Vis.Unlit, r * 1.05f);
                go.SetActive(false);
                s.StaticHoles.Add((go, s.Start + s.LocalAtSim(ah.Sim)));
                _staticHoles++;
            }
        }

        static void Add(GameObject parent, string name, Mesh mesh, Material mat, float scale = 1f)
        {
            var go = new GameObject(name) { layer = parent.layer };
            go.transform.SetParent(parent.transform, false);
            go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        /// A unit disc, near black (the inside of a hole), shared by every static hole.
        static Mesh Disc()
        {
            if (_disc != null) return _disc;
            const int n = 14;
            var v = new Vector3[n + 1];
            var c = new Color[n + 1];
            var t = new int[n * 3];
            v[0] = Vector3.zero; c[0] = new Color(0.02f, 0.02f, 0.02f);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                v[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                c[i + 1] = new Color(0.08f, 0.05f, 0.04f);
                t[3 * i] = 0; t[3 * i + 1] = 1 + (i + 1) % n; t[3 * i + 2] = 1 + i;
            }
            _disc = new Mesh { name = "static hole", vertices = v, colors = c, triangles = t };
            _disc.RecalculateBounds();
            return _disc;
        }
        const float BurstPlatesBelowMm = 40f;
        int _plates, _fragments;

        void BurstFromHole(Segment s, HoleFx h)
        {
            var ah = h.Hit;
            if (!ah.Penetrated || ah.Era || _fragments >= MaxPhysicsFragments) return;
            float r = Mathf.Max(ah.HoleRadius * 1.6f, 0.022f);
            bool he = ah.Category == AmmoType.AmmoCategory.Explosive;
            int count = he ? Mathf.Clamp(Mathf.RoundToInt(r * 60f), 8, 22) : ah.Category == AmmoType.AmmoCategory.ShapedCharge ? 2 : 5;
            if (_segs.Count > 1) count = Mathf.Max(1, count / 2);   // bursts: keep the fragment count sane
            if (ah.IsSpall) count = Mathf.Min(count, 2);
            count = Mathf.Min(count, MaxPhysicsFragments - _fragments);
            _fragments += count;
            float size = he ? r * 0.45f : r * 0.9f;
            float speed = he ? 160f : 260f;   // plate fragments leave at hundreds of m/s
            // born behind the plate (the physics has the armour as walls now: inside the plate they'd be pushed back out)
            _phys.SpawnFragments(ah.L + ah.DirL.normalized * (Mathf.Max(ah.Los, ah.Nominal) * 0.001f + 0.03f), ah.DirL, size, count, speed, s.Rec.Id * 97 + s.HoleBurst.Count);
        }

        /// Momentum delivered by whatever hit this body part (N*s, target-local).
        Vector3 CrewImpulse(Segment s, HitEvent e)
        {
            Vector3 dir = e.HasDir ? e.DirL.normalized : s.Dir;
            float v = e.Speed > 1f ? e.Speed : 900f;
            float j;
            var ammo = s.Rec.Ammo;
            if (e.Spall)
            {
                // fragment mass from its remaining penetration (a 10 mm-RHA fragment ~ 2 g, 100 mm ~ 60 g); it stays in the body
                float m = Mathf.Clamp(0.0019f * Mathf.Pow(Mathf.Max(e.Power, 1f) / 10f, 1.5f), 0.0005f, 0.25f);
                j = m * v;
            }
            else if (ammo != null && ammo.Category == AmmoType.AmmoCategory.Explosive && !e.Jet)
            {
                return Vector3.zero;   // HE: the crew is hit by blast + fragments (handled by the blast), not by the shell body
            }
            else
            {
                // rod / jet passing through: drag impulse through the body part (water-like tissue), J = 0.5 * rho * A * v * L
                float area = e.Jet ? 3e-5f : Mathf.Clamp(ammo != null ? ammo.SectionalArea : 0.0005f, 1e-4f, 0.02f);
                float thick = 0.12f;   // limb
                if (e.Ref is CrewHitRef chr)
                {
                    var cp = chr.Crew.Parts.FirstOrDefault(x => x.Comp == chr.Part);
                    if (cp != null && (cp.Part == BodyPart.Torso || cp.Part == BodyPart.UpperSpine || cp.Part == BodyPart.LowerSpine)) thick = 0.25f;
                    else if (cp != null && cp.Part == BodyPart.Head) thick = 0.18f;
                }
                j = 0.5f * 1000f * area * v * thick;
            }
            return dir * Mathf.Min(j, 400f);
        }

        void RefreshColors()
        {
            foreach (var kv in _ghost.Modules)
            {
                float hp = _hp.TryGetValue(kv.Key, out var h) ? h : 100f;
                Color c = Vis.ModuleColor(kv.Key.Cat, hp);
                if (kv.Key.Cat == ModCat.Tracks)
                {
                    // tracks / wheels read as exterior until they are damaged
                    var hc = Vis.HullColor;
                    c = hp >= 99.5f ? new Color(hc.r * 1.3f, hc.g * 1.3f, hc.b * 1.3f, hc.a * 1.4f) : new Color(c.r, c.g, c.b, 0.55f);
                }
                foreach (var r in kv.Value) Vis.SetColor(r, Flashed(r, c));
            }
            foreach (var kv in _ghost.Crew)
            {
                Color c = Vis.CrewColor(_crew.TryGetValue(kv.Key, out var st) ? st : HealthStatus.Fine);
                if (_crewFires.ContainsKey(kv.Key))
                {
                    // on fire: flickering orange over his status colour
                    float fl = 0.5f + 0.5f * Mathf.Sin(T * 19f + kv.Key.Label.Length);
                    c = Color.Lerp(c, Color.Lerp(new Color(1f, 0.32f, 0.04f, c.a), new Color(1f, 0.62f, 0.15f, c.a), fl), 0.85f);
                }
                foreach (var r in kv.Value) Vis.SetColor(r, Flashed(r, c));
            }
            foreach (var kv in _ghost.Ammo)
            {
                int s = _ammo.TryGetValue(kv.Key, out var v) ? v : 0;
                Color c = s == 0 ? Vis.ModuleColor(ModCat.Ammo, 100f) : s == 1 ? Vis.Red : Vis.Dead;
                foreach (var r in kv.Value) Vis.SetColor(r, Flashed(r, c));
            }
            if (_killWall >= 0f)
            {
                float k = Mathf.Clamp01((T - _killWall) / 0.6f);
                Color hull = Color.Lerp(Vis.HullColor, new Color(0.9f, 0.25f, 0.2f, 0.24f), k);
                foreach (var r in _ghost.Hull) Vis.SetColor(r, hull);
            }
        }

        Color Flashed(Renderer r, Color c)
        {
            if (_flash.TryGetValue(r, out var until) && T < until)
                return Color.Lerp(c, Color.white, Mathf.Clamp01((until - T) / 0.22f));
            return c;
        }

        /// Something caught fire: flames licking up from it, smoke, embers; burning propellant is a fierce jet.
        void AddFire(Vector3 l, bool big)
        {
            foreach (var f in _fires) if ((f.L - l).sqrMagnitude < 0.04f) return;
            _fires.Add(new XrayFire(_fx, l, big ? 0.3f : 0.22f, big, T, _fires.Count * 31 + 7));
        }

        /// The plane of an exit (its outward direction, two axes across it) and its radius (the hatch lid's size).
        static void ExitFrame(HitEvent e, out Vector3 n, out Vector3 a, out Vector3 b, out float r)
        {
            n = e.HasDir && e.DirL.sqrMagnitude > 1e-4f ? e.DirL.normalized : Vector3.up;
            a = Vector3.Cross(n, Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up).normalized;
            b = Vector3.Cross(n, a);
            r = e.Extent.x > 0.005f ? e.Extent.x : e.Power > 0.005f ? e.Power : 0.25f;
        }

        /// Flames filling an exit: a fire in its middle and more round it, as far out as the opening goes.
        void ExitFlames(HitEvent e)
        {
            ExitFrame(e, out var n, out var a, out var b, out float r);
            Vector3 c = e.L + n * 0.08f;
            if (e.Subject == "the hole" && HoleMouth(e.L, out var mouth, out var outN))
            {
                // the round's own hole: the game's exit sits where the round broke into the compartment, behind the
                // armour (20 cm in on a T-72's glacis), facing the way it went in, and the flames burned on the driver's
                // legs. They come out of the hole in the outermost plate of that path, outwards.
                n = outN;
                a = Vector3.Cross(n, Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up).normalized;
                b = Vector3.Cross(n, a);
                c = mouth + n * 0.06f;
            }
            float size = Mathf.Clamp(r * 0.62f, 0.12f, 0.42f) * (e.After > 0.8f ? 1.15f : 1f);
            int ring = Mathf.Clamp(Mathf.RoundToInt(r / 0.11f), 0, 6);
            _fires.Add(new XrayFire(_fx, c, size, e.After > 0.8f, T, _fires.Count * 31 + 7));
            for (int i = 0; i < ring; i++)
            {
                float ang = (i + 0.5f) * Mathf.PI * 2f / ring + Rec.Id;
                Vector3 p = c + (a * Mathf.Cos(ang) + b * Mathf.Sin(ang)) * r * 0.62f;
                _fires.Add(new XrayFire(_fx, p, size * 0.8f, false, T + 0.05f * i, _fires.Count * 31 + 7));
            }
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: flames out of {e.Subject} at T={T:0.00}: {1 + ring} fires over {r * 2000f:0} mm");
        }

        /// Where a round's hole opens to the outside: of the armour plates the round went through near `inside` (the point it
        /// broke into the compartment), the one furthest back along its path, and that plate's face outwards.
        bool HoleMouth(Vector3 inside, out Vector3 mouth, out Vector3 outN)
        {
            mouth = inside; outN = Vector3.up;
            ArmorHit best = null;
            float bestBack = float.MinValue;
            foreach (var sg in _segs)
                foreach (var h in sg.Rec.ArmorHits)
                {
                    if (h.IsSpall || h.Era || !h.Penetrated || h.DirL.sqrMagnitude < 1e-6f || (h.L - inside).sqrMagnitude > 0.6f * 0.6f) continue;   // (an ERA brick stands off the hull: the hole is in the plate under it)
                    float back = Vector3.Dot(h.L - inside, -h.DirL.normalized);
                    if (back > bestBack) { bestBack = back; best = h; }
                }
            if (best == null) return false;
            Vector3 dIn = best.DirL.normalized;
            outN = best.NormalL.sqrMagnitude > 1e-6f ? best.NormalL.normalized : -dIn;
            if (Vector3.Dot(outN, dIn) > 0f) outN = -outN;
            mouth = best.HasSurf ? best.SurfPointL : best.L;
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: the hole's flames at its mouth in {best.Name}, {bestBack * 100f:0} cm out from where the round broke in");
            return true;
        }

        /// A detonation venting through an exit: its hot products leave the opening and burn with the air outside as a
        /// ball of fire over it, not as jets. The ball is that opening's share of the detonation (by its area against
        /// ~0.5 m^2, about what a tank's hatches open up), ~0.9 W^(1/3) m across its radius; then the opening flames on.
        int _burstFx;
        void ExitBurst(HitEvent e)
        {
            if (_burstFx >= 8) return;
            _burstFx++;
            ExitFrame(e, out var n, out _, out _, out float r);
            float share = Mathf.Clamp(Mathf.PI * r * r / 0.5f, 0.1f, 1f);
            float w = Mathf.Max(e.After, 0.05f) * share;
            float grade = DetGrade(e.RealT, out _);
            float radius = Mathf.Clamp(0.9f * Mathf.Pow(w, 1f / 3f), 0.35f, 2.2f) * Mathf.Max(grade, 0.45f);
            AddBlast(e.L + n * (radius * 0.6f), radius, false);
            ExitFlames(e);
            Duration = Mathf.Max(Duration, T + 1.6f);
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: flame burst out of {e.Subject} at T={T:0.00}: a fireball {radius * 2f:0.0} m across ({w:0.0} of {e.After:0.0} kg TNT through {r * 2000f:0} mm, grade {grade:0.00})");
        }

        /// An explosion inside: the fireball fills at most what the hull holds; modules caught in it flash white-hot.
        void AddBlast(Vector3 l, float radius, bool big)
        {
            float room = Mathf.Max(_bounds.extents.x, Mathf.Max(_bounds.extents.y, _bounds.extents.z));
            radius = Mathf.Min(radius, room * 0.9f);
            _blasts.Add(new XrayExplosion(_fx, l, radius, big, T, _blasts.Count * 17 + 3));
            foreach (var kv in _ghost.Modules)
                foreach (var r in kv.Value)
                    if (r != null && (r.bounds.center - _ghost.Root.transform.TransformPoint(l)).sqrMagnitude < radius * radius) _flash[r] = T + (big ? 0.45f : 0.25f);
        }

        void UpdateFires()
        {
            BlastFlash = 0f;
            for (int i = _blasts.Count - 1; i >= 0; i--) BlastFlash = Mathf.Max(BlastFlash, _blasts[i].Flash(T) * (_blasts[i].Big ? 1f : 0.45f));
            // roaring propellant lights the whole view a little, flickering
            float roar = 0f;
            foreach (var j in _jets) roar += j.Level(T);
            BlastFlash = Mathf.Max(BlastFlash, Mathf.Min(roar, 1.5f) * 0.1f);
        }

        /// Effects that face the camera are updated once it has moved this frame.
        void TickFx(Vector3 camWorld)
        {
            foreach (var f in _fires) f.Tick(T, camWorld);
            foreach (var f in _crewFires.Values) f.Tick(T, camWorld);
            foreach (var b in _blasts) if (!b.Done(T)) b.Tick(T, camWorld);
            foreach (var j in _jets) j.Tick(T, camWorld);
        }

        // ------------------------------------------------------------------ cook-off

        /// A propellant charge caught fire: its case splits and the burning grains vent as a roaring jet. A case the
        /// round itself broke open blows out of its rupture along the round's path (and weaker back out of the entry
        /// tear); a charge lit by the fire around it vents up, toward the hatches, the way out of the hull.
        void PropellantFire(HitEvent e)
        {
            Vector3 at = e.L + Vector3.up * (e.Extent.y * 0.4f);
            Vector3 dir = Vector3.zero;
            float near = 0.6f + e.Extent.magnitude;
            foreach (var sg in _segs)
            {
                foreach (var ev in sg.Rec.Events)
                    if (ev.Kind == EvKind.AmmoHit && ev.HasPos && (ev.L - e.L).sqrMagnitude < near * near) { at = ev.L; dir = sg.Dir; break; }
                if (dir != Vector3.zero) break;
            }
            AddFire(e.L + Vector3.up * (e.Extent.y * 0.7f), true);     // the charge itself burning
            foreach (var ap in Rec.Snapshot.Ammo)
                if (ap.Shapes.Count > 0 && (ap.Shapes[0].LocalAabb().center - at).sqrMagnitude < 0.36f) _ammo[ap] = 2;
            Duration = Mathf.Max(Duration, T + 2.2f);
            if (_jets.Count >= 5) return;
            int seed = Rec.Id * 41 + _jets.Count * 7;
            if (dir != Vector3.zero && _jets.Count < 4)
            {
                // out of the rupture along the round's path, turned up by the hull ahead
                Vector3 d = (dir + Vector3.up * 0.55f).normalized;
                _jets.Add(new PropellantJet(_fx, at, d, JetLength(at, d, 1.5f), T, 3f, seed));
                Vector3 b = (-dir + Vector3.up * 0.4f).normalized;
                _jets.Add(new PropellantJet(_fx, at, b, JetLength(at, b, 0.9f), T + 0.04f, 2f, seed + 1));
            }
            else
            {
                // up and out through the roof, each a little differently tilted so several read apart
                float ang = _jets.Count * 2.4f + Rec.Id;
                Vector3 d = (Vector3.up + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 0.22f).normalized;
                _jets.Add(new PropellantJet(_fx, at, d, Mathf.Clamp(_bounds.max.y - at.y + 1.3f * SizeScale, 1.2f, 3.6f), T, 3.5f, seed));
            }
            AddKick(0.03f, 1.6f, 9f, 14f, Vector3.zero);                // the case bursting
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: propellant fire {e.Subject} at T={T:0.00} ({(dir != Vector3.zero ? "broken case, along the path" : "venting up")}), {_jets.Count} jets");
        }

        /// How far a jet reaches: to the hull wall it hits and a little splash beyond, at most `max` x the vehicle scale.
        float JetLength(Vector3 at, Vector3 d, float max)
        {
            max *= SizeScale;
            if (_phys != null && _phys.RaycastHull(at, d, max, out float dist)) return Mathf.Clamp(dist + 0.3f, 0.6f, max);
            return max;
        }

        /// The rack detonates: one blast centred on the rounds that went (not the vehicle's pivot), the jets snuffed
        /// out, the view slammed (the rest of the rack going off right behind it), torn pieces thrown everywhere, then
        /// what is left of the charges burns on, flames venting up out of the hull.
        void Boom(HitEvent e)
        {
            Vector3 c = e.L;
            var inner = _bounds;
            int n = 0;
            Vector3 sum = Vector3.zero;
            foreach (var p in _detPts) if (inner.Contains(p)) { sum += p; n++; }
            if (e.Subject == "rack" && inner.Contains(e.L)) { c = e.L; n = -1; }      // where the game itself detonated it
            else if (n > 0) c = sum / n;
            // the game shows a detonation as flame bursts out of the open exits (and maybe the turret thrown off); the x-ray
            // matches it: a contained detonation shakes less than one that throws the turret
            bool bursts = false;
            foreach (var sg in _segs)
                foreach (var ev in sg.Rec.Events)
                    if (ev.Kind == EvKind.FireBurst) bursts = true;
            // its TNT: the event's, else what the game's flame bursts of the same moment carry (their ratio is the TNT)
            float tnt = e.After;
            foreach (var g in _detGroups)
                if (Mathf.Abs(g.RealT - e.RealT) < 0.5f) { tnt = Mathf.Max(tnt, g.Tnt); g.Done = true; }
            tnt = Mathf.Max(tnt, 0.5f);
            if (float.IsNaN(_boomRealT)) { _boomRealT = e.RealT; _boomC = c; }
            float grade = DetGrade(e.RealT, out bool tossed);
            AddBlast(c, Mathf.Clamp(Mathf.Pow(tnt, 0.33f) * 1.4f, 1.2f, 4.5f) * grade, tossed);
            // the crew the detonation kills (their events come with it or right after): they are thrown by it
            foreach (var sg in _segs)
                foreach (var ce in sg.Rec.Events)
                    if (ce.Kind == EvKind.Crew && ce.After >= (float)HealthStatus.Critical && ce.RealT <= e.RealT + 0.5f && !string.IsNullOrEmpty(ce.Subject)) _down.Add(ce.Subject);
            // self-test only (throwall=true): the whole crew counts as killed by it, so the blast on every body is seen
            if (AutoTest.Active && AutoTest.Opt("throwall", "false") == "true") foreach (var ci in _crew.Keys) _down.Add(ci.Label);
            _phys?.Blast(c, tnt, true);
            _dets.Add((e.RealT, tnt, c));
            DetonationPressure(e, tnt);
            AddFire(c, true);
            foreach (var ap in Rec.Snapshot.Ammo) _ammo[ap] = 2;
            foreach (var j in _jets) if (!_bursts.Contains(j) && !j.Vent) j.Cut(T + 0.02f);
            if (_boomed) return;
            _boomed = true;
            float lv = Mathf.Clamp01(Mathf.Pow(tnt, 0.33f) / 3f) * grade;      // 0.7 kg ~0.3, 19 kg ~0.9
            AddKick(0.09f + 0.09f * lv, 4f + 4f * lv, 3.2f, 8f, Vector3.zero);
            AddKick(0.04f + 0.03f * lv, 2f, 6f, 13f, Vector3.zero, 0.13f);
            AddKick(0.03f + 0.03f * lv, 1.5f, 6f, 11f, Vector3.zero, 0.34f);
            AddKick(0.02f + 0.02f * lv, 1f, 7f, 14f, Vector3.zero, 0.6f);
            if (_phys != null)
            {
                Vector3[] dirs = { Vector3.up, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
                for (int i = 0; i < dirs.Length && _fragments < MaxPhysicsFragments; i++)
                {
                    int k = Mathf.Min(3, MaxPhysicsFragments - _fragments);
                    _fragments += k;
                    _phys.SpawnFragments(c, dirs[i] + Vector3.up * 0.3f, 0.06f, k, 40f + 40f * lv, Rec.Id * 31 + i);
                }
            }
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: cook-off at T={T:0.00}, {tnt:0.0} kg TNT, centre {c} {(n < 0 ? "(the game's detonation point)" : $"from {n} rounds")}, " +
                                                   $"{_jets.Count - _bursts.Count} jets cut, {_bursts.Count} game bursts kept, turret {(tossed ? "thrown off by it" : "stays")}, grade {grade:0.00}");
            // flames on out of the hull: where the game showed its bursts they already are, else up through the roof
            if (!bursts)
            {
                float roof = _bounds.max.y - c.y;
                _jets.Add(new PropellantJet(_fx, c + Vector3.up * 0.1f, (Vector3.up + new Vector3(0.12f, 0f, -0.08f)).normalized, Mathf.Clamp(roof + 1.6f * SizeScale, 1.6f, 4f), T + 0.35f, 2.5f, Rec.Id * 7 + 3));
            }
            Duration = Mathf.Max(Duration, T + 2.8f);
        }

        // ------------------------------------------------------------------ detonations the bodies feel

        /// One detonation as the game showed it: its flame bursts out of the exits, all at one moment (their ratio is its TNT).
        class DetGroup { public float RealT, Tnt, SeenT; public bool Done; }
        readonly List<DetGroup> _detGroups = new List<DetGroup>();
        readonly List<(float realT, float tnt, Vector3 c)> _dets = new List<(float, float, Vector3)>();
        float _boomRealT = float.NaN;
        Vector3 _boomC;

        /// How big a detonation reads here, graded the way its sound is (BlastSound.Grade): the one that throws the turret
        /// off is the full blast, a rack going with the turret staying on is smaller, the rest of it going later smaller still.
        float DetGrade(float realT, out bool tossed)
        {
            tossed = false;
            foreach (var sg in _segs)
                foreach (var ev in sg.Rec.Events)
                    if (ev.Kind == EvKind.TurretOff && ev.RealT >= realT - 0.3f && ev.RealT <= realT + 1f) { tossed = true; return 1f; }
            return float.IsNaN(_boomRealT) || Mathf.Abs(realT - _boomRealT) < 0.5f ? 0.55f : 0.35f;
        }

        void BurstDetonation(HitEvent e)
        {
            if (e.After <= 0f) return;
            foreach (var g in _detGroups) if (Mathf.Abs(g.RealT - e.RealT) < 0.15f) { g.Tnt = Mathf.Max(g.Tnt, e.After); return; }
            _detGroups.Add(new DetGroup { RealT = e.RealT, Tnt = e.After, SeenT = T, Done = !float.IsNaN(_boomRealT) && Mathf.Abs(_boomRealT - e.RealT) < 0.5f });
        }

        /// A detonation the game showed by its flame bursts alone (the rest of the rack going a moment after the first, or
        /// no explosion event at all): the hull takes its pressure and the bodies inside its blast, as with the first.
        void LaterDetonations()
        {
            foreach (var g in _detGroups)
            {
                if (g.Done) continue;
                if (float.IsNaN(_boomRealT))
                {
                    if (T < g.SeenT + 0.35f) continue;          // the first one: its explosion event usually comes with it
                    g.Done = true;
                    Boom(new HitEvent { Kind = EvKind.Explosion, Text = "AMMUNITION EXPLODED", L = _bounds.center, HasPos = true, RealT = g.RealT, After = g.Tnt });
                    continue;
                }
                g.Done = true;
                float tnt = Mathf.Max(g.Tnt, 0.5f);
                Vector3 c = _boomC;
                foreach (var sg in _segs)
                    foreach (var ce in sg.Rec.Events)
                        if (ce.Kind == EvKind.Crew && ce.After >= (float)HealthStatus.Critical && ce.RealT <= g.RealT + 0.5f && !string.IsNullOrEmpty(ce.Subject)) _down.Add(ce.Subject);
                PressureSpike(tnt);
                _phys?.Blast(c, tnt, true);
                _dets.Add((g.RealT, tnt, c));
                float grade = DetGrade(g.RealT, out bool tossed);
                AddBlast(c, Mathf.Clamp(Mathf.Pow(tnt, 0.33f) * 1.1f, 0.9f, 3.5f) * grade, tossed);
                float lv = Mathf.Clamp01(Mathf.Pow(tnt, 0.33f) / 3f) * grade;
                AddKick(0.05f + 0.06f * lv, 2.5f + 3f * lv, 3.5f, 9f, Vector3.zero);
                Duration = Mathf.Max(Duration, T + 2f);
                if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: another detonation at T={T:0.00} ({g.RealT - _boomRealT:0.00} s after the first), {tnt:0.0} kg TNT, grade {grade:0.00}{(tossed ? " (it threw the turret)" : "")}: the bodies inside thrown again");
            }
        }

        /// A crewman the game reports killed a moment after a detonation (its blast killed him): he is thrown by it too.
        void LateThrow(HitEvent e)
        {
            if (_phys == null || _dets.Count == 0) return;
            var d = _dets[_dets.Count - 1];
            if (e.RealT < d.realT - 0.05f || e.RealT > d.realT + 0.8f) return;
            var keep = _phys.MayGoLimp;
            string who = e.Subject;
            _phys.MayGoLimp = ci => ci.Label == who;
            _phys.Blast(d.c, d.tnt, true);
            _phys.MayGoLimp = keep;
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: {who} killed {e.RealT - d.realT:0.00} s after the detonation: thrown by its {d.tnt:0.0} kg");
        }

        // ------------------------------------------------------------------ pressure in the hull

        /// A hole a round left in the hull, open to the outside: when burning propellant pressurises the hull (HullPressure),
        /// hot gas and flame are forced out of it as a jet.
        class VentHole { public Vector3 L, Dir; public float D; public PropellantJet Jet; }
        readonly List<VentHole> _vents = new List<VentHole>();
        readonly HashSet<HitEvent> _ventSeen = new HashSet<HitEvent>();
        readonly List<HitEvent> _breaches = new List<HitEvent>();     // the holes the game opened into the compartments
        HullPressure _hpm;
        float _pLog = -1f;
        const float MaxVents = 6;

        HullPressure Hp => _hpm ?? (_hpm = new HullPressure(HullPressure.FreeVolume(
            _phys != null && _phys.Inner.size.sqrMagnitude > 0.5f ? _phys.Inner : HullPressure.InnerOf(Rec.Snapshot))));

        void Charge(HitEvent e) => Hp.AddCharge(e.L, T, e.Extent, e.Kind == EvKind.AmmoFire);

        /// An exit the game opened (a hatch blown open, flames bursting out) or showed fire seeping from (a gap round a
        /// shut hatch, 10 % of it). A hole the round made counts with the holes.
        void Opening(HitEvent e, bool open)
        {
            if (e.Subject == "the hole") return;
            float r = e.Extent.x > 0.005f ? e.Extent.x : e.Power > 0.005f ? e.Power : 0.2f;
            Hp.AddOpening(e.L, Mathf.PI * r * r * (open ? 1f : 0.1f), T);
        }

        /// A detonation's pressure, once per detonation (its event and its drawn blast are the same one), at its full TNT.
        void DetonationPressure(HitEvent e, float tnt) { if (Hp.Detonation(e.RealT, tnt, T)) LogSpike(tnt); }

        /// Another, separate detonation.
        void PressureSpike(float tnt) { Hp.Spike(tnt, T); LogSpike(tnt); }

        void LogSpike(float tnt)
        {
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: detonation pressure {Hp.Ratio:0.0} bar at T={T:0.00} ({tnt:0.0} kg TNT in {Hp.V:0.0} m^3), open {Hp.OpenArea(T) * 1e4f:0} cm^2 so far, {_vents.Count} holes");
        }

        /// The holes into the compartments (the game's own impact exits, where a round broke in) near the fire, each with
        /// its jet (HullPressure.VentShape: as big as the hole drawn there, out along the plate or the round's channel).
        void FindVents()
        {
            if (_vents.Count >= MaxVents) return;
            foreach (var e in _breaches)
            {
                if (_ventSeen.Contains(e) || _vents.Count >= MaxVents) continue;
                bool near = false;
                foreach (var c in Hp.Charges) if ((c.l - e.L).sqrMagnitude < 3.5f * 3.5f) { near = true; break; }
                if (!near) continue;            // maybe a later fire is nearer: look again then
                _ventSeen.Add(e);
                HullPressure.VentShape(e, _segs.SelectMany(sg => sg.Rec.ArmorHits), out Vector3 d, out float r, out ArmorHit ah, out float los);
                float max = HullPressure.MaxReach(2f * r);
                // something in its way outside (a skirt, a stowage box): it splashes there
                if (_phys != null && _phys.RaycastHull(e.L + d * 0.1f, d, max, out float dist) && dist > 0.15f) max = Mathf.Min(max, dist + 0.35f);
                max = Mathf.Max(max, 0.4f);
                var j = new PropellantJet(_fx, e.L, d, max, T, 999f, Rec.Id * 17 + _vents.Count * 11, 16f * r) { Vent = true, Push = 0f, Bright = 1.7f };
                _jets.Add(j);
                _vents.Add(new VentHole { L = e.L, Dir = d, D = 2f * r, Jet = j });
                Hp.AddHole(2f * r);
                if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: hole into {e.Subject} at {e.L} ({2000f * r:0} mm across{(ah != null ? $" in {ah.Name}, {los * 1000f:0} mm" : ", no plate hit near")}): gas out along {d}, up to {max:0.00} m");
            }
        }

        /// The hull's pressure on to now; it drives the jets out of the holes.
        void UpdatePressure()
        {
            if (_hpm == null || !_hpm.Started) return;
            if (_hpm.Charges.Count > 0) FindVents();
            float dt = T - _hpm.Clock;
            if (dt <= 0f) return;
            _hpm.StepTo(T);
            float pr = _hpm.Ratio, longest = 0f;
            int jetting = 0;
            foreach (var v in _vents)
            {
                float reach = HullPressure.Reach(v.D, pr);
                // the burning gas already out of the hole goes on flaming for a moment after the push drops
                v.Jet.Push = Mathf.Max(Mathf.Clamp01(reach / v.Jet.Len), v.Jet.Push * Mathf.Exp(-dt / 0.25f));
                if (v.Jet.Push > 0.05f) { jetting++; longest = Mathf.Max(longest, Mathf.Min(reach, v.Jet.Len)); }
            }
            if (jetting > 0) Duration = Mathf.Max(Duration, T + 0.5f);
            if (AutoTest.Active && T - _pLog >= 0.25f && (pr > 1.02f || _pLog < 0f))
            {
                _pLog = T;
                HitCamMod.Log.Msg($"x-ray: hull pressure {pr:0.00} bar at T={T:0.00} (free air {_hpm.V:0.0} m^3, gas in {_hpm.Gas:0.00} kg/s, open {_hpm.Area * 1e4f:0} cm^2 of it {_hpm.HoleArea * 1e4f:0} holes), {jetting}/{_vents.Count} holes jetting, longest {longest:0.00} m"
                                   + string.Concat(_vents.Select(v => $"; jet at {v.L}: level {v.Jet.Level(T):0.00}, drawn {v.Jet.Len * Mathf.Min(v.Jet.Level(T), 1.5f):0.00} m")));
            }
        }

        /// Crew still alive with fire in their compartment (a burning charge, flames at their hatch, a fire next to them)
        /// thrash in their seats: they can't get out in time, and the game kills crew left in a burning vehicle after
        /// ~30 s. The moment the game reports one critical or dead, he goes still.
        readonly Dictionary<CrewInfo, XrayFire> _crewFires = new Dictionary<CrewInfo, XrayFire>();

        void UpdatePanic()
        {
            if (_phys == null || !Settings.CrewPanic.Value) return;
            foreach (var kv in _crewFires) if (_phys.TorsoL(kv.Key, out var tl)) kv.Value.MoveTo(tl + Vector3.up * 0.05f);   // he keeps burning, also once he lies still
            foreach (var kv in _crew)
            {
                var ci = kv.Key;
                bool alive = kv.Value < HealthStatus.Critical && !_down.Contains(ci.Label);
                if (_phys.Panicking(ci)) { if (!alive) _phys.EndPanic(ci); continue; }
                if (!alive || ci.Shapes.Count == 0) continue;
                Bounds cb = ci.Shapes[0].LocalAabb();
                foreach (var sh in ci.Shapes) cb.Encapsulate(sh.LocalAabb());
                Vector3 at = cb.center;
                bool burning = false;
                foreach (var f in _fires) if (T >= f.Start && (f.L - at).sqrMagnitude < 1.8f * 1.8f) { burning = true; break; }
                if (!burning) foreach (var j in _jets) if (j.Level(T) > 0.2f && (j.L - at).sqrMagnitude < 2f * 2f) { burning = true; break; }
                if (!burning) continue;
                _phys.StartPanic(ci, Rec.Id * 131 + ci.Label.Length * 17 + _crew.Count);
                if (!_crewFires.ContainsKey(ci) && _phys.TorsoL(ci, out var tl)) _crewFires[ci] = new XrayFire(_fx, tl, 0.13f, false, T, Rec.Id * 7 + _crewFires.Count);
                if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: {ci.Label} ({kv.Value}) alive in the fire at T={T:0.00}: thrashing");
            }
        }

        /// A propellant fire often ends in a cook-off seconds later (the warheads heat up in the flames): the live hit
        /// cam keeps watching it roar for a while (CookOffWait, game seconds after the hit) so the detonation is seen.
        void WaitForCookOff()
        {
            if (!Live || _noWait || _boomed || _jets.Count == 0 || !Settings.CinematicXray.Value || Settings.CookOffWait.Value <= 0f) return;
            if (Time.time - Rec.FirstImpactReal > Settings.CookOffWait.Value) return;
            if (T > Duration - 0.6f) Duration = T + 0.6f;
        }

        // ------------------------------------------------------------------ camera shake

        struct Kick { public float T0, Amp, Roll, Decay, Freq, Seed; public Vector3 Dir; }
        readonly List<Kick> _kicks = new List<Kick>();
        readonly HashSet<HoleFx> _hitKick = new HashSet<HoleFx>(), _breakKick = new HashSet<HoleFx>();

        /// One jolt of the camera (cinematic x-ray only): it lurches along dir (zero = away from what it looks at),
        /// rings down at freq Hz with some roll; amp in metres for a medium tank, scaled with the vehicle.
        void AddKick(float amp, float rollDeg, float decay, float freq, Vector3 dir, float delay = 0f)
        {
            if (!Settings.CinematicXray.Value) return;
            if (_kicks.Count > 40) _kicks.RemoveAt(0);
            _kicks.Add(new Kick { T0 = T + delay, Amp = amp * SizeScale, Roll = rollDeg, Decay = decay, Freq = freq, Seed = _kicks.Count * 7.31f + T * 3.7f, Dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.zero });
        }

        /// How hard a plate hit slams the view: from the energy the round brings (log scale, ~20 kJ MG round none,
        /// ~5-8 MJ of a 105/120 mm long rod full); a ricochet glances off with less, a stopped round puts it all in.
        static float ImpactLevel(ArmorHit ah)
        {
            float lv = Mathf.Clamp01((Mathf.Log10(Mathf.Max(ah.Energy, 1f)) - 4.3f) / 2.6f);
            if (ah.Ricochet) lv *= 0.45f;
            else if (!ah.Penetrated) lv = Mathf.Min(1f, lv * 1.1f);
            return lv;
        }

        /// The round slamming into the plate jolts the view, and the plate giving way (plug out, petals open) jolts it again.
        void ImpactKicks(Segment s)
        {
            int n = 0;
            foreach (var h in s.Holes)
            {
                var ah = h.Hit;
                if (ah.IsSpall || ah.Era || ah.Frag != s.Rec.Main) continue;
                if (++n > 3) break;
                float lv = ImpactLevel(ah) * (s.Compact ? 0.5f : 1f) * (n == 1 ? 1f : 0.6f);
                if (lv < 0.03f) continue;
                Vector3 d = ah.DirL.sqrMagnitude > 1e-6f ? ah.DirL : s.Dir;
                if (T >= h.Wall && _hitKick.Add(h)) AddKick(0.012f + 0.055f * lv, 0.4f + 2.8f * lv, 9f, 15f, d);
                if (h.Broke && ah.Penetrated && _breakKick.Add(h)) AddKick((0.012f + 0.055f * lv) * 0.55f, (0.4f + 2.8f * lv) * 0.5f, 12f, 22f, d);
            }
        }

        /// Every jolt ringing down on its own, plus a rumble while propellant roars: an offset (target-local) and a
        /// small rotation (degrees) for the camera.
        void Shake(Vector3 camL, Vector3 focus, out Vector3 offset, out Vector3 euler)
        {
            offset = Vector3.zero; euler = Vector3.zero;
            Vector3 away = (camL - focus).sqrMagnitude > 1e-6f ? (camL - focus).normalized : Vector3.back;
            for (int i = _kicks.Count - 1; i >= 0; i--)
            {
                var k = _kicks[i];
                float a = T - k.T0;
                if (a < 0f) continue;
                float env = Mathf.Exp(-a * k.Decay);
                if (env < 0.01f) { _kicks.RemoveAt(i); continue; }
                env *= Mathf.Clamp01(a / 0.012f);                              // hard, but not a teleport
                Vector3 d = k.Dir == Vector3.zero ? away : k.Dir;
                float ph = a * k.Freq;
                Vector3 nz = new Vector3(Noise(k.Seed, 0, ph), Noise(k.Seed, 1, ph), Noise(k.Seed, 2, ph));
                offset += (d * Mathf.Cos(ph * 6.2832f) + nz * 0.7f) * (k.Amp * env);
                euler += new Vector3(nz.y * 0.45f, nz.x * 0.45f, Noise(k.Seed, 3, ph * 0.8f)) * (k.Roll * env);
            }
            float roar = 0f;
            foreach (var j in _jets) roar += j.Level(T);
            if (roar > 0.01f)
            {
                roar = Mathf.Min(roar, 2f);
                offset += new Vector3(Noise(1.7f, 0, T * 23f), Noise(1.7f, 1, T * 23f), Noise(1.7f, 2, T * 23f)) * (0.009f * SizeScale * roar);
                euler += new Vector3(Noise(2.9f, 0, T * 17f), Noise(2.9f, 1, T * 17f), Noise(2.9f, 2, T * 11f)) * (0.35f * roar);
            }
        }

        static float Noise(float seed, int axis, float t) => Mathf.PerlinNoise(seed * 3.17f + axis * 19.3f, t) * 2f - 1f;

        // ------------------------------------------------------------------ camera

        bool _orbit;
        float _closeW, _jetW, _rodW, _rodSide, _rodLift = 0.22f;
        Vector3 _rodDirS, _rodAtS;

        /// The rod cam's side: left / right, level or from above, whichever has the fewest modules between it and the
        /// round's way through the vehicle (they fill the view up close).
        void PickRodSide(Segment s, Vector3 at, Vector3 dir, Vector3 side, float dist)
        {
            var boxes = new List<Bounds>();
            foreach (var mp in s.Rec.Snapshot.Modules) foreach (var sh in mp.Shapes) { var b = sh.LocalAabb(); if (b.size.magnitude < 4f) boxes.Add(b); }
            float best = float.MaxValue;
            _rodSide = _yawSign;
            foreach (float sg in new[] { _yawSign, -_yawSign })
                foreach (float lift in new[] { 0.22f, 0.9f })
                {
                    float score = lift > 0.5f ? 0.5f : 0f;
                    // along the stretch it will follow (the round's way into the vehicle)
                    for (int k = 0; k < 4; k++)
                    {
                        Vector3 p = at + dir * (k * 0.45f);
                        Vector3 c = p + side * (dist * sg) + Vector3.up * (dist * lift);
                        Vector3 d = p - c;
                        var ray = new Ray(c, d.normalized);
                        foreach (var b in boxes) if (b.IntersectRay(ray, out float h) && h < d.magnitude - 0.05f) score += 1f;
                    }
                    if (score < best) { best = score; _rodSide = sg; _rodLift = lift; }
                }
        }

        /// Where the rod cam looks: the rod's nose (the rig once it has met armour, else the flying round).
        static bool RodCamTarget(Segment s, out Vector3 nose, out Vector3 dir, out float len)
        {
            nose = Vector3.zero; dir = Vector3.forward; len = s.Rod ? s.RodLen0 : 0.5f;
            if (s.RodRig != null && s.RodRig.Visible) { nose = s.RodRig.Nose; dir = s.RodRig.Dir; len = s.RodRig.Length; return true; }
            if (s.Shell != null && s.Shell.activeSelf) { nose = s.Shell.transform.localPosition; dir = s.Shell.transform.localRotation * Vector3.forward; return true; }
            return false;
        }
        Vector3 _jetFocus;
        Vector3 _lastCamL = new Vector3(0f, 5f, -5f);
        float _camYaw, _camPitch, _camDist;
        Vector3 _camFocus;
        bool _camInit;

        public void UpdateCamera(Camera cam, float dt)
        {
            float s = SizeScale;
            var s0 = _segs[0];
            float yaw, pitch, dist;
            Vector3 focus;
            if (!_orbit)
            {
                // single hit, WT-style: start wide behind the shell, swoop in, then orbit
                // with an impact slow motion the camera closes in on the struck plate (WT shows the panel big), then pulls out
                float t = T - s0.Start;
                float k1 = s0.A, k2 = s0.A + s0.B, k3 = Mathf.Max(s0.A + s0.B + s0.C, k2 + 0.1f);
                bool close = s0.MainPlate != null && Settings.ImpactSlowMo.Value > 0.01f && Settings.CinematicXray.Value && !ProvingGround.RodCam && !s0.Steady;
                float kd = close ? k1 + Mathf.Max(s0.Dwell * 1.15f + 0.15f, 0.9f) : k1;   // hold at least while the plate tears
                if (t < k1) { float u = Smooth(t / k1); yaw = Mathf.Lerp(22f, 34f, u); pitch = Mathf.Lerp(14f, 22f, u); dist = Mathf.Lerp(7.5f, 4.4f, u); }
                else if (t < kd) { yaw = 34f; pitch = 22f; dist = 4.4f; }
                else if (t < k2) { float u = Smooth((t - kd) / Mathf.Max(k2 - kd, 1e-3f)); yaw = Mathf.Lerp(34f, 72f, u); pitch = Mathf.Lerp(22f, 30f, u); dist = Mathf.Lerp(4.4f, 4.9f, u); }
                else if (t < k3) { float u = Mathf.Clamp01((t - k2) / (k3 - k2)); yaw = Mathf.Lerp(72f, 100f, u); pitch = Mathf.Lerp(30f, 34f, u); dist = Mathf.Lerp(4.9f, 5.6f, u); }
                else { yaw = 100f + (t - k3) * 8f; pitch = 34f; dist = 5.6f + Mathf.Min(t - k3, 3f) * 0.12f; }   // a fire / cook-off: keep orbiting
                // an autocannon burst in the light hit cam: once the first round is in, the view holds still on the spot
                if (LightRound(s0.Rec) && t >= k1) { yaw = 34f; pitch = 22f; dist = 4.4f; }
                yaw *= _yawSign;
                pitch += _pitchLift;
                dist *= s;
                focus = t < kd || LightRound(s0.Rec) ? s0.Rec.EntryL : Vector3.Lerp(s0.Rec.EntryL, s0.FocusEnd, Smooth(Mathf.Clamp01((t - kd) / (s0.B0Public * 0.6f))));
                // impact close-up: swing in front of the struck plate (whatever its obliquity), hold through the slow motion, swing back out
                _closeW = close ? Smooth((t - k1 * 0.55f) / Mathf.Max(k1 * 0.45f, 0.05f)) * (1f - Smooth((t - kd) / 0.9f)) : 0f;
            }
            else
            {
                // a burst: keep slowly orbiting, follow the newest round that is arriving
                var live = _segs.LastOrDefault(x => T >= x.Start && T <= x.Start + x.A + x.B) ?? _segs.Last(x => T >= x.Start || x == _segs[0]);
                float lt = T - live.Start;
                Vector3 want = lt < live.A ? live.Rec.EntryL : Vector3.Lerp(live.Rec.EntryL, live.FocusEnd, Smooth(Mathf.Clamp01((lt - live.A) / Mathf.Max(live.B, 0.1f))));
                float k = 1f - Mathf.Exp(-dt * 3f);
                yaw = _camYaw + dt * 12f * _yawSign;
                pitch = Mathf.Lerp(_camPitch, 30f + _pitchLift, k);
                dist = Mathf.Lerp(_camDist, 5.2f * s, k);
                focus = Vector3.Lerp(_camFocus, want, k);
            }
            // burning propellant: frame the jets (they reach out of the hull)
            float jw = 0f;
            Vector3 jf = Vector3.zero;
            foreach (var j in _jets) { float lv = Mathf.Min(j.Level(T), 1f); if (lv > 0.01f) { jf += (j.L + j.Dir * j.Len * 0.35f) * lv; jw += lv; } }
            if (_phys != null) foreach (var ci in _crewFires.Keys) if (_phys.Panicking(ci) && _phys.TorsoL(ci, out var pt)) { jf += pt * 1.5f; jw += 1.5f; }
            _jetW = Mathf.MoveTowards(_jetW, jw > 0.01f ? 1f : 0f, dt * 1.2f);
            if (jw > 0.01f) _jetFocus = jf / jw;
            if (_jetW > 0.001f) focus = Vector3.Lerp(focus, _jetFocus, 0.6f * Smooth(_jetW) * (1f - _closeW));
            if (!_camInit || !_orbit) { _camInit = true; }
            _camYaw = yaw; _camPitch = pitch; _camDist = dist; _camFocus = focus;

            Vector3 camL = CameraLocal(s0.Dir, focus, yaw, pitch, dist);
            if (!_orbit && _closeW > 0.001f)
            {
                var ah = s0.MainPlate.Hit;
                // back along the round's own approach (always outside the vehicle, nothing in between), tilted toward the
                // plate face so a grazing hit is still seen from the front of the plate, a little to the side
                Vector3 n = ah.HasSurf ? ah.SurfNormalL : ah.NormalL.sqrMagnitude > 0.01f ? ah.NormalL.normalized : -s0.Dir;
                Vector3 back = -(ah.DirL.sqrMagnitude > 1e-6f ? ah.DirL.normalized : s0.Dir);
                Vector3 side = Vector3.Cross(back, Vector3.up);
                side = side.sqrMagnitude > 0.01f ? side.normalized * _yawSign : Vector3.right;
                Vector3 v = (back * 0.75f + n * 0.45f + side * 0.3f + Vector3.up * 0.08f).normalized;
                if (Vector3.Dot(v, n) < 0.25f) v = (v + n * (0.25f - Vector3.Dot(v, n)) * 1.5f).normalized;
                // frame the hole and its petals, and the spall cone / jet behind (far enough that a crewman right behind the
                // plate doesn't fill the view)
                bool jet = s0.Rec.Ammo != null && s0.Rec.Ammo.Category == AmmoType.AmmoCategory.ShapedCharge;
                float near = Mathf.Clamp(2.0f + s0.MainPlate.Feature * 6f, jet ? 2.6f : 2.2f, 3.5f);
                camL = Vector3.Lerp(camL, ah.L + v * near, _closeW);
                focus = Vector3.Lerp(focus, ah.L, _closeW);
            }
            float nearClip = 0.03f;
            // the rod cam: beside the round, following it through the plates
            if (!_orbit && ProvingGround.RodCam)
            {
                float t = T - s0.Start;
                var near = _bounds;
                near.Expand(2f);
                bool want = t >= s0.A * 0.4f && t <= s0.A + s0.B * 0.85f && RodCamTarget(s0, out var nz, out _, out _)
                    && (t < s0.A || near.Contains(nz));   // after the impact only while it's in or at the vehicle
                _rodW = Mathf.MoveTowards(_rodW, want ? 1f : 0f, dt * 2f);
                if (_rodW > 0.001f && RodCamTarget(s0, out var nose, out var rdir, out var rlen))
                {
                    // smoothed: the rod's direction and the spot it looks at catch up softly (no jerks when it bends,
                    // breaks or the pieces spin)
                    if (_rodDirS == Vector3.zero) { _rodDirS = rdir; _rodAtS = nose - rdir * (rlen * 0.35f); }
                    _rodDirS = Vector3.Slerp(_rodDirS, rdir, 1f - Mathf.Exp(-dt * 5f)).normalized;
                    _rodAtS = Vector3.Lerp(_rodAtS, nose - _rodDirS * (rlen * 0.35f), 1f - Mathf.Exp(-dt * 9f));
                    Vector3 side = Vector3.Cross(_rodDirS, Vector3.up);
                    side = side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.right;
                    float rd = Mathf.Clamp(Mathf.Max(rlen, 0.3f) * 1.5f, 0.5f, 1.6f);
                    Vector3 at = _rodAtS;
                    if (_rodSide == 0f) PickRodSide(s0, at, _rodDirS, side, rd);
                    Vector3 rc = at + side * (rd * _rodSide) + Vector3.up * (rd * _rodLift) - _rodDirS * (rd * 0.12f);
                    float w = Smooth(_rodW);
                    camL = Vector3.Lerp(camL, rc, w);
                    focus = Vector3.Lerp(focus, at, w);
                    // a metre from the rod the camera is often inside the vehicle's parts: whatever is much nearer than
                    // the rod is cut away, so the parts in between don't fill the view
                    nearClip = Mathf.Lerp(nearClip, rd * 0.55f, w);
                }
            }
            cam.nearClipPlane = nearClip;
            Vector3 look = focus - camL;
            var rot = Quaternion.LookRotation(look, Mathf.Abs(Vector3.Dot(look.normalized, Vector3.up)) > 0.97f ? s0.Dir : Vector3.up);
            if (Settings.CinematicXray.Value)
            {
                Shake(camL, focus, out var off, out var eul);
                float calm = 1f - 0.8f * Smooth(_rodW);   // up close a jolt is much bigger on screen
                camL += off * calm;
                rot *= Quaternion.Euler(eul * calm);
            }
            cam.transform.position = _ghost.Root.transform.position + camL;
            cam.transform.rotation = rot;
            _lastCamL = camL;
            LookL = focus;
            FaceCamera(cam);
        }

        public IEnumerable<HoleFx> AllHoles() => _segs.SelectMany(s => s.Holes);

        /// Billboards (glows, flames, blasts) and plate colours follow whichever camera looks (also the AAR's free camera).
        public void FaceCamera(Camera cam)
        {
            Vector3 cp = cam.transform.position;
            foreach (var b in _billboards)
                if (b != null && b.gameObject.activeInHierarchy) b.rotation = Quaternion.LookRotation(b.position - cp, cam.transform.up);
            // the struck plate follows the view every frame, the others (mostly small spall exits) take turns
            using (Perf.T("Camera/plates"))
            {
                int i = 0;
                foreach (var sg in _segs) foreach (var h in sg.Holes) h.FaceCamera(cp, cam.transform.up, h == sg.MainPlate || (Time.frameCount + i++) % 3 == 0);
            }
            using (Perf.T("Camera/fx")) TickFx(cp);
        }

        // for the After Action Report's replay controls
        public Vector3 LookL;                                         // what the scripted camera looks at (target-local)
        public Vector3 Origin => _ghost.Root.transform.position;     // where the x-ray copy sits in the world
        public float ImpactWall => _segs[0].MainPlate != null ? _segs[0].MainPlate.Wall : _segs[0].Start + _segs[0].A;

        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        // ------------------------------------------------------------------ UI data

        public IEnumerable<(ModCat cat, string label, float hp)> Debuffs()
        {
            var cats = new[] { (ModCat.Engine, "ENG"), (ModCat.Transmission, "TRN"), (ModCat.Gun, "GUN"), (ModCat.TurretDrive, "TUR"), (ModCat.Tracks, "TRK"), (ModCat.Optics, "OPT"), (ModCat.Ammo, "AMMO"), (ModCat.Fuel, "FUEL") };
            foreach (var (cat, label) in cats)
            {
                float min = float.MaxValue;
                foreach (var kv in _hp) if (kv.Key.Cat == cat) min = Mathf.Min(min, kv.Value);
                if (cat == ModCat.Ammo && _ammo.Count > 0) min = Mathf.Min(min, _ammo.Values.Max() >= 2 ? 0f : 30f);
                if (min != float.MaxValue) yield return (cat, label, min);
            }
        }

        public (int alive, int total) CrewCount()
        {
            int alive = 0;
            foreach (var kv in _crew) if (kv.Value < HealthStatus.Critical) alive++;
            return (alive, _crew.Count);
        }

        public void Dispose()
        {
            if (_fx != null)
                foreach (var mf in _fx.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null && (mf.sharedMesh.name == "hole rim" || mf.sharedMesh.name == "petals"))
                        UnityEngine.Object.Destroy(mf.sharedMesh);
            foreach (var sg in _segs) foreach (var h in sg.Holes) h.Dispose();
            foreach (var f in _fires) f.Dispose();
            foreach (var f in _crewFires.Values) f.Dispose();
            foreach (var sg in _segs) sg.JetVis?.Dispose();
            foreach (var sg in _segs) sg.RodRig?.Dispose();
            foreach (var b in _blasts) b.Dispose();
            foreach (var j in _jets) j.Dispose();
            if (AutoTest.Active && _phys != null) HitCamMod.Log.Msg("Physics report " + Rec.TargetName + $" ({Hits} hits): " + _phys.Report());
            if (AutoTest.Active) foreach (var sg in _segs) if (sg.RodRig != null && sg.RodRig.SoftSummary.Length > 0) HitCamMod.Log.Msg("x-ray rod " + sg.RodRig.SoftSummary);
            _ghost?.Destroy();
        }
    }
}
