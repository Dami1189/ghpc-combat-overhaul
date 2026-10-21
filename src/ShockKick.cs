using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// The blast front reaching the camera: a short kick of the view, sized by the front's positive-phase impulse (what
    /// shoves a body or a vehicle for a pulse this short), arriving with the sound (BlastSound starts it). Below 0.4 Pa*s
    /// nothing: a 0.75 kg shell stops kicking at ~300 m, a 20 kg rack at ~2 km. Strength log2(impulse / threshold):
    /// 0.18 deg a step in a 60 deg view, scaled down with the field of view (to a tenth in a narrow sight, so a gunner
    /// sees a light jolt, not a lost aim), plus a few cm of push in wide views. A shove away from the blast (rocking about
    /// the horizontal axis across its direction) that peaks after twice the front's positive phase (22-60 ms: a big charge
    /// shoves longer), then a ring that dies in ~0.5 s, lower for bigger charges. The first frame after the peak always
    /// shows it in full (it would fall between two slow frames).
    /// Applied to the game cameras' view matrices just before they render and taken off again at the end of the frame. The
    /// camera transforms are never touched: the game's own camera code turns the camera incrementally, and its tree and
    /// grass renderer (GPU instancing, culled in Camera.onPreCull) keeps the camera rotation it culled for and checks it
    /// against the transform: a kicked transform failed that check every frame and the trees vanished for the whole kick.
    internal static class ShockKick
    {
        const float Threshold = 0.4f;     // Pa*s

        class Kick { public float T0, Deg, Push, Freq, Rise = 0.022f, Roll, Norm = 1f, MaxApplied; public bool PeakShown; public Vector3 Away; public string Note; }
        static readonly List<Kick> _kicks = new List<Kick>();
        static readonly List<Camera> _kicked = new List<Camera>();
        static bool _installed;
        // self-test kicktransform=true: the old way (the camera's transform moved), to show what it did to the trees
        static Transform _oldT;
        static Vector3 _oldLp;
        static Quaternion _oldLr;
        static int _frame = -1;

        public static int Count;
        public static float MaxApplied;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            Camera.onPreCull += PreCull;
            var go = new GameObject("HitCam shock kick") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<KickRestore>();
        }

        public static void Reset()
        {
            Restore();
            _kicks.Clear();
        }

        static float ViewScale(float fov) => Mathf.Clamp(fov / 60f, 0.1f, 1f);

        public static string Add(Vector3 blast, Vector3 cam, float peakPa, float impulse, float wKg, float positiveSeconds)
        {
            if (impulse < Threshold) return $"no kick (impulse {impulse:0.00} Pa*s, below {Threshold:0.0})";
            float s = Mathf.Min(Mathf.Log(impulse / Threshold, 2f), 6f);
            Vector3 away = cam - blast;
            away.y *= 0.3f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
            float w3 = Mathf.Pow(Mathf.Max(wKg, 0.01f), 1f / 3f);
            var k = new Kick
            {
                T0 = Time.time, Deg = 0.18f * s, Push = 0.025f * s, Freq = Mathf.Clamp(13f / Mathf.Pow(w3, 0.35f), 5f, 14f),
                Away = away, Roll = (Random.value < 0.5f ? -1f : 1f) * 0.35f, Rise = Mathf.Clamp(2f * positiveSeconds, 0.022f, 0.06f),
            };
            // the ring adds onto the shove: scale the curve so its peak is the kick's size
            float top = 0f;
            for (float tau = 0f; tau < 0.9f; tau += 0.001f) top = Mathf.Max(top, Mathf.Abs(Shape(tau, k.Freq, k.Rise)));
            k.Norm = top > 0.01f ? 1f / top : 1f;
            _kicks.Add(k);
            Count++;
            var c = GHPC.Camera.CameraManager.MainCam;
            float fov = c != null ? c.fieldOfView : 60f;
            k.Note = $"kick {k.Deg:0.000} deg";
            if (AutoTest.Opt("kickcap", false))
                MelonLoader.MelonCoroutines.Start(AutoTest.ScreenFrames(AutoTest.Opt("kicktransform", false) ? "kick_old" : "kick_new", 0f, 0.05f, 0.12f, 0.25f, 0.45f, 0.8f, 1.6f));
            return $"kick {k.Deg:0.000} deg in a 60 deg view = {k.Deg * ViewScale(fov):0.000} deg in this {fov:0.#} deg view, push {(fov > 25f ? k.Push * 100f : 0f):0.0} cm, shove peaks at {k.Rise * 1000f:0} ms, ring {k.Freq:0.#} Hz (impulse {impulse:0.00} Pa*s = {impulse / Threshold:0.0}x the threshold)";
        }

        static void PreCull(Camera c) { using (Perf.T("ShockKick.PreCull")) TimedPreCull(c); }
        static void TimedPreCull(Camera c)
        {
            if (_frame == Time.frameCount || c == null || c.targetTexture != null) return;
            if (_kicks.Count == 0) return;
            var main = GHPC.Camera.CameraManager.MainCam;
            if (main == null || !main.isActiveAndEnabled) return;
            _frame = Time.frameCount;
            float now = Time.time;
            if (Time.timeScale <= 0f || !Settings.ShockwaveShake.Value || ArmorSim.Active) return;
            var t = main.transform;
            float view = ViewScale(main.fieldOfView);
            Quaternion q = Quaternion.identity;
            Vector3 push = Vector3.zero;
            for (int i = _kicks.Count - 1; i >= 0; i--)
            {
                var k = _kicks[i];
                float tau = now - k.T0;
                if (tau > 0.9f)
                {
                    if (AutoTest.Active) HitCamMod.Log.Msg($"Shock kick: {k.Note} done, largest applied {k.MaxApplied:0.000} deg (view {main.fieldOfView:0.#} deg)");
                    _kicks.RemoveAt(i);
                    continue;
                }
                if (tau < 0f) continue;
                float a = Shape(tau, k.Freq, k.Rise) * k.Norm;
                // the shove's peak lasts a few ms: on slow frames it falls between two of them, so the first frame after it
                // shows it in full, then the curve goes on
                if (!k.PeakShown && tau >= k.Rise * 0.6f) { k.PeakShown = true; a = Mathf.Max(a, 1f); }
                float deg = k.Deg * view * a;
                Vector3 axis = Vector3.Cross(Vector3.up, k.Away);
                axis = axis.sqrMagnitude > 1e-4f ? axis.normalized : t.right;
                q = Quaternion.AngleAxis(deg, axis) * Quaternion.AngleAxis(deg * k.Roll, t.forward) * q;
                if (main.fieldOfView > 25f) push += k.Away * k.Push * a;
                k.MaxApplied = Mathf.Max(k.MaxApplied, Mathf.Abs(deg));
                MaxApplied = Mathf.Max(MaxApplied, Mathf.Abs(deg));
            }
            if (q == Quaternion.identity && push == Vector3.zero) return;
            Restore();
            if (AutoTest.Opt("kicktransform", false))
            {
                _oldT = t; _oldLp = t.localPosition; _oldLr = t.localRotation;
                t.rotation = q * t.rotation;
                t.position += push;
                return;
            }
            // the kick as a world-space move of the camera rig: turn about the main camera, then push
            Matrix4x4 kick = Matrix4x4.Translate(t.position + push) * Matrix4x4.Rotate(q) * Matrix4x4.Translate(-t.position);
            var flip = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));   // Unity's view space looks down -z
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || cam.targetTexture != null || (cam != main && !cam.transform.IsChildOf(t))) continue;
                cam.worldToCameraMatrix = flip * (kick * cam.transform.localToWorldMatrix).inverse;
                _kicked.Add(cam);
            }
        }

        /// A shove peaking after `rise`, then a ring dying in ~0.5 s.
        static float Shape(float tau, float freq, float rise) =>
            tau / rise * Mathf.Exp(1f - tau / rise) + 0.4f * Mathf.Sin(2f * Mathf.PI * freq * tau) * Mathf.Exp(-tau / 0.16f) * (1f - Mathf.Exp(-tau / 0.012f));

        internal static void Restore()
        {
            if (_oldT != null) { _oldT.localPosition = _oldLp; _oldT.localRotation = _oldLr; _oldT = null; }
            if (_kicked.Count == 0) return;
            foreach (var cam in _kicked) if (cam != null) cam.ResetWorldToCameraMatrix();
            _kicked.Clear();
        }
    }

    /// Takes the kick off the camera once the frame is drawn.
    internal class KickRestore : MonoBehaviour
    {
        IEnumerator Start()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                ShockKick.Restore();
            }
        }
    }
}
