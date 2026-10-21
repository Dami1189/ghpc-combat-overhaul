using System.Linq;
using UnityEngine;

namespace GHPCHitCam
{
    /// IMGUI overlay: the picture-in-picture, result banner, module/crew status row and event list.
    internal static class HitCamUI
    {
        static GUIStyle _title, _text, _small, _chip;
        static Texture2D _white;
        static int _styleFor;
        public static Rect PipRect;

        // War Thunder's own HUD colours for module state
        static readonly Color Olive = new Color32(0x90, 0x9E, 0x35, 255);
        static readonly Color Amber = new Color32(0xF6, 0xB2, 0x36, 255);
        static readonly Color WtRed = new Color32(0xFD, 0x00, 0x01, 255);
        static readonly Color Black = new Color(0.12f, 0.12f, 0.12f, 1f);

        static void Styles(float k)
        {
            int key = Mathf.RoundToInt(k * 100);
            if (_title != null && key == _styleFor) return;
            _styleFor = key;
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            _title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(26 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            _text = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(14 * k), alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            _small = new GUIStyle(_text) { fontSize = Mathf.RoundToInt(12 * k) };
            _chip = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(11 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }

        public static void Draw()
        {
            var p = HitCamPlayer.Current;
            var rt = HitCamPlayer.Texture;
            if (p == null || rt == null) return;
            if (ArmorSim.FullScreenHitCam) return;   // the Armor Sim draws its shots full screen itself
            float a = p.Alpha;
            if (a <= 0.001f) return;

            float w = rt.width, h = rt.height;
            float k = h / 360f;
            Styles(k);
            float m = 14f;
            string corner = Settings.PipCorner.Value ?? "TopRight";
            bool right = !corner.Contains("Left");
            bool top = !corner.Contains("Bottom");
            float extra = 26f * k + 7 * 19f * k;   // status row + event list below / above the picture
            float x = right ? Screen.width - w - m : m;
            float y = top ? m : Screen.height - h - m - extra;
            PipRect = new Rect(x, y, w, h);

            var old = GUI.color;
            // frame + picture
            Box(new Rect(x - 3, y - 3, w + 6, h + 6), new Color(0f, 0f, 0f, 0.65f * a));
            GUI.color = new Color(1, 1, 1, a);
            GUI.DrawTexture(PipRect, rt, ScaleMode.StretchToFill, false);
            GUI.color = Color.white;   // everything below carries its own alpha
            bool cine = Settings.CinematicXray.Value;
            if (cine && p.RedFlash > 0.01f) Box(PipRect, new Color(0.85f, 0.08f, 0.05f, 0.5f * p.RedFlash * a));
            if (cine && p.BlastFlash > 0.01f) Box(PipRect, new Color(1f, 0.55f, 0.15f, 0.5f * p.BlastFlash * a));   // an explosion inside lights up the whole view

            var rec = p.Latest;
            // info strip
            Box(new Rect(x, y + h - 20f * k, w, 20f * k), new Color(0f, 0f, 0f, 0.55f * a));
            string ammo = rec.Ammo != null ? rec.Ammo.Name : "?";
            string info = $"{ammo}  →  {rec.TargetName}" + (rec.RootShot != null && rec.RootShot.Distance > 0 ? $"   ·   {rec.RootShot.Distance:0} m" : "")
                + (p.Hits > 1 ? $"   ·   hits {p.Hits}" : "");
            Shadow(new Rect(x + 8 * k, y + h - 20f * k, w - 16 * k, 20f * k), info, _small, new Color(0.9f, 0.92f, 0.95f, a));

            // title banner (War Thunder shows it for ~3 s)
            if (p.TitleWall >= 0f)
            {
                float age = p.T - p.TitleWall;
                float ta = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01((3.2f - age) / 0.4f) * a;
                if (ta > 0f)
                {
                    var tr = new Rect(x, y + 10f * k, w, 34f * k);
                    Box(new Rect(x + w * 0.12f, y + 12f * k, w * 0.76f, 30f * k), new Color(0f, 0f, 0f, 0.45f * ta));
                    var c = p.TitleColor; c.a = ta;
                    Shadow(tr, p.TitleShown ?? rec.Title, _title, c);
                }
            }

            // status row: modules + crew
            float ry = top ? y + h + 6f : y - 6f - 22f * k;
            float cx = x;
            float cw = 46f * k, ch = 20f * k;
            foreach (var (cat, label, hp) in p.Debuffs())
            {
                Chip(new Rect(cx, ry, cw, ch), label, hp, a);
                cx += cw + 4f;
            }
            var (alive, total) = p.CrewCount();
            if (total > 0)
            {
                float hp = alive == total ? 100f : alive == 0 ? 0f : alive * 100f / total <= 50f ? 20f : 60f;
                Chip(new Rect(cx, ry, cw * 1.35f, ch), $"CREW {alive}/{total}", hp, a);
            }

            // event list
            var shown = p.Shown.OrderBy(s => s.wall).ToList();
            int max = 7;
            int start = Mathf.Max(0, shown.Count - max);
            float ly = top ? ry + ch + 6f : y + h + 6f;
            for (int i = start; i < shown.Count; i++)
            {
                var (e, wall) = shown[i];
                float age = p.T - wall;
                Color c = Sev(e.Severity);
                float flash = Mathf.Clamp01(1f - age / 0.35f);
                c = Color.Lerp(c, Color.white, flash * 0.6f);
                c.a = a * Mathf.Clamp01(age / 0.12f);
                var lr = new Rect(x, ly, w, 19f * k);
                Box(lr, new Color(0f, 0f, 0f, 0.42f * c.a));
                if (e.Severity >= 3 && age < 0.6f)
                    Box(new Rect(x, ly, w * Mathf.Clamp01(age / 0.4f), 19f * k), new Color(0.75f, 0.05f, 0.03f, 0.35f * a));   // WT-style red sweep
                Shadow(new Rect(x + 8 * k, ly, w - 16 * k, 19f * k), "•  " + e.Text, _text, c);
                ly += 19f * k + 1f;
            }
            GUI.color = old;
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

        static void Chip(Rect r, string label, float hp, float a)
        {
            Color c = hp <= 0f ? Black : hp <= 25f ? WtRed : hp <= 75f ? Amber : Olive;
            Box(r, new Color(c.r, c.g, c.b, 0.85f * a));
            Shadow(r, label, _chip, new Color(1f, 1f, 1f, a));
            if (hp <= 0f) Box(new Rect(r.x + 2, r.center.y - 1, r.width - 4, 2), new Color(1f, 0.2f, 0.15f, a));
        }

        static void Box(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        static void Shadow(Rect r, string text, GUIStyle st, Color c)
        {
            var old = st.normal.textColor;
            st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.85f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, st);
            st.normal.textColor = c;
            GUI.Label(r, text, st);
            st.normal.textColor = old;
        }
    }
}
