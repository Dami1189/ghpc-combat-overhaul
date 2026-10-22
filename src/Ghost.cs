using System.Collections.Generic;
using System.Linq;
using GHPC.Humans;
using UnityEngine;
using UnityEngine.Rendering;

namespace GHPCHitCam
{
    /// materials, the private render layers and the colour scheme (War Thunder x-ray look, see WT_VISUAL_SPEC.md)
    internal static class Vis
    {
        public static int Layer = -1;      // the x-ray copy
        public static int DimLayer = -1;   // dark veil only the hit cam's world camera sees
        // where the current x-ray copy lives: high above the hit vehicle. has to stay inside the map's physics broadphase
        // bounds (far below the map nothing collides), own physics scene keeps it away from the game, game cams don't draw its layer
        public static Vector3 Origin = new Vector3(0f, 400f, 0f);
        public static Material Hull, HullFill, HullEdge, Solid, Outline, Unlit, Additive, Line, Panel, Alpha, Veil, Through, ThroughHidden;
        static bool _init;
        static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();

        public static void Init()
        {
            if (_init) return;
            _init = true;
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { if (Layer < 0) Layer = i; else { DimLayer = i; break; } }
            if (Layer < 0) Layer = 31;
            if (DimLayer < 0) DimLayer = Layer - 1;
            // project may have self-collision of unused layers off, the x-ray physics needs it (nothing else uses this layer)
            bool wasOff = Physics.GetIgnoreLayerCollision(Layer, Layer);
            Physics.IgnoreLayerCollision(Layer, Layer, false);
            if (wasOff) HitCamMod.Log.Msg("Layer " + Layer + " self-collision was off in the game's settings; switched on for the x-ray physics");

            Shader colored = Shader.Find("Hidden/Internal-Colored");
            Shader diffuse = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Standard");
            Shader sprites = Shader.Find("Sprites/Default");

            // light-blue glass (old "Blueprint" style)
            Hull = Make(colored, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false, CullMode.Off, 3000);
            Hull.color = new Color(0.62f, 0.72f, 0.86f, 0.13f);

            // WT: opaque near-black hull that never hides the internals (no depth write) ...
            HullFill = Make(colored, BlendMode.One, BlendMode.Zero, false, CullMode.Off, 2000);
            HullFill.color = HullBlack;
            // ... with a thin light edge: an inflated copy drawn before the fill so only its rim shows
            HullEdge = Make(colored, BlendMode.One, BlendMode.Zero, false, CullMode.Off, 1990);
            HullEdge.color = new Color(0.62f, 0.66f, 0.72f);

            // clay-grey internals, lit by our own lights, drawn over the hull
            Solid = diffuse != null ? new Material(diffuse) : Make(colored, BlendMode.One, BlendMode.Zero, true, CullMode.Back, 2010);
            Solid.color = Color.white;
            Solid.renderQueue = WtStyle ? 2010 : 2000;

            // inverted-hull outline (back faces of a slightly bigger copy): red for damage, white glow for crew
            Outline = Make(colored, BlendMode.One, BlendMode.Zero, true, CullMode.Front, 2012);

            Unlit = Make(colored, BlendMode.One, BlendMode.Zero, true, CullMode.Off, WtStyle ? 2015 : 2450);
            Additive = Make(colored, BlendMode.One, BlendMode.One, false, CullMode.Off, 3100);
            Alpha = Make(colored, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false, CullMode.Off, 3060);
            Panel = Make(colored, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false, CullMode.Off, 3030);
            Veil = Make(colored, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false, CullMode.Off, 4000);
            Veil.SetInt("_ZTest", (int)CompareFunction.Always);
            // long rod inside the armour: drawn over it, like a flash radiograph shows it
            Through = Make(colored, BlendMode.One, BlendMode.Zero, false, CullMode.Off, 3150);
            Through.SetInt("_ZTest", (int)CompareFunction.Always);
            // the bit of something hidden behind armour or a module, faint (soft-body rod inside the plate)
            ThroughHidden = Make(colored, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false, CullMode.Back, 3160);
            ThroughHidden.SetInt("_ZTest", (int)CompareFunction.Greater);
            ThroughHidden.color = new Color(1f, 1f, 1f, 0.5f);

            Line = new Material(sprites != null ? sprites : colored);
            if (sprites == null) SetBlend(Line, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false, CullMode.Off);
            Line.renderQueue = 3050;
        }

        static Material Make(Shader s, BlendMode src, BlendMode dst, bool zwrite, CullMode cull, int queue)
        {
            var m = new Material(s) { renderQueue = queue };
            SetBlend(m, src, dst, zwrite, cull);
            return m;
        }

        static void SetBlend(Material m, BlendMode src, BlendMode dst, bool zwrite, CullMode cull)
        {
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_ZWrite", zwrite ? 1 : 0);
            m.SetInt("_Cull", (int)cull);
            m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        }

        public static void SetColor(Renderer r, Color c)
        {
            if (r == null) return;
            _mpb.Clear();
            _mpb.SetColor("_Color", c);
            r.SetPropertyBlock(_mpb);
        }

        /// matrix as a transform's position, rotation and scale. a mirrored one keeps its mirror in the scale (m.rotation of
        /// it turned the part round its pivot, a Leopard 2KA3's rubber skirts came out 18 m off the tank). false when it's
        /// skewed too (unevenly scaled parent, turned child), a transform can't hold that
        public static bool ToTrs(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            pos = m.GetColumn(3);
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            float sx = x.magnitude, sy = y.magnitude, sz = z.magnitude;
            rot = Quaternion.identity;
            scale = new Vector3(sx, sy, sz);
            if (sx < 1e-8f || sy < 1e-8f || sz < 1e-8f) { rot = m.rotation; scale = m.lossyScale; return false; }
            float big = Mathf.Max(sx, Mathf.Max(sy, sz));
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f) sx = -sx;
            scale = new Vector3(sx, sy, sz);
            rot = Quaternion.LookRotation(z / sz, y / sy);
            var back = Matrix4x4.TRS(pos, rot, scale);
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    if (Mathf.Abs(back[r, c] - m[r, c]) > 1e-3f * big) return false;
            return true;
        }

        /// how far (m) the mesh's box corners land off when the matrix goes through ToTrs
        public static float TrsError(Matrix4x4 m, Bounds mb)
        {
            ToTrs(m, out var pos, out var rot, out var scale);
            var back = Matrix4x4.TRS(pos, rot, scale);
            float err = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                err = Mathf.Max(err, (back.MultiplyPoint3x4(c) - m.MultiplyPoint3x4(c)).magnitude);
            }
            return err;
        }

        public static GameObject Child(Transform parent, string name, Mesh mesh, Matrix4x4 m, Material mat)
        {
            var go = new GameObject(name);
            go.layer = Layer;
            go.transform.SetParent(parent, false);
            // skewed ones come out a bit off: snapshot parts that'd be off by more than ~2 cm got flattened before this
            ToTrs(m, out var pos, out var rot, out var scale);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            Quiet(mr);
            mr.sharedMaterial = mat;
            return go;
        }

        /// copy of a renderer's mesh grown by ~`width` m on every axis (about the mesh centre), as its child.
        /// with back-face-only materials it's an outline, drawn before an opaque fill it's an edge
        public static Renderer Shell(Transform owner, Mesh mesh, Material mat, float width)
        {
            var b = mesh.bounds;
            Vector3 ls = owner.lossyScale;
            Vector3 s = new Vector3(
                Mathf.Clamp(1f + width / Mathf.Max(b.extents.x * Mathf.Abs(ls.x), 0.01f), 1f, 1.6f),
                Mathf.Clamp(1f + width / Mathf.Max(b.extents.y * Mathf.Abs(ls.y), 0.01f), 1f, 1.6f),
                Mathf.Clamp(1f + width / Mathf.Max(b.extents.z * Mathf.Abs(ls.z), 0.01f), 1f, 1.6f));
            var go = new GameObject("shell");
            go.layer = Layer;
            go.transform.SetParent(owner, false);
            go.transform.localScale = s;
            go.transform.localPosition = b.center - Vector3.Scale(s, b.center);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            Quiet(mr);
            int subs = Mathf.Max(1, mesh.subMeshCount);
            mr.sharedMaterials = Enumerable.Repeat(mat, subs).ToArray();
            return mr;
        }

        /// outline mesh: verts pushed out along their normals (exact, for readable meshes like the baked crew)
        public static Mesh Inflated(Mesh src, float width)
        {
            if (src == null || !src.isReadable) return null;
            var v = src.vertices;
            var n = src.normals;
            if (n == null || n.Length != v.Length) return null;
            for (int i = 0; i < v.Length; i++) v[i] += n[i] * width;
            var m = new Mesh { name = src.name + " outline", indexFormat = src.indexFormat };
            m.vertices = v;
            m.normals = n;
            m.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++) m.SetTriangles(src.GetTriangles(s), s, false);
            m.RecalculateBounds();
            return m;
        }

        public static void Quiet(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic = false;
        }

        static readonly Dictionary<Texture, Material> _texHull = new Dictionary<Texture, Material>();
        static bool _texLogged;

        /// vehicle's own albedo as a see-through skin over the black x-ray hull (null if its material has none)
        public static Material TexturedHull(Material src)
        {
            if (src == null) return null;
            Texture tex = FindAlbedo(src);
            if (tex == null) return null;
            if (_texHull.TryGetValue(tex, out var m) && m != null) return m;
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) return null;
            m = new Material(sh) { mainTexture = tex, renderQueue = 2005, name = "HitCam textured hull" };
            if (src.HasProperty("_MainTex")) { m.mainTextureScale = src.mainTextureScale; m.mainTextureOffset = src.mainTextureOffset; }
            m.color = new Color(1f, 1f, 1f, 1f);
            _texHull[tex] = m;
            return m;
        }

        /// a material's colour texture whatever its shader calls it (asking a shader without _MainTex for mainTexture
        /// spams an error into unity's log every time)
        internal static Texture Albedo(Material src) => src != null ? FindAlbedo(src) : null;

        static Texture FindAlbedo(Material src)
        {
            try
            {
                if (src.HasProperty("_MainTex") && src.mainTexture != null) return src.mainTexture;
                string[] names = src.GetTexturePropertyNames();
                if (!_texLogged) { _texLogged = true; HitCamMod.Log.Msg("Hull shader " + src.shader?.name + " textures: " + string.Join(", ", names)); }
                foreach (var key in new[] { "albedo", "basecolor", "base", "diffuse", "color", "main" })
                    foreach (var n in names)
                        if (n.ToLowerInvariant().Contains(key))
                        {
                            var t = src.GetTexture(n);
                            if (t is Texture2D) return t;
                        }
            }
            catch { }
            return null;
        }

        public static bool WtStyle => !string.Equals(Settings.Style.Value, "Blueprint", System.StringComparison.OrdinalIgnoreCase);
        public static Color HullColor => WtStyle ? HullBlack : new Color(0.62f, 0.72f, 0.86f, 0.13f);
        public static readonly Color HullBlack = new Color(0.035f, 0.045f, 0.07f, 1f);
        public static Color Background => WtStyle ? new Color(0.085f, 0.09f, 0.10f, 1f) : new Color(0.055f, 0.065f, 0.08f, 1f);

        // --- colours (WT: clay grey, then yellow -> orange -> red -> black)
        public static Color Healthy => WtStyle ? new Color(0.50f, 0.53f, 0.57f) : new Color(0.80f, 0.84f, 0.90f);
        public static readonly Color Yellow = new Color(1f, 0.82f, 0.10f);
        public static readonly Color Orange = new Color(1f, 0.50f, 0.06f);
        public static readonly Color Red = new Color(0.92f, 0.10f, 0.07f);
        public static readonly Color Dead = new Color(0.08f, 0.07f, 0.07f);
        public static readonly Color DamageOutline = new Color(1f, 0.12f, 0.08f);
        public static readonly Color CrewGlow = new Color(0.86f, 0.92f, 1f);

        public static Color ModuleColor(ModCat cat, float hp)
        {
            if (hp <= 0f) return Dead;
            if (hp < 34f) return Red;
            if (hp < 67f) return Orange;
            if (hp < 99.5f) return Yellow;
            switch (cat)
            {
                case ModCat.Ammo: return WtStyle ? new Color(0.82f, 0.83f, 0.85f) : new Color(0.86f, 0.70f, 0.34f);
                case ModCat.Fuel: return WtStyle ? new Color(0.44f, 0.47f, 0.52f) : new Color(0.62f, 0.66f, 0.78f);
                default: return Healthy;
            }
        }

        public static Color CrewColor(HealthStatus s)
        {
            switch (s)
            {
                case HealthStatus.Wounded: return Yellow;
                case HealthStatus.HeavilyWounded: return Orange;
                case HealthStatus.Critical: return Red;
                case HealthStatus.Dead: return Dead;
                default: return WtStyle ? new Color(0.74f, 0.77f, 0.82f) : new Color(0.93f, 0.88f, 0.78f);   // lit by our key light: reads as soft white
            }
        }

        /// WT plate stress colour scale: 0 = failure (red) ... 1 = barely loaded (blue)
        public static Color Heat(float d)
        {
            var c = _heatC;
            var k = _heatK;
            d = Mathf.Clamp01(d);
            for (int i = 1; i < k.Length; i++)
                if (d <= k[i]) return Color.Lerp(c[i - 1], c[i], (d - k[i - 1]) / (k[i] - k[i - 1]));
            return c[c.Length - 1];
        }

        static readonly Color[] _heatC = { new Color(0.30f, 0.02f, 0.02f), new Color(0.85f, 0.06f, 0.04f), new Color(1f, 0.42f, 0.04f), new Color(1f, 0.90f, 0.12f), new Color(0.30f, 0.92f, 0.28f), new Color(0.12f, 0.82f, 0.92f), new Color(0.32f, 0.58f, 1f), new Color(0.55f, 0.74f, 1f) };
        static readonly float[] _heatK = { 0f, 0.07f, 0.17f, 0.29f, 0.42f, 0.57f, 0.78f, 1f };

        /// flat disc in the XY plane, white in the middle fading to black at the rim: soft glow when drawn additive
        public static Mesh SoftDisc => _softDisc ??= MakeDisc();
        static Mesh _softDisc, _heatRing;

        static Mesh MakeDisc()
        {
            const int n = 24;
            var v = new Vector3[n + 1];
            var c = new Color[n + 1];
            var t = new int[n * 3];
            v[0] = Vector3.zero; c[0] = Color.white;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                v[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.5f;
                c[i + 1] = Color.black;
                t[i * 3] = 0; t[i * 3 + 1] = i + 1; t[i * 3 + 2] = (i + 1) % n + 1;
            }
            var m = new Mesh { name = "soft disc", vertices = v, colors = c, triangles = t };
            m.RecalculateBounds();
            return m;
        }

        /// small round stress mark (strikes on modules / crew), radius 0.5, XY plane, transparent edge
        public static Mesh HeatRing => _heatRing ??= MakeHeatRing();

        static Mesh MakeHeatRing()
        {
            const int seg = 32, rings = 10;
            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            for (int b = 0; b <= rings; b++)
            {
                float d = (float)b / rings;
                for (int i = 0; i < seg; i++)
                {
                    float a = i * Mathf.PI * 2f / seg;
                    v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.5f * d);
                    var col = Heat(d);
                    col.a = d < 0.85f ? 0.9f : Mathf.Lerp(0.9f, 0f, (d - 0.85f) / 0.15f);
                    c.Add(col);
                }
            }
            for (int b = 0; b < rings; b++)
                for (int i = 0; i < seg; i++)
                {
                    int a0 = b * seg + i, a1 = b * seg + (i + 1) % seg, b0 = a0 + seg, b1 = a1 + seg;
                    t.Add(a0); t.Add(b0); t.Add(a1);
                    t.Add(a1); t.Add(b0); t.Add(b1);
                }
            var m = new Mesh { name = "heat ring" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        public static Color SpallColor(float power)
        {
            // WT: orange / yellow, strongest almost white
            float t = Mathf.Clamp01(power / 30f);
            return t < 0.5f ? Color.Lerp(new Color(1f, 0.32f, 0.08f), new Color(1f, 0.72f, 0.18f), t * 2f)
                            : Color.Lerp(new Color(1f, 0.72f, 0.18f), new Color(1f, 0.95f, 0.75f), (t - 0.5f) * 2f);
        }

        /// copy of a mesh with white vertex colours (Internal-Colored multiplies by vertex colour)
        public static Mesh WithColors(Mesh src, Color c)
        {
            var m = Object.Instantiate(src);
            var cols = new Color[m.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = c;
            m.colors = cols;
            return m;
        }

        static Mesh _wCube, _wSphere, _wCyl, _wQuad, _wCapsule;
        public static Mesh WCapsule => _wCapsule ??= WithColors(Prims.Capsule, Color.white);
        public static Mesh WCube => _wCube ??= WithColors(Prims.Cube, Color.white);
        public static Mesh WSphere => _wSphere ??= WithColors(Prims.Sphere, Color.white);
        public static Mesh WCylinder => _wCyl ??= WithColors(Prims.Cylinder, Color.white);
        public static Mesh WQuad => _wQuad ??= WithColors(Prims.Quad, Color.white);
    }

    /// instantiated x-ray copy of the target, on the private layer
    internal class Ghost
    {
        public GameObject Root;
        public Transform HullRoot, ModRoot, CrewRoot, AmmoRoot;
        public readonly List<MeshRenderer> Hull = new List<MeshRenderer>();
        public readonly List<Renderer> HullEdges = new List<Renderer>();
        public readonly List<MeshPart> HullParts = new List<MeshPart>();
        public readonly Dictionary<ModulePart, List<Renderer>> Modules = new Dictionary<ModulePart, List<Renderer>>();
        public readonly Dictionary<ModulePart, List<Renderer>> ModuleOutlines = new Dictionary<ModulePart, List<Renderer>>();
        public readonly Dictionary<CrewInfo, List<Renderer>> Crew = new Dictionary<CrewInfo, List<Renderer>>();
        public readonly Dictionary<CrewInfo, List<Renderer>> CrewOutlines = new Dictionary<CrewInfo, List<Renderer>>();
        public readonly Dictionary<CrewInfo, List<GameObject>> CrewParts = new Dictionary<CrewInfo, List<GameObject>>();   // aligned with CrewInfo.Parts
        public readonly Dictionary<AmmoPart, List<Renderer>> Ammo = new Dictionary<AmmoPart, List<Renderer>>();
        public readonly Dictionary<AmmoPart, List<Renderer>> AmmoOutlines = new Dictionary<AmmoPart, List<Renderer>>();
        public readonly List<Mesh> OwnedMeshes = new List<Mesh>();
        static readonly Color WtStyleFixture = new Color(0.36f, 0.38f, 0.41f);
        public readonly List<Material> Skin = new List<Material>();     // vehicle's own textures over the hull

        /// alpha = how much of the real textured vehicle shows, overInternals = drawn over the modules (before the reveal)
        public void SetSkin(float alpha, bool overInternals)
        {
            float tint = Mathf.Lerp(0.62f, 1f, Mathf.Clamp01((alpha - 0.3f) / 0.6f));   // full colour like the real tank, greyed as x-ray skin
            foreach (var m in Skin)
            {
                if (m == null) continue;
                m.color = new Color(tint, tint * 1.01f, tint * 1.04f, alpha);
                m.renderQueue = overInternals ? 3000 : 2005;
            }
        }

        public static Ghost Build(GhostSnapshot s, Vector3 origin)
        {
            Vis.Origin = origin;
            Vis.Init();
            long lap = Perf.Now;
            bool wt = Vis.WtStyle;
            var g = new Ghost { Root = new GameObject("HitCam Ghost: " + s.UnitName) };
            g.Root.layer = Vis.Layer;
            g.Root.transform.position = Vis.Origin;
            // own physics world (ragdolls, fragments), falls back to a plain persistent object
            if (Settings.XrayPhysics.Value) GhostPhysics.Adopt(g.Root);
            if (g.Root.scene.name != "HitCamPhysics") Object.DontDestroyOnLoad(g.Root);
            Perf.Lap("Ghost/adopt", ref lap);

            g.HullRoot = new GameObject("hull").transform;
            g.HullRoot.SetParent(g.Root.transform, false);
            foreach (var p in s.Exterior)
            {
                if (p.Mesh == null) continue;   // destroyed since the snapshot
                var go = Vis.Child(g.HullRoot, p.Mesh.name, p.Mesh, p.M, wt ? Vis.HullFill : Vis.Hull);
                var mr = go.GetComponent<MeshRenderer>();
                int subs = Mathf.Max(1, p.Mesh.subMeshCount);
                mr.sharedMaterials = Enumerable.Repeat(wt ? Vis.HullFill : Vis.Hull, subs).ToArray();
                g.Hull.Add(mr);
                g.HullParts.Add(p);
                if (wt && p.Mats != null && p.Mats.Length > 0)
                {
                    var mats = new Material[subs];
                    bool any = false;
                    for (int i = 0; i < subs; i++)
                    {
                        mats[i] = Vis.TexturedHull(p.Mats[Mathf.Min(i, p.Mats.Length - 1)]);
                        if (mats[i] != null) { any = true; if (!g.Skin.Contains(mats[i])) g.Skin.Add(mats[i]); }
                        else mats[i] = Vis.Hull;
                    }
                    if (any)
                    {
                        var skin = Vis.Child(go.transform, "skin", p.Mesh, Matrix4x4.identity, mats[0]).GetComponent<MeshRenderer>();
                        skin.sharedMaterials = mats;
                    }
                }
                if (wt && !p.Inner) g.HullEdges.Add(Vis.Shell(go.transform, p.Mesh, Vis.HullEdge, 0.012f));
            }

            Perf.Lap("Ghost/hull", ref lap);
            g.ModRoot = new GameObject("modules").transform;
            g.ModRoot.SetParent(g.Root.transform, false);
            foreach (var mp in s.Modules)
            {
                var list = new List<Renderer>();
                var outl = new List<Renderer>();
                bool track = mp.Cat == ModCat.Tracks;
                // game's own x-ray model of the module if it has one, else its hit zone
                foreach (var sh in mp.Detail.Count > 0 ? mp.Detail : mp.Shapes)
                {
                    // running gear belongs to the exterior look (dark, outlined) until it gets damaged
                    var go = Vis.Child(g.ModRoot, mp.Name, sh.Mesh, sh.M, track ? (wt ? Vis.HullFill : Vis.Hull) : Vis.Solid);
                    var r = go.GetComponent<Renderer>();
                    Vis.SetColor(r, Vis.ModuleColor(mp.Cat, mp.InitialHealth));
                    list.Add(r);
                    if (wt)
                    {
                        if (track) g.HullEdges.Add(Vis.Shell(go.transform, sh.Mesh, Vis.HullEdge, 0.01f));
                        var o = Vis.Shell(go.transform, sh.Mesh, Vis.Outline, 0.012f);
                        Vis.SetColor(o, Vis.DamageOutline);
                        o.enabled = false;
                        outl.Add(o);
                    }
                }
                g.Modules[mp] = list;
                g.ModuleOutlines[mp] = outl;
            }

            Perf.Lap("Ghost/modules", ref lap);
            // interior fittings (seats, brackets, boxes, racks): darker clay, no state
            foreach (var fx in s.Fixtures)
            {
                var go = Vis.Child(g.ModRoot, fx.Name, fx.Mesh, fx.M, Vis.Solid);
                Vis.SetColor(go.GetComponent<Renderer>(), WtStyleFixture);
            }

            Perf.Lap("Ghost/fixtures", ref lap);
            g.CrewRoot = new GameObject("crew").transform;
            g.CrewRoot.SetParent(g.Root.transform, false);
            CrewBuilder.Build(g, s, g.CrewRoot);
            Perf.Lap("Ghost/crew", ref lap);

            g.AmmoRoot = new GameObject("ammo").transform;
            g.AmmoRoot.SetParent(g.Root.transform, false);
            foreach (var ap in s.Ammo)
            {
                var list = new List<Renderer>();
                var outl = new List<Renderer>();
                foreach (var sh in ap.Shapes)
                {
                    var go = Vis.Child(g.AmmoRoot, "round", sh.Mesh, sh.M, Vis.Solid);
                    var r = go.GetComponent<Renderer>();
                    Vis.SetColor(r, Vis.ModuleColor(ModCat.Ammo, 100f));
                    list.Add(r);
                    if (wt) { var o = Vis.Shell(go.transform, sh.Mesh, Vis.Outline, 0.008f); Vis.SetColor(o, Vis.DamageOutline); o.enabled = false; outl.Add(o); }
                }
                g.Ammo[ap] = list;
                g.AmmoOutlines[ap] = outl;
            }

            // own lights (the scene's sun is told to ignore our layer)
            Perf.Lap("Ghost/ammo", ref lap);
            AddLight(g.Root.transform, new Vector3(50f, -35f, 0f), 1.05f, new Color(1f, 0.97f, 0.92f));
            AddLight(g.Root.transform, new Vector3(-30f, 140f, 0f), 0.45f, new Color(0.75f, 0.85f, 1f));
            ExcludeLayerFromSceneLights();
            Perf.Lap("Ghost/lights", ref lap);
            return g;
        }

        /// show / hide the x-ray copy itself (fx under the fx root stay as they are)
        bool _internalsShown = true;
        Renderer[] _internals;

        /// hide / show what's inside (modules, crew, ammo): WT shows only the real vehicle until the x-ray takes over
        public void SetInternalsVisible(bool v)
        {
            if (v == _internalsShown) return;
            _internalsShown = v;
            if (_internals == null)
            {
                var list = new List<Renderer>();
                foreach (var t in new[] { ModRoot, CrewRoot, AmmoRoot }) if (t != null) list.AddRange(t.GetComponentsInChildren<Renderer>(true));
                _internals = list.ToArray();
            }
            foreach (var r in _internals) if (r != null) r.forceRenderingOff = !v;
        }

        public void SetBodyVisible(bool v)
        {
            foreach (var t in new[] { HullRoot, ModRoot, CrewRoot, AmmoRoot })
                if (t != null)
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                        r.forceRenderingOff = !v;
        }

        static void AddLight(Transform parent, Vector3 euler, float intensity, Color c)
        {
            var go = new GameObject("hitcam light");
            go.transform.SetParent(parent, false);
            go.transform.rotation = Quaternion.Euler(euler);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.color = c;
            l.shadows = LightShadows.None;
            l.cullingMask = 1 << Vis.Layer;
            l.renderMode = LightRenderMode.ForcePixel;
        }

        public static void ExcludeLayerFromSceneLights()
        {
            foreach (var l in Object.FindObjectsOfType<Light>())
                if (l != null && l.gameObject.name != "hitcam light")
                    l.cullingMask &= ~(1 << Vis.Layer);
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            foreach (var m in OwnedMeshes) if (m != null) Object.Destroy(m);
            OwnedMeshes.Clear();
        }
    }
}
