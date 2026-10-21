using System;
using System.Collections.Generic;
using GHPC;
using GHPC.Humans;
using GHPC.Player;
using GHPC.State;
using GHPC.Weapons;
using UnityEngine;

namespace GHPCHitCam
{
    /// War Thunder-style arcade aids, each behind its own setting (master: Settings.ArcadeHud, key F6):
    /// shot callouts, a pen-indicator crosshair, enemy markers, the predicted impact point and a lead marker.
    /// Spotting lives in Contacts.cs, the minimap in Minimap.cs. UI only: nothing here touches damage or ballistics.
    internal static class ArcadeHud
    {
        // ------------------------------------------------------------------ player context (refreshed every frame)

        public static Unit Player;
        public static WeaponSystem Gun;
        public static FireControlSystem Fcs;
        public static AmmoType Ammo;
        public static Camera Cam;
        public static bool Visible;    // the game's HUD is up (no menu, map, pause or AAR)
        public static string HiddenWhy = "";

        public static bool On => Settings.ArcadeHud.Value;
        /// The callouts need the recorder even when the hit cam itself is switched off.
        public static bool WantsRecords => Settings.ArcadeHud.Value && Settings.ArcadeCallouts.Value;

        // the sight line: what's under the crosshair
        public static PenResult Probe;
        public static bool HasProbe;          // the sight ray hit something within range
        public static Vector3 AimPoint;       // what's under the crosshair (or the sight line at the FCS range)
        public static Vector3 SightPos, SightDir;
        public static bool HasAim;
        public static float AimRange;         // m from the muzzle
        // the round's real flight (ImpactArc): the cross carries the pen verdict, the ring is where the barrel points.
        // As in War Thunder the cross is only there while the round would hit a vehicle or a helicopter.
        public enum CrossKind { None, Armour, Friendly }
        public static ArcResult Arc;
        public static bool HasArc;
        public static Vector3 ImpactPoint;    // where the cross is drawn
        public static bool HasImpact;
        public static CrossKind Cross;        // debounced, with CrossVerdict
        public static PenVerdict CrossVerdict;
        public static Vector3 GunRingPoint;
        public static bool HasGunRing;
        public static Vector3 LeadPoint, LeadFrom;   // the lead nearest the sight (self-test log)
        public static bool HasLead;
        static Unit _impactUnit;                      // the cross rides on this vehicle between the 30 Hz updates
        static Vector3 _impactLocal;
        static float _nextProbe;
        internal static AmmoType TestAmmo;   // self-test: judge with this round instead of the loaded one
        internal static void ProbeNow() => _nextProbe = 0f;

        public static void Reset()
        {
            Player = null; Gun = null; Fcs = null; Ammo = null; Cam = null;
            HasProbe = HasAim = HasImpact = HasLead = HasArc = HasGunRing = false;
            Probe = default;
            Arc = default;
            _cAmmo = null;
            _call = null;
            _toasts.Clear();
            KillFeed.Reset();
            PenProbe.Reset();
            Contacts.Reset();
            Minimap.Reset();
        }

        // ------------------------------------------------------------------ update

        public static void Update()
        {
            if (Hotkeys.Down(Settings.ArcadeKey))
            {
                Settings.ArcadeHud.Value = !Settings.ArcadeHud.Value;
                HitCamMod.Log.Msg("Arcade HUD " + (Settings.ArcadeHud.Value ? "on" : "off"));
            }
            if (!On) { Visible = false; return; }
            RefreshPlayer();
            if (Player != null) HudSfx.Preload();
            KillFeed.Update();
            if (Player == null) { HasProbe = HasAim = HasImpact = HasLead = HasArc = HasGunRing = false; return; }
            if (Settings.ArcadePenIndicator.Value || Settings.ArcadeImpactMarker.Value || Settings.ArcadeLeadMarker.Value)
            {
                if (Time.unscaledTime >= _nextProbe) { _nextProbe = Time.unscaledTime + 1f / 30f; UpdateAim(); }
            }
            else HasProbe = HasAim = HasImpact = HasLead = HasArc = HasGunRing = false;
            if (Settings.ArcadeMarkers.Value || Settings.ArcadeMinimap.Value || Settings.ArcadeImpactMarker.Value || Settings.ArcadeLeadMarker.Value) Contacts.Update(Player);
            UpdateCallout();
            if (Settings.ArcadeMinimap.Value && Visible) Minimap.Update();
        }

        static void RefreshPlayer()
        {
            var pi = SceneUtil.Player;
            Player = pi != null ? pi.CurrentPlayerUnit : null;
            if (Player == null || Player.Destroyed) { Player = null; Visible = false; return; }
            WeaponSystemInfo wsi = null;
            try { wsi = pi.CurrentPlayerWeapon; } catch { }
            Gun = wsi != null && wsi.Weapon != null ? wsi.Weapon : Arsenal.MainGun(Player);
            Fcs = wsi != null && wsi.FCS != null ? wsi.FCS : Gun != null ? Gun.FCS : null;
            Ammo = LoadedAmmo(Gun, Fcs);
            Cam = GHPC.Camera.CameraManager.MainCam;
            string why = Cam == null ? "no camera" : "";
            try
            {
                if (!pi.ShowHUD) why = "game HUD hidden";
                else if (pi.IsMapActive) why = "map open";
                else if (pi.IsMainMenuActive) why = "menu open";
                else if (AarController.InAar) why = "AAR";
                else if (TimeController.Paused && !ConfigMenu.IsOpen) why = "paused";
            }
            catch { }
            HiddenWhy = why;
            Visible = why.Length == 0;
        }

        /// The round the next shot will be: the arsenal's pick while it is on, else what's in the breech.
        static AmmoType LoadedAmmo(WeaponSystem gun, FireControlSystem fcs)
        {
            if (TestAmmo != null) return TestAmmo;
            AmmoType a = null;
            try { a = gun != null && gun.Feed != null ? gun.Feed.AmmoTypeInBreech : null; } catch { }
            if (a == null && gun != null) a = gun.CurrentAmmoType;
            if (a == null && fcs != null) a = fcs.CurrentAmmoType;
            if (Arsenal.On && a != null && a.GetFinalCaliber() >= 20f)
            {
                var e = Arsenal.Selected;
                if (e != null && !e.Bomb) return e.Ammo;
            }
            return a;
        }

        // ------------------------------------------------------------------ ballistics (the game's own firing tables)

        static BallisticComputer _bc;
        static AmmoType _bcFor;
        static float _bcMax;

        static BallisticComputer Bc(AmmoType a)
        {
            if (a == null || a.Guidance != 0) return null;
            if (_bcFor == a && _bc != null) return _bc;
            _bc = null; _bcFor = a; _bcMax = 0f;
            try
            {
                // the FCS registers the rounds it carries; anything else (arsenal) gets its table built once, quietly
                if (!BallisticComputerRepository._precalculatedComputers.TryGetValue(a, out _bc))
                    _bc = BallisticComputerRepository.Instance.RegisterAmmoType(a);
                var pts = _bc?.AllSimPoints;
                if (pts != null && pts.Count > 1) _bcMax = pts[pts.Count - 1].ImpactDistance * 0.98f;
            }
            catch (Exception e) { HitCamMod.Log.Warning("Arcade HUD: no firing table for " + a.Name + ": " + e.Message); _bc = null; }
            return _bc;
        }

        static float Fall(AmmoType a, float range)
        {
            var bc = Bc(a);
            if (bc == null || _bcMax <= 1f) return 0f;
            return bc.GetFallOfShot(Mathf.Clamp(range, 1f, _bcMax));
        }

        public static float FlightTime(AmmoType a, float range)
        {
            if (a == null) return 0f;
            var bc = Bc(a);
            if (bc == null || _bcMax <= 1f) return range / Mathf.Max(a.MuzzleVelocity, 50f);
            return bc.GetFlightTime(Mathf.Clamp(range, 1f, _bcMax));
        }

        // ------------------------------------------------------------------ aim: pen probe, impact point, lead

        static void UpdateAim()
        {
            HasProbe = HasAim = HasImpact = HasLead = HasGunRing = false;
            LeadCount = 0;
            _impactUnit = null;
            Transform muzzle = Gun != null ? Gun.MuzzleIdentity : null;
            if (muzzle == null || Ammo == null) { HasArc = false; SetCross(CrossKind.None, PenVerdict.None); return; }

            Vector3 sightPos = muzzle.position, sightDir = muzzle.forward;
            if (Fcs != null)
            {
                Transform at = null;
                try { at = Fcs.AimTransform; } catch { }
                if (at != null) sightPos = at.position;
                Vector3 v = Fcs.AimWorldVector;
                sightDir = v.sqrMagnitude > 0.5f ? v : at != null ? at.forward : muzzle.forward;
            }
            float maxDist = Mathf.Min(Ammo.MaximumRange > 10f ? Ammo.MaximumRange : 4000f, 4000f);
            SightPos = sightPos;
            SightDir = sightDir.normalized;

            // the sight line: what the crosshair is on (misses after a shot, kill credit, the minimap)
            float offset = Vector3.Distance(sightPos, muzzle.position);
            HasProbe = PenProbe.Probe(sightPos, sightDir, maxDist, Ammo, Player, Player.Allegiance, out Probe, default, -1f, offset);
            if (HasProbe) { AimPoint = Probe.Point; HasAim = true; }
            else if (Fcs != null && Fcs.CurrentRange > 1f) { AimPoint = sightPos + sightDir.normalized * Fcs.CurrentRange; HasAim = true; }
            AimRange = HasAim ? Vector3.Distance(muzzle.position, AimPoint) : 0f;
            Vector3 own = SafeVelocity(Player);
            FillMoving(muzzle.position, maxDist);

            if (Ammo.Guidance != 0)
            {
                // a missile flies to the sight: judge what's under it
                HasArc = false;
                Arc = default;
                if (HasProbe) { Arc.Hit = true; Arc.Point = Probe.Point; Arc.Probe = Probe; Arc.Pen = Probe.Pen; Arc.Armed = true; Arc.Unit = Probe.Unit; }
            }
            else
            {
                March(muzzle, own, maxDist * 1.05f);
                // the barrel's straight line out to where the round comes down
                GunRingPoint = muzzle.position + muzzle.forward * (Arc.Hit ? Vector3.Distance(muzzle.position, Arc.Point) : AimRange > 1f ? AimRange : 1000f);
                HasGunRing = true;
            }

            // the cross: only while the round would hit a vehicle or a helicopter (a moving one met in its own frame, so
            // the cross sits where it would really be hit if fired now); ground, scenery and sky get nothing
            CrossKind kind = CrossKind.None;
            PenVerdict verdict = PenVerdict.None;
            Unit hit = Arc.Hit ? (Arc.Unit != null ? Arc.Unit : Arc.Probe.Unit) : null;
            if (hit != null && hit != Player && hit.Type != UnitType.Infantry)
            {
                var pr = Arc.Probe;
                ImpactPoint = Arc.Point;
                HasImpact = true;
                _impactUnit = hit;
                _impactLocal = hit.transform.InverseTransformPoint(Arc.Point);
                if (pr.Verdict == PenVerdict.Friendly || hit.Allegiance == Player.Allegiance) kind = CrossKind.Friendly;
                else
                {
                    kind = CrossKind.Armour;
                    verdict = pr.Plates > 0 ? pr.Verdict : PenVerdict.Pen;   // no armour on that line (a helicopter's skin, a truck): it goes in
                }
            }
            SetCross(kind, verdict);
            UpdateLeads(muzzle, own, maxDist);
        }

        /// Vehicles and helicopters on the move within the gun's reach: the arc meets each one in its own moving frame.
        static void FillMoving(Vector3 from, float reach)
        {
            ImpactArc.Moving.Clear();
            ImpactArc.MovingVel.Clear();
            float r2 = (reach + 60f) * (reach + 60f);
            for (int pass = 0; pass < 2; pass++)
            {
                var list = pass == 0 ? Contacts.All : Contacts.Friends;
                for (int i = 0; i < list.Count; i++)
                {
                    var u = list[i].Unit;
                    if (u == null || u == Player || list[i].Destroyed) continue;
                    if ((list[i].Center - from).sqrMagnitude > r2) continue;
                    Vector3 v = SafeVelocity(u);
                    if (v.sqrMagnitude < 0.25f) continue;
                    ImpactArc.Moving.Add(u);
                    ImpactArc.MovingVel.Add(v);
                }
            }
        }

        // ------------------------------------------------------------------ lead on aircraft (War Thunder: air targets only)

        internal struct LeadMark
        {
            public Contacts.Contact C;
            public Vector3 Offset;    // from the aircraft's middle to where the sight goes (drawn on its current position)
            public float Tof, Drop;
        }
        public static readonly LeadMark[] Leads = new LeadMark[6];
        public static int LeadCount;

        // the barrel's offset from the sight (zeroing, the FCS's own lead): azimuth / elevation in degrees, sight frame
        static float _offAz, _offEl;

        /// For every enemy helicopter / aircraft in reach: where to put the sight so the selected weapon's round (its own
        /// muzzle velocity, drag and drop, out of its own muzzle, plus our own speed) meets it. The flight time is iterated
        /// against the aircraft's velocity; the sight-to-barrel offset the FCS holds (zeroing, its own lead) is kept. That
        /// offset is only taken while it is small: while the turret is still slewing after the sight the last one stands.
        static void UpdateLeads(Transform muzzle, Vector3 own, float reach)
        {
            LeadCount = 0;
            HasLead = false;
            if (!Settings.ArcadeLeadMarker.Value || Ammo == null || Ammo.Guidance != 0) return;
            Vector3 m = muzzle.position;
            Vector3 f = SightDir, side = Vector3.Cross(Vector3.up, f);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
            side.Normalize();
            Vector3 u = Vector3.Cross(f, side), b = muzzle.forward;
            float az = Mathf.Atan2(Vector3.Dot(b, side), Vector3.Dot(b, f)) * Mathf.Rad2Deg;
            float el = Mathf.Asin(Mathf.Clamp(Vector3.Dot(b, u), -1f, 1f)) * Mathf.Rad2Deg;
            if (Mathf.Abs(az) < 5f && Mathf.Abs(el) < 10f) { _offAz = az; _offEl = el; }
            float bestAng = float.MaxValue;
            foreach (var c in Contacts.All)
            {
                if (LeadCount >= Leads.Length) break;
                if (!c.Shown || c.Destroyed || c.Unit == null || c.Unit.Type != UnitType.AirVehicle || c.Distance > reach) continue;
                Vector3 p = c.Center, v = SafeVelocity(c.Unit), at = p;
                float t = 0f, drop = 0f;
                bool ok = false;
                for (int it = 0; it < 5; it++)
                {
                    Vector3 d = at - m;
                    float r = Vector3.Dot(p + v * t - m, d.normalized);
                    if (r < 5f || !ImpactArc.Flight(Ammo, d, own, r, out t, out drop)) { ok = false; break; }
                    ok = true;
                    at = p + v * t + Vector3.up * drop;
                }
                if (!ok) continue;
                // the barrel must point at `at`: the sight goes that much lower / to the side
                Vector3 need = at - m;
                float dist = need.magnitude;
                need /= dist;
                float yaw = Mathf.Atan2(need.x, need.z) * Mathf.Rad2Deg - _offAz;
                float pitch = Mathf.Asin(Mathf.Clamp(need.y, -1f, 1f)) * Mathf.Rad2Deg - _offEl;
                Vector3 sightAt = SightPos + Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward * dist;
                Leads[LeadCount++] = new LeadMark { C = c, Offset = sightAt - p, Tof = t, Drop = drop };
                float ang = Vector3.Angle(SightDir, sightAt - SightPos);
                if (ang < bestAng) { bestAng = ang; LeadFrom = p; LeadPoint = sightAt; HasLead = true; }
            }
        }

        // the arc is reused while the gun, the round and what it hit barely move
        static Vector3 _cPos, _cFwd, _cOwn, _cUnitPos;
        static AmmoType _cAmmo;
        static Unit _cUnit;
        static float _cTime;
        public static int ArcRecomputes;

        static void March(Transform muzzle, Vector3 own, float maxPath)
        {
            Vector3 pos = muzzle.position, fwd = muzzle.forward;
            float now = Time.unscaledTime;
            bool same = ImpactArc.Moving.Count == 0 && _cAmmo == Ammo && now - _cTime < 0.3f && (pos - _cPos).sqrMagnitude < 0.0004f && Vector3.Angle(fwd, _cFwd) < 0.01f
                && (own - _cOwn).sqrMagnitude < 0.04f && (_cUnit == null || (_cUnit.transform.position - _cUnitPos).sqrMagnitude < 0.0025f);
            if (same && HasArc) return;
            _cAmmo = Ammo; _cPos = pos; _cFwd = fwd; _cOwn = own; _cTime = now;
            ImpactArc.March(pos, fwd, own, Ammo, maxPath, Player, Player.Allegiance, out Arc);
            HasArc = true;
            ArcRecomputes++;
            _cUnit = Arc.Hit ? Arc.Probe.Unit : null;
            if (_cUnit != null) _cUnitPos = _cUnit.transform.position;
        }

        // the cross's state only switches after two updates in a row agree (no flicker on plate edges)
        static CrossKind _candKind;
        static PenVerdict _candVerdict;
        static int _candCount;

        static void SetCross(CrossKind kind, PenVerdict verdict)
        {
            if (kind == Cross && verdict == CrossVerdict) { _candCount = 0; return; }
            if (kind == _candKind && verdict == _candVerdict) _candCount++;
            else { _candKind = kind; _candVerdict = verdict; _candCount = 1; }
            if (_candCount >= 2 || Cross == CrossKind.None) { Cross = kind; CrossVerdict = verdict; _candCount = 0; }
        }

        static Vector3 SafeVelocity(Unit u)
        {
            try { return u != null ? u.Velocity : Vector3.zero; } catch { return Vector3.zero; }
        }

        public static bool ToScreen(Vector3 w, out Vector2 gui)
        {
            gui = default;
            if (Cam == null) return false;
            Vector3 sp = Cam.WorldToScreenPoint(w);
            gui = new Vector2(sp.x, Screen.height - sp.y);
            return sp.z > 0.05f;
        }

        // ------------------------------------------------------------------ shot callouts

        internal class Callout
        {
            public Unit Target;
            public float Last;
            public string Title;
            public Color Color;
            public readonly List<HitRecord> Recs = new List<HitRecord>();
            public readonly List<string> Lines = new List<string>();
            public readonly List<int> Sev = new List<int>();
            public int EventCount;
        }

        static Callout _call;
        public static Callout Current => _call;
        const float CallSeconds = 2.5f;

        /// A player's shot finished (Recorder: the record is complete and its verdict computed).
        public static void OnRecordReady(HitRecord rec)
        {
            try
            {
                if (!WantsRecords || rec == null || !rec.PlayerShot) return;
                float now = Time.unscaledTime;
                // a burst on the same vehicle stays one callout with the worst result
                if (_call == null || _call.Target != rec.Target || now - _call.Last > CallSeconds + 0.5f)
                    _call = new Callout { Target = rec.Target };
                _call.Recs.Add(rec);
                if (_call.Recs.Count > 40) _call.Recs.RemoveAt(0);
                _call.Last = now;
                Rebuild(_call);
                ToastFromCallout(_call);
            }
            catch (Exception e) { HitCamMod.Log.Error("Arcade callout: " + e); }
            KillFeed.OnRecordReady(rec);
        }

        static void UpdateCallout()
        {
            var c = _call;
            if (c == null) return;
            float now = Time.unscaledTime;
            if (now - c.Last > CallSeconds + 1f) { _call = null; return; }
            // late events (kill, knock-out, cook-off) arrive after the record was handed over: refresh the verdict
            int n = 0;
            foreach (var r in c.Recs) n += r.Events.Count;
            if (n != c.EventCount)
            {
                string before = c.Title;
                foreach (var r in c.Recs) Timeline.ComputeTitle(r);
                Rebuild(c);
                if (c.Title != before) { c.Last = now; ToastFromCallout(c); }   // a new, worse verdict: show it again
            }
        }

        static void Rebuild(Callout c)
        {
            HitRecord worst = null;
            int n = 0;
            foreach (var r in c.Recs)
            {
                n += r.Events.Count;
                if (worst == null || Timeline.Rank(r.Title) > Timeline.Rank(worst.Title)) worst = r;
            }
            c.EventCount = n;
            c.Title = worst != null ? worst.Title : "HIT";
            c.Color = worst != null ? worst.TitleColor : Color.white;
            if (c.Recs.Count > 1) c.Title += "  ×" + c.Recs.Count;

            // what actually got hurt: the same filter as the verdict (Timeline.Matters / Real), worst state per part
            var best = new Dictionary<string, (string text, int sev, float t)>();
            foreach (var r in c.Recs)
                foreach (var e in r.Events)
                {
                    string key, text;
                    int sev;
                    switch (e.Kind)
                    {
                        case EvKind.Module:
                            if (!Timeline.Real(e)) continue;
                            key = "m:" + e.Subject;
                            sev = e.After <= 0f ? 3 : 2;
                            text = e.After <= 0f ? e.Subject + " destroyed" : e.Subject + " damaged";
                            break;
                        case EvKind.Crew:
                            var st = (HealthStatus)Mathf.RoundToInt(e.After);
                            if (st < HealthStatus.Wounded) continue;
                            key = "c:" + e.Subject;
                            sev = st >= HealthStatus.Critical ? 3 : st >= HealthStatus.HeavilyWounded ? 2 : 1;
                            text = e.Subject + " " + Recorder.StatusText(st);
                            break;
                        case EvKind.AmmoFire: key = "af:" + e.Subject; sev = 3; text = e.Text; break;
                        case EvKind.Fire: key = "f:" + e.Subject; sev = 2; text = e.Text; break;
                        case EvKind.AmmoHit: key = "ah"; sev = 2; text = "Ammunition hit"; break;
                        default: continue;
                    }
                    if (best.TryGetValue(key, out var old) && old.sev >= sev) continue;
                    best[key] = (text, sev, e.RealT);
                }
            var list = new List<(string text, int sev, float t)>(best.Values);
            list.Sort((a, b) => a.sev != b.sev ? b.sev.CompareTo(a.sev) : a.t.CompareTo(b.t));
            c.Lines.Clear();
            c.Sev.Clear();
            int max = 6;
            for (int i = 0; i < list.Count && i < max; i++) { c.Lines.Add(list[i].text); c.Sev.Add(list[i].sev); }
            if (list.Count > max) { c.Lines.Add("+" + (list.Count - max) + " more"); c.Sev.Add(0); }
        }

        // ------------------------------------------------------------------ drawing

        public static void Draw()
        {
            if (!On || Event.current.type != EventType.Repaint) return;
            if (Player == null || !Visible) return;
            HudGfx.Init();
            // War Thunder proportions: sizes are pixels at 1080p, scaled by the screen height only (3440x1440 = x1.33)
            float k = Mathf.Max(Screen.height / 1080f, 0.6f);
            HudGfx.Styles(k);
            var old = GUI.color;
            try
            {
                if (Settings.ArcadeMarkers.Value) DrawMarkers(k * Scale(Settings.HudScaleMarkers));
                float kc = k * Scale(Settings.HudScaleCross);
                if (Settings.ArcadeImpactMarker.Value)
                {
                    if (HasGunRing) DrawGunRing(kc);
                    if (_impactUnit != null) ImpactPoint = _impactUnit.transform.TransformPoint(_impactLocal);
                    if (HasImpact && Cross != CrossKind.None) DrawCross(kc);
                }
                if (Settings.ArcadeLeadMarker.Value && LeadCount > 0) DrawLeads(kc);
                if (Settings.ArcadeMinimap.Value) Minimap.Draw(k);
                if (Settings.ArcadeKillFeed.Value) KillFeed.Draw(k * Scale(Settings.HudScaleKillFeed));
                if (Settings.ArcadeCallouts.Value) DrawCallout(k * Scale(Settings.HudScaleCallouts));
            }
            finally { GUI.color = old; }
        }

        public static float Scale(MelonLoader.MelonPreferences_Entry<float> e) => e != null ? Mathf.Clamp(e.Value, 0.4f, 3f) : 1f;

        // the cross's text is rebuilt only when what it says changes (10 mm / 1 degree steps)
        static string _crossText = "";
        static int _crossKey = int.MinValue;
        static readonly Color CrossGrey = new Color(0.78f, 0.8f, 0.83f, 0.95f);

        static void DrawCross(float k)
        {
            if (!ToScreen(ImpactPoint, out Vector2 s)) return;
            bool armour = Cross == CrossKind.Armour;
            Color c = armour ? (Settings.ArcadePenIndicator.Value ? PenProbe.ColorOf(CrossVerdict) : Color.white) : CrossGrey;
            c.a = 0.97f;
            Color dark = new Color(0f, 0f, 0f, 0.5f);
            float g = 0f, l = 6f * k, t = Mathf.Max(1.4f * k, 1.3f);
            for (int pass = 0; pass < 2; pass++)
            {
                Color cc = pass == 0 ? dark : c;
                float o = pass == 0 ? 1f : 0f;
                HudGfx.Box(new Rect(s.x - g - l - o, s.y - t * 0.5f - o, l + 2 * o, t + 2 * o), cc);
                HudGfx.Box(new Rect(s.x + g - o, s.y - t * 0.5f - o, l + 2 * o, t + 2 * o), cc);
                HudGfx.Box(new Rect(s.x - t * 0.5f - o, s.y - g - l - o, t + 2 * o, l + 2 * o), cc);
                HudGfx.Box(new Rect(s.x - t * 0.5f - o, s.y + g - o, t + 2 * o, l + 2 * o), cc);
            }

            var a = Arc;
            int key;
            switch (Cross)
            {
                case CrossKind.Armour:
                    if (!Settings.ArcadePenIndicator.Value || !Settings.ArcadePenNumbers.Value) return;
                    int pen10 = Mathf.RoundToInt(a.Probe.Pen / 10f), los10 = Mathf.RoundToInt(a.Probe.Los / 10f), ang = Mathf.RoundToInt(a.Probe.Angle);
                    key = CrossVerdict == PenVerdict.Ricochet ? (1 << 28) + ang * 100 + Mathf.RoundToInt(a.Probe.RicochetAngle)
                        : (int)CrossVerdict * 1000000 + pen10 * 1000 + los10 + (a.Armed ? 0 : 1 << 27);
                    if (key != _crossKey)
                        _crossText = CrossVerdict == PenVerdict.Ricochet ? $"RICOCHET  {ang}° < {a.Probe.RicochetAngle:0}°"
                            : (a.Armed ? "" : "fuze not armed  ") + $"{pen10 * 10} / {los10 * 10} mm";
                    break;
                // a miss is only named after a real shot (ShowMiss); the cross just stays grey where the round would land
                case CrossKind.Friendly: key = -4; if (key != _crossKey) _crossText = "FRIENDLY"; break;
                default: return;
            }
            _crossKey = key;
            HudGfx.Text(new Rect(s.x + g + l + 5f * k, s.y - 8f * k, 260f * k, 16f * k), _crossText, HudGfx.Font(11f * k, TextAnchor.MiddleLeft, false), c);
        }

        static void DrawGunRing(float k)
        {
            if (!ToScreen(GunRingPoint, out Vector2 s)) return;
            float r = 15f * k;
            HudGfx.Ring(s, r, new Color(0.92f, 0.94f, 0.96f, 0.55f));
        }

        /// War Thunder's air lead: a plain thin circle where to put the crosshair, nothing joining it to the aircraft.
        static void DrawLeads(float k)
        {
            for (int i = 0; i < LeadCount; i++)
            {
                var l = Leads[i];
                if (l.C == null || l.C.Destroyed || !l.C.Shown) continue;
                if (!ToScreen(l.C.Center + l.Offset, out Vector2 s)) continue;
                float r = 7.5f * k;
                HudGfx.Ring(s, r + 0.8f, new Color(0f, 0f, 0f, 0.4f));
                HudGfx.Ring(s, r, new Color(1f, 0.3f, 0.25f, 0.95f));
            }
        }

        /// An aircraft / helicopter (War Thunder): thin corner brackets round it, its name above in red, the range in km below.
        static void DrawAir(Contacts.Contact c, float k, float a)
        {
            if (!ToScreen(c.Center, out Vector2 s)) return;
            float px = Screen.height / (2f * Mathf.Max(c.Distance, 1f) * Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float h = Mathf.Clamp(Mathf.Max(c.HalfHeight * 2.2f, 4f) * px, 9f * k, 42f * k);
            // hairline thin, as War Thunder draws them, with only a faint dark edge to read against a bright sky
            float len = Mathf.Max(h * 0.35f, 4f * k), t = Mathf.Max(0.75f * k, 1f);
            Color red = new Color(1f, 0.25f, 0.2f, a), dark = new Color(0f, 0f, 0f, 0.3f * a);
            for (int pass = 0; pass < 2; pass++)
            {
                Color cc = pass == 0 ? dark : red;
                float o = pass == 0 ? 0.5f : 0f;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        float x = s.x + sx * h, y = s.y + sy * h;
                        // each corner's two arms point inwards
                        HudGfx.Box(new Rect((sx < 0 ? x : x - len) - o, y - t * 0.5f - o, len + 2 * o, t + 2 * o), cc);
                        HudGfx.Box(new Rect(x - t * 0.5f - o, (sy < 0 ? y : y - len) - o, t + 2 * o, len + 2 * o), cc);
                    }
            }
            var f = HudGfx.Font(11f * k, TextAnchor.MiddleCenter, false);
            HudGfx.Text(new Rect(s.x - 150f * k, s.y - h - 17f * k, 300f * k, 14f * k), c.Name, f, red);
            HudGfx.Text(new Rect(s.x - 150f * k, s.y + h + 3f * k, 300f * k, 14f * k), c.KmLabel, f, red);
        }

        // off-screen markers already placed this frame (to keep them from piling up on one spot of the edge)
        static readonly Vector2[] _placed = new Vector2[48];
        static int _placedN;

        static Vector2 Unpile(Vector2 e, Rect inner, float k)
        {
            bool sideEdge = Mathf.Abs(e.x - inner.xMin) < 2f || Mathf.Abs(e.x - inner.xMax) < 2f;
            float sepY = 17f * k, sepX = 70f * k;
            for (int pass = 0; pass < 8; pass++)
            {
                bool moved = false;
                for (int i = 0; i < _placedN; i++)
                {
                    Vector2 p = _placed[i];
                    if (Mathf.Abs(p.x - e.x) < sepX && Mathf.Abs(p.y - e.y) < sepY)
                    {
                        if (sideEdge) e.y = Mathf.Clamp(p.y + sepY * (e.y >= inner.center.y ? -1f : 1f), inner.yMin, inner.yMax);
                        else e.x = Mathf.Clamp(p.x + sepX * (e.x >= inner.center.x ? -1f : 1f), inner.xMin, inner.xMax);
                        moved = true;
                    }
                }
                if (!moved) break;
            }
            if (_placedN < _placed.Length) _placed[_placedN++] = e;
            return e;
        }

        static void DrawMarkers(float k)
        {
            _placedN = 0;
            float m = 26f * k;
            Rect inner = new Rect(m, m, Screen.width - 2 * m, Screen.height - 2 * m);
            Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            // the one nearest the crosshair also gets its distance (War Thunder keeps the rest to a name)
            Vector2 aim = HasImpact && ToScreen(ImpactPoint, out Vector2 ip) ? ip : centre;
            Contacts.Contact nearest = null;
            float best = 90f * k;
            foreach (var c in Contacts.All)
            {
                if (!c.Shown || c.Destroyed || c.Unit == null || !ToScreen(c.Center, out Vector2 cs0)) continue;
                float dd = (cs0 - aim).magnitude;
                if (dd < best) { best = dd; nearest = c; }
            }
            foreach (var c in Contacts.All)
            {
                if (!c.Shown || c.Unit == null) continue;
                Vector3 top = c.Center + Vector3.up * (c.HalfHeight + 0.8f);
                Vector3 sp3 = Cam.WorldToScreenPoint(top);
                bool behind = sp3.z <= 0.05f;
                Vector2 s = new Vector2(sp3.x, Screen.height - sp3.y);
                if (c.Destroyed)
                {
                    if (behind || !inner.Contains(s)) continue;
                    Vector2 p = ToScreen(c.Center, out Vector2 cs) ? cs : s;
                    HudGfx.Cross(p, 7f * k, 2f * k, new Color(0.75f, 0.75f, 0.75f, 0.45f));
                    continue;
                }
                float age = Time.time - c.LastSeen;
                float a = c.SeenNow ? 1f : Mathf.Lerp(0.85f, 0.35f, Mathf.Clamp01(age / Mathf.Max(Settings.MarkerMemorySeconds.Value, 0.1f)));
                Color red = new Color(1f, 0.25f, 0.2f, a);
                if (!behind && inner.Contains(s) && c.Unit.Type == UnitType.AirVehicle) { DrawAir(c, k, a); continue; }
                if (!behind && inner.Contains(s))
                {
                    float up = 17f * k;
                    if (Settings.MarkerClassIcons.Value)
                    {
                        HudGfx.Icon(s + new Vector2(0f, 1f * k), 7.5f * k, c.Class, red);
                        up = 22f * k;
                    }
                    else
                    {
                        HudGfx.Triangle(s + new Vector2(0f, 4f * k), 5f * k, 180f, new Color(0f, 0f, 0f, 0.45f * a));
                        HudGfx.Triangle(s + new Vector2(0f, 3.5f * k), 4f * k, 180f, red);
                    }
                    HudGfx.Text(new Rect(s.x - 150f * k, s.y - up, 300f * k, 14f * k), c == nearest ? c.NameDist : c.Name, HudGfx.Font(11f * k, TextAnchor.MiddleCenter, false), new Color(1f, 0.85f, 0.82f, a));
                }
                else
                {
                    // off screen: on the edge, with an arrow towards it
                    Vector2 d = s - centre;
                    if (behind) d = -d;
                    if (d.sqrMagnitude < 1f) d = Vector2.down;
                    float sx = d.x != 0f ? (inner.width * 0.5f) / Mathf.Abs(d.x) : float.MaxValue;
                    float sy = d.y != 0f ? (inner.height * 0.5f) / Mathf.Abs(d.y) : float.MaxValue;
                    Vector2 e = Unpile(centre + d * Mathf.Min(sx, sy), inner, k);
                    float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg + 90f;   // the triangle points up at 0
                    HudGfx.Triangle(e, 6.5f * k, ang, new Color(0f, 0f, 0f, 0.45f * a));
                    HudGfx.Triangle(e, 5.5f * k, ang, red);
                }
            }
        }

        // ------------------------------------------------------------------ War Thunder style message stack (top centre)

        /// One line of the stack: what happened (sentence case, coloured) and a short grey detail line under it.
        internal class Toast
        {
            public string Title, Sub;
            public Color Color;
            public float Updated;
            public object Key;
        }

        static readonly List<Toast> _toasts = new List<Toast>();
        public static IReadOnlyList<Toast> Toasts => _toasts;
        const float ToastSeconds = 3.6f;
        static readonly Color MissGrey = new Color(0.82f, 0.84f, 0.88f);

        /// Push (or, with the same key, update) a message. War Thunder shows hit results and kills this way, small, under the compass.
        public static void PushToast(string title, Color c, string sub, object key = null)
        {
            Toast t = null;
            string who = Who(sub);
            float now = Time.unscaledTime;
            foreach (var x in _toasts)
                if ((key != null && x.Key == key) || (x.Title == title && who.Length > 0 && Who(x.Sub) == who && now - x.Updated < 3f)) { t = x; break; }
            if (t != null && key != null) t.Key = key;
            if (t != null && (sub ?? "").Length < (t.Sub ?? "").Length && Who(t.Sub) == who) sub = t.Sub;   // keep the fuller detail
            if (t == null)
            {
                t = new Toast { Key = key };
                _toasts.Insert(0, t);
                while (_toasts.Count > 3) _toasts.RemoveAt(_toasts.Count - 1);
            }
            t.Title = title; t.Sub = sub ?? ""; t.Color = c; t.Updated = Time.unscaledTime;
            if (AutoTest.Active) HitCamMod.Log.Msg($"Arcade HUD: message '{title}' {sub}");
        }

        static string Who(string sub)
        {
            if (string.IsNullOrEmpty(sub)) return "";
            int i = sub.IndexOf("  ·  ", StringComparison.Ordinal);
            return i < 0 ? sub : sub.Substring(0, i);
        }

        /// One of your rounds came down without touching a vehicle: name the miss (only after a real shot, as War Thunder).
        public static void ShowMiss(string title, string vehicle)
        {
            if (!WantsRecords) return;
            PushToast(Sentence(title), MissGrey, vehicle ?? "");
        }

        /// The enemy the player is shooting at: under the crosshair, under the impact cross, or the marked one nearest the crosshair.
        public static Unit AimedEnemy(out Vector3 center)
        {
            center = default;
            var me = Player;
            if (me == null) return null;
            // what the round will hit (the arc, moving targets in their own frame) first: with the sight held on a lead
            // the sight line can run past another vehicle
            if (HasArc && Arc.Hit && Arc.Probe.Unit != null && Arc.Probe.Unit != me && Arc.Probe.Unit.Allegiance != me.Allegiance && !Arc.Probe.Unit.Destroyed)
            {
                // a moving target is met where the round and it get to together (the arc's point at that flight distance)
                center = Arc.Moving && ImpactArc.PointAtPath(Arc.Path, out Vector3 meet, out _) ? meet : Arc.Point;
                return Arc.Probe.Unit;
            }
            if (HasProbe && Probe.Unit != null && Probe.Unit != me && Probe.Unit.Allegiance != me.Allegiance && !Probe.Unit.Destroyed)
            { center = Probe.Point; return Probe.Unit; }
            // aiming over it (a lob) or beside it: the marked enemy closest to the sight line, by angle (a zoomed sight
            // makes pixel distances meaningless)
            Contacts.Contact best = null;
            float bd = 3f;
            if (SightDir.sqrMagnitude > 0.5f)
                foreach (var c in Contacts.All)
                {
                    if (!c.Shown || c.Destroyed || c.Unit == null) continue;
                    float d = Vector3.Angle(SightDir, c.Center - SightPos);
                    if (d < bd) { bd = d; best = c; }
                }
            if (best == null) return null;
            center = best.Center;
            return best.Unit;
        }

        /// "CRITICAL HIT" -> "Critical hit".
        static string Sentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
        }

        static void ToastFromCallout(Callout c)
        {
            if (c == null || c.Recs.Count == 0) return;
            var worst = c.Recs[0];
            foreach (var r in c.Recs) if (Timeline.Rank(r.Title) > Timeline.Rank(worst.Title)) worst = r;
            string title = Sentence(worst.Title) + (c.Recs.Count > 1 ? "  ×" + c.Recs.Count : "");
            // the kill hooks announce kills themselves ("Target destroyed"); here the detail: what it did, two items at most
            string sub = "";
            int n = 0;
            for (int i = 0; i < c.Lines.Count && n < 2; i++) { if (c.Sev[i] <= 0) continue; sub += (n > 0 ? "  ·  " : "") + c.Lines[i]; n++; }
            int more = 0;
            for (int i = 0; i < c.Lines.Count; i++) if (c.Sev[i] > 0) more++;
            if (more > n) sub += $"  (+{more - n})";
            string who = c.Target != null ? Recorder.SafeName(c.Target) : "";
            if (who.Length > 0) sub = sub.Length > 0 ? who + "  ·  " + sub : who;
            PushToast(title, worst.TitleColor, sub, c);
        }

        static readonly Toast[] _sampleToasts =
        {
            new Toast { Title = "Target destroyed", Sub = "T-72M1  ·  ammunition exploded", Color = new Color(1f, 0.28f, 0.22f) },
            new Toast { Title = "Critical hit", Sub = "T-72M1  ·  engine destroyed  ·  Gunner killed", Color = new Color(1f, 0.6f, 0.15f) },
        };

        static void DrawCallout(float k)
        {
            bool preview = ConfigMenu.IsOpen && _toasts.Count == 0;
            int count = preview ? _sampleToasts.Length : _toasts.Count;
            float y = Screen.height * 0.15f;
            float w = 600f * k, x = (Screen.width - w) * 0.5f;
            var tf = HudGfx.Font(16f * k, TextAnchor.MiddleCenter);
            var sf = HudGfx.Font(11f * k, TextAnchor.MiddleCenter, false);
            float now = Time.unscaledTime;
            for (int i = 0; i < count; i++)
            {
                var t = preview ? _sampleToasts[i] : _toasts[i];
                float age = preview ? 1f : now - t.Updated;
                if (age > ToastSeconds + 0.5f) continue;
                float a = Mathf.Clamp01(age / 0.12f) * Mathf.Clamp01((ToastSeconds + 0.5f - age) / 0.5f);
                if (a <= 0.01f) continue;
                float pop = 1f + 0.12f * (1f - Mathf.Clamp01(age / 0.15f));
                var c = t.Color; c.a = a;
                HudGfx.Text(new Rect(x, y, w, 20f * k), t.Title, i == 0 ? HudGfx.Font(16f * k * pop, TextAnchor.MiddleCenter) : tf, c);
                y += 18f * k;
                if (!string.IsNullOrEmpty(t.Sub))
                {
                    HudGfx.Text(new Rect(x, y, w, 14f * k), t.Sub, sf, new Color(0.9f, 0.9f, 0.92f, 0.9f * a));
                    y += 14f * k;
                }
                y += 6f * k;
            }
            for (int i = _toasts.Count - 1; i >= 0; i--) if (now - _toasts[i].Updated > ToastSeconds + 1f) _toasts.RemoveAt(i);
        }
    }

    /// IMGUI drawing helpers for the arcade HUD (procedural textures, rotated shapes), allocation-free per frame.
    internal static class HudGfx
    {
        public static Texture2D White, RingTex, DotTex, TriTex, GearTex;
        public static GUIStyle Title, Small, Center, Line18;
        static int _styleFor = -1;

        public static void Init()
        {
            if (White != null) return;
            White = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            White.SetPixel(0, 0, Color.white);
            White.Apply();
            RingTex = Make(64, (x, y) => { float d = Mathf.Sqrt(x * x + y * y); return Mathf.Clamp01(1.5f - Mathf.Abs(d - 27f)); });
            DotTex = Make(32, (x, y) => Mathf.Clamp01(15f - Mathf.Sqrt(x * x + y * y)));
            // a gear: 8 teeth on a ring with a hole
            GearTex = Make(64, (x, y) =>
            {
                float d = Mathf.Sqrt(x * x + y * y), ang = Mathf.Atan2(y, x);
                float tooth = Mathf.Clamp01((Mathf.Cos(ang * 8f) - 0.15f) * 3f);
                float outer = 21f + 8f * tooth;
                return Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - 9f + 0.5f);
            });
            // a triangle pointing up (towards -y in GUI space)
            TriTex = Make(64, (x, y) =>
            {
                float yy = -y;            // texture rows go up
                float h = 28f;
                if (yy < -h || yy > h) return Mathf.Clamp01(h + 1f - Mathf.Abs(yy));
                float half = (h - yy) * 0.5f * 0.95f;
                return Mathf.Clamp01(half + 0.5f - Mathf.Abs(x)) * Mathf.Clamp01(h + 0.5f - Mathf.Abs(yy));
            });
        }

        static Texture2D Make(int n, Func<float, float, float> alpha)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    px[j * n + i] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(alpha(i - c, j - c))));
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        public static void Styles(float k)
        {
            int key = Mathf.RoundToInt(k * 100);
            if (Title != null && key == _styleFor) return;
            _styleFor = key;
            Title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(28 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Overflow };
            Line18 = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Overflow };
            Small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(13 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Overflow };
            Center = new GUIStyle(Small) { alignment = TextAnchor.MiddleCenter };
        }

        static readonly Dictionary<int, GUIStyle> _fonts = new Dictionary<int, GUIStyle>();

        /// A bold label style of this pixel size and alignment (cached; one per size the HUD scales use).
        public static GUIStyle Font(float px, TextAnchor anchor, bool bold = true)
        {
            int size = Mathf.Clamp(Mathf.RoundToInt(px), 6, 120);
            int key = (size * 16 + (int)anchor) * 2 + (bold ? 1 : 0);
            if (_fonts.TryGetValue(key, out var st) && st != null) return st;
            st = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal, alignment = anchor, wordWrap = false, clipping = TextClipping.Overflow };
            _fonts[key] = st;
            return st;
        }

        public static void Box(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, White);
        }

        public static void Ring(Vector2 s, float r, Color c)
        {
            GUI.color = c;
            float R = r * 32f / 27f;   // the texture's ring sits at 27 of 32 px
            GUI.DrawTexture(new Rect(s.x - R, s.y - R, 2 * R, 2 * R), RingTex);
        }

        public static void Dot(Vector2 s, float r, Color c)
        {
            GUI.color = c;
            float R = r * 16f / 15f;
            GUI.DrawTexture(new Rect(s.x - R, s.y - R, 2 * R, 2 * R), DotTex);
        }

        /// Triangle centred on s, pointing up at angle 0 (degrees, clockwise).
        public static void Triangle(Vector2 s, float r, float angle, Color c)
        {
            var m = GUI.matrix;
            if (angle != 0f) GUIUtility.RotateAroundPivot(angle, s);
            GUI.color = c;
            float R = r * 32f / 28f;
            GUI.DrawTexture(new Rect(s.x - R, s.y - R, 2 * R, 2 * R), TriTex);
            GUI.matrix = m;
        }

        /// A vehicle class icon (MapIcons atlas: white ink, dark rim) tinted with c, centred on s.
        public static void Icon(Vector2 s, float half, MapClass cls, Color c)
        {
            GUI.color = c;
            GUI.DrawTextureWithTexCoords(new Rect(s.x - half, s.y - half, 2f * half, 2f * half), MapIcons.Atlas, MapIcons.Uv(cls));
        }

        public static void Diamond(Vector2 s, float r, Color c)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, s);
            Box(new Rect(s.x - r * 0.72f, s.y - r * 0.72f, r * 1.44f, r * 1.44f), c);
            GUI.matrix = m;
        }

        public static void Cross(Vector2 s, float r, float t, Color c)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, s);
            Box(new Rect(s.x - r, s.y - t * 0.5f, 2 * r, t), c);
            Box(new Rect(s.x - t * 0.5f, s.y - r, t, 2 * r), c);
            GUI.matrix = m;
        }

        public static void Line(Vector2 a, Vector2 b, float t, Color c)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            Box(new Rect(a.x, a.y - t * 0.5f, len, t), c);
            GUI.matrix = m;
        }

        public static void Text(Rect r, string text, GUIStyle st, Color c)
        {
            var old = st.normal.textColor;
            GUI.color = Color.white;
            st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.85f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, st);
            st.normal.textColor = c;
            GUI.Label(r, text, st);
            st.normal.textColor = old;
        }
    }
}
