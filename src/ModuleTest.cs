using System;
using System.Collections;
using System.IO;
using System.Linq;
using GHPC;
using UnityEngine;

namespace GHPCHitCam
{
    /// Self-test pictures of the world module damage (AutoTest shots aimed at a barrel, a track, an engine deck or a tail
    /// boom): an inspection camera looks at the spot at several moments after the hit, so a bend / break, a falling track,
    /// a growing fire or a tail boom coming off can be judged. Files: module{shot}_{n}.png.
    internal static class ModuleTest
    {
        public static bool IsModuleAspect(string aspect) =>
            aspect != null && (aspect.StartsWith("barrel") || aspect.StartsWith("track") || aspect == "engine" || aspect == "boom" || aspect == "cook");

        public static IEnumerator Shots(Vector3 aim, Vector3 shotDir, int shot, string aspect, string outDir, Unit target)
        {
            if (AutoTest.ModuleFilm > 0f && aspect.StartsWith("track")) { yield return Film(aim, shotDir, shot, outDir, target, AutoTest.ModuleFilm); yield break; }
            float t0 = Time.realtimeSinceStartup;
            var cam = NewCamera(out var rt);
            var go = cam.gameObject;
            // the shot comes from aim + shotDir * 30; seen from the round's side, the far side, and wider
            Vector3 d = -shotDir.normalized;               // toward the shooter
            Vector3 side = Vector3.Cross(Vector3.up, d).normalized;
            Transform tt = target != null ? target.transform : null;
            Vector3 local = tt != null ? tt.InverseTransformPoint(aim) : aim;
            float[] at = aspect == "cook" ? new[] { 1f, 4f, 7f, 10f, 14f, 18f } : new[] { 0.5f, 1.3f, 2.6f, 5f, 8f, 11f };
            for (int k = 0; k < at.Length; k++)
            {
                while (Time.realtimeSinceStartup - t0 < at[k]) yield return null;
                // follow the vehicle (it may move); a helicopter's falling tail is looked at where it is
                Vector3 c = tt != null ? tt.TransformPoint(local) : aim;
                Vector3 pos;
                Vector3 look = c;
                if (aspect.StartsWith("barrel"))
                {
                    Vector3[] views = { d * 1.3f + Vector3.up * 0.35f + side * 0.4f, -d * 1.3f + Vector3.up * 0.35f - side * 0.4f, d * 2.4f + Vector3.up * 1.2f + side * 1.6f,
                                        d * 3f + Vector3.up * 1.5f - side * 2.5f, -d * 3f + Vector3.up * 2f + side * 2f, d * 5f + Vector3.up * 2.5f };
                    pos = c + views[k];
                    if (k >= 3) look = c - Vector3.up * 0.6f;
                }
                else if (aspect.StartsWith("track"))
                {
                    Vector3[] views = { d * 2.6f + Vector3.up * 0.7f + side * 1.2f, d * 3.2f + Vector3.up * 1.0f - side * 1.8f, d * 4.5f + Vector3.up * 1.6f + side * 2.5f,
                                        d * 4.5f + Vector3.up * 1.2f - side * 3f, d * 6f + Vector3.up * 2.5f, d * 5f + Vector3.up * 1f + side * 4f };
                    pos = c + views[k];
                    look = c - Vector3.up * 0.3f;
                }
                else if (aspect == "cook")
                {
                    // the whole vehicle, the side the round came in, a little above: the game's own fire and cook-off
                    Vector3[] views = { d * 9f + Vector3.up * 4f + side * 4f, d * 9f + Vector3.up * 4f - side * 4f, d * 10f + Vector3.up * 5f,
                                        d * 9f + Vector3.up * 4f + side * 4f, d * 11f + Vector3.up * 6f - side * 3f, d * 12f + Vector3.up * 7f };
                    pos = c + views[k];
                }
                else if (aspect == "engine")
                {
                    Vector3 back = tt != null ? -tt.forward : d;
                    Vector3[] views = { back * 5f + Vector3.up * 3f, back * 6f + Vector3.up * 3.5f + side * 3f, back * 7f + Vector3.up * 4f - side * 3f,
                                        back * 8f + Vector3.up * 4f, back * 9f + Vector3.up * 5f + side * 5f, back * 10f + Vector3.up * 5f - side * 5f };
                    pos = c + views[k];
                }
                else
                {
                    // the tail boom: on the helicopter until it breaks, then on the falling piece
                    var piece = UnityEngine.Object.FindObjectsOfType<Rigidbody>().Where(r => r != null && r.name == "HitCam tail boom").OrderByDescending(r => r.GetInstanceID()).FirstOrDefault();
                    if (piece != null) look = piece.worldCenterOfMass;
                    pos = look + d * 16f + Vector3.up * 4f + side * 6f;
                }
                cam.transform.position = pos;
                cam.transform.LookAt(look);
                try
                {
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    File.WriteAllBytes(Path.Combine(outDir, $"module{shot}_{k}.png"), tex.EncodeToPNG());
                    UnityEngine.Object.Destroy(tex);
                    HitCamMod.Log.Msg($"AUTOTEST module shot {shot} frame {k} ({aspect}) at {at[k]:0.0} s");
                }
                catch (Exception e) { HitCamMod.Log.Warning("module capture failed: " + e.Message); }
            }
            UnityEngine.Object.Destroy(go);
            rt.Release();
        }

        /// One picture now, from `from` looking at `look` (self-test close-ups of a moment: world vent flames).
        public static void Snap(Vector3 look, Vector3 from, string name, string note, int w = 1280, int h = 720, float fov = 40f, bool post = true)
        {
            if (!AutoTest.Active) return;
            Camera cam = null;
            RenderTexture rt = null;
            try
            {
                cam = NewCamera(out rt, w, h);
                cam.fieldOfView = fov;
                cam.transform.position = from;
                cam.transform.LookAt(look);
                if (!post) foreach (var b in cam.GetComponents<Behaviour>()) if (!(b is Camera)) b.enabled = false;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(HitCamMod.DataDir, "autotest", name + ".png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                HitCamMod.Log.Msg($"AUTOTEST snap {name}: {note}");
            }
            catch (Exception e) { HitCamMod.Log.Warning("snap failed: " + e.Message); }
            if (cam != null) UnityEngine.Object.Destroy(cam.gameObject);
            if (rt != null) rt.Release();
        }

        /// A track shot filmed from one view, three-quarter front from the struck track's side: a frame every 1/30 s of
        /// game time (Time.captureFramerate holds the game to the capture), module{shot}_f{n}.png.
        static IEnumerator Film(Vector3 aim, Vector3 shotDir, int shot, string outDir, Unit target, float seconds)
        {
            var cam = NewCamera(out var rt);
            Transform tt = target != null ? target.transform : null;
            Vector3 fwd = -shotDir.normalized;
            Vector3 local = tt != null ? tt.InverseTransformPoint(aim) : aim;
            Vector3 across = tt != null ? (Vector3.Dot(aim - tt.position, tt.right) < 0f ? -tt.right : tt.right) : Vector3.Cross(Vector3.up, fwd).normalized;
            int before = Time.captureFramerate;
            Time.captureFramerate = 30;
            int n = Mathf.RoundToInt(seconds * 30f);
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            for (int i = 0; i < n; i++)
            {
                Vector3 c = tt != null ? tt.TransformPoint(local) : aim;
                Vector3 mid = tt != null ? tt.position + Vector3.up * 1.0f : c;
                cam.transform.position = c + fwd * 6f + across * 5f + Vector3.up * 2f;
                cam.transform.LookAt(Vector3.Lerp(c, mid, 0.4f) - Vector3.up * 0.25f);
                try
                {
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    File.WriteAllBytes(Path.Combine(outDir, $"module{shot}_f{i:000}.png"), tex.EncodeToPNG());
                }
                catch (Exception e) { HitCamMod.Log.Warning("module film frame failed: " + e.Message); }
                yield return null;
            }
            Time.captureFramerate = before;
            HitCamMod.Log.Msg($"AUTOTEST module film {shot}: {n} frames ({seconds:0.0} s)");
            UnityEngine.Object.Destroy(tex);
            UnityEngine.Object.Destroy(cam.gameObject);
            rt.Release();
        }

        /// A camera like the game's main one (its render effects, not its scripts), drawing into a 1280x720 picture.
        static Camera NewCamera(out RenderTexture rt, int w = 1280, int h = 720)
        {
            Vis.Init();
            var main = WorldDamage.MainCamera();
            GameObject go;
            if (main != null)
            {
                go = UnityEngine.Object.Instantiate(main.gameObject);
                foreach (Transform ch in go.transform) UnityEngine.Object.Destroy(ch.gameObject);
                foreach (var mb in go.GetComponents<Behaviour>())
                {
                    if (mb is Camera) continue;
                    string n = mb.GetType().FullName ?? "";
                    bool render = n.Contains("PostProcess") || n.Contains("Render") || n.Contains("Fog") || n.Contains("Sky") || n.Contains("Atmos") || n.Contains("Scatter") || n.Contains("Occlusion") || n.Contains("Celestial");
                    if (mb is AudioListener || !render) UnityEngine.Object.Destroy(mb);
                }
                go.name = "autotest module camera";
            }
            else go = new GameObject("autotest module camera", typeof(Camera));
            var cam = go.GetComponent<Camera>();
            cam.cullingMask = WorldDamage.WorldCameraMask() & ~(1 << Vis.Layer);
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 4000f;
            cam.enabled = false;
            rt = new RenderTexture(w, h, 24) { antiAliasing = 1 };
            cam.targetTexture = rt;
            return cam;
        }
    }
}
