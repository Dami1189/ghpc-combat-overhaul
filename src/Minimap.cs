using System;
using System.Collections.Generic;
using GHPC;
using GHPC.UI.Hud.Objectives;
using GHPC.Utility;
using HarmonyLib;
using UnityEngine;

namespace GHPCHitCam
{
    /// Corner map, War Thunder style: you (hull arrow, view cone, gun line), every unit as its class icon (MapIcons: light /
    /// medium / heavy tank, tank destroyer, SPAA, IFV, truck, infantry, anti-tank team, emplacement, helicopter, plane), blue
    /// friendly / red enemy / grey with a cross when destroyed. Enemies by MinimapEnemies (Spotted = Contacts' spotting,
    /// Always, Off). The map is cut into 8 x 8 lettered squares (A-H from the north, 1-8 from the west), mission objectives
    /// with a map marker are lettered circles. North-up or player-up (MinimapPlayerUp), zoom keys. Scale from the game's own
    /// map bounds (GHPC.UI.TopoMapCamera.bounds), else the terrain tiles; under it a one-time top-down picture of the map
    /// (MinimapTerrain). Drawn with GL into a small render texture; labels with IMGUI over it.
    internal static class Minimap
    {
        static RenderTexture _rt;
        static Texture2D _terrain;
        static Material _lines, _tex;
        static Bounds _map;
        static bool _hasMap, _mapTried, _pg;
        static readonly float[] Zooms = { 250f, 500f, 1000f, 2000f, 4000f };   // half-width in metres
        static int _zoom = 2;
        static float _nextRender;
        const int N = 320;

        public static float HalfWidth => Zooms[Mathf.Clamp(_zoom, 0, Zooms.Length - 1)];
        internal static int Zoom { get => _zoom; set => _zoom = Mathf.Clamp(value, 0, Zooms.Length - 1); }

        public static void Reset()
        {
            _hasMap = false;
            _mapTried = false;
            if (_terrain != null) UnityEngine.Object.Destroy(_terrain);
            _terrain = null;
        }

        public static void Update()
        {
            if (Hotkeys.Down(Settings.MinimapZoomInKey)) _zoom = Mathf.Max(0, _zoom - 1);
            if (Hotkeys.Down(Settings.MinimapZoomOutKey)) _zoom = Mathf.Min(Zooms.Length - 1, _zoom + 1);
            // the proving ground's deck appears (or goes): picture the map again so the deck is on it
            if (ProvingGround.Active != _pg) { _pg = ProvingGround.Active; Reset(); }
            if (!_mapTried) { _mapTried = true; FindMap(); }
            if (Time.unscaledTime >= _nextObjectives) { _nextObjectives = Time.unscaledTime + 1f; FindObjectives(); }
            if (Time.unscaledTime < _nextRender) return;
            _nextRender = Time.unscaledTime + 1f / 30f;
            try { Render(); } catch (Exception e) { HitCamMod.Log.Warning("Minimap render: " + e.Message); _nextRender = Time.unscaledTime + 5f; }
        }

        // ------------------------------------------------------------------ objectives

        internal struct Objective { public Vector3 Pos; public string Letter; public ObjectiveState State; public string Text; }
        internal static readonly List<Objective> Objectives = new List<Objective>();
        static float _nextObjectives;
        static AccessTools.FieldRef<ObjectiveManager, IDictionary<int, ObjectiveListItem>> _listRefs;
        static AccessTools.FieldRef<ObjectiveListItem, ObjectiveState> _itemState;
        static bool _objReflectFailed;
        static string _objKey = "";

        /// The mission's objectives that have a marker in the world (ObjectiveData.LinkedMarker, what the tactical map
        /// centres on), with their state from the objective list (shown ones only).
        static void FindObjectives()
        {
            Objectives.Clear();
            if (!Settings.MinimapObjectives.Value || ArcadeHud.Player == null) return;
            try
            {
                var om = ObjectiveManager.Instance;
                if (om == null || !om.IsInitialized) return;
                var data = om.ActiveObjectiveData;
                if (data == null) return;
                IDictionary<int, ObjectiveListItem> refs = null;
                if (!_objReflectFailed)
                {
                    try
                    {
                        _listRefs ??= AccessTools.FieldRefAccess<ObjectiveManager, IDictionary<int, ObjectiveListItem>>("_listReferences");
                        _itemState ??= AccessTools.FieldRefAccess<ObjectiveListItem, ObjectiveState>("_state");
                        refs = _listRefs(om);
                    }
                    catch { _objReflectFailed = true; }
                }
                char letter = 'A';
                for (int i = 0; i < data.Length; i++)
                {
                    var d = data[i];
                    if (d == null || d.LinkedMarker == null) continue;
                    var st = ObjectiveState.Active;
                    if (refs != null && refs.TryGetValue(i, out var item) && item != null) st = _itemState(item);
                    else if (!d.LinkedMarker.activeInHierarchy) continue;
                    if (st == ObjectiveState.Inactive || st == ObjectiveState.Removed) continue;
                    Objectives.Add(new Objective { Pos = d.LinkedMarker.transform.position, Letter = letter.ToString(), State = st, Text = d.ObjectiveText });
                    if (letter < 'Z') letter++;
                }
                if (AutoTest.Active)
                {
                    string key = string.Join(" | ", Objectives.ConvertAll(o => $"{o.Letter} {o.State} '{o.Text}' at {o.Pos:F0}"));
                    if (key != _objKey) { _objKey = key; HitCamMod.Log.Msg($"Minimap: {Objectives.Count} objectives with a map marker (of {data.Length}){(key.Length > 0 ? ": " + key : "")}"); }
                }
            }
            catch (Exception e) { if (AutoTest.Active) HitCamMod.Log.Warning("Minimap objectives: " + e.Message); }
        }

        // ------------------------------------------------------------------ the map's extent and picture

        static void FindMap()
        {
            try
            {
                var topo = UnityEngine.Object.FindObjectOfType<GHPC.UI.TopoMapCamera>(true);
                if (topo != null && topo.bounds.size.x > 50f)
                {
                    _map = topo.bounds;
                    // the tactical map pans in +-extents around the origin
                    _map.center = new Vector3(0f, _map.center.y, 0f);
                    _hasMap = true;
                }
            }
            catch { }
            if (!_hasMap)
            {
                foreach (var t in Terrain.activeTerrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    var sz = t.terrainData.size;
                    if (sz.x >= 16384f || sz.z >= 16384f) continue;   // background terrains (VegProximityManager skips them too)
                    var b = new Bounds(t.transform.position + sz * 0.5f, sz);
                    if (!_hasMap) { _map = b; _hasMap = true; } else _map.Encapsulate(b);
                }
            }
            HitCamMod.Log.Msg(_hasMap ? $"Minimap: map {(_map.center - _map.extents):F0} .. {(_map.center + _map.extents):F0}" : "Minimap: no map bounds, grid only");
            if (_hasMap && Settings.MinimapTerrain.Value) BuildTerrain();
        }

        /// One top-down picture of the whole map: the terrain from far above, rendered once.
        static void BuildTerrain()
        {
            GameObject go = null;
            RenderTexture rt = null;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                int size = 1024;
                go = new GameObject("hitcam minimap camera");
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(_map.extents.x, _map.extents.z);
                cam.aspect = 1f;
                float top = 3000f;
                foreach (var t in Terrain.activeTerrains)
                    if (t != null && t.terrainData != null) top = Mathf.Max(top, t.transform.position.y + t.terrainData.size.y + 500f);
                cam.transform.position = new Vector3(_map.center.x, top, _map.center.z);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // looking down, +z (north) up
                cam.nearClipPlane = 10f;
                cam.farClipPlane = top + 2000f;
                cam.renderingPath = RenderingPath.Forward;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.06f, 0.07f, 1f);
                cam.cullingMask = 1 << CodeUtils.LAYER_INDEX_TERRAIN;
                cam.allowMSAA = false;
                cam.allowHDR = false;
                rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                float fog = RenderSettings.fogDensity;
                bool fogOn = RenderSettings.fog;
                RenderSettings.fog = false;
                try { cam.Render(); }
                finally { RenderSettings.fog = fogOn; RenderSettings.fogDensity = fog; }
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.RGB24, true) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply(true);
                RenderTexture.active = prev;
                // a flat picture (nothing drawn) is useless: keep the plain grid then
                var px = tex.GetPixels32(4);
                float min = 999f, max = -1f;
                foreach (var p in px) { float l = p.r + p.g + p.b; min = Mathf.Min(min, l); max = Mathf.Max(max, l); }
                if (max - min > 30f) _terrain = tex;
                else UnityEngine.Object.Destroy(tex);
                HitCamMod.Log.Msg($"Minimap: terrain picture {(_terrain != null ? "ok" : "empty, using the grid")} ({(Time.realtimeSinceStartup - t0) * 1000f:0} ms)");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Minimap: terrain picture failed: " + e.Message); }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            }
        }

        // ------------------------------------------------------------------ rendering

        static void Mats()
        {
            if (_lines != null) return;
            _lines = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            _lines.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lines.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lines.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _lines.SetInt("_ZWrite", 0);
            _lines.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            var sp = Shader.Find("Sprites/Default");
            if (sp != null) _tex = new Material(sp) { hideFlags = HideFlags.HideAndDontSave };
        }

        // map frame for this render
        static Vector3 _c;
        static float _s, _cos = 1f, _sin;

        /// World x/z to minimap pixels (y up, north up unless player-up).
        static Vector2 P(Vector3 w)
        {
            float x = w.x - _c.x, z = w.z - _c.z;
            float rx = x * _cos - z * _sin, rz = x * _sin + z * _cos;
            return new Vector2(N * 0.5f + rx * _s, N * 0.5f + rz * _s);
        }

        /// A world direction (x/z) rotated into the map.
        static Vector2 D(Vector3 d)
        {
            float rx = d.x * _cos - d.z * _sin, rz = d.x * _sin + d.z * _cos;
            var v = new Vector2(rx, rz);
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector2.up;
        }

        static void Render()
        {
            var player = ArcadeHud.Player;
            if (player == null) return;
            Mats();
            if (_rt == null)
            {
                _rt = new RenderTexture(N, N, 0, RenderTextureFormat.ARGB32) { antiAliasing = 4, hideFlags = HideFlags.HideAndDontSave };
                _rt.Create();
            }
            _c = player.transform.position;
            _s = N * 0.5f / HalfWidth;
            if (Settings.MinimapPlayerUp.Value)
            {
                Vector3 f = player.transform.forward;
                float yaw = Mathf.Atan2(f.x, f.z);           // clockwise from north; turning the map back by it puts the hull up
                _sin = Mathf.Sin(yaw);
                _cos = Mathf.Cos(yaw);
            }
            else { _cos = 1f; _sin = 0f; }

            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, N, 0, N);
            GL.Clear(false, true, new Color(0.04f, 0.05f, 0.06f, 0.92f));

            // terrain picture over the map's extent
            if (_terrain != null && _tex != null && _hasMap)
            {
                _tex.mainTexture = _terrain;
                _tex.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(new Color(0.62f, 0.66f, 0.62f, 1f));
                Vector3 mn = _map.center - _map.extents, mx = _map.center + _map.extents;
                Quad(new Vector3(mn.x, 0, mn.z), new Vector3(mx.x, 0, mn.z), new Vector3(mx.x, 0, mx.z), new Vector3(mn.x, 0, mx.z));
                GL.End();
            }

            _lines.SetPass(0);
            // grid, world-aligned
            float g = HalfWidth <= 300f ? 100f : HalfWidth <= 600f ? 250f : HalfWidth <= 1200f ? 500f : 1000f;
            float reach = HalfWidth * 1.5f;
            GL.Begin(GL.LINES);
            GL.Color(new Color(0.55f, 0.7f, 0.65f, _terrain != null ? 0.22f : 0.16f));
            for (float x = Mathf.Floor((_c.x - reach) / g) * g; x <= _c.x + reach; x += g)
                Seg(new Vector3(x, 0, _c.z - reach), new Vector3(x, 0, _c.z + reach));
            for (float z = Mathf.Floor((_c.z - reach) / g) * g; z <= _c.z + reach; z += g)
                Seg(new Vector3(_c.x - reach, 0, z), new Vector3(_c.x + reach, 0, z));
            // the map's 8 x 8 lettered squares (War Thunder's grid), a little brighter
            if (_hasMap)
            {
                float sq = Square;
                Vector3 mn0 = _map.center - _map.extents;
                GL.Color(new Color(0.85f, 0.9f, 0.85f, 0.3f));
                for (int i = 1; i < 8; i++)
                {
                    float x = mn0.x + i * sq, z = mn0.z + i * sq;
                    Seg(new Vector3(x, 0, mn0.z), new Vector3(x, 0, mn0.z + 8f * sq));
                    Seg(new Vector3(mn0.x, 0, z), new Vector3(mn0.x + 8f * sq, 0, z));
                }
            }
            // map edge
            if (_hasMap)
            {
                GL.Color(new Color(0.9f, 0.75f, 0.35f, 0.55f));
                Vector3 mn = _map.center - _map.extents, mx = _map.center + _map.extents;
                Seg(new Vector3(mn.x, 0, mn.z), new Vector3(mx.x, 0, mn.z));
                Seg(new Vector3(mx.x, 0, mn.z), new Vector3(mx.x, 0, mx.z));
                Seg(new Vector3(mx.x, 0, mx.z), new Vector3(mn.x, 0, mx.z));
                Seg(new Vector3(mn.x, 0, mx.z), new Vector3(mn.x, 0, mn.z));
            }
            // where the gun points: a faint line to the aim point
            if (ArcadeHud.HasAim)
            {
                GL.Color(new Color(1f, 1f, 1f, 0.35f));
                Seg(_c, ArcadeHud.AimPoint);
            }
            GL.End();

            float u = Mathf.Max(N / 320f, 0.5f);
            Vector2 mid = new Vector2(N * 0.5f, N * 0.5f);
            // your view: a faint cone along the camera
            var cam = ArcadeHud.Cam;
            if (cam != null)
            {
                Vector3 cf = cam.transform.forward;
                cf.y = 0f;
                if (cf.sqrMagnitude > 1e-4f)
                {
                    float half = Mathf.Atan(Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.aspect);
                    half = Mathf.Clamp(half, 0.02f, 1.3f);
                    Vector2 d = D(cf);
                    float len = N * 0.36f;
                    Vector2 a = Rot(d, half) * len, b = Rot(d, -half) * len;
                    GL.Begin(GL.TRIANGLES);
                    GL.Color(new Color(1f, 1f, 1f, 0.075f));
                    GL.Vertex3(mid.x, mid.y, 0); GL.Vertex3(mid.x + a.x, mid.y + a.y, 0); GL.Vertex3(mid.x + b.x, mid.y + b.y, 0);
                    GL.End();
                }
            }

            // every unit as its class icon
            _drawn.Clear();
            string mode = Settings.MinimapEnemies.Value ?? "Spotted";
            bool always = mode.Equals("Always", StringComparison.OrdinalIgnoreCase), off = mode.Equals("Off", StringComparison.OrdinalIgnoreCase);
            float now = Time.time, memory = Mathf.Max(Settings.MarkerMemorySeconds.Value, 0.1f);
            Color blue = new Color(0.38f, 0.72f, 1f, 0.95f), grey = new Color(0.62f, 0.62f, 0.62f, 0.75f);
            foreach (var c in Contacts.Friends)
                if (c.Unit != null && c.Unit != player && (!c.Destroyed || now - c.DestroyedAt < 30f)) _drawn.Add((c, c.Destroyed ? grey : blue));
            if (!off)
                foreach (var c in Contacts.All)
                {
                    if (c.Unit == null) continue;
                    if (c.Destroyed)
                    {
                        if ((always ? c.DestroyedAt > 0f : c.Shown) && now - c.DestroyedAt < 30f) _drawn.Add((c, grey));
                        continue;
                    }
                    if (!always && !c.Shown) continue;
                    float a = c.SeenNow || always ? 1f : Mathf.Lerp(0.85f, 0.3f, Mathf.Clamp01((now - c.LastSeen) / memory));
                    _drawn.Add((c, new Color(1f, 0.25f, 0.2f, a)));
                }
            if (Settings.MinimapInfantry.Value)
                foreach (var c in Contacts.Troops)
                {
                    if (c.Unit == null || c.Destroyed) continue;
                    if (!c.Friendly && (off || (!always && !c.Shown))) continue;
                    _drawn.Add((c, c.Friendly ? blue : new Color(1f, 0.25f, 0.2f, c.SeenNow || always ? 1f : 0.6f)));
                }
            if (_drawn.Count > 0 && _tex != null)
            {
                _tex.mainTexture = MapIcons.Atlas;
                _tex.SetPass(0);
                GL.Begin(GL.QUADS);
                foreach (var (c, col) in _drawn)
                {
                    bool small = c.Class == MapClass.Infantry || c.Class == MapClass.AntiTankTeam;
                    Icon(P(c.Center), (small ? 5.8f : 9f) * u, c.Class, col);
                }
                GL.End();
                _lines.SetPass(0);
            }
            GL.Begin(GL.LINES);
            foreach (var (c, col) in _drawn)
            {
                if (!c.Destroyed) continue;
                Vector2 q = P(c.Center);
                float r = 5f * u;
                GL.Color(new Color(0.9f, 0.9f, 0.9f, 0.8f));
                GL.Vertex3(q.x - r, q.y - r, 0); GL.Vertex3(q.x + r, q.y + r, 0);
                GL.Vertex3(q.x - r, q.y + r, 0); GL.Vertex3(q.x + r, q.y - r, 0);
            }
            GL.End();
            // you: hull arrow
            GL.Begin(GL.TRIANGLES);
            Arrow(mid, D(player.transform.forward), 8f * u, new Color(1f, 0.92f, 0.45f, 1f));
            GL.End();

            GL.Begin(GL.LINES);
            // you: gun line
            var gun = ArcadeHud.Gun;
            Transform mz = gun != null ? gun.MuzzleIdentity : null;
            if (mz != null)
            {
                Vector2 d = D(mz.forward);
                Vector2 o = new Vector2(N * 0.5f, N * 0.5f);
                GL.Color(new Color(1f, 0.92f, 0.45f, 1f));
                for (int k = -1; k <= 1; k++)
                {
                    Vector2 side = new Vector2(-d.y, d.x) * (k * 0.5f);
                    GL.Vertex3(o.x + side.x, o.y + side.y, 0);
                    GL.Vertex3(o.x + side.x + d.x * 18f * u, o.y + side.y + d.y * 18f * u, 0);
                }
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        static readonly List<(Contacts.Contact c, Color col)> _drawn = new List<(Contacts.Contact, Color)>();

        /// The side of one of the map's 8 x 8 squares, in metres.
        static float Square => Mathf.Max(_map.extents.x, _map.extents.z) * 2f / 8f;

        static Vector2 Rot(Vector2 v, float a)
        {
            float cs = Mathf.Cos(a), sn = Mathf.Sin(a);
            return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        /// A class icon from the atlas, upright, centred on p (map pixels).
        static void Icon(Vector2 p, float h, MapClass cls, Color c)
        {
            var uv = MapIcons.Uv(cls);
            GL.Color(c);
            GL.TexCoord2(uv.xMin, uv.yMin); GL.Vertex3(p.x - h, p.y - h, 0);
            GL.TexCoord2(uv.xMax, uv.yMin); GL.Vertex3(p.x + h, p.y - h, 0);
            GL.TexCoord2(uv.xMax, uv.yMax); GL.Vertex3(p.x + h, p.y + h, 0);
            GL.TexCoord2(uv.xMin, uv.yMax); GL.Vertex3(p.x - h, p.y + h, 0);
        }

        /// Which of the 8 x 8 squares a point is in ("C4": rows A-H from the north, columns 1-8 from the west).
        public static string SquareName(Vector3 w)
        {
            if (!_hasMap) return "";
            float sq = Square;
            Vector3 mn = _map.center - _map.extents;
            int col = Mathf.Clamp(Mathf.FloorToInt((w.x - mn.x) / sq), 0, 7);
            int row = Mathf.Clamp(Mathf.FloorToInt((mn.z + 8f * sq - w.z) / sq), 0, 7);
            return ((char)('A' + row)).ToString() + (col + 1);
        }

        static void Seg(Vector3 a, Vector3 b)
        {
            Vector2 pa = P(a), pb = P(b);
            GL.Vertex3(pa.x, pa.y, 0);
            GL.Vertex3(pb.x, pb.y, 0);
        }

        static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector2 pa = P(a), pb = P(b), pc = P(c), pd = P(d);
            GL.TexCoord2(0, 0); GL.Vertex3(pa.x, pa.y, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(pb.x, pb.y, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(pc.x, pc.y, 0);
            GL.TexCoord2(0, 1); GL.Vertex3(pd.x, pd.y, 0);
        }

        /// A filled arrowhead at p pointing along d (map pixels).
        static void Arrow(Vector2 p, Vector2 d, float r, Color c)
        {
            Vector2 side = new Vector2(-d.y, d.x);
            Vector2 tip = p + d * r * 1.3f, l = p - d * r * 0.8f + side * r * 0.85f, rr = p - d * r * 0.8f - side * r * 0.85f, notch = p - d * r * 0.35f;
            GL.Color(new Color(0f, 0f, 0f, 0.6f * c.a));
            Vector2 o = new Vector2(0.8f, -0.8f);
            GL.Vertex3(tip.x + o.x, tip.y + o.y, 0); GL.Vertex3(l.x + o.x, l.y + o.y, 0); GL.Vertex3(notch.x + o.x, notch.y + o.y, 0);
            GL.Vertex3(tip.x + o.x, tip.y + o.y, 0); GL.Vertex3(notch.x + o.x, notch.y + o.y, 0); GL.Vertex3(rr.x + o.x, rr.y + o.y, 0);
            GL.Color(c);
            GL.Vertex3(tip.x, tip.y, 0); GL.Vertex3(l.x, l.y, 0); GL.Vertex3(notch.x, notch.y, 0);
            GL.Vertex3(tip.x, tip.y, 0); GL.Vertex3(notch.x, notch.y, 0); GL.Vertex3(rr.x, rr.y, 0);
        }

        // ------------------------------------------------------------------ on screen

        static string _scale = "", _sqShown = "", _sqText = "";
        static int _scaleFor = -1;
        static readonly string[] Letters = { "A", "B", "C", "D", "E", "F", "G", "H" }, Digits = { "1", "2", "3", "4", "5", "6", "7", "8" };

        public static void Draw(float k)
        {
            if (_rt == null || ArcadeHud.Player == null) return;
            float size = Mathf.Clamp(Settings.MinimapSize.Value, 0.1f, 0.6f) * Screen.height;
            float m = 14f;
            string corner = Settings.MinimapCorner.Value ?? "BottomRight";
            bool right = !corner.Contains("Left");
            bool top = corner.Contains("Top");
            float x = right ? Screen.width - size - m : m;
            float y = top ? m : Screen.height - size - m;
            var r = new Rect(x, y, size, size);
            HudGfx.Box(new Rect(x - 2, y - 2, size + 4, size + 4), new Color(0f, 0f, 0f, 0.7f));
            GUI.color = Color.white;
            GUI.DrawTexture(r, _rt, ScaleMode.StretchToFill, true);
            // north marker on the frame, player-up only (north-up has the lettered squares instead)
            if (Settings.MinimapPlayerUp.Value)
            {
                Vector2 c = r.center;
                Vector2 n = new Vector2(-_sin, -_cos);   // world north (0,1) through the map rotation, GUI y down
                float t = Mathf.Min(Mathf.Abs(size * 0.5f / Mathf.Max(Mathf.Abs(n.x), 1e-3f)), Mathf.Abs(size * 0.5f / Mathf.Max(Mathf.Abs(n.y), 1e-3f)));
                Vector2 np = c + n * (t - 10f * k);
                HudGfx.Text(new Rect(np.x - 10f * k, np.y - 9f * k, 20f * k, 18f * k), "N", HudGfx.Center, new Color(1f, 0.9f, 0.6f, 0.95f));
            }
            // map pixels -> screen
            Vector2 G(Vector2 pm) => new Vector2(x + pm.x / N * size, y + (N - pm.y) / N * size);
            var lf = HudGfx.Font(10f * k, TextAnchor.MiddleCenter, true);
            // the squares' letters and numbers along the top and left edges (north-up only: they stay put)
            if (_hasMap && !Settings.MinimapPlayerUp.Value)
            {
                float sq = Square;
                Vector3 mn = _map.center - _map.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector2 col = G(P(new Vector3(mn.x + (i + 0.5f) * sq, 0f, _c.z)));
                    if (col.x > x + 12f * k && col.x < x + size - 12f * k)
                        HudGfx.Text(new Rect(col.x - 10f * k, y + 2f * k, 20f * k, 13f * k), Digits[i], lf, new Color(0.92f, 0.95f, 0.9f, 0.8f));
                    Vector2 row = G(P(new Vector3(_c.x, 0f, mn.z + (7.5f - i) * sq)));
                    if (row.y > y + 16f * k && row.y < y + size - 22f * k)
                        HudGfx.Text(new Rect(x + 2f * k, row.y - 7f * k, 14f * k, 14f * k), Letters[i], lf, new Color(0.92f, 0.95f, 0.9f, 0.8f));
                }
            }
            // objectives: lettered circles (on the edge when off the map)
            if (Settings.MinimapObjectives.Value)
                foreach (var o in Objectives)
                {
                    Vector2 q = G(P(o.Pos));
                    float m2 = 9f * k;
                    q.x = Mathf.Clamp(q.x, x + m2, x + size - m2);
                    q.y = Mathf.Clamp(q.y, y + m2, y + size - m2);
                    Color oc = o.State == ObjectiveState.Pass ? new Color(0.4f, 0.95f, 0.45f, 0.95f) : o.State == ObjectiveState.Fail ? new Color(0.6f, 0.6f, 0.6f, 0.9f) : new Color(1f, 0.85f, 0.35f, 0.95f);
                    HudGfx.Ring(q, 7.5f * k, new Color(0f, 0f, 0f, 0.6f));
                    HudGfx.Ring(q, 6.8f * k, oc);
                    HudGfx.Text(new Rect(q.x - 8f * k, q.y - 7f * k, 16f * k, 14f * k), o.Letter, lf, oc);
                }
            if (_scaleFor != _zoom)
            {
                _scaleFor = _zoom;
                float g = HalfWidth <= 300f ? 100f : HalfWidth <= 600f ? 250f : HalfWidth <= 1200f ? 500f : 1000f;
                _scale = $"grid {(g >= 1000f ? (g / 1000f).ToString("0") + " km" : g.ToString("0") + " m")}   ·   {Hotkeys.Label(Settings.MinimapZoomInKey)} / {Hotkeys.Label(Settings.MinimapZoomOutKey)}";
            }
            string sqName = SquareName(_c);
            if (sqName != _sqShown) { _sqShown = sqName; _sqText = sqName.Length > 0 ? sqName + "   ·   " : ""; }
            HudGfx.Text(new Rect(x + 6f * k, y + size - 18f * k, size - 12f * k, 16f * k), _sqText + _scale, HudGfx.Small, new Color(0.85f, 0.88f, 0.9f, 0.8f));
        }
    }
}
