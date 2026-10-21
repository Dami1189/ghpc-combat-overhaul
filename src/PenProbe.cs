using GHPC;
using GHPC.Equipment;
using GHPC.PhysicsHelpers;
using GHPC.Utility;
using GHPC.Weapons;
using UnityEngine;

namespace GHPCHitCam
{
    internal enum PenVerdict { None, Pen, Maybe, NoPen, Ricochet, NotArmour, Friendly }

    /// What a round would meet along a ray, judged with the game's own rules (read-only, never fires anything).
    internal struct PenResult
    {
        public PenVerdict Verdict;
        public bool Hit;              // the ray hit something within range
        public Vector3 Point, Normal; // first surface hit (the first plate when it is a vehicle)
        public float Distance;        // m from the ray origin to that surface
        public Unit Unit;             // vehicle struck (null: terrain / scenery)
        public string Blocker;        // NotArmour: the scenery object that stops the round (null = the ground)
        public string Plate;          // first plate's name
        public float FirstLos;        // mm RHA, line of sight, first plate only
        public float Los;             // mm RHA from the outside to the crew compartment (all plates, ERA, HEAT stand-off)
        public float Angle;           // degrees between the round's path and the first plate's surface (90 = face-on)
        public float RicochetAngle;   // the ammo's certain-ricochet angle (game rule: grazing below it always bounces)
        public float Pen;             // mm RHA the round still has at this range
        public int Plates;
        public float Ratio => Los > 0.01f ? Pen / Los : 99f;
    }

    /// Pen indicator: walks a ray through a vehicle the way LiveRound.penCheck rates it and compares with the round's pen.
    /// - thickness: IArmor.GetLosThickness(CE for HEAT else KE, point, normal, dir, false), times the ammo's ArmorOptimizations
    /// - every plate until the crew compartment counts: the game takes LOS mm off the pen for each plate it passes
    ///   (v^2 scales with 1 - los/pen, and pen scales with v^2, so pen_after = pen - los); slat armour counts x5 against HEAT
    ///   (WeaponMath.GetArmorAlongPath). No stand-off loss for the jet: penCheck has a 75 mm/m term, but in game a 3BK18M lost
    ///   ~1 mm between a T-72M1's skirt and hull side (Armor Sim measurement, 2026-10-19)
    /// - the ground stops it, tree trunks don't (terrain-layer faces steeper than 85 deg, as penCheck), any other solid
    ///   collider without armour does (a wall, a hay bale)
    /// - ricochet: grazing angle below AmmoType.CertainRicochetAngle on a plate that CanRicochet (HEAT shells too: their body
    ///   touches the plate before the jet forms)
    /// - pen at range: KE/HE pen = RhaPenetration * (v/v0)^2 (LiveRound.CurrentPenRating), v from the game's drag model
    ///   (a = g - Coeff*SectionalArea*AirDensity/Mass * v|v|, BallisticEvaluatorRK4); HEAT / missiles keep their full pen
    /// Shared with the Armor Sim mode. At most 20 raycasts per call (usually 2-6), no allocations.
    internal static class PenProbe
    {
        public const float GreenRatio = 1.15f, RedRatio = 0.85f;
        static int _mask = -1;
        static readonly Color Green = new Color(0.25f, 0.95f, 0.3f), Yellow = new Color(1f, 0.85f, 0.15f), Red = new Color(1f, 0.2f, 0.15f),
            Grey = new Color(0.7f, 0.72f, 0.75f), Blue = new Color(0.45f, 0.75f, 1f);

        /// The mask the game's rounds use (vehicles' armour lives on "Raycast Only" colliders that switch on when a ray passes).
        public static int Mask
        {
            get
            {
                if (_mask == -1)
                {
                    try { _mask = ConstantsAndInfoManager.Instance.LiveRoundLayerMask.value; } catch { }
                    if (_mask == -1 || _mask == 0) { _mask = -1; return Physics.DefaultRaycastLayers; }
                }
                return _mask;
            }
        }

        public static void Reset() { _mask = -1; }

        public static Color ColorOf(PenVerdict v)
        {
            switch (v)
            {
                case PenVerdict.Pen: return Green;
                case PenVerdict.Maybe: return Yellow;
                case PenVerdict.NoPen: return Red;
                case PenVerdict.Ricochet: return Blue;
                default: return Grey;
            }
        }

        public static PenVerdict Judge(float pen, float los)
        {
            if (los <= 0.01f) return PenVerdict.Pen;
            float r = pen / los;
            return r > GreenRatio ? PenVerdict.Pen : r >= RedRatio ? PenVerdict.Maybe : PenVerdict.NoPen;
        }

        /// Drag constant of the game's ballistic model (1/m).
        public static float DragK(AmmoType a) => a == null || a.Mass <= 0f ? 0f : a.Coeff * a.SectionalArea * AirDensity / a.Mass;

        /// The air density live rounds fly through (WorldEnvironmentManager; the FCS firing tables assume 1.2).
        public static float AirDensity
        {
            get { try { return GHPC.World.WorldEnvironmentManager.AirDensity; } catch { return 1.25f; } }
        }

        /// Speed after flying `range` metres (flat fire: dv/dx = -k v).
        public static float SpeedAtRange(AmmoType a, float range)
        {
            if (a == null) return 0f;
            if (a.Guidance != 0) return a.MuzzleVelocity;
            return a.MuzzleVelocity * Mathf.Exp(-DragK(a) * Mathf.Max(range, 0f));
        }

        /// The round's pen rating after `range` metres, as LiveRound.CurrentPenRating works it out.
        public static float PenAtRange(AmmoType a, float range)
        {
            if (a == null) return 0f;
            float pen = a.RhaPenetration;
            if (a.Category == AmmoType.AmmoCategory.ShapedCharge || a.Guidance != 0) return pen;
            float v = SpeedAtRange(a, range) / Mathf.Max(a.MuzzleVelocity, 1f);
            float k = v * v;
            if (a.NutationPenaltyDistance > 0f && range < a.NutationPenaltyDistance)
                k *= 1f - 0.07f * (a.NutationPenaltyDistance - range) / a.NutationPenaltyDistance;
            return pen * k;
        }

        /// Walk a ray (origin, dir) and judge the first vehicle on it.
        /// roundDir: the round's own direction at impact (falling a bit on a long shot); zero = dir.
        /// penRange: range the pen is worked out for; negative = the hit distance + rangeOffset.
        /// ignore: the shooter (its own hull, barrel). own: the shooter's side (a friendly vehicle gives Friendly).
        /// penOverride: the round's pen when it gets there, if the caller already knows it (an integrated trajectory).
        public static bool Probe(Vector3 origin, Vector3 dir, float maxDist, AmmoType ammo, Unit ignore, Faction own, out PenResult r,
            Vector3 roundDir = default, float penRange = -1f, float rangeOffset = 0f, float penOverride = -1f)
        {
            r = default;
            r.Verdict = PenVerdict.None;
            if (ammo == null || maxDist <= 0f) return false;
            dir.Normalize();
            Vector3 rd = roundDir.sqrMagnitude > 0.5f ? roundDir.normalized : dir;
            var type = ammo.Category == AmmoType.AmmoCategory.ShapedCharge ? ResistanceType.CE : ResistanceType.KE;
            bool heat = ammo.Category == AmmoType.AmmoCategory.ShapedCharge;
            int mask = Mask;
            int terrain = CodeUtils.LAYER_INDEX_TERRAIN;

            Vector3 o = origin;
            float travelled = 0f, vehicleStart = 0f, lastPlate = 0f;
            bool onVehicle = false, passedHull = false;
            for (int i = 0; i < 20; i++)
            {
                float left = maxDist - travelled;
                if (left <= 0.01f) break;
                if (!RaycastColliderUtils.Raycast(new Ray(o, dir), out RaycastHit hit, left, mask)) break;
                var col = hit.collider;
                float d = travelled + hit.distance;
                o = hit.point + dir * 0.004f;
                travelled = d + 0.004f;
                if (col == null) continue;
                var go = col.gameObject;

                if (!onVehicle)
                {
                    if (ignore != null && BelongsTo(col, ignore)) continue;
                    if (go.layer == terrain)
                    {
                        if (Vector3.Angle(Vector3.up, hit.normal) > 85f) continue;   // a tree trunk: rounds go through

                        r.Hit = true; r.Point = hit.point; r.Normal = hit.normal; r.Distance = d; r.Verdict = PenVerdict.NotArmour;
                        return true;
                    }
                    if (col.isTrigger && !go.CompareTag("Compartment")) continue;   // detection zones and the like
                    col.TryGetComponent(out IArmor armor0);
                    if (armor0 == null)
                    {
                        if (go.CompareTag("Penetrable") || go.CompareTag("Target") || go.CompareTag("Compartment")) continue;
                        // a wall, a tree, a rock: the round stops on it
                        r.Hit = true; r.Point = hit.point; r.Normal = hit.normal; r.Distance = d; r.Verdict = PenVerdict.NotArmour;
                        r.Unit = CodeUtils.GetParentUnitFollowAware(col.transform);
                        r.Blocker = go.name;
                        return true;
                    }
                    onVehicle = true;
                    vehicleStart = d;
                    r.Hit = true;
                    r.Point = hit.point;
                    r.Normal = hit.normal;
                    r.Distance = d;
                    r.Unit = armor0.Unit as Unit ?? CodeUtils.GetParentUnitFollowAware(col.transform);
                    r.Plate = armor0.Name;
                    r.Angle = 90f - Vector3.Angle(hit.normal, -rd);
                    r.RicochetAngle = ammo.CertainRicochetAngle;
                    float range = penRange >= 0f ? penRange : d + rangeOffset;
                    r.Pen = penOverride >= 0f ? penOverride : PenAtRange(ammo, range);
                    if (r.Unit != null && r.Unit.Allegiance == own && own != Faction.Neutral) { r.Verdict = PenVerdict.Friendly; return true; }
                    if (r.Angle < ammo.CertainRicochetAngle && armor0.CanRicochet && !armor0.IsEra) r.Verdict = PenVerdict.Ricochet;
                    if (AddPlate(ref r, armor0, col, hit, rd, type, ammo, heat, 0f)) { lastPlate = d; if (!(armor0 is DestructibleComponent)) passedHull = true; }
                    continue;
                }

                // inside the vehicle's outline: stop at the crew compartment, an internal module, or after a few metres
                if (d - vehicleStart > 6f) break;
                if (go.layer == terrain) { if (Vector3.Angle(Vector3.up, hit.normal) > 85f) continue; break; }
                if (go.CompareTag("Compartment")) break;
                if (col.TryGetComponent(out ProxyHitZone phz) && phz.Compartment) break;
                if (!col.TryGetComponent(out IArmor armor)) continue;
                if (armor is DestructibleComponent && passedHull) break;          // a module behind the hull armour: we are inside
                if (ignore != null && armor.Unit == (IUnit)ignore) continue;
                float gap = d - lastPlate;
                if (AddPlate(ref r, armor, col, hit, rd, type, ammo, heat, gap)) { lastPlate = d; if (!(armor is DestructibleComponent)) passedHull = true; }
            }
            if (!r.Hit) return false;
            if (r.Verdict == PenVerdict.None) r.Verdict = Judge(r.Pen, r.Los);
            return true;
        }

        static bool AddPlate(ref PenResult r, IArmor armor, Collider col, RaycastHit hit, Vector3 rd, ResistanceType type, AmmoType ammo, bool heat, float gap)
        {
            if (armor.IsEra && armor.IsDetonated) return false;
            float los;
            try { los = armor.GetLosThickness(type, hit.point, hit.normal, rd, false); }
            catch { return false; }
            if (float.IsNaN(los) || float.IsInfinity(los) || los < 0f) return false;
            var opt = ammo.ArmorOptimizations;
            if (opt != null)
                for (int i = 0; i < opt.Length; i++)
                    if (opt[i].Armor == armor.ArmorType) { los *= opt[i].RhaRatio; break; }
            if (heat && armor.IsSlatArmor && !ammo.IgnoreSlat) los *= 5f;
            if (r.Plates == 0) r.FirstLos = los;
            r.Los += los;
            r.Plates++;
            return true;
        }

        internal static bool BelongsTo(Collider c, Unit u)
        {
            var t = c.transform;
            if (t.IsChildOf(u.transform)) return true;
            if (c.TryGetComponent(out IArmor a) && a.Unit == (IUnit)u) return true;
            return CodeUtils.GetParentUnitFollowAware(t) == u;
        }
    }
}
