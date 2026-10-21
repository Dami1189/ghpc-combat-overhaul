using System;
using GHPC;
using GHPC.AI;
using UnityEngine;

namespace GHPCHitCam
{
    /// The vehicle class of a unit, War Thunder style, and our own icon for it (drawn in code into one small atlas; no
    /// War Thunder assets). The class comes from the game's own unit data: UnitType (ground / air / emplacement / infantry)
    /// and the short name its crews call it by (TargetShortNameUs: Tank, Pc, Truck, Antitank, Antiair, Troops, Chopper,
    /// FastMover, ...); tanks are split light / medium / heavy by mass.
    internal enum MapClass { LightTank, MediumTank, HeavyTank, TankDestroyer, Spaa, Ifv, Truck, Infantry, AntiTankTeam, Emplacement, Helicopter, Plane, Unknown }

    internal static class MapIcons
    {
        const int Cell = 64, Cols = 4;
        static Texture2D _atlas;

        public static string Name(MapClass c)
        {
            switch (c)
            {
                case MapClass.LightTank: return "light tank";
                case MapClass.MediumTank: return "medium tank";
                case MapClass.HeavyTank: return "heavy tank";
                case MapClass.TankDestroyer: return "tank destroyer";
                case MapClass.Spaa: return "SPAA";
                case MapClass.Ifv: return "IFV / APC";
                case MapClass.Truck: return "truck";
                case MapClass.Infantry: return "infantry";
                case MapClass.AntiTankTeam: return "anti-tank team";
                case MapClass.Emplacement: return "emplacement";
                case MapClass.Helicopter: return "helicopter";
                case MapClass.Plane: return "plane";
                default: return "unknown";
            }
        }

        public static MapClass Classify(Unit u)
        {
            if (u == null) return MapClass.Unknown;
            TargetShortNameUs sn = TargetShortNameUs.Tank;
            try { sn = u.ShortNameUs; } catch { }
            switch (u.Type)
            {
                case UnitType.AirVehicle:
                    return sn == TargetShortNameUs.FastMover ? MapClass.Plane : MapClass.Helicopter;
                case UnitType.Infantry:
                    return sn == TargetShortNameUs.Antitank || sn == TargetShortNameUs.Missile ? MapClass.AntiTankTeam : MapClass.Infantry;
                case UnitType.Emplacement:
                    if (sn == TargetShortNameUs.Antitank || sn == TargetShortNameUs.Missile) return MapClass.AntiTankTeam;
                    if (sn == TargetShortNameUs.Antiair) return MapClass.Spaa;
                    return MapClass.Emplacement;
            }
            switch (sn)
            {
                case TargetShortNameUs.Antitank: case TargetShortNameUs.Missile: return MapClass.TankDestroyer;
                case TargetShortNameUs.Antiair: return MapClass.Spaa;
                case TargetShortNameUs.Pc: case TargetShortNameUs.Troops: return MapClass.Ifv;
                case TargetShortNameUs.Truck: return MapClass.Truck;
                case TargetShortNameUs.Bunker: case TargetShortNameUs.Emplacement: case TargetShortNameUs.Building: return MapClass.Emplacement;
                case TargetShortNameUs.Chopper: return MapClass.Helicopter;
                case TargetShortNameUs.FastMover: return MapClass.Plane;
            }
            // a tank: by weight (PT-76 ~14 t light; T-55 .. M1 medium, as War Thunder files them; heavier than 62 t heavy)
            float t = 40f;
            try
            {
                var rb = u.GetComponent<Rigidbody>() ?? u.GetComponentInChildren<Rigidbody>();
                if (rb != null && rb.mass > 5000f) t = rb.mass / 1000f;   // a believable vehicle mass, else leave it medium
            }
            catch { }
            return t < 25f ? MapClass.LightTank : t > 62f ? MapClass.HeavyTank : MapClass.MediumTank;
        }

        /// The icon's part of the atlas (for GUI.DrawTextureWithTexCoords and GL texture coordinates).
        public static Rect Uv(MapClass c)
        {
            int i = (int)c;
            float s = 1f / Cols;
            return new Rect((i % Cols) * s, 1f - (i / Cols + 1) * s, s, s);
        }

        public static Texture2D Atlas
        {
            get
            {
                if (_atlas == null) Build();
                return _atlas;
            }
        }

        // ------------------------------------------------------------------ drawing the icons

        // signed distances in icon units (-1..1, y up)
        static float Seg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return (p - (a + ab * t)).magnitude;
        }
        static float Rhombus(Vector2 p, float r) => (Mathf.Abs(p.x) + Mathf.Abs(p.y) - r) * 0.7071f;
        static float Box(Vector2 p, float a, float b) { float dx = Mathf.Abs(p.x) - a, dy = Mathf.Abs(p.y) - b; return Mathf.Max(dx, dy) > 0f ? new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude : Mathf.Max(dx, dy); }
        static float Circle(Vector2 p, float r) => p.magnitude - r;
        static float TriDown(Vector2 p)
        {
            // corners (-0.8, 0.6), (0.8, 0.6), (0, -0.78)
            float top = p.y - 0.6f;
            float l = Vector2.Dot(p - new Vector2(-0.8f, 0.6f), new Vector2(-0.865f, -0.502f));
            float r = Vector2.Dot(p - new Vector2(0.8f, 0.6f), new Vector2(0.865f, -0.502f));
            return Mathf.Max(top, Mathf.Max(l, r));
        }
        static float Ring(float d, float t) => Mathf.Abs(d) - t * 0.5f;
        static float Line(Vector2 p, Vector2 a, Vector2 b, float t) => Seg(p, a, b) - t * 0.5f;
        static float U(float a, float b) => Mathf.Min(a, b);

        /// The shape of each class (distance < 0 = ink).
        static float Shape(MapClass c, Vector2 p)
        {
            const float T = 0.17f;
            switch (c)
            {
                case MapClass.LightTank: return Ring(Rhombus(p, 0.82f), T);
                case MapClass.MediumTank: return U(Ring(Rhombus(p, 0.82f), T), Line(p, new Vector2(-0.42f, 0f), new Vector2(0.42f, 0f), T));
                case MapClass.HeavyTank: return U(Ring(Rhombus(p, 0.82f), T), Rhombus(p, 0.42f));
                case MapClass.TankDestroyer: return U(Ring(TriDown(p), T), Line(p, new Vector2(-0.38f, 0.28f), new Vector2(0.38f, 0.28f), T));
                case MapClass.Spaa:
                    return U(Ring(Rhombus(p, 0.82f), T), U(Line(p, new Vector2(-0.3f, -0.2f), new Vector2(0f, 0.22f), T), Line(p, new Vector2(0f, 0.22f), new Vector2(0.3f, -0.2f), T)));
                case MapClass.Ifv: return U(Ring(Box(p, 0.72f, 0.46f), T), Circle(p, 0.17f));
                case MapClass.Truck: return U(Ring(Box(p, 0.66f, 0.4f), T), Line(p, new Vector2(0.2f, -0.4f), new Vector2(0.2f, 0.4f), T * 0.8f));
                case MapClass.Infantry: return U(Ring(Circle(p, 0.5f), T), Circle(p, 0.2f));
                case MapClass.AntiTankTeam:
                    return U(Ring(Circle(p, 0.62f), T), U(Line(p, new Vector2(0f, -0.38f), new Vector2(0f, 0.36f), T),
                        U(Line(p, new Vector2(-0.24f, 0.12f), new Vector2(0f, 0.38f), T), Line(p, new Vector2(0.24f, 0.12f), new Vector2(0f, 0.38f), T))));
                case MapClass.Emplacement: return U(Ring(Box(p, 0.55f, 0.55f), T), Box(p, 0.16f, 0.16f));
                case MapClass.Helicopter:
                    return U(Line(p, new Vector2(-0.88f, 0.5f), new Vector2(0.88f, 0.5f), 0.12f), U(Circle(p - new Vector2(0f, 0.05f), 0.3f),
                        U(Line(p, new Vector2(0f, 0.3f), new Vector2(0f, 0.5f), 0.12f), U(Line(p, new Vector2(0f, -0.2f), new Vector2(0f, -0.82f), 0.13f),
                        Line(p, new Vector2(-0.2f, -0.82f), new Vector2(0.2f, -0.82f), 0.12f)))));
                case MapClass.Plane:
                    return U(Line(p, new Vector2(0f, -0.82f), new Vector2(0f, 0.84f), 0.18f), U(Line(p, new Vector2(-0.86f, 0.08f), new Vector2(0.86f, 0.08f), 0.2f),
                        Line(p, new Vector2(-0.36f, -0.72f), new Vector2(0.36f, -0.72f), 0.14f)));
                default: return Circle(p, 0.28f);
            }
        }

        /// White ink with a dark rim (tinting with GUI.color / GL colour keeps the rim dark), anti-aliased.
        static void Build()
        {
            int rows = (Enum.GetValues(typeof(MapClass)).Length + Cols - 1) / Cols;
            int w = Cols * Cell, h = Cols * Cell;
            _atlas = new Texture2D(w, h, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[w * h];
            float unit = Cell * 0.5f / 1.12f;          // pixels per icon unit (a little margin for the rim)
            float rim = 2.6f / unit;                    // dark rim width in icon units
            foreach (MapClass c in Enum.GetValues(typeof(MapClass)))
            {
                int i = (int)c, cx = (i % Cols) * Cell, cy = h - (i / Cols + 1) * Cell;
                for (int y = 0; y < Cell; y++)
                    for (int x = 0; x < Cell; x++)
                    {
                        Vector2 p = new Vector2((x + 0.5f - Cell * 0.5f) / unit, (y + 0.5f - Cell * 0.5f) / unit);
                        float d = Shape(c, p);
                        float ink = Mathf.Clamp01(0.5f - d * unit);
                        float edge = Mathf.Clamp01(0.5f - (d - rim) * unit) * 0.85f;
                        float a = Mathf.Max(ink, edge);
                        byte v = (byte)(a > 0.001f ? Mathf.RoundToInt(255f * ink / a) : 0);
                        px[(cy + y) * w + cx + x] = new Color32(v, v, v, (byte)Mathf.RoundToInt(a * 255f));
                    }
            }
            _atlas.SetPixels32(px);
            _atlas.Apply(true);
            if (rows > Cols) HitCamMod.Log.Warning("Map icons: atlas too small");
        }
    }
}
