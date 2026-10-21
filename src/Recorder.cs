using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GHPC;
using GHPC.AI.Interfaces;
using GHPC.Crew;
using GHPC.Effects;
using GHPC.Humans;
using GHPC.Utility;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    using ShotInfo = GHPC.Weapons.ShotInfo;   // the game also has a legacy global ShotInfo

    /// Listens to the game's own ballistics / damage code (Harmony) and builds a timestamped HitRecord per shot.
    internal static class Recorder
    {
        static readonly Dictionary<ShotInfo, HitRecord> _records = new Dictionary<ShotInfo, HitRecord>();
        static readonly List<HitRecord> _recent = new List<HitRecord>();   // records that hit a vehicle, newest last
        static int _nextId = 1;
        static int _nextFragId = 1;

        // context of the round currently being simulated (rounds update one at a time on the main thread)
        static LiveRound _currentRound;
        static bool _inPenCheck;
        static Vector3 _penPoint;

        static AccessTools.FieldRef<LiveRound, bool> _ricochetRef;
        static AccessTools.FieldRef<LiveRound, bool> _shatterRef;
        static AccessTools.FieldRef<LiveRound, bool> _jetRef;

        public static HitRecord LastRecord => _recent.Count > 0 ? _recent[_recent.Count - 1] : null;

        // ------------------------------------------------------------------ install

        public static void Install(HarmonyLib.Harmony h)
        {
            try { _ricochetRef = AccessTools.FieldRefAccess<LiveRound, bool>("_ricochet"); } catch (Exception e) { HitCamMod.Log.Warning("no _ricochet field: " + e.Message); }
            try { _shatterRef = AccessTools.FieldRefAccess<LiveRound, bool>("_shatter"); } catch { }
            try { _jetRef = AccessTools.FieldRefAccess<LiveRound, bool>("_jetActive"); } catch { }

            Patch(h, AccessTools.Method(typeof(LiveRound), "DoUpdate"), nameof(DoUpdatePrefix), nameof(DoUpdatePostfix));
            Patch(h, AccessTools.Method(typeof(LiveRound), "Init"), null, nameof(InitPostfix));
            Patch(h, AccessTools.Method(typeof(LiveRound), "penCheck"), nameof(PenCheckPrefix), nameof(PenCheckPostfix));
            Patch(h, AccessTools.Method(typeof(LiveRound), "AddEvent"), null, nameof(AddEventPostfix));
            Patch(h, AccessTools.Method(typeof(LiveRound), "DoExplosiveBlast"), nameof(RoundBlastPrefix), null);
            Patch(h, AccessTools.Method(typeof(ShotInfo), "AddShotFrame"), null, nameof(AddShotFramePostfix));
            Patch(h, AccessTools.Method(typeof(ShotInfo), "NotifyShotTerminated"), null, nameof(TerminatedPostfix));
            Patch(h, AccessTools.PropertySetter(typeof(DC), "Health"), nameof(HealthPrefix), nameof(HealthPostfix));
            Patch(h, AccessTools.Method(typeof(FlammableItem), "Ignite"), nameof(IgnitePrefix), nameof(IgnitePostfix));
            Patch(h, AccessTools.Method(typeof(FlammablesManager), "NotifyDidBurst"), null, nameof(BurstPostfix));
            Patch(h, AccessTools.Method(typeof(Compartment), "NotifyExplosion"), null, nameof(CompartmentExplosionPostfix));
            // what the game shows of a fire / cook-off (the x-ray draws the same): flames out of the exits, flame bursts,
            // hatches blown open, a turret thrown off, and where in the hull the rack detonated
            try
            {
                _exitFire = AccessTools.FieldRefAccess<CompartmentExit, float>("_fireRatio");
                _exitAllow = AccessTools.FieldRefAccess<CompartmentExit, bool>("_allowFire");
                _exitComp = AccessTools.FieldRefAccess<CompartmentExit, Compartment>("_compartment");
                _compBurstT = AccessTools.FieldRefAccess<Compartment, float>("_lastBurstEffectTime");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Compartment exit fields: " + e.Message); }
            Patch(h, AccessTools.Method(typeof(CompartmentExit), "SetFireRatio"), nameof(ExitFirePrefix), nameof(ExitFirePostfix));
            Patch(h, AccessTools.Method(typeof(Compartment), "NotifyPenetrated"), null, nameof(BreachPostfix));
            Patch(h, AccessTools.Method(typeof(CompartmentExit), "MakeFireBurst"), nameof(ExitBurstPrefix), nameof(ExitBurstPostfix));
            Patch(h, AccessTools.Method(typeof(CompartmentExit), "Open"), nameof(ExitOpenPrefix), nameof(ExitOpenPostfix));
            Patch(h, AccessTools.Method(typeof(DetachableParent), "Detach"), nameof(DetachPrefix), nameof(DetachPostfix));
            Patch(h, AccessTools.Method(typeof(GHPC.Weaponry.BlastEffectManager), "HandleExplosiveBlast"), nameof(BlastPrefix), null);
            Patch(h, AccessTools.Method(typeof(AmmoVisual), "OnRoundImpact"), null, nameof(AmmoImpactPostfix));
            Patch(h, AccessTools.Method(typeof(Unit), "NotifyDestroyed"), null, nameof(UnitDestroyedPostfix));
            Patch(h, AccessTools.Method(typeof(Unit), "NotifyIncapacitated"), null, nameof(UnitIncapacitatedPostfix));
            Patch(h, AccessTools.Method(typeof(Unit), "NotifyAbandoned"), null, nameof(UnitAbandonedPostfix));
        }

        static void Patch(HarmonyLib.Harmony h, MethodBase target, string prefix, string postfix)
        {
            if (target == null)
            {
                HitCamMod.Log.Warning("Patch target not found for " + (prefix ?? postfix) + " - that part of the recording is disabled (game update?)");
                return;
            }
            try
            {
                h.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(Recorder).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix == null ? null : new HarmonyMethod(typeof(Recorder).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                HitCamMod.Log.Error("Failed to patch " + target.DeclaringType?.Name + "." + target.Name + ": " + e.Message);
            }
        }

        public static void Reset()
        {
            _records.Clear();
            _recent.Clear();
            _currentRound = null;
            _inPenCheck = false;
        }

        // ------------------------------------------------------------------ helpers

        static ShotInfo Root(ShotInfo s)
        {
            int guard = 0;
            while (s != null && s.ParentShot != null && guard++ < 8) s = s.ParentShot;
            return s;
        }

        static HitRecord RecordFor(ShotInfo s)
        {
            if (s == null) return null;
            _records.TryGetValue(Root(s), out var rec);
            return rec;
        }

        static HitRecord RecordForUnit(Unit u, float window)
        {
            if (u == null) return null;
            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                var r = _recent[i];
                if (r.Target == u && Time.time - r.FirstImpactReal < window) return r;
            }
            return null;
        }

        static HitRecord RecordForDamage(Unit u)
        {
            if (_currentRound != null)
            {
                var r = RecordFor(_currentRound.ShotInfo);
                if (r != null && r.Target == u) return r;
            }
            return RecordForUnit(u, 25f);
        }

        static bool IsVehicle(Unit u) => u != null && u.Type != UnitType.Infantry;

        static Fragment FragFor(HitRecord rec, ShotInfo s)
        {
            if (rec.ByShot.TryGetValue(s, out var f)) return f;
            f = new Fragment
            {
                Id = _nextFragId++,
                Shot = s,
                Kind = s.ParentShot == null ? FragKind.Main : FragKind.Spall,
                Power = s.ActualPen,
                Caliber = s.TypeInfo != null ? s.TypeInfo.Caliber : 0f,
            };
            if (f.Kind == FragKind.Spall) f.Parent = rec.ByShot.TryGetValue(s.ParentShot, out var p) ? p : rec.Main;
            rec.ByShot[s] = f;
            rec.Fragments.Add(f);
            if (f.Kind == FragKind.Main && rec.Main == null) rec.Main = f;
            return f;
        }

        static Vector3 EventPos() => _inPenCheck ? _penPoint : (_currentRound != null ? _currentRound.transform.position : Vector3.zero);

        static void SetTarget(HitRecord rec, Unit u)
        {
            if (!AfterAction.Accept(rec, u)) { _records.Remove(rec.RootShot); return; }   // someone else's round that didn't hit you
            rec.Target = u;
            rec.TargetName = SafeName(u);
            rec.FirstImpactReal = Time.time;
            rec.W2LAtImpact = u.transform.worldToLocalMatrix;
            rec.L2WAtImpact = u.transform.localToWorldMatrix;
            foreach (var f in rec.Fragments)
            {
                foreach (var p in f.Points)
                    if (!p.HasL) { p.L = rec.W2LAtImpact.MultiplyPoint3x4(p.W); p.HasL = true; }
                if (!f.HasStart && f.Kind == FragKind.Main)
                {
                    f.StartL = rec.W2LAtImpact.MultiplyPoint3x4(f.StartW);
                    f.HasStart = true;
                }
            }
            try { using (Perf.T("Recorder/snapshot")) rec.Snapshot = GhostSnapshot.Capture(u); }
            catch (Exception e) { HitCamMod.Log.Error("Snapshot failed for " + rec.TargetName + ": " + e); }
            // a hit the hit cam will show: its crew skin is worked out in the background from now on
            if (rec.Snapshot != null && (rec.PlayerShot || !Settings.PlayerShotsOnly.Value) && (Settings.Enabled.Value || AutoTest.Active))
                using (Perf.T("Recorder/skin prepare")) { CrewBuilder.Prepare(rec.Snapshot); GhostPhysics.Prebake(rec.Snapshot); }
            HitCamPlayer.OnFirstImpact(rec);
            _recent.Add(rec);
            if (_recent.Count > 40) _recent.RemoveAt(0);
        }

        internal static string SafeName(IUnit u)
        {
            try { return string.IsNullOrEmpty(u?.FriendlyName) ? (u as Unit)?.name ?? "?" : u.FriendlyName; }
            catch { return "?"; }
        }

        // ------------------------------------------------------------------ round lifecycle

        static void DoUpdatePrefix(LiveRound __instance, out LiveRound __state)
        {
            __state = _currentRound;
            _currentRound = __instance;
        }

        static void DoUpdatePostfix(LiveRound __state)
        {
            _currentRound = __state;
        }

        static void InitPostfix(LiveRound __instance)
        {
            try
            {
                var shot = __instance.ShotInfo;
                if (shot == null || __instance.Info == null) return;
                if (__instance.ParentRound == null)
                {
                    if (!Settings.Enabled.Value && !AutoTest.Active && !ArcadeHud.WantsRecords && !AfterAction.Recording) return;
                    var ammo = __instance.Info;
                    float cal = Mathf.Max(ammo.Caliber, ammo.Radius * 2000f);
                    if (cal < Settings.MinCaliberMm.Value) return;
                    if (Settings.PlayerShotsOnly.Value && !shot.IsPlayerShot && !AfterAction.Recording) return;   // the AAR keeps hits on you too (SetTarget drops the rest)
                    var rec = new HitRecord
                    {
                        Id = _nextId++,
                        RootShot = shot,
                        Ammo = ammo,
                        PlayerShot = shot.IsPlayerShot,
                        ShooterName = SafeName(__instance.Shooter),
                        CreatedReal = Time.time,
                        LastActivityReal = Time.time,
                    };
                    _records[shot] = rec;
                    var main = FragFor(rec, shot);
                    main.StartW = __instance.transform.position; // converted to local when the target is known
                    main.StartSpeed = __instance.CurrentSpeed;
                    main.Power = ammo.RhaPenetration;
                }
                else
                {
                    var rec = RecordFor(shot);
                    if (rec == null || rec.Target == null) return;
                    var f = FragFor(rec, shot);
                    f.StartL = rec.ToLocal(__instance.transform.position);
                    f.HasStart = true;
                    f.StartSpeed = __instance.CurrentSpeed;
                    f.Power = __instance.RhaPenetrationOverride > 0 ? __instance.RhaPenetrationOverride : shot.ActualPen;
                    rec.LastActivityReal = Time.time;
                }
            }
            catch (Exception e) { HitCamMod.Log.Error("InitPostfix: " + e); }
        }

        static void TerminatedPostfix(ShotInfo __instance)
        {
            var rec = RecordFor(__instance);
            if (rec == null) return;
            if (rec.ByShot.TryGetValue(__instance, out var f)) f.Terminated = true;
            if (__instance.ParentShot == null && rec.Target == null)
            {
                _records.Remove(__instance);   // a miss: nothing to show
            }
        }

        // ------------------------------------------------------------------ path frames

        static void AddShotFramePostfix(ShotInfo __instance, Vector3 position, ShotInfo.ShotFrameData frameData)
        {
            try
            {
                var rec = RecordFor(__instance);
                if (rec == null) return;
                if (rec.Target == null)
                {
                    Unit u = frameData.VehicleStruck as Unit;
                    if (u == null && frameData.ArmorStruck != null) u = frameData.ArmorStruck.Unit as Unit;
                    if (IsVehicle(u) && __instance.ParentShot == null) SetTarget(rec, u);
                }
                var f = FragFor(rec, __instance);
                var p = new PathPoint
                {
                    W = position,
                    Impact = frameData.HitSomething || frameData.ArmorStruck != null || frameData.VehicleStruck != null,
                    Jet = frameData.IsJet,
                    Speed = (_currentRound != null && _currentRound.ShotInfo == __instance) ? _currentRound.CurrentSpeed : -1f,
                };
                if (rec.Target != null) { p.L = rec.ToLocal(position); p.HasL = true; }
                f.Points.Add(p);
                if (p.Impact) f.HitSomething = true;
                if (frameData.IsDamaging) f.Damaging = true;
                // keep only the last part of a long flight before the target is known
                if (rec.Target == null && f.Points.Count > 40) f.Points.RemoveAt(0);
                // what's still going on at the target (a rod flying on after it went through, or off after a ricochet, isn't)
                if (rec.Target == null || p.Impact || (position - rec.Target.transform.position).sqrMagnitude < 12f * 12f) rec.LastActivityReal = Time.time;
            }
            catch (Exception e) { HitCamMod.Log.Error("AddShotFramePostfix: " + e); }
        }

        // ------------------------------------------------------------------ armor

        internal class PenState
        {
            public HitRecord Rec;
            public IArmor Armor;
            public Vector3 Point, Normal, Dir;
            public float Los, Nominal, Pen, Angle;
            public bool WasSpall;
            public bool PreJet;     // HEAT shell body touching the plate, before the jet forms
            public float Speed;     // m/s when it reached the plate
            public Unit Unit;
            public Collider Col;
            public DC Module;       // a module hit zone (barrel, track, tail drive...): world module damage only
        }

        static void PenCheckPrefix(LiveRound __instance, Collider cObject, Vector3 surfaceNormal, Vector3 roundPath, Vector3 impactPoint, ITarget targetStruck, out PenState __state)
        {
            __state = null;
            _inPenCheck = true;
            _penPoint = impactPoint;
            // (spall is left out: a vehicle blowing up throws fragments at its own tracks)
            if (__instance.IsSpall) WorldModules.PenEnded(); else WorldModules.PenStarted(targetStruck?.Owner, impactPoint);
            try
            {
                var rec = RecordFor(__instance.ShotInfo);
                IArmor armor = null;
                if (cObject != null) cObject.TryGetComponent(out armor);
                if (rec != null && rec.Target == null && !__instance.IsSpall)
                {
                    Unit u = targetStruck?.Owner;
                    if (u == null && armor != null) u = armor.Unit as Unit;
                    if (u == null && cObject != null) u = CodeUtils.GetParentUnitFollowAware(cObject.transform);
                    if (IsVehicle(u)) SetTarget(rec, u);
                }
                if (armor == null || __instance.IsSpall || __instance.Info == null) return;
                // modules and crew body parts are armor too in GHPC; they get their own damage events
                var moduleDc = cObject.GetComponent<DC>();
                if (moduleDc != null)
                {
                    // the world damage draws some modules from outside (a holed / bent barrel, a chewed track, a helicopter's tail)
                    Unit mu = armor.Unit as Unit ?? targetStruck?.Owner;
                    if (IsVehicle(mu) && Mathf.Max(__instance.Info.Caliber, __instance.Info.Radius * 2000f) >= Settings.MinCaliberMm.Value && WorldModules.Wants(mu, moduleDc))
                        __state = new PenState { Unit = mu, Col = cObject, Armor = armor, Module = moduleDc, Point = impactPoint, Normal = surfaceNormal, Dir = roundPath.normalized,
                                                 Speed = __instance.CurrentSpeed, Pen = __instance.CurrentPenRating };
                    return;
                }
                Unit unit = armor.Unit as Unit ?? targetStruck?.Owner;
                if (!IsVehicle(unit)) return;
                bool forRecord = rec != null && rec.Target != null && unit == rec.Target;
                bool forWorld = Settings.WorldDamage.Value && Mathf.Max(__instance.Info.Caliber, __instance.Info.Radius * 2000f) >= Settings.MinCaliberMm.Value;
                if (!forRecord && !forWorld) return;

                var st = new PenState { Rec = forRecord ? rec : null, Unit = unit, Col = cObject, Armor = armor, Point = impactPoint, Normal = surfaceNormal, Dir = roundPath.normalized, WasSpall = __instance.IsSpall };
                st.Pen = __instance.CurrentPenRating;
                st.Speed = __instance.CurrentSpeed;
                st.PreJet = __instance.Info.Category == AmmoType.AmmoCategory.ShapedCharge && _jetRef != null && !_jetRef(__instance);
                st.Angle = 90f - Vector3.Angle(surfaceNormal, -st.Dir);
                var type = __instance.Info.Category == AmmoType.AmmoCategory.ShapedCharge ? ResistanceType.CE : ResistanceType.KE;
                try
                {
                    st.Los = armor.GetLosThickness(type, impactPoint, surfaceNormal, st.Dir, false);
                    st.Nominal = armor.GetNormalThickness(type, impactPoint, surfaceNormal, false);
                }
                catch { }
                __state = st;
            }
            catch (Exception e) { HitCamMod.Log.Error("PenCheckPrefix: " + e); }
        }

        static void PenCheckPostfix(LiveRound __instance, bool __result, PenState __state)
        {
            _inPenCheck = false;
            WorldModules.PenEnded();
            if (__state == null) return;
            try
            {
                var rec = __state.Rec;
                bool ric = _ricochetRef != null && _ricochetRef(__instance);
                bool shat = _shatterRef != null && _shatterRef(__instance);
                bool pen = __result && !ric && !shat && !__state.PreJet;
                if (__state.Module != null)
                {
                    WorldModules.OnModuleHit(__instance.ID, __state.Unit, __state.Col, __state.Module, __state.Point, __state.Normal, __state.Dir, pen, ric,
                                             __instance.Info, __state.Speed, HoleRadius(__instance.Info, 20f));
                    return;
                }
                if (!__state.Armor.IsEra)
                    using (Perf.T("Recorder/world damage")) WorldDamage.OnArmorHit(__instance.ID, __state.Unit, __state.Col, __state.Point, __state.Normal, __state.Dir, pen, ric, __state.PreJet,
                        __state.Nominal, __state.Los, __state.Pen, __state.Speed, ImpactEnergy(__instance, __state.PreJet), __instance.Info, HoleRadius(__instance.Info, __state.Nominal));
                if (rec == null) return;
                var f = FragFor(rec, __instance.ShotInfo);
                var ah = new ArmorHit
                {
                    Frag = f,
                    Name = __state.Armor.Name,
                    L = rec.ToLocal(__state.Point),
                    NormalL = rec.DirToLocal(__state.Normal),
                    DirL = rec.DirToLocal(__state.Dir),
                    Los = __state.Los,
                    Nominal = __state.Nominal,
                    Pen = __state.Pen,
                    Angle = __state.Angle,
                    Ricochet = ric,
                    Era = __state.Armor.IsEra,
                    Penetrated = __result && !ric && !shat && !__state.PreJet,
                    Category = __instance.Info.Category,
                    IsSpall = __state.WasSpall,
                    PreJet = __state.PreJet,
                    Speed = __state.Speed,
                    Caliber = Mathf.Max(__instance.Info.Caliber, __instance.Info.Radius * 2000f) * 0.001f,
                    RodRadius = Mathf.Sqrt(Mathf.Max(__instance.Info.SectionalArea, 1e-6f) / Mathf.PI),
                    Tnt = __instance.Info.TntEquivalentKg,
                };
                ah.HoleRadius = HoleRadius(__instance.Info, ah.Nominal);
                ah.Energy = ImpactEnergy(__instance, __state.PreJet);
                ah.PlateTris = PlateGeometry(__state.Col, __state.Point, rec);
                if (AutoTest.Active && !__state.WasSpall)
                {
                    var col = __state.Col;
                    string shape = col is BoxCollider bc ? $"box {Vector3.Scale(bc.size, col.transform.lossyScale)}"
                                 : col is MeshCollider mc ? $"mesh {(mc.sharedMesh != null ? mc.sharedMesh.vertexCount + "v readable=" + mc.sharedMesh.isReadable + " convex=" + mc.convex : "none")}"
                                 : col != null ? col.GetType().Name : "null";
                    HitCamMod.Log.Msg($"  armour hit {ah.Name}: collider '{col?.name}' {shape}, angle {ah.Angle:0.#}, los {ah.Los:0} nom {ah.Nominal:0}, pen={ah.Penetrated} ric={ah.Ricochet}");
                }
                // the game can re-test the same plate many times while a round slides along / through it (HE on a
                // curved turret gave ~60 hits a few mm apart): that's one impact, keep the first
                float merge = Mathf.Max(0.1f, ah.HoleRadius * 1.5f);
                foreach (var o in rec.ArmorHits)
                    if (o.Frag == f && o.Name == ah.Name && o.PreJet == ah.PreJet && (o.L - ah.L).sqrMagnitude < merge * merge)
                    {
                        o.Los = Mathf.Max(o.Los, ah.Los);
                        o.Penetrated |= ah.Penetrated;
                        return;
                    }
                rec.ArmorHits.Add(ah);
                if (!rec.HasEntry)
                {
                    rec.HasEntry = true;
                    rec.EntryL = ah.L;
                    rec.EntryDirL = ah.DirL;
                }
                string thick = ah.Los > 0 ? $" ({ah.Los:0} mm LOS, pen {ah.Pen:0} mm)" : $" (pen {ah.Pen:0} mm)";
                var e = new HitEvent { Frag = f, L = ah.L, HasPos = true, RealT = Time.time, Ref = ah, Subject = ah.Name };
                if (ah.Era) { e.Kind = EvKind.ArmorPen; e.Text = "ERA hit: " + ah.Name; e.Severity = 1; }
                else if (__state.PreJet) { e.Kind = EvKind.Story; e.Text = "Warhead struck " + ah.Name; e.Severity = 0; }
                else if (ric) { e.Kind = EvKind.Ricochet; e.Text = "Ricochet off " + ah.Name + $" ({ah.Angle:0}°)"; e.Severity = 0; }
                else if (ah.Penetrated) { e.Kind = EvKind.ArmorPen; e.Text = "Penetrated " + ah.Name + thick; e.Severity = 1; }
                else { e.Kind = EvKind.ArmorStop; e.Text = (shat ? "Shattered on " : "Stopped by ") + ah.Name + thick; e.Severity = 0; }
                if (rec.Dedupe.Add(e.Text)) rec.AddEvent(e);
            }
            catch (Exception e) { HitCamMod.Log.Error("PenCheckPostfix: " + e); }
        }

        /// The struck armour collider's triangles in target-local space (GHPC's armour pieces are small readable meshes,
        /// e.g. "9mm004" with 34 vertices; boxes are expanded to their 12 triangles). Big meshes: only what lies near the hit.
        /// rec == null: world space (the in-world plates).
        internal static Vector3[] PlateGeometry(Collider col, Vector3 point, HitRecord rec)
        {
            Vector3 L(Vector3 w) => rec != null ? rec.ToLocal(w) : w;
            try
            {
                if (col is BoxCollider bc)
                {
                    var t = col.transform;
                    Vector3 h = bc.size * 0.5f;
                    var c = new Vector3[8];
                    for (int i = 0; i < 8; i++)
                        c[i] = L(t.TransformPoint(bc.center + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z)));
                    int[] f = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
                    var o = new Vector3[f.Length];
                    for (int i = 0; i < f.Length; i++) o[i] = c[f[i]];
                    return o;
                }
                if (col is MeshCollider mc && mc.sharedMesh != null && mc.sharedMesh.isReadable)
                {
                    var m = mc.sharedMesh;
                    var v = m.vertices;
                    var tr = m.triangles;
                    var l2w = col.transform.localToWorldMatrix;
                    var wv = new Vector3[v.Length];
                    for (int i = 0; i < v.Length; i++) wv[i] = l2w.MultiplyPoint3x4(v[i]);
                    bool big = tr.Length > 3 * 3000;
                    var o = new List<Vector3>(Mathf.Min(tr.Length, 9000));
                    for (int i = 0; i + 2 < tr.Length; i += 3)
                    {
                        Vector3 a = wv[tr[i]], b = wv[tr[i + 1]], cc = wv[tr[i + 2]];
                        if (big && ((a - point).sqrMagnitude > 2.25f && (b - point).sqrMagnitude > 2.25f && (cc - point).sqrMagnitude > 2.25f)) continue;
                        o.Add(L(a)); o.Add(L(b)); o.Add(L(cc));
                        if (o.Count >= 9000) break;
                    }
                    return o.ToArray();
                }
            }
            catch { }
            return null;
        }

        /// Energy (J) the projectile brings to this plate.
        static float ImpactEnergy(LiveRound r, bool preJet)
        {
            var a = r.Info;
            switch (a.Category)
            {
                case AmmoType.AmmoCategory.ShapedCharge:
                    // the jet: roughly 0.4 kJ per mm of RHA it can still defeat; the shell body before it forms is slow
                    return preJet ? 0.5f * Mathf.Max(a.Mass, 1f) * r.CurrentSpeed * r.CurrentSpeed * 0.2f : 400f * Mathf.Max(r.CurrentPenRating, 1f);
                case AmmoType.AmmoCategory.Explosive:
                    // contact charge: ~15 % of 4.184 MJ/kg TNT couples into the plate, plus the shell's own impact
                    return Mathf.Max(a.TntEquivalentKg, 0.01f) * 4.184e6f * 0.15f + 0.5f * a.Mass * r.CurrentSpeed * r.CurrentSpeed * 0.1f;
                default:
                    return 0.5f * Mathf.Max(a.Mass, 0.01f) * r.CurrentSpeed * r.CurrentSpeed;
            }
        }

        static float HoleRadius(AmmoType ammo, float nominalMm)
        {
            // what the round physically leaves; visuals scale it up a bit so it reads at hit-cam distance
            float calR = Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) * 0.0005f;           // caliber radius in m
            float rodR = Mathf.Sqrt(Mathf.Max(ammo.SectionalArea, 1e-5f) / Mathf.PI);          // penetrator radius
            switch (ammo.Category)
            {
                case AmmoType.AmmoCategory.ShapedCharge: return Mathf.Max(calR * 0.3f, 0.012f);
                case AmmoType.AmmoCategory.Explosive:
                    // HE punching through thin armor tears a hole that grows with charge size
                    float tnt = Mathf.Max(ammo.TntEquivalentKg, 0.05f);
                    float thin = Mathf.Clamp01(1f - nominalMm / 60f);
                    return Mathf.Max(calR, 0.08f * Mathf.Pow(tnt, 0.33f) * (0.4f + thin));
                default: return Mathf.Max(rodR * 1.3f, Mathf.Min(calR, 0.08f) * 0.8f, 0.012f);
            }
        }

        // ------------------------------------------------------------------ story

        static readonly string[] _storySkip = { "Penetrated ", "Ricocheted off", "Stopped by ", "Hit " };

        static void AddEventPostfix(LiveRound __instance, string eventString)
        {
            try
            {
                if (__instance.IsSpall) return;   // spall story lines duplicate the module events
                // an HE / HEAT round going off (also on a helicopter's plain hull box, which isn't armour): the world damage
                // works out what it did to nearby helicopter tails
                if (eventString != null && eventString.IndexOf("detonat", StringComparison.OrdinalIgnoreCase) >= 0 && __instance.Info != null && __instance.Info.TntEquivalentKg > 0f)
                    WorldModules.OnDetonation(_inPenCheck ? _penPoint : __instance.transform.position, __instance.Info);
                var rec = RecordFor(__instance.ShotInfo);
                if (rec == null || rec.Target == null || string.IsNullOrEmpty(eventString)) return;
                foreach (var s in _storySkip) if (eventString.StartsWith(s)) return;
                if (!rec.Dedupe.Add("story:" + eventString)) return;
                var e = new HitEvent
                {
                    Kind = EvKind.Story,
                    Text = eventString,
                    Frag = FragFor(rec, __instance.ShotInfo),
                    L = rec.ToLocal(EventPos()),
                    HasPos = true,
                    RealT = Time.time,
                };
                if (eventString.StartsWith("Entered")) e.Severity = 1;
                if (eventString.Contains("not armed") || eventString.Contains("Shattered") || eventString.Contains("Crushed")) e.Severity = 0;
                rec.AddEvent(e);
            }
            catch (Exception e) { HitCamMod.Log.Error("AddEventPostfix: " + e); }
        }

        // ------------------------------------------------------------------ modules & crew

        static void HealthPrefix(DC __instance, out float __state)
        {
            __state = __instance.HealthPercent;
        }

        static void HealthPostfix(DC __instance, float __state)
        {
            try
            {
                float now = __instance.HealthPercent;
                if (Mathf.Approximately(now, __state)) return;
                var unit = __instance.Unit;
                if (unit == null) return;
                WorldModules.OnHealth(__instance, __state, now);
                var rec = RecordForDamage(unit);
                if (rec == null) return;

                var snap = rec.Snapshot;
                Vector3 w = _inPenCheck || _currentRound != null ? EventPos() : __instance.transform.position;
                var e = new HitEvent
                {
                    RealT = Time.time,
                    Frag = _currentRound != null ? (rec.ByShot.TryGetValue(_currentRound.ShotInfo, out var f) ? f : null) : null,
                    L = rec.ToLocal(w),
                    HasPos = true,
                    Before = __state,
                    After = now,
                    Ref = __instance,
                };

                if (snap != null && snap.CrewOfPart.TryGetValue(__instance, out var crew))
                {
                    if (now < __state)
                    {
                        var hit = new HitEvent
                        {
                            Kind = EvKind.CrewHit, Text = crew.Label + " hit", Subject = crew.Label, RealT = Time.time,
                            Frag = e.Frag, L = e.L, HasPos = true, Before = __state, After = now,
                            Ref = new CrewHitRef { Crew = crew, Part = __instance },
                        };
                        if (_currentRound != null)
                        {
                            hit.Speed = _currentRound.CurrentSpeed;
                            hit.Power = _currentRound.CurrentPenRating;
                            hit.Spall = _currentRound.IsSpall;
                            hit.Jet = _jetRef != null && _jetRef(_currentRound);
                            hit.Mass = _currentRound.Info != null ? _currentRound.Info.Mass : 0f;
                            hit.DirL = rec.DirToLocal(_currentRound.transform.forward);
                            hit.HasDir = true;
                        }
                        rec.AddEvent(hit);
                    }
                    e.Kind = EvKind.Crew;
                    e.Subject = crew.Label;
                    e.Ref = crew;
                    HealthStatus st = crew.Status();
                    e.After = (float)st;   // crew events carry the overall status, not part health
                    e.Severity = st >= HealthStatus.Critical ? 3 : st >= HealthStatus.HeavilyWounded ? 2 : 1;
                    e.Text = crew.Label + " " + StatusText(st);
                    string key = "crew:" + crew.Label + ":" + st;
                    if (st == HealthStatus.Fine || !rec.Dedupe.Add(key)) return;
                }
                else
                {
                    e.Kind = EvKind.Module;
                    e.Subject = __instance.Name;
                    e.Severity = now <= 0 ? 3 : now < 50 ? 2 : 1;
                    e.Text = now <= 0 ? __instance.Name + " destroyed" : __instance.Name + $" damaged ({now:0}%)";
                }
                rec.AddEvent(e);
            }
            catch (Exception ex) { HitCamMod.Log.Error("HealthPostfix: " + ex); }
        }

        internal static string StatusText(HealthStatus s)
        {
            switch (s)
            {
                case HealthStatus.Wounded: return "wounded";
                case HealthStatus.HeavilyWounded: return "heavily wounded";
                case HealthStatus.Critical: return "critical";
                case HealthStatus.Dead: return "killed";
                default: return "ok";
            }
        }

        // ------------------------------------------------------------------ fire / ammo / kill

        static void IgnitePrefix(FlammableItem __instance, out bool __state) { __state = __instance.IsBurning; }

        static void IgnitePostfix(FlammableItem __instance, bool __state)
        {
            try
            {
                if (__state || !__instance.IsBurning) return;
                var unit = CodeUtils.GetParentUnitFollowAware(__instance.transform);
                var rec = RecordForDamage(unit);
                if (rec == null) return;
                bool ammo = __instance.Explosive;
                string name = Pretty(__instance.gameObject.name);
                if (!rec.Dedupe.Add("fire:" + name)) return;
                // the fire burns where the thing is, not at its pivot (often a corner): its colliders' (or renderers') box
                Vector3 at = rec.ToLocal(__instance.transform.position), ext = Vector3.zero;
                if (LocalBox(__instance.transform, rec, out var bc, out var be)) { at = bc; ext = be; }
                rec.AddEvent(new HitEvent
                {
                    Kind = ammo ? EvKind.AmmoFire : EvKind.Fire,
                    Text = ammo ? "Ammo detonated: " + name : "Fire: " + name,
                    Subject = name,
                    Extent = ext,
                    L = at,
                    HasPos = true,
                    RealT = Time.time,
                    Ref = __instance,
                    Severity = ammo ? 3 : 2,
                });
            }
            catch (Exception e) { HitCamMod.Log.Error("IgnitePostfix: " + e); }
        }

        /// The target-local box of a part: its colliders, else its renderers.
        static bool LocalBox(Transform t, HitRecord rec, out Vector3 centre, out Vector3 half)
        {
            centre = half = Vector3.zero;
            var bs = new List<Bounds>();
            foreach (var c in t.GetComponentsInChildren<Collider>(true)) if (c != null) bs.Add(c.bounds);
            if (bs.Count == 0) foreach (var r in t.GetComponentsInChildren<Renderer>(true)) if (r != null) bs.Add(r.bounds);
            if (bs.Count == 0) return false;
            bool first = true;
            Bounds lb = default;
            foreach (var wb in bs)
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = rec.ToLocal(wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1)));
                    if (first) { lb = new Bounds(p, Vector3.zero); first = false; } else lb.Encapsulate(p);
                }
            if (lb.size.magnitude > 6f) return false;    // not a part (a whole-vehicle collider)
            centre = lb.center;
            half = lb.extents;
            return true;
        }

        static void BurstPostfix(FlammablesManager __instance, float tntEquivalentKg)
        {
            KillCrewInBlast(__instance, tntEquivalentKg);
            Explosion(__instance.transform, tntEquivalentKg, "burst");
        }

        /// Realism (AmmoExplosionKillsCrew): an ammunition rack going off inside a closed hull kills the crew within the
        /// confined blast's lethal reach, ~2.2 W^(1/3) m (the reflected overpressure fills the compartment: all of a tank
        /// for a rack's 10+ kg, a couple of metres for a small one). GHPC itself sometimes leaves a crewman alive in
        /// a tank whose carousel blew up.
        static void KillCrewInBlast(FlammablesManager fm, float tnt)
        {
            try
            {
                if (!Settings.AmmoExplosionKillsCrew.Value || tnt <= 0.05f) return;
                var unit = CodeUtils.GetParentUnitFollowAware(fm.transform);
                if (unit == null || unit.CrewManager == null || unit.CrewManager.CrewMembers == null) return;
                Vector3 at = Time.time - _detTime < 0.05f ? _detAt : fm.transform.position;
                float reach = Mathf.Max(1.5f, 2.2f * Mathf.Pow(Mathf.Max(_detTnt, tnt), 1f / 3f));
                var rec = RecordForDamage(unit);
                foreach (var cm in unit.CrewManager.CrewMembers)
                {
                    if (cm == null || cm.Evacuated || cm.Human == null || cm.Human.IsDead) continue;
                    // the crewman's body: the middle of his hit zones
                    Vector3 p = Vector3.zero; int n = 0;
                    if (cm.BodyPartRegistry != null && cm.BodyPartRegistry.BodyParts != null)
                        foreach (var b in cm.BodyPartRegistry.BodyParts)
                            if (b?.Component != null) { p += b.Component.transform.position; n++; }
                    if (n == 0) continue;
                    p /= n;
                    float d = Vector3.Distance(p, at);
                    if (d > reach) continue;
                    string label = GhostSnapshot.CrewLabel(cm.Position);
                    if (rec != null && rec.Dedupe.Add("crew:" + label + ":" + HealthStatus.Dead))
                    {
                        var ci = rec.Snapshot != null ? rec.Snapshot.Crew.FirstOrDefault(c => c.Member == cm) : null;
                        rec.AddEvent(new HitEvent
                        {
                            Kind = EvKind.Crew, Subject = label, Ref = ci, RealT = Time.time, L = rec.ToLocal(p), HasPos = true,
                            Before = -1f, After = (float)HealthStatus.Dead, Severity = 3, Text = label + " killed by the ammunition explosion",
                        });
                    }
                    cm.Human.Kill();   // after our event: the health hooks it sets off then dedupe against it
                    if (AutoTest.Active) HitCamMod.Log.Msg($"Ammunition explosion ({tnt:0.0} kg) in {SafeName(unit)}: {label} {d:0.0} m from it (lethal within {reach:0.0} m) killed");
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Ammunition explosion crew: " + e.Message); }
        }

        static void CompartmentExplosionPostfix(Compartment __instance, float tntEquivalent) => Explosion(__instance.transform, tntEquivalent, "comp");

        // ------------------------------------------------------------------ what the game shows of it

        static AccessTools.FieldRef<CompartmentExit, float> _exitFire;
        static AccessTools.FieldRef<CompartmentExit, bool> _exitAllow;
        static AccessTools.FieldRef<CompartmentExit, Compartment> _exitComp;
        static AccessTools.FieldRef<Compartment, float> _compBurstT;
        static Vector3 _detAt;
        static float _detTnt, _detTime = -99f;

        static string ExitName(CompartmentExit x)
        {
            string n = x.Name ?? "";
            if (n == "impact exit" || n.Length == 0) return "the hole";
            return Pretty(n);
        }

        /// How big an exit is: its hatch lid's own size across (the lids are the exit's animated parts), else the game's
        /// radius for the exit (a hole the round made has the round's).
        static float ExitRadius(CompartmentExit x, out string from)
        {
            float best = 0f;
            from = "exit radius";
            var marker = x.PositionMarker;
            var sb = AutoTest.Active ? new System.Text.StringBuilder() : null;
            if (x.AnimatedParts != null && marker != null)
                foreach (var ap in x.AnimatedParts)
                {
                    var t = ap != null ? (ap.Transform != null ? ap.Transform : ap.transform) : null;
                    if (t == null) continue;
                    Vector3 n = marker.forward;
                    Vector3 a = Vector3.Cross(n, Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up).normalized, b = Vector3.Cross(n, a);
                    // a lid swings on a hinge at the edge of its opening: from the hinge to the opening's middle is its radius
                    float hinge = Vector3.ProjectOnPlane(t.position - marker.position, n).magnitude;
                    sb?.Append($" [{t.name}: hinge {hinge * 1000f:0} mm");
                    // (its pivot stands a little off the rim, and a lid that swings aside pivots further out: 0.8 x, at most 0.28 m)
                    float hr = 0.8f * Mathf.Min(hinge, 0.35f);
                    if (hinge > 0.1f && hinge < 0.8f && hr > best) { best = hr; from = "hinge of " + t.name; }
                    // the lid's own mesh, if it is one of its own: its half-size across the opening
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r is ParticleSystemRenderer) continue;
                        Vector3 e = r.bounds.extents;
                        float ra = Mathf.Abs(a.x) * e.x + Mathf.Abs(a.y) * e.y + Mathf.Abs(a.z) * e.z;
                        float rb = Mathf.Abs(b.x) * e.x + Mathf.Abs(b.y) * e.y + Mathf.Abs(b.z) * e.z;
                        float across = 0.5f * (ra + rb);
                        sb?.Append($", mesh {r.name} {across * 1000f:0} mm");
                        if (e.magnitude > 1.2f) continue;      // a whole turret / hull mesh, not a lid
                        if (across > best) { best = across; from = "hatch lid " + r.name; }
                    }
                    sb?.Append("]");
                }
            if (sb != null) HitCamMod.Log.Msg($"x-ray: exit {ExitName(x)}: {x.AnimatedParts?.Length ?? 0} animated parts{sb}");
            if (best < x.Radius) { best = x.Radius; from = from == "exit radius" ? from : from + ", the game's radius bigger"; }
            return Mathf.Clamp(best, 0.03f, 0.7f);
        }

        /// A round broke into a compartment: the game opens a small exit there (its fire and gas can get out of it).
        static void BreachPostfix(Compartment __instance, AmmoType roundType, Vector3 impactPoint, Vector3 roundPath)
        {
            try
            {
                if (__instance == null || roundType == null) return;
                var unit = CodeUtils.GetParentUnitFollowAware(__instance.transform);
                var rec = RecordForDamage(unit);
                if (rec == null) return;
                if (!rec.Dedupe.Add($"breach:{Mathf.RoundToInt(impactPoint.x * 20f)},{Mathf.RoundToInt(impactPoint.y * 20f)},{Mathf.RoundToInt(impactPoint.z * 20f)}")) return;
                // the exit the game just opened there (its flame, when the fire reaches it)
                CompartmentExit exit = null;
                if (__instance.Exits != null)
                    foreach (var x in __instance.Exits)
                        if (x?.PositionMarker != null && x.Name == "impact exit" && (x.PositionMarker.position - impactPoint).sqrMagnitude < 1e-4f) exit = x;
                rec.AddEvent(new HitEvent
                {
                    Kind = EvKind.Breach,
                    Ref = exit,
                    Text = "Hole into " + __instance.name,
                    Subject = __instance.name,
                    L = rec.ToLocal(impactPoint),
                    HasPos = true,
                    DirL = rec.DirToLocal(-roundPath).normalized,
                    HasDir = true,
                    Power = roundType.Radius,
                    Extent = Vector3.one * roundType.Radius,
                    RealT = Time.time,
                    Severity = 0,
                });
            }
            catch (Exception e) { HitCamMod.Log.Error("BreachPostfix: " + e.Message); }
        }

        static void ExitEvent(CompartmentExit x, EvKind kind, string text, float ratio, string key, int severity)
        {
            var marker = x.PositionMarker;
            if (marker == null) return;
            var unit = CodeUtils.GetParentUnitFollowAware(marker);
            var rec = RecordForDamage(unit);
            if (rec == null || !rec.Dedupe.Add(key + x.GetHashCode())) return;
            float rad = ExitRadius(x, out string from);
            if (AutoTest.Active) HitCamMod.Log.Msg($"x-ray: {text}: radius {rad * 1000f:0} mm ({from}; the game's exit radius {x.Radius * 1000f:0} mm), ratio {ratio:0.00}");
            rec.AddEvent(new HitEvent
            {
                Extent = new Vector3(rad, rad, rad),
                Power = rad,                    // (archived; the extent is not)
                Kind = kind,
                Text = text,
                Subject = ExitName(x),
                L = rec.ToLocal(marker.position),
                HasPos = true,
                DirL = rec.DirToLocal(marker.forward),
                HasDir = true,
                After = ratio,
                RealT = Time.time,
                Severity = severity,
            });
        }

        static void ExitFirePrefix(CompartmentExit __instance, out float __state) { __state = _exitFire != null ? _exitFire(__instance) : 0f; }

        /// Flames start coming out of an exit (a hatch, a vent, a hole the round made): the fire inside venting there.
        static void ExitFirePostfix(CompartmentExit __instance, float ratio, float __state)
        {
            try
            {
                if (__state > 0f || ratio <= 0f || __instance.Closed || (_exitAllow != null && !_exitAllow(__instance))) return;
                ExitEvent(__instance, EvKind.VentFire, "Flames from " + ExitName(__instance), ratio, "vent:", 1);
            }
            catch (Exception e) { HitCamMod.Log.Error("ExitFirePostfix: " + e.Message); }
        }

        static void ExitBurstPrefix(CompartmentExit __instance, out float __state)
        {
            var c = _exitComp != null ? _exitComp(__instance) : null;
            __state = c != null && _compBurstT != null ? _compBurstT(c) : -1f;
        }

        /// A flame burst out of an open exit (the game's effect for a detonation inside, sized by its TNT).
        static void ExitBurstPostfix(CompartmentExit __instance, float ratio, float __state)
        {
            try
            {
                var c = _exitComp != null ? _exitComp(__instance) : null;
                if (c == null || _compBurstT == null || _compBurstT(c) == __state || ratio <= 0f) return;
                ExitEvent(__instance, EvKind.FireBurst, "Fire burst from " + ExitName(__instance), ratio, "burst:" + Time.frameCount + ":", 2);
            }
            catch (Exception e) { HitCamMod.Log.Error("ExitBurstPostfix: " + e.Message); }
        }

        static void ExitOpenPrefix(CompartmentExit __instance, out bool __state) { __state = __instance.Closed; }

        /// A hatch blown open by the pressure inside (the game opens them that way with lockAnim).
        static void ExitOpenPostfix(CompartmentExit __instance, bool lockAnim, bool __state)
        {
            try
            {
                if (!__state || __instance.Closed || !lockAnim) return;
                ExitEvent(__instance, EvKind.HatchBlown, "Blown open: " + ExitName(__instance), 0f, "open:", 2);
            }
            catch (Exception e) { HitCamMod.Log.Error("ExitOpenPostfix: " + e.Message); }
        }

        static void DetachPrefix(DetachableParent __instance, out (bool was, Unit unit) __state)
        {
            __state = (__instance.Detached, CodeUtils.GetParentUnitFollowAware(__instance.transform));
        }

        /// The turret (or another part) thrown off by the pressure or the flames inside.
        static void DetachPostfix(DetachableParent __instance, float detachForce, (bool was, Unit unit) __state)
        {
            try
            {
                if (__state.was || !__instance.Detached) return;
                var part = __instance.Transform != null ? __instance.Transform : __instance.transform;
                BlastSound.OnDetach(part.position);
                WorldDamage.OpenedUp(__state.unit, part);
                var rec = RecordForDamage(__state.unit);
                if (rec == null || !rec.Dedupe.Add("detach:" + __instance.GetInstanceID())) return;
                var t = __instance.Transform != null ? __instance.Transform : __instance.transform;
                string n = Pretty(__instance.gameObject.name);
                bool turret = n.IndexOf("turret", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("turret", StringComparison.OrdinalIgnoreCase) >= 0;
                rec.AddEvent(new HitEvent
                {
                    Kind = EvKind.TurretOff,
                    Text = turret ? "TURRET BLOWN OFF" : n + " blown off",
                    Subject = turret ? "turret" : n,
                    L = rec.ToLocal(t.position),
                    HasPos = true,
                    After = detachForce,
                    RealT = Time.time,
                    Severity = 3,
                });
            }
            catch (Exception e) { HitCamMod.Log.Error("DetachPostfix: " + e.Message); }
        }

        /// A compartment's rack detonating (the game calls the blast with a reduced fragment factor for those): where it went.
        static void RoundBlastPrefix(LiveRound __instance)
        {
            if (__instance != null && !__instance.IsSpall) WorldModules.OnRoundBlast(__instance.transform.position, __instance.Info);
        }

        static void BlastPrefix(Vector3 blastWorldPos, float blastTntEquivalenceKg, float fragScalingOverride)
        {
            if (fragScalingOverride >= 0.5f) return;
            _detAt = blastWorldPos;
            _detTnt = blastTntEquivalenceKg;
            _detTime = Time.time;
        }

        static void Explosion(Transform t, float tnt, string src)
        {
            try
            {
                var unit = CodeUtils.GetParentUnitFollowAware(t);
                var rec = RecordForDamage(unit);
                if (rec == null) return;
                // the round's own filler going off in the compartment (HEAT / HE): the warhead, not the vehicle's ammunition
                if (rec.Ammo != null && rec.Ammo.TntEquivalentKg > 0f && Mathf.Abs(tnt - rec.Ammo.TntEquivalentKg) <= rec.Ammo.TntEquivalentKg * 0.01f + 1e-3f
                    && Time.time - rec.FirstImpactReal < 0.5f) return;
                if (!rec.Dedupe.Add("explosion")) return;
                // where the rack went (the game's own detonation point, this frame), else the compartment
                bool atRack = Time.time - _detTime < 0.05f;
                rec.AddEvent(new HitEvent
                {
                    Kind = EvKind.Explosion,
                    Text = "AMMUNITION EXPLODED",
                    Subject = atRack ? "rack" : null,
                    L = rec.ToLocal(atRack ? _detAt : t.position),
                    HasPos = true,
                    RealT = Time.time,
                    After = atRack ? Mathf.Max(tnt, _detTnt) : tnt,     // the compartment's own figure can be 0: the blast's is real
                    Severity = 3,
                });
                HitCamPlayer.OnLateExplosion(rec);
            }
            catch (Exception e) { HitCamMod.Log.Error("Explosion(" + src + "): " + e); }
        }

        static void AmmoImpactPostfix(AmmoVisual __instance, bool effective, bool hitPropellant)
        {
            try
            {
                if (!effective) return;
                var unit = CodeUtils.GetParentUnitFollowAware(__instance.transform);
                var rec = RecordForDamage(unit);
                if (rec == null) return;
                string what = hitPropellant ? "Propellant hit" : "Ammo hit";
                rec.AddEvent(new HitEvent
                {
                    Kind = EvKind.AmmoHit,
                    Text = what + ": " + (string.IsNullOrEmpty(__instance.Name) ? "round" : __instance.Name),
                    Subject = __instance.Name,
                    Frag = _currentRound != null && rec.ByShot.TryGetValue(_currentRound.ShotInfo, out var f) ? f : null,
                    L = rec.ToLocal(_inPenCheck ? _penPoint : __instance.transform.position),
                    HasPos = true,
                    RealT = Time.time,
                    Ref = __instance,
                    Severity = 2,
                });
            }
            catch (Exception e) { HitCamMod.Log.Error("AmmoImpactPostfix: " + e); }
        }

        static void UnitDestroyedPostfix(Unit __instance)
        {
            WorldModules.OnUnitDestroyed(__instance);
            var rec = RecordForUnit(__instance, 25f);
            if (rec == null || rec.TargetKilled) return;
            rec.TargetKilled = true;
            if (rec.OutAt < 0f) rec.OutAt = Time.time;
            rec.AddEvent(new HitEvent { Kind = EvKind.Kill, Text = "TARGET DESTROYED", RealT = Time.time, Severity = 3 });
        }

        static void UnitIncapacitatedPostfix(Unit __instance)
        {
            var rec = RecordForUnit(__instance, 25f);
            if (rec == null || rec.TargetKnockedOut) return;
            rec.TargetKnockedOut = true;
            if (rec.OutAt < 0f) rec.OutAt = Time.time;
            rec.AddEvent(new HitEvent { Kind = EvKind.Knockout, Text = "Crew knocked out", RealT = Time.time, Severity = 3 });
        }

        static void UnitAbandonedPostfix(Unit __instance)
        {
            var rec = RecordForUnit(__instance, 25f);
            if (rec != null && rec.OutAt < 0f) rec.OutAt = Time.time;
        }

        internal static string Pretty(string goName)
        {
            if (string.IsNullOrEmpty(goName)) return "?";
            string s = goName.Replace("_", " ").Replace("(Clone)", "").Trim();
            return s.Length > 32 ? s.Substring(0, 32) : s;
        }

        // ------------------------------------------------------------------ per-frame

        public static void Update()
        {
            if (_records.Count == 0) return;
            float now = Time.time;
            List<ShotInfo> drop = null;
            foreach (var kv in _records)
            {
                var rec = kv.Value;
                if (rec.Target == null)
                {
                    // a round still flying (or one that only hit the ground); give up after its max flight time
                    if (now - rec.CreatedReal > 95f) (drop ??= new List<ShotInfo>()).Add(kv.Key);
                    continue;
                }
                if (!rec.Ready)
                {
                    // nothing more at the target for a few frames (the round stopped, or flew on out of it): the hit cam
                    // (already being built since the impact) can play
                    float sinceImpact = now - rec.FirstImpactReal;
                    if ((now - rec.LastActivityReal > 0.12f && sinceImpact > 0.2f) || sinceImpact > 1.0f)
                    {
                        rec.Ready = true;
                        Timeline.Build(rec);
                        HitCamPlayer.Enqueue(rec);
                        ArcadeHud.OnRecordReady(rec);
                        AfterAction.OnRecordReady(rec);
                    }
                }
                else if (now - rec.FirstImpactReal > 30f)
                {
                    // late events window over
                    if (!rec.Saved && Settings.SaveRecords.Value) Archive.Save(rec);
                    (drop ??= new List<ShotInfo>()).Add(kv.Key);
                }
            }
            if (drop != null) foreach (var k in drop) _records.Remove(k);
        }
    }
}
