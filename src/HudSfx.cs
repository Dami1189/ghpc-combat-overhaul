using System;
using System.Collections;
using System.IO;
using MelonLoader;
using UnityEngine;
using UnityEngine.Networking;

namespace GHPCHitCam
{
    /// Arcade HUD sounds, synthesized at start (no game or War Thunder assets): a short tick when your round strikes a
    /// vehicle, a metallic ring when it penetrates / does real damage, and a low boom with a rising two-note chime for a kill.
    /// Your own files replace them: UserData/HitCam/sfx/hit|pen|kill .ogg / .wav / .mp3 (loaded once per game start).
    /// Played through Unity audio (GHPC plays its music and crew voices that way too), 2D, volume ArcadeSfxVolume.
    internal static class HudSfx
    {
        const int Rate = 44100;
        static AudioSource _src;
        static AudioClip _hit, _pen, _kill;
        public static string LastPlayed = "";
        public static float LastPlayedAt = -99f;
        static readonly float[] _probe = new float[1024];

        public static void Hit() => Play(_hit, "hit", 0.85f);
        public static void Pen() => Play(_pen, "pen", 1f);
        public static void Kill() => Play(_kill, "kill", 1f);

        static void Play(AudioClip clip, string name, float gain)
        {
            if (!Settings.ArcadeSfx.Value || !Settings.ArcadeHud.Value) return;
            try
            {
                Ensure();
                clip = name == "hit" ? _hit : name == "pen" ? _pen : _kill;
                if (_src == null || clip == null) return;
                _src.PlayOneShot(clip, Mathf.Clamp01(Settings.ArcadeSfxVolume.Value) * gain);
                LastPlayed = name;
                LastPlayedAt = Time.unscaledTime;
            }
            catch (Exception e) { HitCamMod.Log.Warning("HUD sound failed: " + e.Message); }
        }

        /// Loudest sample Unity is putting out right now (self-test: proves the sound reaches the mix).
        public static float OutputPeak()
        {
            try
            {
                float m = 0f;
                for (int ch = 0; ch < 2; ch++)
                {
                    AudioListener.GetOutputData(_probe, ch);
                    for (int i = 0; i < _probe.Length; i++) m = Mathf.Max(m, Mathf.Abs(_probe[i]));
                }
                return m;
            }
            catch { return -1f; }
        }

        static bool _customTried;
        public static string CustomLoaded = "";

        /// Looks for the player's own sounds once (UserData/HitCam/sfx), loading them in the background.
        public static void Preload()
        {
            if (_customTried) return;
            _customTried = true;
            try
            {
                Ensure();
                string dir = Path.Combine(HitCamMod.DataDir, "sfx");
                Directory.CreateDirectory(dir);
                string note = Path.Combine(dir, "put your sounds here.txt");
                if (!File.Exists(note))
                    File.WriteAllText(note, "Your own arcade HUD sounds: hit.ogg (your round strikes a vehicle), pen.ogg (penetration / real damage), kill.ogg.\r\n"
                        + ".wav and .mp3 work too. They replace the generated sounds at the next game start; delete them to go back.\r\n");
                foreach (var name in new[] { "hit", "pen", "kill" })
                    foreach (var (ext, type) in new[] { (".ogg", AudioType.OGGVORBIS), (".wav", AudioType.WAV), (".mp3", AudioType.MPEG) })
                    {
                        string f = Path.Combine(dir, name + ext);
                        if (!File.Exists(f)) continue;
                        MelonCoroutines.Start(LoadClip(name, f, type));
                        break;
                    }
            }
            catch (Exception e) { HitCamMod.Log.Warning("HUD sounds: custom files: " + e.Message); }
        }

        static IEnumerator LoadClip(string name, string path, AudioType type)
        {
            var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
            yield return req.SendWebRequest();
            try
            {
                if (req.result != UnityWebRequest.Result.Success) { HitCamMod.Log.Warning($"HUD sounds: could not load {path}: {req.error}"); yield break; }
                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null || clip.length <= 0f) { HitCamMod.Log.Warning("HUD sounds: empty clip in " + path); yield break; }
                clip.name = "hitcam_custom_" + name;
                if (name == "hit") _hit = clip; else if (name == "pen") _pen = clip; else _kill = clip;
                CustomLoaded += (CustomLoaded.Length > 0 ? ", " : "") + Path.GetFileName(path);
                HitCamMod.Log.Msg($"HUD sounds: using your {Path.GetFileName(path)} ({clip.length:0.00} s)");
            }
            finally { req.Dispose(); }
        }

        static void Ensure()
        {
            if (_src != null) return;
            var go = new GameObject("HitCam HUD sounds") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _src = go.AddComponent<AudioSource>();
            go.AddComponent<SfxMeter>();
            _src.playOnAwake = false;
            _src.spatialBlend = 0f;
            _src.priority = 0;
            _src.bypassReverbZones = true;
            _hit = Make("hitcam_hit", 0.09f, HitWave);
            _pen = Make("hitcam_pen", 0.45f, PenWave);
            _kill = Make("hitcam_kill", 1.1f, KillWave);
        }

        static AudioClip Make(string name, float seconds, Func<float, float> wave)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            float peak = 1e-6f;
            for (int i = 0; i < n; i++) { data[i] = wave(i / (float)Rate); peak = Mathf.Max(peak, Mathf.Abs(data[i])); }
            float g = 0.85f / peak;
            int fade = Mathf.Min(n / 4, Rate / 200);
            for (int i = 0; i < n; i++)
            {
                data[i] *= g;
                if (i > n - fade) data[i] *= (n - i) / (float)fade;   // no click at the end
            }
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// Loudest sample our own source has put out since the last reset (after its volume, before the game's master volume).
        public static float MeterPeak => SfxMeter.Peak;
        public static void ResetMeter() => SfxMeter.Peak = 0f;

        static float Env(float t, float attack, float tau) => (t < attack ? t / attack : 1f) * Mathf.Exp(-Mathf.Max(t - attack, 0f) / tau);

        // deterministic noise (no UnityEngine.Random state touched)
        static uint _seed = 1234567;
        static float Noise() { _seed = _seed * 1664525u + 1013904223u; return ((_seed >> 9) / 4194304f) - 1f; }

        static float _hp;
        /// Tick: a bright click (high-passed noise + 2.6 kHz) over a tiny thump.
        static float HitWave(float t)
        {
            if (t == 0f) { _hp = 0f; _seed = 99; }
            float n = Noise();
            float hp = n - _hp; _hp = n;
            return 0.6f * hp * Env(t, 0.0005f, 0.004f)
                 + 0.5f * Mathf.Sin(2f * Mathf.PI * 2600f * t) * Env(t, 0.001f, 0.014f)
                 + 0.45f * Mathf.Sin(2f * Mathf.PI * 520f * t) * Env(t, 0.001f, 0.012f);
        }

        /// Penetration / real damage: a struck-steel ring (inharmonic partials) on a short low knock.
        static float PenWave(float t)
        {
            float ring = 0.55f * Mathf.Sin(2f * Mathf.PI * 1180f * t) * Env(t, 0.002f, 0.13f)
                       + 0.35f * Mathf.Sin(2f * Mathf.PI * 1812f * t) * Env(t, 0.002f, 0.085f)
                       + 0.22f * Mathf.Sin(2f * Mathf.PI * 2963f * t) * Env(t, 0.002f, 0.05f);
            float knock = 0.7f * Mathf.Sin(2f * Mathf.PI * 170f * t) * Env(t, 0.002f, 0.035f);
            return ring + knock;
        }

        /// Kill: a falling low boom with a burst of noise, then two rising chime notes.
        static float KillWave(float t)
        {
            if (t == 0f) _seed = 4242;
            float f = Mathf.Lerp(95f, 48f, Mathf.Clamp01(t / 0.35f));
            float boom = 0.9f * Mathf.Sin(2f * Mathf.PI * f * t) * Env(t, 0.004f, 0.24f) + 0.3f * Noise() * Env(t, 0.001f, 0.05f);
            float c1 = t > 0.08f ? Mathf.Sin(2f * Mathf.PI * 880f * (t - 0.08f)) * Env(t - 0.08f, 0.004f, 0.22f) : 0f;
            float c2 = t > 0.24f ? Mathf.Sin(2f * Mathf.PI * 1320f * (t - 0.24f)) * Env(t - 0.24f, 0.004f, 0.32f) : 0f;
            return boom + 0.32f * c1 + 0.32f * c2;
        }
    }

    /// Sits on the sound object: Unity hands it the source's samples on the audio thread (self-test: proves the clips play).
    internal class SfxMeter : MonoBehaviour
    {
        public static volatile float Peak;

        void OnAudioFilterRead(float[] data, int channels)
        {
            float m = Peak;
            for (int i = 0; i < data.Length; i++) { float a = data[i] < 0f ? -data[i] : data[i]; if (a > m) m = a; }
            Peak = m;
        }
    }
}
