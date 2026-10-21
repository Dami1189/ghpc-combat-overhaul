using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GHPCHitCam
{
    /// A hit replayed full screen: the hit cam's own Playback (x-ray copy, slow-motion round, plate, spall, ragdolls,
    /// fires, events) on a free clock: pause, scrub (forward = fast-forward, backward = rebuild + fast-forward),
    /// speed, and a mouse-orbit camera, rendered into a screen-sized texture.
    internal class AarReplay
    {
        public readonly AarEntry Entry;
        public Playback P;
        public bool Schematic, Paused, Free;
        public float Speed = 1f;
        public string Error;
        float _seek = -1f, _backTo = -1f, _lastBuild = -10f;
        Vector3 _pivot;
        float _yaw, _pitch, _dist = 6f;

        static Camera _cam;
        static RenderTexture _rt;
        public static RenderTexture Texture => _rt;

        public bool Seeking => _seek >= 0f || _backTo >= 0f;

        public AarReplay(AarEntry e)
        {
            Entry = e;
            Build();
        }

        void Build()
        {
            Dispose();
            Error = null;
            try
            {
                var rec = AfterAction.ReplayRecord(Entry, out Schematic);
                P = new Playback(rec);
                _lastBuild = Time.unscaledTime;
                foreach (var c in Camera.allCameras)
                    if (c != _cam) c.cullingMask &= ~(1 << Vis.Layer);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                HitCamMod.Log.Error("AAR replay of hit #" + Entry.Rec.Id + " failed: " + ex);
                P?.Dispose();
                P = null;
            }
        }

        public void Restart()
        {
            Build();
            Paused = false;
        }

        public void SeekTo(float t)
        {
            if (P == null) return;
            t = Mathf.Clamp(t, 0f, P.Duration);
            if (t < P.T - 1e-4f) { _backTo = t; _seek = -1f; }   // physics and plates only run forward: rebuild, then fast-forward
            else { _seek = t; _backTo = -1f; }
        }

        public void Update(float dt)
        {
            if (P == null) return;
            if (_backTo >= 0f && Time.unscaledTime - _lastBuild > 0.2f)   // throttled while the scrub bar is dragged back
            {
                float t = _backTo;
                _backTo = -1f;
                Build();
                if (P == null) return;
                _seek = t;
            }
            if (_seek >= 0f)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (P.T < _seek - 1e-5f && sw.ElapsedMilliseconds < 35) P.Tick(Mathf.Min(1f / 30f, _seek - P.T));
                if (P.T >= _seek - 1e-5f) _seek = -1f;
                return;
            }
            if (_backTo >= 0f || Paused) return;
            P.Tick(dt * Speed);
            if (P.T >= P.Duration) Paused = true;
        }

        public void Render(float dt)
        {
            if (P == null) return;
            Ensure();
            if (Free)
            {
                var rot = Quaternion.Euler(_pitch, _yaw, 0f);
                _cam.transform.rotation = rot;
                _cam.transform.position = P.Origin + _pivot - rot * Vector3.forward * _dist;
                P.FaceCamera(_cam);
            }
            else P.UpdateCamera(_cam, Paused || Seeking ? 0f : dt * Speed);
            _cam.enabled = true;
        }

        /// Takes over from the scripted camera where it is: orbit around the point it looks at.
        public void StartFreeCam()
        {
            if (P == null || Free) return;
            Ensure();
            _pivot = P.LookL;
            Vector3 camL = _cam.transform.position - P.Origin;
            Vector3 d = _pivot - camL;
            _dist = Mathf.Clamp(d.magnitude, 0.4f, 40f);
            Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
            _yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            _pitch = -Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            Free = true;
        }

        public void Orbit(float dyaw, float dpitch)
        {
            _yaw += dyaw;
            _pitch = Mathf.Clamp(_pitch + dpitch, -85f, 85f);
        }

        public void Zoom(float k) => _dist = Mathf.Clamp(_dist * k, 0.3f, 40f);

        public void Pan(float dx, float dy)
        {
            if (_cam == null) return;
            float s = _dist * 0.0016f;
            _pivot += (-_cam.transform.right * dx - _cam.transform.up * dy) * s;
        }

        static void Ensure()
        {
            int w = Mathf.Max(Screen.width, 320), h = Mathf.Max(Screen.height, 180);
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "HitCam AAR RT" };
                _rt.Create();
            }
            if (_cam == null)   // also after the game's SceneCleanup destroyed every camera on a scene change
            {
                var go = new GameObject("HitCam AAR Camera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _cam = go.AddComponent<Camera>();
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.fieldOfView = 46f;
                _cam.nearClipPlane = 0.03f;
                _cam.farClipPlane = 300f;
                _cam.depth = -49;
                _cam.useOcclusionCulling = false;
                _cam.allowHDR = false;
                _cam.allowMSAA = true;
                _cam.renderingPath = RenderingPath.Forward;   // same reason as the hit cam: deferred lights the modules under the hull fill
                _cam.enabled = false;
            }
            _cam.cullingMask = 1 << Vis.Layer;
            _cam.targetTexture = _rt;
            _cam.backgroundColor = Vis.Background;
        }

        public static void Hide()
        {
            if (_cam != null) _cam.enabled = false;
        }

        public void Dispose()
        {
            try { P?.Dispose(); } catch (Exception e) { HitCamMod.Log.Warning("AAR replay dispose: " + e.Message); }
            P = null;
            _seek = -1f;
        }
    }

    internal static partial class AfterAction
    {
        enum View { Hits, Stats, Past, Replay }

        static View _view = View.Hits;
        static AarMission _shown;
        static AarReplay _replay;
        static float _scroll;
        static int _filter;               // 0 all, 1 your shots, 2 hits on you
        static List<AarEntry> _order = new List<AarEntry>();
        static List<AarArchive.Session> _sessions;
        static AarStats _stats;
        static AarMission _statsFor;
        static float _statsAt;

        // ------------------------------------------------------------------ actions

        static void Play(AarEntry e)
        {
            StopReplay();
            _replay = new AarReplay(e);
            _view = View.Replay;
            HitCamMod.Log.Msg($"AAR replay: hit #{e.Rec.Id} {e.Rec.Title} | {e.Rec.ShooterName} -> {e.Rec.TargetName}" + (_replay.Schematic ? " (box model)" : ""));
        }

        static void StopReplay()
        {
            _replay?.Dispose();
            _replay = null;
            AarReplay.Hide();
            if (_view == View.Replay) _view = View.Hits;
        }

        static void Step(int dir)
        {
            if (_replay == null || _order.Count == 0) return;
            int i = _order.IndexOf(_replay.Entry);
            int j = Mathf.Clamp(i + dir, 0, _order.Count - 1);
            if (j != i && j >= 0) { float speed = _replay.Speed; Play(_order[j]); if (_replay != null) _replay.Speed = speed; }
        }

        static void ShowPast()
        {
            StopReplay();
            _sessions = AarArchive.Sessions();
            _view = View.Past;
            _scroll = 0f;
        }

        static void OpenSession(AarArchive.Session s)
        {
            // the current / last mission is already in memory with its full models
            var live = new[] { _mission, _last }.FirstOrDefault(x => x != null && x.Folder != null && string.Equals(x.Folder.TrimEnd('\\', '/'), s.Dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            try { _shown = live ?? AarArchive.Load(s); }
            catch (Exception e) { HitCamMod.Log.Error("AAR: could not load " + s.Dir + ": " + e); return; }
            _view = View.Hits;
            _scroll = 0f;
            _filter = 0;
            HitCamMod.Log.Msg($"AAR: opened {(live != null ? "in-memory" : "archived")} mission \"{_shown.Name}\" ({_shown.Entries.Count} hits)");
        }

        // ------------------------------------------------------------------ input

        static Rect _viewport;
        static readonly List<Rect> _uiRects = new List<Rect>();
        static Vector3 _lastMouse;
        static int _dragBtn = -1;
        static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f };

        static void ViewUpdate()
        {
            if (_view != View.Replay || _replay == null)
            {
                _dragBtn = -1;
                return;
            }
            if (Input.GetKeyDown(KeyCode.Space)) TogglePlay();
            if (Input.GetKeyDown(KeyCode.R)) _replay.Restart();
            if (Input.GetKeyDown(KeyCode.RightArrow)) { Step(+1); if (_replay == null) return; }
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { Step(-1); if (_replay == null) return; }
            if (Input.GetKeyDown(KeyCode.UpArrow)) _replay.Speed = Speeds.FirstOrDefault(s => s > _replay.Speed + 1e-3f) is float up && up > 0f ? up : 2f;
            if (Input.GetKeyDown(KeyCode.DownArrow)) _replay.Speed = Speeds.LastOrDefault(s => s < _replay.Speed - 1e-3f) is float dn && dn > 0f ? dn : 0.1f;
            if (Input.GetKeyDown(KeyCode.C)) { if (_replay.Free) _replay.Free = false; else _replay.StartFreeCam(); }
            if (Input.GetKeyDown(KeyCode.Backspace)) { StopReplay(); return; }

            // mouse: left-drag orbits, right / middle-drag pans, wheel zooms (switches to the free camera)
            Vector3 mp = Input.mousePosition;
            var gui = new Vector2(mp.x, Screen.height - mp.y);
            bool overUi = !_viewport.Contains(gui) || _uiRects.Any(r => r.Contains(gui));
            for (int b = 0; b < 3; b++)
                if (Input.GetMouseButtonDown(b) && !overUi) { _dragBtn = b; _lastMouse = mp; _replay.StartFreeCam(); }
            if (_dragBtn >= 0 && !Input.GetMouseButton(_dragBtn)) _dragBtn = -1;
            if (_dragBtn >= 0)
            {
                Vector3 d = mp - _lastMouse;
                if (_dragBtn == 0) _replay.Orbit(d.x * 0.3f, -d.y * 0.3f);
                else _replay.Pan(d.x, d.y);
            }
            _lastMouse = mp;
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f && !overUi)
            {
                _replay.StartFreeCam();
                _replay.Zoom(Mathf.Pow(0.87f, wheel));
            }
            _replay.Update(Time.unscaledDeltaTime);
        }

        static void TogglePlay()
        {
            if (_replay?.P == null) return;
            if (_replay.Paused && _replay.P.T >= _replay.P.Duration - 1e-3f) { _replay.Restart(); return; }
            _replay.Paused = !_replay.Paused;
        }

        static void ViewLateUpdate()
        {
            if (_view == View.Replay && _replay != null) _replay.Render(Time.unscaledDeltaTime);
            else AarReplay.Hide();
        }

        // ------------------------------------------------------------------ drawing

        static GUIStyle _h1, _h2, _txt, _small, _bold, _btn, _cell, _big, _banner, _chip;
        static Texture2D _white;
        static float _k = -1f;

        static readonly Color Bg = new Color(0.045f, 0.05f, 0.06f, 0.97f);
        static readonly Color Panel = new Color(0.09f, 0.10f, 0.12f, 0.92f);
        static readonly Color PanelHi = new Color(0.14f, 0.16f, 0.19f, 0.95f);
        static readonly Color Line = new Color(1f, 1f, 1f, 0.07f);
        static readonly Color Accent = new Color(0.96f, 0.70f, 0.21f);           // WT amber
        static readonly Color Mine = new Color(0.35f, 0.62f, 1f);
        static readonly Color OnMe = new Color(1f, 0.32f, 0.25f);
        static readonly Color Grey = new Color(0.62f, 0.66f, 0.72f);
        static readonly Color Text1 = new Color(0.92f, 0.94f, 0.97f);

        static void Styles()
        {
            float k = Mathf.Clamp(Screen.height / 900f, 0.7f, 2.2f);
            if (_h1 != null && Mathf.Abs(k - _k) < 0.01f) return;
            _k = k;
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            GUIStyle L(int size, FontStyle fs = FontStyle.Normal, TextAnchor a = TextAnchor.MiddleLeft) =>
                new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(size * k), fontStyle = fs, alignment = a, wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0) };
            _h1 = L(26, FontStyle.Bold);
            _h2 = L(13, FontStyle.Bold);
            _txt = L(14);
            _small = L(12);
            _bold = L(14, FontStyle.Bold);
            _btn = L(13, FontStyle.Bold, TextAnchor.MiddleCenter);
            _cell = L(14);
            _big = L(28, FontStyle.Bold);
            _banner = L(34, FontStyle.Bold, TextAnchor.MiddleCenter);
            _chip = L(11, FontStyle.Bold, TextAnchor.MiddleCenter);
        }

        static float K => _k;

        static void Fill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        static void Label(Rect r, string s, GUIStyle st, Color c, bool shadow = false)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = st.normal.textColor;
            if (shadow)
            {
                st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.8f);
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, st);
            }
            st.normal.textColor = c;
            GUI.Label(r, s, st);
            st.normal.textColor = old;
        }

        static bool Button(Rect r, string label, bool on = false, bool enabled = true)
        {
            var ev = Event.current;
            bool hover = enabled && r.Contains(ev.mousePosition);
            Fill(r, on ? new Color(Accent.r, Accent.g, Accent.b, 0.9f) : hover ? PanelHi : new Color(0.16f, 0.17f, 0.2f, 0.95f));
            Label(r, label, _btn, on ? new Color(0.08f, 0.08f, 0.08f) : enabled ? Text1 : new Color(1f, 1f, 1f, 0.3f));
            if (ev.type == EventType.Repaint) _uiRects.Add(r);
            if (enabled && ev.type == EventType.MouseDown && ev.button == 0 && hover) { ev.Use(); return true; }
            return false;
        }

        static int _sliderHot;

        static float Slider(Rect r, float v, float min, float max, int id, Color fill)
        {
            var ev = Event.current;
            Rect hit = new Rect(r.x - 6, r.y - 8, r.width + 12, r.height + 16);
            float At() => Mathf.Lerp(min, max, Mathf.InverseLerp(r.x, r.xMax, ev.mousePosition.x));
            if (ev.type == EventType.MouseDown && ev.button == 0 && hit.Contains(ev.mousePosition)) { _sliderHot = id; v = At(); ev.Use(); }
            else if (_sliderHot == id && ev.type == EventType.MouseDrag) { v = At(); ev.Use(); }
            if (_sliderHot == id && ev.rawType == EventType.MouseUp) _sliderHot = 0;
            if (ev.type == EventType.Repaint)
            {
                _uiRects.Add(hit);
                float u = Mathf.InverseLerp(min, max, v);
                Fill(r, new Color(1f, 1f, 1f, 0.12f));
                Fill(new Rect(r.x, r.y, r.width * u, r.height), fill);
                float kw = 6f * K;
                Fill(new Rect(r.x + r.width * u - kw * 0.5f, r.y - 5f * K, kw, r.height + 10f * K), Text1);
            }
            return v;
        }

        /// HitCamMod.OnGUI, after everything else (drawn on top).
        public static void Draw()
        {
            if (Enabled == null) return;
            try
            {
                Styles();
                if (!_open) { DrawHint(); return; }
                if (Event.current.type == EventType.Repaint) _uiRects.Clear();
                var old = GUI.color;
                switch (_view)
                {
                    case View.Replay: DrawReplay(); break;
                    case View.Stats: DrawFrame(); DrawStats(); break;
                    case View.Past: DrawFrame(); DrawPast(); break;
                    default: DrawFrame(); DrawHits(); break;
                }
                GUI.color = old;
            }
            catch (Exception e) { HitCamMod.Log.Error("AAR draw: " + e); }
        }

        static void DrawHint()
        {
            if (_hintUntil < 0f || Time.unscaledTime > _hintUntil || string.IsNullOrEmpty(_hint)) return;
            float a = Mathf.Clamp01((_hintUntil - Time.unscaledTime) / 1.5f);
            float w = Mathf.Min(Screen.width - 40f, 920f * K), h = 40f * K;
            var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.16f, w, h);
            Fill(r, new Color(0f, 0f, 0f, 0.72f * a));
            Fill(new Rect(r.x, r.y, 4f * K, h), new Color(Accent.r, Accent.g, Accent.b, a));
            var st = new GUIStyle(_bold) { alignment = TextAnchor.MiddleCenter };
            Label(r, _hint, st, new Color(1f, 0.92f, 0.75f, a), true);
        }

        /// Background, title, tabs.
        static void DrawFrame()
        {
            float W = Screen.width, H = Screen.height, k = K;
            Fill(new Rect(0, 0, W, H), Bg);
            Fill(new Rect(0, 0, W, 66f * k), new Color(0f, 0f, 0f, 0.35f));
            Fill(new Rect(0, 66f * k, W, 2f * k), new Color(Accent.r, Accent.g, Accent.b, 0.6f));
            Label(new Rect(24f * k, 8f * k, W * 0.5f, 32f * k), "AFTER ACTION REPORT", _h1, Text1);
            string sub;
            if (_shown == null) sub = "no mission recorded yet";
            else
            {
                string when = _shown.Archived ? (_shown.Info?.started ?? FolderName(_shown.Folder)) : _shown == _mission ? "this mission" : "last mission";
                sub = $"{_shown.Name}   ·   {when}" + (_shown.Result != null ? "   ·   " + _shown.Result : "") + (_shown.Duration > 1f ? $"   ·   {Clock(_shown.Duration)}" : "")
                    + (_shown.Archived ? "   ·   loaded from disk: replays use box models" : "")
                    + (_shown.Unplayable > 0 ? $"   ·   {_shown.Unplayable} older hit files have no paths (saved before this version)" : "");
            }
            Label(new Rect(24f * k, 40f * k, Mathf.Min(W * 0.6f, W - 780f * k), 20f * k), sub, _small, Grey);
            if (!Recording) Label(new Rect(24f * k, 40f * k + 18f * k, W * 0.6f, 18f * k), "Recording is off (AarEnabled = false in MelonPreferences.cfg)", _small, OnMe);

            float bw = 118f * k, bh = 30f * k, right = W - 24f * k, y = 18f * k;
            Rect Next(float w, float gap = 8f) { right -= w; var r = new Rect(right, y, w, bh); right -= gap * k; return r; }
            if (Button(Next(bw), "CLOSE  " + KeyCode)) Close();
            if (Button(Next(bw + 24f * k), "PAST MISSIONS", _view == View.Past)) ShowPast();
            if (Button(Next(bw), "STATS", _view == View.Stats, _shown != null)) { _view = View.Stats; _scroll = 0f; }
            if (Button(Next(bw, 28f), "HITS", _view == View.Hits, _shown != null)) { _view = View.Hits; _scroll = 0f; }
            var cur = _mission ?? _last;
            if (cur != null && _shown != cur && Button(Next(bw + 24f * k), _mission != null ? "THIS MISSION" : "LAST MISSION")) { _shown = cur; _view = View.Hits; _scroll = 0f; }
        }

        static string FolderName(string dir) => string.IsNullOrEmpty(dir) ? "" : System.IO.Path.GetFileName(dir);

        static string Clock(float s)
        {
            if (s < 0f) return "--:--";
            int t = Mathf.FloorToInt(s);
            return t >= 3600 ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 60:00}:{t % 60:00}";
        }

        static AarStats CurrentStats()
        {
            if (_shown == null) return null;
            if (_stats == null || _statsFor != _shown || Time.unscaledTime - _statsAt > 1f)
            {
                foreach (var e in _shown.Entries) Retitle(e);
                _stats = Stats(_shown);
                _statsFor = _shown;
                _statsAt = Time.unscaledTime;
            }
            return _stats;
        }

        static readonly Dictionary<HitRecord, int> _titled = new Dictionary<HitRecord, int>();

        /// Late events (a fire, the kill) can change the verdict after the hit was recorded.
        static void Retitle(AarEntry e)
        {
            if (e.Archived) return;
            if (_titled.TryGetValue(e.Rec, out int n) && n == e.Rec.Events.Count) return;
            Timeline.ComputeTitle(e.Rec);
            _titled[e.Rec] = e.Rec.Events.Count;
        }

        static void StatCard(Rect r, string label, string value, Color vc, string sub = null)
        {
            Fill(r, Panel);
            Fill(new Rect(r.x, r.y, r.width, 2f * K), new Color(1f, 1f, 1f, 0.06f));
            Label(new Rect(r.x + 12f * K, r.y + 6f * K, r.width - 16f * K, 16f * K), label, _h2, Grey);
            Label(new Rect(r.x + 12f * K, r.y + 22f * K, r.width - 16f * K, 34f * K), value, _big, vc);
            if (sub != null) Label(new Rect(r.x + 12f * K, r.y + r.height - 20f * K, r.width - 16f * K, 16f * K), sub, _small, Grey);
        }

        static string Pct(float f) => f < 0f ? "-" : (f * 100f).ToString("0") + "%";
        static string N(int v) => v < 0 ? "?" : v.ToString();

        static void DrawStatStrip(AarStats s, float y)
        {
            float W = Screen.width, k = K, m = 24f * k, gap = 8f * k;
            float h = 84f * k;
            int n = 8;
            float cw = (W - 2 * m - gap * (n - 1)) / n;
            float x = m;
            void Card(string l, string v, Color c, string sub = null) { StatCard(new Rect(x, y, cw, h), l, v, c, sub); x += cw + gap; }
            Card("SHOTS FIRED", s.Shots.ToString(), Text1, s.Incoming > 0 ? $"{s.Incoming} fired at you" : null);
            Card("HITS", s.Hits.ToString(), Text1, s.FriendlyHits > 0 ? $"{s.FriendlyHits} on friendlies" : null);
            Card("ACCURACY", Pct(s.Accuracy), Text1, s.MaxRange > 0f ? $"longest {s.MaxRange:0} m" : null);
            Card("PENETRATIONS", s.Pens.ToString(), Accent, s.Hits > 0 ? Pct(s.Pens / (float)s.Hits) + " of hits" : null);
            Card("KILLS", s.Kills.ToString(), new Color(1f, 0.35f, 0.28f));
            Card("KNOCK-OUTS", s.KnockOuts.ToString(), new Color(1f, 0.55f, 0.3f));
            Card("CREW LOST", $"{N(s.CrewLostFriendly)} / {N(s.CrewLostEnemy)}", Text1, "yours / theirs");
            Card("DAMAGE TAKEN", s.HitsTaken == 0 ? "none" : $"{s.HitsTaken} hit{(s.HitsTaken == 1 ? "" : "s")}", s.PensTaken > 0 ? OnMe : Text1,
                s.HitsTaken == 0 ? null : $"{s.PensTaken} pen · {s.ModulesLost.Count} modules · {s.CrewKilled.Count} crew");
        }

        static void DrawHits()
        {
            float W = Screen.width, H = Screen.height, k = K, m = 24f * k;
            if (_shown == null)
            {
                Label(new Rect(m, 100f * k, W - 2 * m, 30f * k), "No hits recorded in this session yet. Past missions are under PAST MISSIONS.", _txt, Grey);
                return;
            }
            var s = CurrentStats();
            DrawStatStrip(s, 80f * k);

            // filters
            float fy = 176f * k, fh = 28f * k;
            int all = _shown.Entries.Count, mine = _shown.Entries.Count(e => e.Rec.PlayerShot), onMe = _shown.Entries.Count(e => e.OnPlayer);
            if (Button(new Rect(m, fy, 110f * k, fh), $"ALL  {all}", _filter == 0)) { _filter = 0; _scroll = 0f; }
            if (Button(new Rect(m + 118f * k, fy, 150f * k, fh), $"YOUR SHOTS  {mine}", _filter == 1)) { _filter = 1; _scroll = 0f; }
            if (Button(new Rect(m + 276f * k, fy, 150f * k, fh), $"HITS ON YOU  {onMe}", _filter == 2)) { _filter = 2; _scroll = 0f; }
            Label(new Rect(m + 440f * k, fy, W - m * 2 - 440f * k, fh), "Click a hit to watch it in 3D (x-ray, slow motion, plate, spall, crew).", _small, Grey);

            _order = _shown.Entries.Where(e => _filter == 0 || (_filter == 1 ? e.Rec.PlayerShot : e.OnPlayer)).ToList();

            // table
            float ty = fy + fh + 10f * k, rowH = 32f * k;
            var cols = new[] { 0f, 0.06f, 0.30f, 0.50f, 0.575f, 0.74f };   // time, shooter -> target, ammo, range, verdict, damage
            float tw = W - 2 * m;
            Fill(new Rect(m, ty, tw, 24f * k), new Color(1f, 1f, 1f, 0.05f));
            string[] heads = { "TIME", "SHOOTER  →  TARGET", "AMMUNITION", "RANGE", "RESULT", "DAMAGE" };
            for (int i = 0; i < heads.Length; i++) Label(new Rect(m + 12f * k + tw * cols[i], ty, tw * 0.25f, 24f * k), heads[i], _h2, Grey);
            var list = new Rect(m, ty + 26f * k, tw, H - ty - 26f * k - 34f * k);
            float content = _order.Count * rowH;
            var ev = Event.current;
            if (ev.type == EventType.ScrollWheel && list.Contains(ev.mousePosition)) { _scroll += ev.delta.y * 22f * k; ev.Use(); }
            _scroll = Mathf.Clamp(_scroll, 0f, Mathf.Max(0f, content - list.height));
            if (_order.Count == 0) Label(new Rect(m + 12f * k, list.y + 8f * k, tw, 24f * k), _filter == 2 ? "Nothing hit you." : "No hits.", _txt, Grey);

            GUI.BeginGroup(list);
            int first = Mathf.Max(0, Mathf.FloorToInt(_scroll / rowH));
            for (int i = first; i < _order.Count; i++)
            {
                float ry = i * rowH - _scroll;
                if (ry > list.height) break;
                var e = _order[i];
                Retitle(e);
                var r = new Rect(0, ry, tw, rowH - 2f * k);
                bool hover = r.Contains(ev.mousePosition) && new Rect(0, 0, list.width, list.height).Contains(ev.mousePosition);
                Fill(r, hover ? PanelHi : (i % 2 == 0 ? Panel : new Color(Panel.r, Panel.g, Panel.b, 0.7f)));
                Fill(new Rect(0, ry, 4f * k, rowH - 2f * k), e.OnPlayer ? OnMe : e.Rec.PlayerShot ? Mine : Grey);
                float cx(int c) => 12f * k + tw * cols[c];
                float cwid(int c) => (c + 1 < cols.Length ? tw * cols[c + 1] : tw) - tw * cols[c] - 10f * k;
                var rec = e.Rec;
                Label(new Rect(cx(0), ry, cwid(0), rowH), e.MissionTime >= 0f ? Clock(e.MissionTime) : "#" + rec.Id, _cell, Grey);
                string who = (rec.PlayerShot ? "You" : rec.ShooterName) + "  →  " + (e.OnPlayer ? "You (" + rec.TargetName + ")" : rec.TargetName) + (e.Friendly ? "  (friendly)" : "");
                Label(new Rect(cx(1), ry, cwid(1), rowH), who, _bold, e.OnPlayer ? new Color(1f, 0.75f, 0.7f) : Text1);
                Label(new Rect(cx(2), ry, cwid(2), rowH), rec.Ammo?.Name ?? "?", _cell, Text1);
                Label(new Rect(cx(3), ry, cwid(3), rowH), e.Range > 0f ? $"{e.Range:0} m" : "-", _cell, Grey);
                string credit = e.Killed && rec.Title != "TARGET DESTROYED" ? "  ·  KILL" : e.KnockedOut && rec.Title != "CREW KNOCKED OUT" ? "  ·  KNOCK-OUT" : "";
                Label(new Rect(cx(4), ry, cwid(4), rowH), rec.Title + credit, _bold, rec.TitleColor);
                Label(new Rect(cx(5), ry, cwid(5), rowH), e.Damage, _cell, e.Damage == "-" ? Grey : new Color(1f, 0.86f, 0.62f));
                if (hover && ev.type == EventType.MouseDown && ev.button == 0) { ev.Use(); GUI.EndGroup(); Play(e); return; }
            }
            GUI.EndGroup();
            if (content > list.height)
            {
                float bh = list.height * list.height / content;
                Fill(new Rect(list.xMax - 4f * k, list.y + (list.height - bh) * (_scroll / (content - list.height)), 4f * k, bh), new Color(1f, 1f, 1f, 0.25f));
            }
            Label(new Rect(m, H - 30f * k, W - 2 * m, 24f * k), $"Blue = your shots, red = hits on you.   In a replay: drag to orbit, right-drag to pan, wheel to zoom   ·   Space play / pause   ·   ← → previous / next hit   ·   ↑ ↓ speed   ·   R restart   ·   C camera   ·   Backspace list   ·   {KeyCode} close", _small, Grey);
        }

        static void DrawStats()
        {
            float W = Screen.width, H = Screen.height, k = K, m = 24f * k;
            if (_shown == null) return;
            var s = CurrentStats();
            DrawStatStrip(s, 80f * k);
            float y0 = 180f * k, gap = 14f * k;
            float cw = (W - 2 * m - 2 * gap) / 3f, ch = H - y0 - 24f * k;
            float lh = 24f * k;

            // column 1: your gunnery
            var c1 = new Rect(m, y0, cw, ch);
            Fill(c1, Panel);
            float y = c1.y + 10f * k;
            void Head(Rect c, string t) { Label(new Rect(c.x + 14f * k, y, c.width, 22f * k), t, _h2, Accent); y += 26f * k; }
            void Row(Rect c, string l, string v, Color vc) { Label(new Rect(c.x + 14f * k, y, c.width * 0.62f, lh), l, _txt, Grey); Label(new Rect(c.x + c.width * 0.62f, y, c.width * 0.36f, lh), v, _bold, vc); y += lh; }
            Head(c1, "YOUR GUNNERY");
            Row(c1, "Rounds fired (≥ " + Settings.MinCaliberMm.Value.ToString("0") + " mm)", s.Shots.ToString(), Text1);
            Row(c1, "Hits on vehicles", s.Hits.ToString(), Text1);
            Row(c1, "Accuracy", Pct(s.Accuracy), Text1);
            Row(c1, "Penetrations", s.Pens.ToString(), Accent);
            Row(c1, "Kills", s.Kills.ToString(), new Color(1f, 0.35f, 0.28f));
            Row(c1, "Crews knocked out", s.KnockOuts.ToString(), new Color(1f, 0.55f, 0.3f));
            Row(c1, "Hits on friendlies", s.FriendlyHits.ToString(), s.FriendlyHits > 0 ? OnMe : Text1);
            Row(c1, "Average range", s.AvgRange > 0f ? $"{s.AvgRange:0} m" : "-", Text1);
            Row(c1, "Longest hit", s.MaxRange > 0f ? $"{s.MaxRange:0} m" : "-", Text1);
            y += 10f * k;
            Head(c1, "BY AMMUNITION   (fired / hits / pens)");
            foreach (var a in s.ByAmmo.OrderByDescending(a => a.fired).ThenByDescending(a => a.hits).Take(10))
                Row(c1, a.ammo, $"{a.fired} / {a.hits} / {a.pens}", Text1);
            if (s.ByAmmo.Count == 0) Row(c1, "nothing fired", "", Grey);

            // column 2: targets
            var c2 = new Rect(m + cw + gap, y0, cw, ch);
            Fill(c2, Panel);
            y = c2.y + 10f * k;
            Head(c2, "TARGETS HIT   (hits / pens, worst result)");
            foreach (var t in s.Targets.Take(18))
            {
                Label(new Rect(c2.x + 14f * k, y, c2.width * 0.38f, lh), t.name, _txt, Text1);
                Label(new Rect(c2.x + c2.width * 0.40f, y, c2.width * 0.16f, lh), $"{t.hits} / {t.pens}", _txt, Grey);
                Label(new Rect(c2.x + c2.width * 0.56f, y, c2.width * 0.43f, lh), t.result, _bold, t.color);
                y += lh;
            }
            if (s.Targets.Count == 0) Label(new Rect(c2.x + 14f * k, y, c2.width, lh), "none", _txt, Grey);
            y = Mathf.Max(y + 12f * k, c2.y + ch * 0.55f);
            Head(c2, "LOSSES (the game's count)");
            Row(c2, "Crew lost: yours / enemy", $"{N(s.CrewLostFriendly)} / {N(s.CrewLostEnemy)}", Text1);
            Row(c2, "Vehicles lost: yours / enemy", $"{N(s.UnitsLostFriendly)} / {N(s.UnitsLostEnemy)}", Text1);
            if (_shown.Result != null) Row(c2, "Result", _shown.Result, _shown.Result == "VICTORY" ? new Color(0.5f, 0.9f, 0.5f) : OnMe);

            // column 3: damage taken
            var c3 = new Rect(m + 2 * (cw + gap), y0, cw, ch);
            Fill(c3, Panel);
            y = c3.y + 10f * k;
            Head(c3, "DAMAGE TAKEN" + (string.IsNullOrEmpty(_shown.PlayerName) ? "" : "   (" + _shown.PlayerName + ")"));
            Row(c3, "Rounds fired at you", s.Incoming.ToString(), Text1);
            Row(c3, "Hits taken", s.HitsTaken.ToString(), s.HitsTaken > 0 ? OnMe : Text1);
            Row(c3, "Penetrations taken", s.PensTaken.ToString(), s.PensTaken > 0 ? OnMe : Text1);
            y += 8f * k;
            void List(Rect c, string head, List<string> items, Color col)
            {
                if (items.Count == 0) return;
                Label(new Rect(c.x + 14f * k, y, c.width, 20f * k), head, _h2, Grey);
                y += 22f * k;
                foreach (var it in items.Take(8)) { Label(new Rect(c.x + 24f * k, y, c.width - 30f * k, lh), "•  " + it, _txt, col); y += lh; }
                y += 6f * k;
            }
            List(c3, "CREW KILLED / CRITICAL", s.CrewKilled, OnMe);
            List(c3, "CREW WOUNDED", s.CrewWounded, new Color(1f, 0.8f, 0.4f));
            List(c3, "DESTROYED", s.ModulesLost, OnMe);
            List(c3, "DAMAGED", s.ModulesDamaged, new Color(1f, 0.8f, 0.4f));
            if (s.HitsTaken == 0) Label(new Rect(c3.x + 14f * k, y, c3.width, lh), "Nothing hit you" + (s.Incoming > 0 ? $" ({s.Incoming} rounds missed)." : "."), _txt, Grey);
        }

        static void DrawPast()
        {
            float W = Screen.width, H = Screen.height, k = K, m = 24f * k;
            if (_sessions == null) _sessions = AarArchive.Sessions();
            Label(new Rect(m, 80f * k, W - 2 * m, 24f * k), "Missions saved in UserData\\HitCam (one folder per mission). Old hits replay on a box model of the vehicle: the game's meshes aren't saved.", _small, Grey);
            float ty = 110f * k, rowH = 34f * k, tw = W - 2 * m;
            Fill(new Rect(m, ty, tw, 24f * k), new Color(1f, 1f, 1f, 0.05f));
            Label(new Rect(m + 12f * k, ty, tw * 0.2f, 24f * k), "DATE", _h2, Grey);
            Label(new Rect(m + 12f * k + tw * 0.2f, ty, tw * 0.4f, 24f * k), "MISSION", _h2, Grey);
            Label(new Rect(m + 12f * k + tw * 0.6f, ty, tw * 0.15f, 24f * k), "RESULT", _h2, Grey);
            Label(new Rect(m + 12f * k + tw * 0.75f, ty, tw * 0.25f, 24f * k), "HITS / SHOTS", _h2, Grey);
            var list = new Rect(m, ty + 26f * k, tw, H - ty - 50f * k);
            var ev = Event.current;
            float content = _sessions.Count * rowH;
            if (ev.type == EventType.ScrollWheel && list.Contains(ev.mousePosition)) { _scroll += ev.delta.y * 22f * k; ev.Use(); }
            _scroll = Mathf.Clamp(_scroll, 0f, Mathf.Max(0f, content - list.height));
            if (_sessions.Count == 0) Label(new Rect(m + 12f * k, list.y + 8f * k, tw, 24f * k), "No saved missions yet (SaveRecords = true writes them).", _txt, Grey);
            GUI.BeginGroup(list);
            for (int i = Mathf.Max(0, Mathf.FloorToInt(_scroll / rowH)); i < _sessions.Count; i++)
            {
                float ry = i * rowH - _scroll;
                if (ry > list.height) break;
                var s = _sessions[i];
                var r = new Rect(0, ry, tw, rowH - 2f * k);
                bool hover = r.Contains(ev.mousePosition) && new Rect(0, 0, list.width, list.height).Contains(ev.mousePosition);
                bool live = (_mission?.Folder != null && s.Dir.TrimEnd('\\', '/') == _mission.Folder.TrimEnd('\\', '/')) || (_last?.Folder != null && s.Dir.TrimEnd('\\', '/') == _last.Folder.TrimEnd('\\', '/'));
                Fill(r, hover ? PanelHi : Panel);
                Label(new Rect(12f * k, ry, tw * 0.2f, rowH), s.When, _cell, Grey);
                Label(new Rect(12f * k + tw * 0.2f, ry, tw * 0.4f, rowH), s.Name + (live ? "   (in memory, full models)" : ""), _bold, Text1);
                string res = s.Info?.result ?? "-";
                Label(new Rect(12f * k + tw * 0.6f, ry, tw * 0.15f, rowH), res, _bold, res == "VICTORY" ? new Color(0.5f, 0.9f, 0.5f) : res == "DEFEAT" ? OnMe : Grey);
                Label(new Rect(12f * k + tw * 0.75f, ry, tw * 0.25f, rowH), s.Hits + (s.Info != null ? " / " + s.Info.shots : ""), _cell, Text1);
                if (hover && ev.type == EventType.MouseDown && ev.button == 0) { ev.Use(); GUI.EndGroup(); OpenSession(s); return; }
            }
            GUI.EndGroup();
        }

        // ------------------------------------------------------------------ replay

        static void DrawReplay()
        {
            float W = Screen.width, H = Screen.height, k = K;
            var rp = _replay;
            if (rp == null) { _view = View.Hits; return; }
            var rt = AarReplay.Texture;
            _viewport = new Rect(0, 0, W, H);
            if (Event.current.type == EventType.Repaint)
            {
                Fill(_viewport, Vis.Background);
                if (rt != null && rp.P != null) GUI.DrawTexture(_viewport, rt, ScaleMode.StretchToFill, false);
            }
            var e = rp.Entry;
            var rec = e.Rec;
            var p = rp.P;
            if (p != null && p.RedFlash > 0.01f) Fill(_viewport, new Color(0.85f, 0.08f, 0.05f, 0.35f * p.RedFlash));

            // top bar
            var top = new Rect(0, 0, W, 46f * k);
            Fill(top, new Color(0f, 0f, 0f, 0.55f));
            if (Event.current.type == EventType.Repaint) _uiRects.Add(top);
            Fill(new Rect(0, 0, 5f * k, top.height), e.OnPlayer ? OnMe : rec.PlayerShot ? Mine : Grey);
            string who = (rec.PlayerShot ? "You (" + rec.ShooterName + ")" : rec.ShooterName) + "  →  " + (e.OnPlayer ? "You (" + rec.TargetName + ")" : rec.TargetName);
            int idx = _order.IndexOf(e);
            string head = (idx >= 0 ? $"HIT {idx + 1} / {_order.Count}" : "HIT #" + rec.Id) + (e.MissionTime >= 0f ? "   ·   " + Clock(e.MissionTime) : "")
                + "   ·   " + who + "   ·   " + (rec.Ammo?.Name ?? "?") + (e.Range > 0f ? $"   ·   {e.Range:0} m" : "");
            Label(new Rect(18f * k, 0, W - 420f * k, top.height), head, _bold, Text1, true);
            float bw = 120f * k, bh = 30f * k;
            if (Button(new Rect(W - 16f * k - bw, 8f * k, bw, bh), "CLOSE  " + KeyCode)) { Close(); return; }
            if (Button(new Rect(W - 24f * k - 2 * bw - 20f * k, 8f * k, bw + 20f * k, bh), "BACK TO LIST")) { StopReplay(); return; }

            if (p == null)
            {
                Label(new Rect(40f * k, 80f * k, W - 80f * k, 30f * k), "This hit could not be replayed: " + rp.Error, _txt, OnMe);
                DrawControls(rp);
                return;
            }

            // verdict banner, WT style: appears when the round arrives
            if (p.TitleWall >= 0f && !string.IsNullOrEmpty(p.TitleShown))
            {
                float age = p.T - p.TitleWall;
                float a = Mathf.Clamp01(age / 0.15f);
                var br = new Rect(W * 0.25f, 58f * k, W * 0.5f, 52f * k);
                Fill(new Rect(W * 0.32f, br.y + 4f * k, W * 0.36f, br.height - 8f * k), new Color(0f, 0f, 0f, 0.45f * a));
                var c = p.TitleColor; c.a = a;
                Label(br, p.TitleShown, _banner, c, true);
            }
            if (rp.Schematic)
            {
                var nr = new Rect(18f * k, 56f * k, 520f * k, 40f * k);
                Fill(nr, new Color(0f, 0f, 0f, 0.5f));
                Label(new Rect(nr.x + 10f * k, nr.y + 2f * k, nr.width - 14f * k, 18f * k), "SIMPLIFIED MODEL", _h2, Accent);
                Label(new Rect(nr.x + 10f * k, nr.y + 19f * k, nr.width - 14f * k, 18f * k), e.Archived ? "Loaded from disk: hull parts, modules and crew drawn as boxes." : "The vehicle's meshes are gone: hull parts, modules and crew drawn as boxes.", _small, Grey);
            }

            // right panel: status + events
            float pw = 400f * k, px = W - pw - 16f * k, py = 120f * k;
            var shown = p.Shown.OrderBy(s => s.wall).ToList();
            int maxLines = Mathf.Max(4, Mathf.FloorToInt((H - py - 170f * k) / (21f * k)));
            int start = Mathf.Max(0, shown.Count - maxLines);
            float panelH = 62f * k + (shown.Count - start) * 21f * k;
            var pr = new Rect(px, py, pw, panelH);
            Fill(pr, new Color(0f, 0f, 0f, 0.5f));
            if (Event.current.type == EventType.Repaint) _uiRects.Add(pr);
            float cx = px + 8f * k, cy = py + 8f * k, chipW = 44f * k, chipH = 20f * k;
            foreach (var (cat, label, hp) in p.Debuffs())
            {
                if (cx + chipW > px + pw - 4f * k) break;
                Chip(new Rect(cx, cy, chipW, chipH), label, hp);
                cx += chipW + 3f * k;
            }
            var (alive, total) = p.CrewCount();
            if (total > 0)
            {
                float hp = alive == total ? 100f : alive == 0 ? 0f : alive * 100f / total <= 50f ? 20f : 60f;
                Chip(new Rect(px + 8f * k, cy + chipH + 4f * k, chipW * 1.6f, chipH), $"CREW {alive}/{total}", hp);
            }
            float ly = py + 58f * k;
            for (int i = start; i < shown.Count; i++)
            {
                var (ev, wall) = shown[i];
                float age = p.T - wall;
                Color c = Sev(ev.Severity);
                c = Color.Lerp(c, Color.white, Mathf.Clamp01(1f - age / 0.35f) * 0.6f);
                if (ev.Severity >= 3 && age < 0.6f) Fill(new Rect(px, ly, pw * Mathf.Clamp01(age / 0.4f), 20f * k), new Color(0.75f, 0.05f, 0.03f, 0.35f));
                Label(new Rect(px + 10f * k, ly, pw - 16f * k, 20f * k), "•  " + ev.Text, _small, c, true);
                ly += 21f * k;
            }
            if (shown.Count == 0) Label(new Rect(px + 10f * k, ly, pw - 16f * k, 20f * k), p.T < p.ImpactWall ? "round on its way..." : "-", _small, Grey);

            DrawControls(rp);
        }

        static void Chip(Rect r, string label, float hp)
        {
            Color c = hp <= 0f ? new Color(0.12f, 0.12f, 0.12f) : hp <= 25f ? new Color32(0xFD, 0x00, 0x01, 255) : hp <= 75f ? new Color32(0xF6, 0xB2, 0x36, 255) : new Color32(0x90, 0x9E, 0x35, 255);
            Fill(r, new Color(c.r, c.g, c.b, 0.85f));
            Label(r, label, _chip, Color.white);
            if (hp <= 0f) Fill(new Rect(r.x + 2, r.center.y - 1, r.width - 4, 2), new Color(1f, 0.2f, 0.15f));
        }

        static Color Sev(int s)
        {
            switch (s)
            {
                case 0: return new Color(0.78f, 0.8f, 0.84f);
                case 1: return new Color(1f, 0.86f, 0.4f);
                case 2: return new Color(1f, 0.6f, 0.2f);
                default: return new Color(1f, 0.3f, 0.25f);
            }
        }

        static void DrawControls(AarReplay rp)
        {
            float W = Screen.width, H = Screen.height, k = K;
            float bh = 34f * k, barH = 92f * k;
            var bar = new Rect(0, H - barH, W, barH);
            Fill(bar, new Color(0f, 0f, 0f, 0.62f));
            if (Event.current.type == EventType.Repaint) _uiRects.Add(bar);
            var p = rp.P;
            float x = 16f * k, y = bar.y + 46f * k;
            float b = 96f * k;
            int idx = _order.IndexOf(rp.Entry);
            if (Button(new Rect(x, y, b, bh), "◄ PREV", false, idx > 0)) { Step(-1); return; }
            x += b + 6f * k;
            if (Button(new Rect(x, y, b, bh), "RESTART")) { rp.Restart(); return; }
            x += b + 6f * k;
            bool atEnd = p != null && rp.Paused && p.T >= p.Duration - 1e-3f;
            if (Button(new Rect(x, y, b * 1.1f, bh), rp.Paused ? (atEnd ? "REPLAY" : "PLAY ►") : "PAUSE", !rp.Paused)) TogglePlay();
            x += b * 1.1f + 6f * k;
            if (Button(new Rect(x, y, b, bh), "NEXT ►", false, idx >= 0 && idx < _order.Count - 1)) { Step(+1); return; }
            x += b + 18f * k;

            // speed
            Label(new Rect(x, y, 56f * k, bh), "SPEED", _h2, Grey);
            x += 56f * k;
            float sw = 150f * k;
            float u = Mathf.InverseLerp(Mathf.Log(0.1f), Mathf.Log(2f), Mathf.Log(Mathf.Clamp(rp.Speed, 0.1f, 2f)));
            float nu = Slider(new Rect(x, y + bh * 0.5f - 3f * k, sw, 6f * k), u, 0f, 1f, 2, Accent);
            if (Mathf.Abs(nu - u) > 1e-4f)
            {
                float s = Mathf.Exp(Mathf.Lerp(Mathf.Log(0.1f), Mathf.Log(2f), nu));
                foreach (var snap in Speeds) if (Mathf.Abs(Mathf.Log(s / snap)) < 0.06f) s = snap;
                rp.Speed = s;
            }
            x += sw + 10f * k;
            Label(new Rect(x, y, 60f * k, bh), rp.Speed.ToString(rp.Speed < 1f ? "0.00" : "0.0") + "x", _bold, Text1);
            x += 64f * k;
            if (Button(new Rect(x, y, b * 1.25f, bh), rp.Free ? "FREE CAMERA" : "AUTO CAMERA", rp.Free)) { if (rp.Free) rp.Free = false; else rp.StartFreeCam(); }
            x += b * 1.25f + 12f * k;
            Label(new Rect(x, y, W - x - 16f * k, bh), "drag: orbit   ·   right-drag: pan   ·   wheel: zoom   ·   Space   ← →   ↑ ↓   R   C   Backspace", _small, Grey);

            // scrub bar with the impact, the penetration slow motion and the end of the interior marked
            if (p == null) return;
            var track = new Rect(16f * k, bar.y + 22f * k, W - 32f * k - 120f * k, 8f * k);
            float dur = Mathf.Max(p.Duration, 0.01f);
            Fill(new Rect(track.x + track.width * (p.ImpactWall / dur), track.y - 4f * k, Mathf.Max(2f, track.width * (Mathf.Max(p.Dwell, 0.05f) / dur)), track.height + 8f * k), new Color(1f, 0.25f, 0.2f, 0.45f));
            Fill(new Rect(track.x + track.width * ((p.A + p.B) / dur) - 1f, track.y - 4f * k, 2f, track.height + 8f * k), new Color(1f, 1f, 1f, 0.35f));
            float t = Slider(track, p.T, 0f, dur, 1, new Color(1f, 0.85f, 0.55f, 0.85f));
            if (Mathf.Abs(t - p.T) > 1e-4f) { rp.Paused = rp.Paused || _sliderHot == 1; rp.SeekTo(t); }
            Label(new Rect(track.xMax + 12f * k, track.y - 8f * k, 110f * k, 24f * k), $"{p.T:0.00} / {dur:0.00} s" + (rp.Seeking ? " ..." : ""), _small, Text1);
        }
    }
}
