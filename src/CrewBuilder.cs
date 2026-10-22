using System.Collections.Generic;
using GHPC.Humans;
using UnityEngine;

namespace GHPCHitCam
{
    /// builds the x-ray crew: one unscaled "body" object per body part (what the ragdoll moves), visual is either the
    /// game's real crew model (baked in its pose at impact, split per body part) or plain capsules
    internal static class CrewBuilder
    {
        public static void Build(Ghost g, GhostSnapshot s, Transform parent)
        {
            foreach (var ci in s.Crew)
            {
                var renderers = new List<Renderer>();
                var bodies = new List<GameObject>();
                for (int i = 0; i < ci.Parts.Count; i++)
                {
                    var cp = ci.Parts[i];
                    var m = cp.Shape.M;
                    var body = new GameObject(ci.Label + " " + cp.Part);
                    body.layer = Vis.Layer;
                    body.transform.SetParent(parent, false);
                    body.transform.localPosition = m.GetColumn(3);
                    body.transform.localRotation = m.rotation;
                    bodies.Add(body);
                }

                var skinOutlines = new List<Renderer>();
                // real model skinned to the ragdoll (bends at the joints), per-part pieces only if that fails
                bool detailed = Settings.DetailedCrew.Value && ci.Visuals.Count > 0
                    && (SkinModel(g, ci, bodies, parent, renderers, skinOutlines) || SplitModel(ci, bodies, renderers));
                for (int i = 0; i < ci.Parts.Count; i++)
                {
                    var cp = ci.Parts[i];
                    if (detailed) continue;   // real model replaces the hit-zone capsules
                    var r = Vis.Child(bodies[i].transform, "shape", cp.Shape.Mesh, Matrix4x4.Scale(cp.Shape.M.lossyScale), Vis.Solid).GetComponent<Renderer>();
                    renderers.Add(r);
                }
                foreach (var r in renderers) Vis.SetColor(r, Vis.CrewColor(ci.InitialStatus));
                // WT crew: white figures with a soft white rim (red when hit). real model pieces get an exact outline (verts pushed
                // out along normals), capsules get a slightly bigger shell
                var outlines = new List<Renderer>();
                if (Vis.WtStyle)
                    foreach (var r in renderers)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null) continue;
                        var inflated = Vis.Inflated(mf.sharedMesh, 0.011f);
                        Renderer o;
                        if (inflated != null)
                        {
                            g.OwnedMeshes.Add(inflated);
                            o = Vis.Child(r.transform, "glow", inflated, Matrix4x4.identity, Vis.Outline).GetComponent<Renderer>();
                        }
                        else o = Vis.Shell(r.transform, mf.sharedMesh, Vis.Outline, 0.011f);
                        Vis.SetColor(o, Vis.CrewGlow);
                        outlines.Add(o);
                    }
                foreach (var r in renderers)
                    if (r != null && r.gameObject.name == "model") g.OwnedMeshes.Add(r.GetComponent<MeshFilter>().sharedMesh);
                outlines.AddRange(skinOutlines);
                g.CrewParts[ci] = bodies;
                g.Crew[ci] = renderers;
                g.CrewOutlines[ci] = outlines;
            }
        }

        /// half-size and centre of a body part's box, in its body's (unscaled) space
        public static void BoxOf(CrewPartInfo cp, out Vector3 center, out Vector3 size)
        {
            var sc = cp.Shape.M.lossyScale;
            var mb = cp.Shape.Mesh.bounds;
            center = Vector3.Scale(mb.center, sc);
            size = Vector3.Scale(mb.size, sc);
            size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
        }

        /// real crew model skinned to the ragdoll, each body part's object is a bone. a vertex follows the part it's in and
        /// near a joint blends smoothly (up to 4 bones) with parts up to two joints away, so hips, waist, knees, elbows and
        /// neck bend like skin instead of stretching or coming apart
        /// GHPC crew have no pelvis/belly part (Torso, UpperSpine, LowerSpine = lower back, Gut is internal) and the thighs
        /// hang off LowerSpine, so belly (Torso) and groin (thigh) are two joints apart: blending only direct joints left
        /// every triangle across the waist stretched when hips and lower spine bent
        /// weights and triangles come from the snapshot alone (math on copies of the crew meshes, nothing from unity), so it
        /// runs on a worker thread right after the hit (Prepare) instead of ~70 ms per crewman in the frame the hit cam opens.
        /// made on the spot when nothing was prepared (a replay, the AAR, the Armor Sim)
        internal sealed class SkinPlan
        {
            public int N, Root = -1;
            public Vector3[] Pos, Cen, Half;
            public Quaternion[] RotInv;
            public bool[] Use;
            public int[] Parent;
            public readonly List<(Vector3[] v, Vector3[] n, int[][] tris, Matrix4x4 m)> Meshes = new List<(Vector3[], Vector3[], int[][], Matrix4x4)>();
            public List<Vector3> Verts, Norms;
            public List<BoneWeight> Bw;
            public List<int> Tris;
            public readonly List<string> Notes = new List<string>();
            public bool Ok;
            public volatile bool Done;
            public string Error;
            public float Made;
        }

        static readonly List<CrewInfo> _prepared = new List<CrewInfo>();

        /// right after the snapshot (hit cam will show this hit): every crewman's skin starts on a worker thread
        public static void Prepare(GhostSnapshot s)
        {
            if (s == null || !Settings.DetailedCrew.Value) return;
            // plans nobody took (hit merged into another, hit cam switched off) get dropped after a while
            Sweep();
            foreach (var ci in s.Crew)
            {
                if (ci.Visuals.Count == 0) continue;
                var plan = Gather(ci);
                if (plan == null) continue;
                ci.Plan = plan;
                _prepared.Add(ci);
                System.Threading.ThreadPool.QueueUserWorkItem(_ => Run(plan));
            }
        }

        /// replay of an older hit (Replay last, the report): crew skins whose plans are gone get redone on workers, the ones
        /// still there are kept alive; true once all are ready (else ~250 ms in the frame it opens)
        public static bool EnsurePrepared(GhostSnapshot s)
        {
            if (s == null || !Settings.DetailedCrew.Value) return true;
            Sweep();
            bool ready = true;
            foreach (var ci in s.Crew)
            {
                if (ci.Visuals.Count == 0) continue;
                if (ci.Plan == null)
                {
                    var plan = Gather(ci);
                    if (plan == null) continue;
                    ci.Plan = plan;
                    _prepared.Add(ci);
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => Run(plan));
                }
                ci.Plan.Made = Time.realtimeSinceStartup;
                if (!ci.Plan.Done) ready = false;
            }
            return ready;
        }

        /// plans are kept a minute after they're made: a cook-off replay (up to ~30 s after the hit) or a replay of the last
        /// hit builds the same crew again. after that they go (a few MB each)
        static void Sweep()
        {
            for (int i = _prepared.Count - 1; i >= 0; i--)
            {
                var c = _prepared[i];
                if (c.Plan != null && Time.realtimeSinceStartup - c.Plan.Made > 60f) c.Plan = null;
                if (c.Plan == null) _prepared.RemoveAt(i);
            }
        }

        /// crewman's prepared plan (waits for its thread if it's still busy), or null
        static SkinPlan Take(CrewInfo ci)
        {
            Sweep();
            var p = ci.Plan;
            if (p == null) return null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!p.Done && sw.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(1);
            return p.Done ? p : null;
        }

        /// snapshot numbers for the skin, copied on the main thread (meshes can only be read there)
        static SkinPlan Gather(CrewInfo ci)
        {
            int n = ci.Parts.Count;
            var p = new SkinPlan { N = n, Pos = new Vector3[n], Cen = new Vector3[n], Half = new Vector3[n], RotInv = new Quaternion[n], Use = new bool[n], Parent = new int[n], Made = Time.realtimeSinceStartup };
            for (int i = 0; i < n; i++)
            {
                var cp = ci.Parts[i];
                var m = cp.Shape.M;
                p.Use[i] = !cp.Internal;
                p.Parent[i] = cp.Parent;
                if (p.Root < 0 && p.Use[i] && (cp.Part == BodyPart.Torso || cp.Part == BodyPart.UpperSpine)) p.Root = i;
                p.Pos[i] = m.GetColumn(3);          // same as the body objects get placed (CrewBuilder.Build)
                p.RotInv[i] = Quaternion.Inverse(m.rotation);
                BoxOf(cp, out p.Cen[i], out var size);
                p.Half[i] = size * 0.5f;
            }
            if (p.Root < 0) p.Root = System.Array.IndexOf(p.Use, true);
            if (p.Root < 0) return null;
            foreach (var vm in ci.Visuals)
            {
                var mesh = vm.Mesh;
                if (mesh == null || !mesh.isReadable) continue;
                var subs = new int[mesh.subMeshCount][];
                for (int sub = 0; sub < subs.Length; sub++) subs[sub] = mesh.GetTriangles(sub);
                p.Meshes.Add((mesh.vertices, mesh.normals, subs, vm.M));
            }
            return p;
        }

        static SkinPlan Run(SkinPlan p)
        {
            try { Compute(p); }
            catch (System.Exception e) { p.Error = e.Message; p.Ok = false; }
            p.Done = true;
            return p;
        }

        /// real crew model skinned to the ragdoll, each body part's object is a bone. a vertex follows the part it's in and
        /// near a joint blends smoothly (up to 4 bones) with parts up to two joints away, so hips, waist, knees, elbows and
        /// neck bend like skin instead of stretching or coming apart
        /// GHPC crew have no pelvis/belly part (Torso, UpperSpine, LowerSpine = lower back, Gut is internal) and the thighs
        /// hang off LowerSpine, so belly (Torso) and groin (thigh) are two joints apart: blending only direct joints left
        /// every triangle across the waist stretched when hips and lower spine bent
        static void Compute(SkinPlan plan)
        {
            int n = plan.N, root = plan.Root;
            var pos = plan.Pos; var rotInv = plan.RotInv; var cen = plan.Cen; var half = plan.Half; var use = plan.Use;
            float Dist(int i, Vector3 p)
            {
                Vector3 q = rotInv[i] * (p - pos[i]) - cen[i];
                Vector3 d = new Vector3(Mathf.Max(Mathf.Abs(q.x) - half[i].x, 0f), Mathf.Max(Mathf.Abs(q.y) - half[i].y, 0f), Mathf.Max(Mathf.Abs(q.z) - half[i].z, 0f));
                return d.sqrMagnitude;
            }
            // skeleton distance in joints (Floyd-Warshall, n <= ~20) and each joint's pivot where the ragdoll puts it:
            // midway between the facing surfaces of the two boxes (GhostPhysics.BuildRagdoll)
            var hop = new int[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) hop[i, j] = i == j ? 0 : 99;
            for (int i = 0; i < n; i++) { int pa = plan.Parent[i]; if (pa >= 0 && pa < n) hop[i, pa] = hop[pa, i] = 1; }
            for (int m = 0; m < n; m++) for (int i = 0; i < n; i++) for (int j = 0; j < n; j++)
                if (hop[i, m] + hop[m, j] < hop[i, j]) hop[i, j] = hop[i, m] + hop[m, j];
            Vector3 Closest(int i, Vector3 p)
            {
                Vector3 q = rotInv[i] * (p - pos[i]) - cen[i];
                q = new Vector3(Mathf.Clamp(q.x, -half[i].x, half[i].x), Mathf.Clamp(q.y, -half[i].y, half[i].y), Mathf.Clamp(q.z, -half[i].z, half[i].z));
                return pos[i] + Quaternion.Inverse(rotInv[i]) * (q + cen[i]);
            }
            Vector3 Center(int i) => pos[i] + Quaternion.Inverse(rotInv[i]) * cen[i];
            var pivot = new Vector3[n];
            var hasPivot = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int pa = plan.Parent[i];
                if (pa < 0 || pa >= n || !use[i] || !use[pa]) continue;
                pivot[i] = (Closest(i, Center(pa)) + Closest(pa, Center(i))) * 0.5f;
                hasPivot[i] = true;
            }
            // distance from a point to the nearest joint pivot on the skeleton path between parts a and b (<= 2 joints)
            float PathPivot(int a, int b, Vector3 p)
            {
                float best = float.MaxValue;
                void Try(int child) { if (child >= 0 && hasPivot[child]) best = Mathf.Min(best, (p - pivot[child]).magnitude); }
                if (hop[a, b] == 1) { Try(plan.Parent[a] == b ? a : b); return best; }
                for (int m = 0; m < n; m++)
                    if (hop[a, m] == 1 && hop[m, b] == 1)
                    {
                        Try(plan.Parent[a] == m ? a : m);
                        Try(plan.Parent[b] == m ? b : m);
                    }
                return best;
            }

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var bw = new List<BoneWeight>();
            var tris = new List<int>();
            const float Sigma = 0.05f;          // how fast a neighbour's weight falls off with its extra distance
            const float Reach = 0.16f;          // blending only this close to the joint pivot between the parts
            var dd = new float[n];
            var bi = new int[4];
            var bwt = new float[4];
            // primary part + every part within two joints that's nearly as close AND whose joint with it is near this point
            // (so a flank doesn't follow an arm resting on it), smooth falloff weights, heaviest first
            BoneWeight Weigh(Vector3 pt, int b1)
            {
                for (int i = 0; i < n; i++) dd[i] = use[i] ? Dist(i, pt) : float.MaxValue;
                float s1 = Mathf.Sqrt(dd[b1]);
                int cnt = 0;
                for (int j = 0; j < 4; j++) { bi[j] = b1; bwt[j] = 0f; }
                for (int i = 0; i < n; i++)
                {
                    if (!use[i] || hop[b1, i] > 2) continue;
                    float wi;
                    if (i == b1) wi = 1f;
                    else
                    {
                        float x = (Mathf.Sqrt(dd[i]) - s1) / Sigma;
                        if (x > 2.2f) continue;
                        float pj = PathPivot(b1, i, pt);
                        if (pj >= Reach) continue;
                        wi = Mathf.Exp(-x * x) * Mathf.Clamp01((Reach - pj) / (Reach * 0.4f)) * (hop[b1, i] == 2 ? 0.8f : 1f);
                    }
                    if (wi <= 0.01f) continue;
                    int slot = cnt < 4 ? cnt++ : -1;
                    if (slot < 0)
                    {
                        int lo = 0;
                        for (int j = 1; j < 4; j++) if (bwt[j] < bwt[lo]) lo = j;
                        if (bwt[lo] >= wi) continue;
                        slot = lo;
                    }
                    bi[slot] = i; bwt[slot] = wi;
                }
                float sum = 0f;
                for (int j = 0; j < 4; j++) sum += bwt[j];
                if (sum <= 1e-5f) { bi[0] = b1; bwt[0] = 1f; sum = 1f; }
                for (int a = 0; a < 3; a++)
                    for (int b = a + 1; b < 4; b++)
                        if (bwt[b] > bwt[a]) { float tw = bwt[a]; bwt[a] = bwt[b]; bwt[b] = tw; int ti = bi[a]; bi[a] = bi[b]; bi[b] = ti; }
                return new BoneWeight
                {
                    boneIndex0 = bi[0], weight0 = bwt[0] / sum, boneIndex1 = bi[1], weight1 = bwt[1] / sum,
                    boneIndex2 = bi[2], weight2 = bwt[2] / sum, boneIndex3 = bi[3], weight3 = bwt[3] / sum,
                };
            }
            var prim = new List<int>();          // each output vertex's primary part
            var primV = new int[0];
            foreach (var (v, nm, subTris, vmM) in plan.Meshes)
            {
                bool hasN = nm != null && nm.Length == v.Length;
                var lp = new Vector3[v.Length];
                var ln = new Vector3[v.Length];
                var w = new BoneWeight[v.Length];
                var ok = new bool[v.Length];
                if (primV.Length < v.Length) primV = new int[v.Length];
                var map = new int[v.Length];
                for (int k = 0; k < v.Length; k++)
                {
                    map[k] = -1;
                    lp[k] = vmM.MultiplyPoint3x4(v[k]);
                    ln[k] = hasN ? vmM.MultiplyVector(nm[k]).normalized : Vector3.up;
                    int b1 = -1;
                    float d1 = float.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        dd[i] = float.MaxValue;
                        if (!use[i]) continue;
                        dd[i] = Dist(i, lp[k]);
                        if (dd[i] < d1) { d1 = dd[i]; b1 = i; }
                    }
                    if (b1 < 0 || d1 > 0.09f) continue;     // more than 30 cm from any part: not this crewman (seat, gear)
                    ok[k] = true;
                    primV[k] = b1;
                    w[k] = Weigh(lp[k], b1);
                }
                foreach (var t in subTris)
                {
                    for (int k = 0; k + 2 < t.Length; k += 3)
                    {
                        if (!ok[t[k]] || !ok[t[k + 1]] || !ok[t[k + 2]]) continue;
                        for (int j = 0; j < 3; j++)
                        {
                            int src = t[k + j];
                            if (map[src] < 0)
                            {
                                map[src] = verts.Count;
                                verts.Add(lp[src]);
                                norms.Add(ln[src]);
                                bw.Add(w[src]);
                                prim.Add(primV[src]);
                            }
                            tris.Add(map[src]);
                        }
                    }
                }
            }
            plan.Verts = verts; plan.Norms = norms; plan.Bw = bw; plan.Tris = tris;
            plan.Ok = tris.Count >= 150;
            if (!plan.Ok) return;
            FixIslands(verts, tris, prim, bw, Weigh, n, hop, plan.Notes);
            SmoothSeams(verts, tris, bw, n, plan.Notes);
        }

        static bool SkinModel(Ghost g, CrewInfo ci, List<GameObject> bodies, Transform parent, List<Renderer> renderers, List<Renderer> outlines)
        {
            var plan = Take(ci);
            if (plan == null)
            {
                plan = Gather(ci);
                if (plan == null) return false;
                Run(plan);
                if (plan.Ok) { ci.Plan = plan; _prepared.Add(ci); }   // next build of this hit (a replay) reuses it
            }
            if (plan.Error != null) HitCamMod.Log.Warning("Crew skin " + ci.Label + ": " + plan.Error);
            foreach (var note in plan.Notes) HitCamMod.Log.Msg(note);
            if (!plan.Ok) return false;
            int n = plan.N, root = plan.Root;
            var verts = plan.Verts; var norms = plan.Norms; var bw = plan.Bw; var tris = plan.Tris;

            var go = new GameObject(ci.Label + " skin");
            go.layer = Vis.Layer;
            go.transform.SetParent(parent, false);
            var bones = new Transform[n];
            var bind = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                bones[i] = bodies[i].transform;
                bind[i] = bodies[i].transform.worldToLocalMatrix * go.transform.localToWorldMatrix;
            }
            var bwa = bw.ToArray();
            Mesh Make(List<Vector3> vv, string name)
            {
                var m = new Mesh { name = name };
                if (vv.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(vv);
                m.SetNormals(norms);
                m.boneWeights = bwa;
                m.bindposes = bind;
                m.SetTriangles(tris, 0);
                m.RecalculateBounds();
                g.OwnedMeshes.Add(m);
                return m;
            }
            SkinnedMeshRenderer Skin(GameObject host, Mesh m, Material mat)
            {
                var smr = host.AddComponent<SkinnedMeshRenderer>();
                Vis.Quiet(smr);
                smr.sharedMesh = m;
                smr.bones = bones;
                smr.rootBone = bones[root];
                smr.sharedMaterial = mat;
                smr.quality = SkinQuality.Bone4;
                smr.updateWhenOffscreen = true;
                return smr;
            }
            renderers.Add(Skin(go, Make(verts, ci.Label + " skinned model"), Vis.Solid));
            if (Vis.WtStyle)
            {
                // soft white rim: same skin pushed out along the normals, back faces only
                var infl = new List<Vector3>(verts.Count);
                for (int k = 0; k < verts.Count; k++) infl.Add(verts[k] + norms[k] * 0.011f);
                var glow = new GameObject("glow");
                glow.layer = Vis.Layer;
                glow.transform.SetParent(go.transform, false);
                var o = Skin(glow, Make(infl, ci.Label + " skinned outline"), Vis.Outline);
                Vis.SetColor(o, Vis.CrewGlow);
                outlines.Add(o);
            }
            return true;
        }

        /// nearest-box skinning glues a patch of one part to another part it just touches: seated, the hands rest on the
        /// thighs, so part of a hand followed the leg and tore out (x70 stretched edges) when the body went limp
        /// so every patch of a part that's cut off from that part's main surface but sits inside another part's surface (shares
        /// edges with it) goes to that neighbour; same for a patch whose edges border mostly a part it isn't jointed to (a thigh
        /// patch the hand box claimed, bigger than the small real hand), unless it's big next to that part's own surface
        /// free-floating pieces (helmet, pouch) keep their nearest part
        static void FixIslands(List<Vector3> verts, List<int> tris, List<int> prim, List<BoneWeight> bw, System.Func<Vector3, int, BoneWeight> weigh, int parts, int[,] hop, List<string> notes)
        {
            int nv = verts.Count;
            // weld the model's split verts (UV seams) by position so connectivity follows the surface
            var weld = new int[nv];
            var key = new Dictionary<long, int>();
            int nw = 0;
            for (int i = 0; i < nv; i++)
            {
                var p = verts[i];
                long k = ((long)Mathf.RoundToInt(p.x * 2000f) * 73856093L) ^ ((long)Mathf.RoundToInt(p.y * 2000f) * 19349663L) ^ ((long)Mathf.RoundToInt(p.z * 2000f) * 83492791L);
                if (!key.TryGetValue(k, out int id)) { id = nw++; key[k] = id; }
                weld[i] = id;
            }
            var wPrim = new int[nw];
            for (int i = 0; i < nv; i++) wPrim[weld[i]] = prim[i];
            var adj = new List<int>[nw];
            for (int i = 0; i < nw; i++) adj[i] = new List<int>(6);
            for (int t = 0; t + 2 < tris.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[tris[t + e]], b = weld[tris[t + (e + 1) % 3]];
                    if (a == b) continue;
                    adj[a].Add(b);
                    adj[b].Add(a);
                }
            var comp = new int[nw];
            var moved = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                // components of same-part verts
                for (int i = 0; i < nw; i++) comp[i] = -1;
                var size = new List<int>();
                var compPart = new List<int>();
                var stack = new Stack<int>();
                for (int i = 0; i < nw; i++)
                {
                    if (comp[i] >= 0) continue;
                    int c = size.Count, cnt = 0;
                    size.Add(0); compPart.Add(wPrim[i]);
                    comp[i] = c; stack.Push(i);
                    while (stack.Count > 0)
                    {
                        int x = stack.Pop(); cnt++;
                        foreach (int y in adj[x]) if (comp[y] < 0 && wPrim[y] == wPrim[x]) { comp[y] = c; stack.Push(y); }
                    }
                    size[c] = cnt;
                }
                var main = new int[parts];
                for (int i = 0; i < parts; i++) main[i] = -1;
                for (int c = 0; c < size.Count; c++)
                {
                    int pt = compPart[c];
                    if (pt >= 0 && pt < parts && (main[pt] < 0 || size[c] > size[main[pt]])) main[pt] = c;
                }
                // which part each stray patch borders most
                var target = new int[size.Count];
                var tally = new Dictionary<int, int>();
                for (int c = 0; c < size.Count; c++) target[c] = -1;
                var border = new Dictionary<int, Dictionary<int, int>>();
                for (int x = 0; x < nw; x++)
                {
                    int c = comp[x];
                    int pt = compPart[c];
                    if (pt < 0 || pt >= parts) continue;
                    foreach (int y in adj[x])
                    {
                        if (comp[y] == c) continue;
                        if (!border.TryGetValue(c, out var d)) { d = new Dictionary<int, int>(); border[c] = d; }
                        d.TryGetValue(wPrim[y], out int nb);
                        d[wPrim[y]] = nb + 1;
                    }
                }
                int changed = 0;
                foreach (var kv in border)
                {
                    int c = kv.Key, own = compPart[c];
                    int best = -1, bestN = 0, far = -1, farN = 0, nearN = 0;
                    foreach (var e in kv.Value)
                    {
                        if (e.Value > bestN) { bestN = e.Value; best = e.Key; }
                        bool jointed = e.Key >= 0 && e.Key < parts && hop[own, e.Key] <= 1;
                        if (jointed) nearN += e.Value;
                        else if (e.Value > farN) { farN = e.Value; far = e.Key; }
                    }
                    int to = -1;
                    if (main[own] != c) to = best;                       // stray patch: goes to the part it sits in
                    else if (far >= 0 && farN > nearN && main[far] >= 0 && size[c] < 0.35f * size[main[far]])
                        to = far;                                        // part's "main" patch really belongs to a part it isn't jointed to
                    if (to >= 0 && to != own) { target[c] = to; changed++; }
                }
                if (changed == 0) break;
                for (int x = 0; x < nw; x++) if (target[comp[x]] >= 0) wPrim[x] = target[comp[x]];
            }
            for (int i = 0; i < nv; i++)
            {
                int np = wPrim[weld[i]];
                if (np == prim[i]) continue;
                prim[i] = np;
                bw[i] = weigh(verts[i], np);
                moved++;
            }
            if (moved > 0) notes.Add($"Crew skin: {moved} of {nv} vertices moved to the part whose surface they belong to");
        }

        /// a seam between two parts far from their joint tears when the body goes limp: seated, the belly (Torso) meets the
        /// thighs at the groin, but the thighs hang off LowerSpine with the hip joint at the back, so the belly/thigh seam got
        /// no blend and one row of triangles stretched x60-70 when a leg swung. so every seam's weights get spread over a band
        /// of connected surface (diffused a few rings either side) and the skin stretches a little over several cm instead
        /// only connected skin blends: a hand resting on a thigh is a separate surface and keeps its weights, verts away from
        /// any seam are untouched
        static void SmoothSeams(List<Vector3> verts, List<int> tris, List<BoneWeight> bw, int parts, List<string> notes)
        {
            const int Band = 10, Passes = 24;
            int nv = verts.Count;
            if (nv == 0 || parts <= 1) return;
            var weld = new int[nv];
            var key = new Dictionary<long, int>();
            int nw = 0;
            for (int i = 0; i < nv; i++)
            {
                var p = verts[i];
                long k = ((long)Mathf.RoundToInt(p.x * 2000f) * 73856093L) ^ ((long)Mathf.RoundToInt(p.y * 2000f) * 19349663L) ^ ((long)Mathf.RoundToInt(p.z * 2000f) * 83492791L);
                if (!key.TryGetValue(k, out int id)) { id = nw++; key[k] = id; }
                weld[i] = id;
            }
            // dense weights per welded vertex (split copies averaged)
            var w = new float[nw * parts];
            var copies = new int[nw];
            for (int i = 0; i < nv; i++)
            {
                int o = weld[i] * parts;
                var b = bw[i];
                if (b.weight0 > 0f && b.boneIndex0 < parts) w[o + b.boneIndex0] += b.weight0;
                if (b.weight1 > 0f && b.boneIndex1 < parts) w[o + b.boneIndex1] += b.weight1;
                if (b.weight2 > 0f && b.boneIndex2 < parts) w[o + b.boneIndex2] += b.weight2;
                if (b.weight3 > 0f && b.boneIndex3 < parts) w[o + b.boneIndex3] += b.weight3;
                copies[weld[i]]++;
            }
            for (int x = 0; x < nw; x++) if (copies[x] > 1) for (int j = 0; j < parts; j++) w[x * parts + j] /= copies[x];
            var adj = new List<int>[nw];
            for (int i = 0; i < nw; i++) adj[i] = new List<int>(6);
            for (int t = 0; t + 2 < tris.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[tris[t + e]], b = weld[tris[t + (e + 1) % 3]];
                    if (a == b) continue;
                    if (!adj[a].Contains(b)) adj[a].Add(b);
                    if (!adj[b].Contains(a)) adj[b].Add(a);
                }
            // main part of each vertex, the seams (a neighbour's main part differs), and the band around them
            var label = new int[nw];
            for (int x = 0; x < nw; x++)
            {
                int best = 0;
                for (int j = 1; j < parts; j++) if (w[x * parts + j] > w[x * parts + best]) best = j;
                label[x] = best;
            }
            var ring = new int[nw];
            var queue = new Queue<int>();
            for (int x = 0; x < nw; x++)
            {
                ring[x] = int.MaxValue;
                foreach (int y in adj[x]) if (label[y] != label[x]) { ring[x] = 0; queue.Enqueue(x); break; }
            }
            int zone = queue.Count;
            while (queue.Count > 0)
            {
                int x = queue.Dequeue();
                if (ring[x] >= Band) continue;
                foreach (int y in adj[x]) if (ring[y] == int.MaxValue) { ring[y] = ring[x] + 1; queue.Enqueue(y); zone++; }
            }
            if (zone == 0) return;
            var idx = new int[zone];
            int z = 0;
            for (int x = 0; x < nw; x++) if (ring[x] <= Band) idx[z++] = x;
            // diffuse inside the band; band's outer rim keeps its weights (pins the blend to the parts)
            var w2 = (float[])w.Clone();
            for (int pass = 0; pass < Passes; pass++)
            {
                foreach (int x in idx)
                {
                    if (ring[x] >= Band) continue;
                    int o = x * parts;
                    float c = 1f;
                    for (int j = 0; j < parts; j++) w2[o + j] = w[o + j];
                    foreach (int y in adj[x])
                    {
                        int oy = y * parts;
                        for (int j = 0; j < parts; j++) w2[o + j] += w[oy + j];
                        c += 1f;
                    }
                    for (int j = 0; j < parts; j++) w2[o + j] /= c;
                }
                var tmp = w; w = w2; w2 = tmp;
                foreach (int x in idx) if (ring[x] >= Band) for (int j = 0; j < parts; j++) w[x * parts + j] = w2[x * parts + j];
            }
            // back to four bones per vertex, heaviest first
            var bi = new int[4];
            var bv = new float[4];
            int changed = 0;
            for (int i = 0; i < nv; i++)
            {
                int x = weld[i];
                if (ring[x] >= Band) continue;
                int o = x * parts;
                for (int k = 0; k < 4; k++) { bi[k] = label[x]; bv[k] = 0f; }
                for (int j = 0; j < parts; j++)
                {
                    float v = w[o + j];
                    if (v <= 0.01f) continue;
                    int lo = 0;
                    for (int k = 1; k < 4; k++) if (bv[k] < bv[lo]) lo = k;
                    if (v > bv[lo]) { bv[lo] = v; bi[lo] = j; }
                }
                float sum = bv[0] + bv[1] + bv[2] + bv[3];
                if (sum <= 1e-5f) continue;
                for (int a = 0; a < 3; a++)
                    for (int b = a + 1; b < 4; b++)
                        if (bv[b] > bv[a]) { float tw = bv[a]; bv[a] = bv[b]; bv[b] = tw; int ti = bi[a]; bi[a] = bi[b]; bi[b] = ti; }
                bw[i] = new BoneWeight
                {
                    boneIndex0 = bi[0], weight0 = bv[0] / sum, boneIndex1 = bi[1], weight1 = bv[1] / sum,
                    boneIndex2 = bi[2], weight2 = bv[2] / sum, boneIndex3 = bi[3], weight3 = bv[3] / sum,
                };
                changed++;
            }
            notes.Add($"Crew skin: seams spread over {zone} of {nw} vertices ({changed} reweighted)");
        }

        /// put every triangle of the real crew model on the nearest body part so the detailed figure can go limp piece by
        /// piece with the ragdoll
        static bool SplitModel(CrewInfo ci, List<GameObject> bodies, List<Renderer> renderers)
        {
            int n = ci.Parts.Count;
            var pos = new Vector3[n];
            var rotInv = new Quaternion[n];
            var cen = new Vector3[n];
            var half = new Vector3[n];
            var use = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var cp = ci.Parts[i];
                use[i] = !cp.Internal;
                pos[i] = bodies[i].transform.localPosition;
                rotInv[i] = Quaternion.Inverse(bodies[i].transform.localRotation);
                BoxOf(cp, out cen[i], out var size);
                half[i] = size * 0.5f;
            }

            float Dist(int i, Vector3 p)
            {
                Vector3 q = rotInv[i] * (p - pos[i]) - cen[i];
                Vector3 d = new Vector3(Mathf.Max(Mathf.Abs(q.x) - half[i].x, 0f), Mathf.Max(Mathf.Abs(q.y) - half[i].y, 0f), Mathf.Max(Mathf.Abs(q.z) - half[i].z, 0f));
                return d.sqrMagnitude;
            }

            var verts = new List<Vector3>[n];
            var norms = new List<Vector3>[n];
            var tris = new List<int>[n];
            for (int i = 0; i < n; i++) { verts[i] = new List<Vector3>(); norms[i] = new List<Vector3>(); tris[i] = new List<int>(); }
            int total = 0;

            foreach (var vm in ci.Visuals)
            {
                var mesh = vm.Mesh;
                if (mesh == null || !mesh.isReadable) continue;
                var v = mesh.vertices;
                var nm = mesh.normals;
                bool hasN = nm != null && nm.Length == v.Length;
                var vl = new Vector3[v.Length];
                var nl = new Vector3[v.Length];
                for (int k = 0; k < v.Length; k++)
                {
                    vl[k] = vm.M.MultiplyPoint3x4(v[k]);
                    nl[k] = hasN ? vm.M.MultiplyVector(nm[k]).normalized : Vector3.up;
                }
                var maps = new Dictionary<int, int>[n];
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var t = mesh.GetTriangles(sub);
                    for (int k = 0; k < t.Length; k += 3)
                    {
                        Vector3 c = (vl[t[k]] + vl[t[k + 1]] + vl[t[k + 2]]) / 3f;
                        int best = -1;
                        float bd = float.MaxValue;
                        for (int i = 0; i < n; i++)
                        {
                            if (!use[i]) continue;
                            float d = Dist(i, c);
                            if (d < bd) { bd = d; best = i; }
                        }
                        if (best < 0 || bd > 0.09f) continue;   // > 30 cm from any part: not this crewman (seat, gear)
                        var map = maps[best] ??= new Dictionary<int, int>();
                        for (int j = 0; j < 3; j++)
                        {
                            int src = t[k + j];
                            if (!map.TryGetValue(src, out int dst))
                            {
                                dst = verts[best].Count;
                                verts[best].Add(rotInv[best] * (vl[src] - pos[best]));
                                norms[best].Add(rotInv[best] * nl[src]);
                                map[src] = dst;
                            }
                            tris[best].Add(dst);
                        }
                        total++;
                    }
                }
            }
            if (total < 50) return false;

            for (int i = 0; i < n; i++)
            {
                if (tris[i].Count == 0) continue;
                var m = new Mesh { name = ci.Label + " model " + ci.Parts[i].Part };
                if (verts[i].Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.SetVertices(verts[i]);
                m.SetNormals(norms[i]);
                m.SetTriangles(tris[i], 0);
                m.RecalculateBounds();
                var go = Vis.Child(bodies[i].transform, "model", m, Matrix4x4.identity, Vis.Solid);
                renderers.Add(go.GetComponent<Renderer>());
            }
            return true;
        }
    }
}
