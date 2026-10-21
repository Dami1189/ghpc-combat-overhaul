using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GHPC;
using GHPC.Mission;
using GHPC.PhysicsHelpers;
using GHPC.Utility;
using GHPC.Weapons;
using MelonLoader;
using UnityEngine;

namespace GHPCHitCam
{
    /// Self-test part for the arcade HUD (runs from AutoTest when autotest.cfg exists, hud=false skips it).
    /// Full-screen PNGs in UserData/HitCam/autotest: hud_callout*.png during the shots, then at the end a test-range wave and
    /// hud_*.png for the markers, the impact cross's pen colours (the judged round swapped to get green / yellow / red), the
    /// zeroing test on a far target (lased / range too short = undershoot / aimed higher until it drops onto it / range too
    /// long = overshoot), the lead marker on a helicopter, live callouts + kill feed, off-screen markers, both minimap modes,
    /// and the settings window.
    /// Only the player's own aim / range / camera are moved, the same way the player's controls move them.
    internal static class ArcadeTest
    {
        static string Dir => Path.Combine(HitCamMod.DataDir, "autotest");

        static IEnumerator Shot(string name)
        {
            yield return null;
            ScreenCapture.CaptureScreenshot(Path.Combine(Dir, name + ".png"));
            yield return null;
            yield return null;
            HitCamMod.Log.Msg("AUTOTEST HUD: saved " + name + ".png  " + State());
        }

        static string State()
        {
            var cam = ArcadeHud.Cam;
            string view = cam != null ? $"; view fov {cam.fieldOfView:0}, exterior {GHPC.Camera.CameraManager.Instance?.ExteriorMode}" : "";
            var p = ArcadeHud.Probe;
            string probe = ArcadeHud.HasProbe
                ? $"probe {p.Verdict} on {(p.Unit != null ? Recorder.SafeName(p.Unit) : "-")} '{p.Plate}' at {p.Distance:0} m: pen {p.Pen:0} vs LOS {p.Los:0} (first plate {p.FirstLos:0}, {p.Plates} plates), angle {p.Angle:0}° (ricochet below {p.RicochetAngle:0}°)"
                : "probe: nothing";
            var a = ArcadeHud.Arc;
            string imp = "; cross " + ArcadeHud.Cross + (ArcadeHud.Cross == ArcadeHud.CrossKind.Armour ? "/" + ArcadeHud.CrossVerdict : "")
                + (a.Hit ? $" (arc{(a.Moving ? ", moving target" : "")}: {a.Path:0} m, {a.Time:0.00} s, {a.Speed:0} m/s, pen {a.Pen:0} vs LOS {a.Probe.Los:0} on {(a.Probe.Unit != null ? Recorder.SafeName(a.Probe.Unit) : a.Probe.Blocker ?? "ground")} '{a.Probe.Plate}', angle {a.Probe.Angle:0} deg)" : " (arc: no hit)")
                + (ArcadeHud.HasImpact && ArcadeHud.HasAim ? $", {(ArcadeHud.ImpactPoint - ArcadeHud.AimPoint).y:+0.00;-0.00} m vertical / {Vector3.Distance(ArcadeHud.ImpactPoint, ArcadeHud.AimPoint):0.00} m from the aim point" : "")
                + $"; arc steps {ImpactArc.LastSteps}, raycasts {ImpactArc.LastRaycasts}, recomputed {ArcadeHud.ArcRecomputes}x";
            string feed = KillFeed.Feed.Count > 0 ? $"; feed [{string.Join(" | ", KillFeed.Feed.Select(m => m.Text))}]" : "";
            string sfx = HudSfx.LastPlayedAt > 0f ? $"; sound '{HudSfx.LastPlayed}' {Time.unscaledTime - HudSfx.LastPlayedAt:0.0} s ago, output peak {HudSfx.OutputPeak():0.000}" : "";
            string lead = ArcadeHud.LeadCount > 0 ? "; lead " + string.Join(", ", ArcadeHud.Leads.Take(ArcadeHud.LeadCount).Select(l => $"{l.C?.Name} {l.Offset.magnitude:0.0} m ahead ({l.Tof:0.00} s, drop {l.Drop:0.0} m, its speed {SpeedOf(l.C?.Unit):0} m/s)")) : "";
            int shown = Contacts.All.Count(c => c.Shown), spotted = Contacts.All.Count(c => c.SeenNow), dead = Contacts.All.Count(c => c.Shown && c.Destroyed);
            var call = ArcadeHud.Current;
            return $"[{ArcadeHud.Ammo?.Name}] {probe}{imp}{lead}; markers {shown} (spotted now {spotted}, destroyed {dead}, game spotting {Contacts.UsingGameSpotting}); callout '{call?.Title}' {(call != null ? string.Join(" | ", call.Lines) : "")}{feed}{sfx}{view}; " + (ArcadeHud.Visible ? "HUD visible" : "HUD hidden: " + ArcadeHud.HiddenWhy);
        }

        /// The hit cam picked up a shot: the callout is up now.
        public static void OnShotHitCam(int shot)
        {
            // skinforce=true: every crewman forced limp under an extra blast for the skin-stretch check (off by default, so
            // the self-test replays show what a player sees: only the crew the game took out ragdoll)
            MelonCoroutines.Start(SkinWatch(shot, force: AutoTest.Opt("skinforce", "false") == "true"));
            if (!Settings.ArcadeHud.Value) return;
            MelonCoroutines.Start(CalloutShots(shot));
        }

        // ------------------------------------------------------------------ crew skin stretch (ragdoll check)

        class SkinRest { public int[] Tris; public Vector3[] Rest; public BoneWeight[] W; }
        static readonly Dictionary<SkinnedMeshRenderer, SkinRest> _rest = new Dictionary<SkinnedMeshRenderer, SkinRest>();

        /// While the hit cam plays, how far the crew skins' triangle edges stretch against their rest pose (max / 99th
        /// percentile, normalised by the median so the copy's scale cancels), and between which body parts the worst one sits.
        /// force: every crewman of the replay goes limp under a small blast in the middle of the hull (the replay's own
        /// ragdolls only, nothing in the game), so each seat's skin is checked in a limp pose whatever the shot did.
        static IEnumerator SkinWatch(int shot, Playback after = null, bool waitNew = false, float firedAt = -1f, bool force = false)
        {
            yield return null;
            var pb = HitCamPlayer.Current;
            // a live shot: its hit cam starts a moment later (and not an earlier shot's that is still queued)
            bool Mine(Playback x) => x != null && x != after && (firedAt < 0f || (x.Rec != null && x.Rec.CreatedReal >= firedAt - 0.2f));
            for (float w0 = Time.realtimeSinceStartup; waitNew && !Mine(pb) && Time.realtimeSinceStartup - w0 < 15f; pb = HitCamPlayer.Current) yield return null;
            if (pb == null || (waitNew && !Mine(pb))) { Log($"shot {shot} crew skin stretch: no hit cam"); yield break; }
            if (force)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                try
                {
                    var phys = HarmonyLib.AccessTools.Field(typeof(Playback), "_phys")?.GetValue(pb) as GhostPhysics;
                    if (phys != null)
                    {
                        phys.MayGoLimp = _ => true;
                        phys.Blast(phys.Inner.center, 0.25f, true);
                        Log($"shot {shot}: replay crew forced limp (0.25 kg blast at the hull middle) for the skin check");
                    }
                    else Log($"shot {shot}: no replay physics to force the crew limp");
                }
                catch (Exception e) { Log("force limp: " + e.Message); }
            }
            float worst = 0f, worst99 = 0f;
            string where = "", worstPairs = "";
            int samples = 0;
            float t0 = Time.realtimeSinceStartup;
            while (HitCamPlayer.Current == pb && Time.realtimeSinceStartup - t0 < 40f)
            {
                try
                {
                    foreach (var smr in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
                    {
                        if (smr == null || smr.gameObject.layer != Vis.Layer || !smr.name.EndsWith(" skin") || smr.sharedMesh == null) continue;
                        if (!_rest.TryGetValue(smr, out var r))
                        {
                            var m = smr.sharedMesh;
                            r = new SkinRest { Tris = m.triangles, Rest = m.vertices, W = m.boneWeights };
                            _rest[smr] = r;
                        }
                        var baked = new Mesh();
                        smr.BakeMesh(baked, true);
                        var bv = baked.vertices;
                        UnityEngine.Object.Destroy(baked);
                        if (bv.Length != r.Rest.Length) continue;
                        var ratios = new List<float>(r.Tris.Length);
                        float mx = 0f; int ma = 0, mb = 0;
                        var pairs = new Dictionary<string, int>();
                        var bn0 = smr.bones;
                        string P(int v) => bn0 != null && r.W[v].boneIndex0 < bn0.Length && bn0[r.W[v].boneIndex0] != null ? bn0[r.W[v].boneIndex0].name.Replace(smr.name.Replace(" skin", "") + " ", "") : "?";
                        for (int k = 0; k + 2 < r.Tris.Length; k += 3)
                            for (int e = 0; e < 3; e++)
                            {
                                int a = r.Tris[k + e], b = r.Tris[k + (e + 1) % 3];
                                float l0 = (r.Rest[a] - r.Rest[b]).magnitude;
                                if (l0 < 0.004f) continue;
                                float q = (bv[a] - bv[b]).magnitude / l0;
                                ratios.Add(q);
                                if (q > mx) { mx = q; ma = a; mb = b; }
                                if (q > 2.2f) { string pk = P(a) + "/" + P(b); pairs.TryGetValue(pk, out int pc); pairs[pk] = pc + 1; }
                            }
                        if (ratios.Count < 50) continue;
                        ratios.Sort();
                        float med = Mathf.Max(ratios[ratios.Count / 2], 1e-4f);
                        float p99 = ratios[(int)(ratios.Count * 0.99f)] / med;
                        mx /= med;
                        samples++;
                        if (mx > worst)
                        {
                            worst = mx;
                            worstPairs = string.Join(", ", pairs.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key + " " + x.Value));
                            var bn = smr.bones;
                            string B(int v) => bn != null && r.W[v].boneIndex0 < bn.Length && bn[r.W[v].boneIndex0] != null ? bn[r.W[v].boneIndex0].name : "?";
                            where = smr.name + ": " + B(ma) + " / " + B(mb);
                        }
                        worst99 = Mathf.Max(worst99, p99);
                    }
                }
                catch (Exception e) { Log("skin watch: " + e.Message); yield break; }
                yield return new WaitForSecondsRealtime(0.3f);
            }
            _rest.Clear();
            Log($"shot {shot} crew skin stretch: worst edge x{worst:0.00} ({where}), worst 99th percentile x{worst99:0.00}, {samples} samples; edges over x2.2 by part pair: {worstPairs}");
        }

        static IEnumerator CalloutShots(int shot)
        {
            yield return new WaitForSecondsRealtime(0.45f);
            // the self-test's own shots run in the planning phase with the map open: the HUD is hidden then, just log the verdict
            if (!ArcadeHud.Visible) { Log($"shot {shot} callout (not drawn): {State()}"); yield break; }
            yield return Shot($"hud_callout{shot}_a");
            yield return new WaitForSecondsRealtime(1.3f);
            yield return Shot($"hud_callout{shot}_b");
        }

        static FireControlSystem Fcs => ArcadeHud.Fcs;

        static float SpeedOf(Unit u)
        {
            try { return u != null ? u.Velocity.magnitude : 0f; } catch { return 0f; }
        }

        /// Switch the player's weapon the way the weapon-select key does (the gunner's active weapon system).
        static bool SelectWeapon(Func<WeaponSystemInfo, bool> pick, out WeaponSystemInfo was)
        {
            was = null;
            try
            {
                var wm = (GHPC.Player.PlayerInput.Instance?.CurrentPlayerCrewBrain as GHPC.Crew.CrewBrain)?.WeaponsModule;
                if (wm == null || wm.Weapons == null) return false;
                was = wm.ActiveWeapon;
                var w = wm.Weapons.FirstOrDefault(x => x != null && x.Weapon != null && pick(x));
                if (w == null) return false;
                wm.ActiveWeapon = w;
                return wm.ActiveWeapon == w;
            }
            catch (Exception e) { Log("weapon switch failed: " + e.Message); return false; }
        }

        /// Keep the sight on the lead circle of this aircraft (the FCS lays the gun like the gunner's controls) until the
        /// cross comes up on it, or the time runs out.
        static IEnumerator OnLead(Unit u, float timeout)
        {
            float t0 = Time.realtimeSinceStartup, onSince = -1f;
            while (Time.realtimeSinceStartup - t0 < timeout && u != null && !u.Destroyed && Fcs != null)
            {
                int i = Array.FindIndex(ArcadeHud.Leads, 0, ArcadeHud.LeadCount, l => l.C != null && l.C.Unit == u);
                Fcs.SetAimWorldPosition(i >= 0 ? ArcadeHud.Leads[i].C.Center + ArcadeHud.Leads[i].Offset : CenterOf(u));
                bool on = ArcadeHud.Cross != ArcadeHud.CrossKind.None && ArcadeHud.Arc.Hit && ArcadeHud.Arc.Probe.Unit == u;
                if (on && onSince < 0f) onSince = Time.realtimeSinceStartup;
                if (!on) onSince = -1f;
                if (onSince > 0f && Time.realtimeSinceStartup - onSince > 0.4f) break;
                yield return null;
            }
        }

        /// Proving ground: one helicopter flying a 300 m loop 60 m over the deck, 700 m down range (moving-target checks).
        static IEnumerator Flyer(List<Unit> got)
        {
            var me = ArcadeHud.Player;
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            if (me == null || sp == null) yield break;
            Faction enemy = me.Allegiance == Faction.Blue ? Faction.Red : Faction.Blue;
            Vector3 fwd = Vector3.ProjectOnPlane(me.transform.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            Vector3 center = me.transform.position + fwd * 700f;
            float deck = me.transform.position.y;
            var g = CodeUtils.GetPositionOnTerrain(new Vector3(center.x, 2000f, center.z), out bool ok, 4000f);   // as TestRange.Fly measures it
            float alt = deck + 60f - (ok ? g.y : deck);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 pos = center + right * 300f;
            pos.y = deck + 60f;
            var u = TestRange.Spawn(sp, "AH1", pos, Quaternion.LookRotation(fwd), enemy, false);
            if (u == null) { Log("could not spawn a flying helicopter"); yield break; }
            yield return null;
            yield return null;
            try { TestRange.Fly(u, center, 300f, alt, 45f); } catch (Exception e) { Log("helicopter loop: " + e.Message); }
            for (float t0 = Time.time; Time.time - t0 < 4f; ) { TestRange.KeepRotor(u); yield return null; }
            got.Add(u);
        }

        /// Turn the exterior (orbit) camera to look at a point, like the player's mouse (TraverseBy also holds off its auto-centring).
        static void FaceCamera(GHPC.Camera.CameraManager cm, Vector3 at)
        {
            try
            {
                var cf = cm != null ? cm.CameraFollow : null;
                if (cf == null || !cm.ExteriorMode) return;
                Vector3 have = cf.transform.forward, want = at - cf.transform.position;
                have.y = 0f;
                want.y = 0f;
                if (have.sqrMagnitude < 1e-4f || want.sqrMagnitude < 1e-4f) return;
                cf.TraverseBy(Vector3.SignedAngle(have, want, Vector3.up));
            }
            catch (Exception e) { Log("camera turn: " + e.Message); }
        }

        static Vector3 CenterOf(Unit u)
        {
            var c = Contacts.All.Concat(Contacts.Friends).FirstOrDefault(x => x.Unit == u);
            return c != null ? c.Center : u.transform.position + Vector3.up * 1.4f;
        }

        /// Point the player's gun at a world point (the FCS slews the turret like the gunner's controls do) and wait for it.
        static IEnumerator Aim(Func<Vector3> at, float timeout, bool lase = true)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < timeout)
            {
                var fcs = Fcs;
                var mz = ArcadeHud.Gun != null ? ArcadeHud.Gun.MuzzleIdentity : null;
                if (fcs == null || mz == null) yield break;
                Vector3 p = at();
                fcs.SetAimWorldPosition(p);
                float err = Vector3.Angle(fcs.AimWorldVector, p - (fcs.AimTransform != null ? fcs.AimTransform.position : mz.position));
                float lay = Vector3.Angle(mz.forward, p - mz.position);
                if (err < 0.3f && lay < 2.5f && Time.realtimeSinceStartup - t0 > 0.8f) break;
                yield return null;
            }
            if (lase && Fcs != null)
            {
                Fcs.Lase();
                for (int i = 0; i < 40; i++) { Fcs.SetAimWorldPosition(at()); yield return null; }
            }
            ArcadeHud.ProbeNow();
            yield return null;
            yield return null;
        }

        static void Log(string m) => HitCamMod.Log.Msg("AUTOTEST HUD: " + m);

        /// A round out of the player's own barrel, the way WeaponSystem.Fire launches one (muzzle, barrel direction, own velocity).
        static void FireFromGun(Arsenal.Entry e)
        {
            var me = ArcadeHud.Player;
            var mz = ArcadeHud.Gun != null ? ArcadeHud.Gun.MuzzleIdentity : null;
            if (me == null || mz == null || e == null) return;
            Arsenal.PrepareForFire(e);
            Vector3 vel = Vector3.zero;
            try { vel = me.Velocity; } catch { }
            Vector3 v = e.Ammo.MuzzleVelocity * mz.forward + vel;
            var round = LiveRoundUtility.GetNewLiveRound(e.Ammo);
            round.Info = e.Ammo;
            round.Shooter = me;
            round.Guided = false;
            round.UseGravity = true;
            round.CurrentSpeed = v.magnitude;
            round.MaxSpeed = v.magnitude;
            round.transform.position = mz.position;
            round.transform.rotation = Quaternion.LookRotation(v);
            round.Init(null, null);
        }

        /// Fire it, wait for whatever the HUD says about it (a hit's callout or a miss callout), photograph it.
        static IEnumerator FireAndWait(Arsenal.Entry e, string shot, Unit expect)
        {
            var before = ArcadeHud.Current;
            float u0 = Time.unscaledTime, r0 = Time.realtimeSinceStartup;
            var cross = ArcadeHud.Cross;
            FireFromGun(e);
            // until the HUD says something about it: the hit's callout, or a miss message
            while (Time.realtimeSinceStartup - r0 < 7f && (ArcadeHud.Current == null || ArcadeHud.Current.Last < u0)
                && (ArcadeHud.Toasts.Count == 0 || ArcadeHud.Toasts[0].Updated < u0)) yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            var c = ArcadeHud.Current;
            string msg = ArcadeHud.Toasts.Count > 0 ? ArcadeHud.Toasts[0].Title + " / " + ArcadeHud.Toasts[0].Sub : "-";
            var rec = Recorder.LastRecord;
            bool onIt = rec != null && rec.Target == expect && rec.CreatedReal >= Time.time - (Time.realtimeSinceStartup - r0) - 0.5f;
            Log($"fired {e.Ammo.Name} with the cross showing {cross}: callout '{c?.Title}', newest message '{msg}'" + (expect != null ? (onIt ? $", the game's round hit {Recorder.SafeName(expect)} ({rec.Title})" : $", no hit on {Recorder.SafeName(expect)}") : ""));
            yield return Shot(shot);
        }

        /// Two passive targets (a side-on T-72M1 and an M1IP facing us) on a spot the sight has a clear line to.
        static List<Unit> SpawnInView(Unit me, Vector3 eye, float[] distances = null, string keyA = "T72M1", string keyB = "M1IP")
        {
            var list = new List<Unit>();
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            var ammo = ArcadeHud.Ammo;
            if (sp == null || ammo == null) return list;
            Vector3 fwd = Vector3.ProjectOnPlane(me.transform.forward, Vector3.up).normalized;
            Faction enemy = me.Allegiance == Faction.Blue ? Faction.Red : Faction.Blue;
            foreach (float yaw in new[] { 0f, 20f, -20f, 40f, -40f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f })
                foreach (float dist in distances ?? new[] { 300f, 220f, 420f, 160f })
                {
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * fwd;
                    Vector3 p = CodeUtils.GetPositionOnTerrain(me.transform.position + dir * dist + Vector3.up * 200f, out bool ok, 4000f);
                    if (!ok) continue;
                    Vector3 side = Vector3.Cross(Vector3.up, dir).normalized;
                    bool clear = true;
                    foreach (float off in new[] { -9f, 9f })
                        foreach (float h in new[] { 0.8f, 1.7f })
                        {
                            Vector3 q = p + side * off + Vector3.up * h;
                            if (PenProbe.Probe(eye, q - eye, Vector3.Distance(eye, q) - 0.5f, ammo, me, me.Allegiance, out _)) clear = false;
                        }
                    if (!clear) continue;
                    Log($"extra targets at {dist:0} m, {yaw:0}° off the hull");
                    var a = TestRange.Spawn(sp, keyA, p - side * 9f, Quaternion.LookRotation(side), enemy, true);
                    var b = TestRange.Spawn(sp, keyB, p + side * 9f, Quaternion.LookRotation(-dir), enemy, true);
                    if (a != null) list.Add(a);
                    if (b != null) list.Add(b);
                    return list;
                }
            Log("no spot with a clear line for extra targets");
            return list;
        }

        static void Passive(Unit u)
        {
            try
            {
                var ai = u != null && u.InfoBroker != null ? u.InfoBroker.AI : null;
                if (ai != null) { ai.Stop(); ai.enabled = false; }
            }
            catch { }
        }

        public static IEnumerator Run()
        {
            if (!Settings.ArcadeHud.Value) { Log("arcade HUD is off, skipped"); yield break; }
            bool playerUp = Settings.MinimapPlayerUp.Value;
            try
            {
                if (ArcadeHud.Player == null || Fcs == null) { Log("no player gun / FCS, skipped"); yield break; }
                // the mission still sits in its planning phase with the map open: start it like the START MISSION button
                var msc = GHPC.State.MissionStateController.Instance;
                if (msc != null && GHPC.State.MissionStateController.CurrentState == GHPC.State.MissionState.Planning)
                {
                    msc.EndPlanningPhase();
                    Log("mission started (planning phase ended)");
                    yield return new WaitForSecondsRealtime(2.5f);
                }
                var cm = GHPC.Camera.CameraManager.Instance;
                Log($"player {Recorder.SafeName(ArcadeHud.Player)}, gun {ArcadeHud.Gun?.name}, ammo {ArcadeHud.Ammo?.Name}, FCS range {Fcs.CurrentRange:0}, exterior view {cm?.ExteriorMode}; "
                    + $"masks: live round {PenProbe.Mask:X}, aim vector {Fcs.AimWorldVector} vs aim transform fwd {(Fcs.AimTransform != null ? Fcs.AimTransform.forward : Vector3.zero)}");
                Log("contacts: " + string.Join(", ", Contacts.All.Select(c => $"{c.Label}{(c.SeenNow ? " (spotted)" : "")}")));

                // targets: on the proving ground (the default) its own rows every 100 m and hovering helicopters, plus one
                // helicopter flying a loop over the deck for the moving-target checks; on the mission's ground a test-range wave
                bool pg = ProvingGround.Active;
                List<Unit> ground, helis;
                if (pg)
                {
                    var pgUnits = GHPC.World.SceneUnitsManager.Instance.AllUnitsInScene.Where(u => u != null && !u.Destroyed && ProvingGround.IsTarget(u)).ToList();
                    ground = pgUnits.Where(u => u.Type == UnitType.GroundVehicle).ToList();
                    helis = pgUnits.Where(u => u.Type == UnitType.AirVehicle).ToList();
                    var flyer = new List<Unit>();
                    yield return Flyer(flyer);
                    helis.InsertRange(0, flyer);
                    yield return new WaitForSecondsRealtime(1.5f);   // contacts pick the new one up
                    Log($"proving ground: {ground.Count} targets, helicopters {string.Join(", ", helis.Select(h => $"{Recorder.SafeName(h)} {SpeedOf(h):0} m/s"))}");
                }
                else
                {
                    yield return TestRange.SpawnWave();
                    yield return new WaitForSecondsRealtime(2f);
                    ground = TestRange.Spawned.Where(u => u != null && u.Type == UnitType.GroundVehicle).ToList();
                    helis = TestRange.Spawned.Where(u => u != null && u.Type == UnitType.AirVehicle && !u.Destroyed).ToList();
                }
                // targets the sight has a clear line to (a hay bale or a crest hides some), side-on and facing us
                var me = ArcadeHud.Player;
                Vector3 eye = Fcs.AimTransform != null ? Fcs.AimTransform.position : me.transform.position + Vector3.up * 2.4f;
                bool Clear(Unit u)
                {
                    if (u == null || u.Destroyed) return false;
                    Vector3 c = CenterOf(u);
                    bool hit = PenProbe.Probe(eye, c - eye, 3000f, ArcadeHud.Ammo, me, me.Allegiance, out var pr);
                    bool ok = hit && (pr.Unit == u || pr.Distance > Vector3.Distance(eye, c) - 4f);
                    Log($"  line to {Recorder.SafeName(u)} at {Vector3.Distance(eye, c):0} m: {(hit ? $"{pr.Verdict} on {(pr.Unit != null ? Recorder.SafeName(pr.Unit) : "-")} '{pr.Plate ?? pr.Blocker}' at {pr.Distance:0} m" : "nothing")} -> {(ok ? "clear" : "blocked")}");
                    return ok;
                }
                float Facing(Unit u) => Mathf.Abs(Vector3.Dot(u.transform.forward, (CenterOf(u) - eye).normalized));
                bool Tank(Unit u) { var mc = MapIcons.Classify(u); return mc == MapClass.LightTank || mc == MapClass.MediumTank || mc == MapClass.HeavyTank; }
                float Dist(Unit u) => Vector3.Distance(eye, CenterOf(u));
                // the proving ground: the nearest tanks first (its rows are 100 m apart; far ones are for the zeroing test)
                var clear = (pg ? ground.Where(Tank).OrderBy(Dist).Take(6) : ground).Where(Clear).ToList();
                Log("clear line to: " + string.Join(", ", clear.Select(u => Recorder.SafeName(u))) + " (of " + ground.Count + ")");
                // on this map the wave can land behind a crest or a hay bale: add a side-on T-72 and an M1 facing us where the sight sees them
                var placed = pg ? new List<Unit>() : SpawnInView(me, eye);
                if (placed.Count > 0)
                {
                    yield return null; yield return null;
                    foreach (var u in placed) Passive(u);
                    yield return new WaitForSecondsRealtime(1.5f);   // contacts pick them up
                    ground.AddRange(placed);
                    clear.InsertRange(0, placed.Where(Clear));
                    Log("clear line to (with the extra pair): " + string.Join(", ", clear.Select(u => Recorder.SafeName(u))));
                }
                var side = clear.OrderBy(Facing).FirstOrDefault() ?? ground.FirstOrDefault(u => Recorder.SafeName(u).Contains("T-72")) ?? ground.FirstOrDefault();
                var front = clear.Where(u => u != side).OrderByDescending(Facing).FirstOrDefault() ?? ground.FirstOrDefault(u => Recorder.SafeName(u).Contains("M1")) ?? ground.LastOrDefault();
                Log($"side-on target {Recorder.SafeName(side)}, facing target {Recorder.SafeName(front)}");
                if (side == null) { Log("no range targets, skipped"); yield break; }

                // 1. exterior view, gun on the side-on target: markers over the wave, crosshair, minimap
                if (cm != null && !cm.ExteriorMode) cm.ToggleCameraSlotSet();
                yield return Aim(() => CenterOf(side), 9f);
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 9f && Contacts.All.Count(c => c.SeenNow) < 3) yield return null;   // the crew's spot delay
                // a hit cam from the shot list may still be up; the exterior camera keeps whatever way it last looked (on the
                // proving ground: away from the rows): turn it to the target the way the mouse does
                for (float w0 = Time.realtimeSinceStartup; HitCamPlayer.Current != null && Time.realtimeSinceStartup - w0 < 15f; ) yield return null;
                for (int i = 0; i < 4; i++) { FaceCamera(cm, CenterOf(side)); yield return null; }
                yield return Shot("hud_1_exterior");

                // 2. gunner's sight: what the loaded round does to its side
                if (cm != null && cm.ExteriorMode) cm.ToggleCameraSlotSet();
                yield return new WaitForSecondsRealtime(0.6f);
                yield return Aim(() => CenterOf(side), 3f);
                yield return Shot("hud_2_sight_loaded");

                // 3. pen colours: judge the same spot with rounds picked for green / yellow / red
                bool crossOn = ArcadeHud.Cross == ArcadeHud.CrossKind.Armour && ArcadeHud.Arc.Hit;
                float los = crossOn ? ArcadeHud.Arc.Probe.Los : ArcadeHud.Probe.Los, range = crossOn ? ArcadeHud.Arc.Path : ArcadeHud.AimRange;
                if (los > 1f)
                {
                    var cands = new List<AmmoType>();
                    foreach (int c in new[] { 20, 25, 30, 35, 40, 57, 76, 90, 100, 105, 115, 120, 125, 130, 152, 203 })
                        foreach (string k in new[] { "APFSDS", "AP", "HEAT" })
                        {
                            var e = Arsenal.Find($"{k} {c} mm");
                            if (e != null && e.Ammo != null && e.Ammo.Name == $"{k} {c} mm" && !cands.Contains(e.Ammo)) cands.Add(e.Ammo);
                        }
                    // each round meets the plate on its own arc (a slower one drops lower, hits another plate at another angle):
                    // try them from about the right pen and keep the first that really comes out green / yellow / red / blue
                    var want = new Dictionary<PenVerdict, string> { { PenVerdict.Pen, "green" }, { PenVerdict.Maybe, "yellow" }, { PenVerdict.NoPen, "red" }, { PenVerdict.Ricochet, "blue" } };
                    var got = new HashSet<PenVerdict>();
                    foreach (var pick in cands.OrderBy(a => Mathf.Abs(Mathf.Log(Mathf.Max(PenProbe.PenAtRange(a, range), 1f) / los))))
                    {
                        ArcadeHud.TestAmmo = pick;
                        for (int i = 0; i < 4; i++) { ArcadeHud.ProbeNow(); yield return null; }   // the cross switches after two agreeing updates
                        var v = ArcadeHud.CrossVerdict;
                        if (ArcadeHud.Cross != ArcadeHud.CrossKind.Armour || !want.ContainsKey(v) || got.Contains(v)) continue;
                        got.Add(v);
                        yield return Shot("hud_3_pen_" + want[v]);
                        if (got.Count == want.Count) break;
                    }
                    Log("colours seen: " + string.Join(", ", got.Select(v => want[v])));
                    ArcadeHud.TestAmmo = null;
                }
                else Log("no armour under the crosshair for the colour test");

                // 3b. the facing target's front with the loaded round
                if (front != null && front != side)
                {
                    yield return Aim(() => CenterOf(front), 6f);
                    yield return Shot("hud_3_pen_front");
                }

                // 4. zeroing on a far target (zeroed short, the target further out): the proving ground's farthest
                // tank with a clear line, else a pair placed 650-1600 m out
                // nothing nearer within half a hull + 1 m of the line of fire (from the muzzle)
                bool LineFree(Unit u)
                {
                    var mz0 = ArcadeHud.Gun != null ? ArcadeHud.Gun.MuzzleIdentity : null;
                    Vector3 a = mz0 != null ? mz0.position : eye, b = CenterOf(u);
                    a.y = b.y = 0f;
                    Vector3 ab = b - a;
                    foreach (var o in ground)
                    {
                        if (o == u || o == null || o.Destroyed) continue;
                        Vector3 q = CenterOf(o);
                        q.y = 0f;
                        float t = Vector3.Dot(q - a, ab) / ab.sqrMagnitude;
                        if (t > 0.02f && t < 0.98f && (a + ab * t - q).magnitude < 3.2f) { Log($"  line of fire to {Recorder.SafeName(u)} passes {(a + ab * t - q).magnitude:0.0} m from {Recorder.SafeName(o)}"); return false; }
                    }
                    return true;
                }
                var far = pg ? ground.Where(u => Tank(u) && Dist(u) > 700f && LineFree(u)).OrderByDescending(Dist).Where(Clear).Take(1).ToList()
                    : SpawnInView(me, eye, new[] { 1600f, 1300f, 1050f, 850f, 650f }, "T72M1", "T80B");
                if (far.Count > 0)
                {
                    yield return null; yield return null;
                    if (!pg) foreach (var u in far) Passive(u);
                    yield return new WaitForSecondsRealtime(1.5f);
                    var tgt = far[0];
                    yield return Aim(() => CenterOf(tgt), 9f);
                    float real = Fcs.CurrentRange > 1f ? Fcs.CurrentRange : ArcadeHud.AimRange;
                    Log($"far target {Recorder.SafeName(tgt)} at {Vector3.Distance(eye, CenterOf(tgt)):0} m, lased {real:0} m");
                    yield return Shot("hud_8_far_lased");
                    // a. a much slower round on a gun laid for APFSDS (like a T-55 zeroed short): it lands in front of it
                    var slowE = Arsenal.Find("HE 125 mm") ?? Arsenal.Find("HEAT 125 mm");
                    var slow = slowE?.Ammo;
                    ArcadeHud.TestAmmo = slow;
                    Log($"slow round for the zeroing test: {slow?.Name} ({slow?.MuzzleVelocity:0} m/s)");
                    for (int i = 0; i < 6; i++) { Fcs.SetAimWorldPosition(CenterOf(tgt)); ArcadeHud.ProbeNow(); yield return null; }
                    yield return Shot("hud_9_far_zeroed_short");
                    // fire it for real: it lands short of the T-72 -> the miss is named once, after the shot
                    if (slowE != null) yield return FireAndWait(slowE, "hud_9b_fired_short", tgt);
                    // b. same gun, aim higher until the arc drops onto it: double the height while it falls short, then halve the gap
                    float hit = -1f, lo = 0f, hi = -1f, h = 0.5f;
                    for (int step = 0; step < 22 && hit < 0f; step++)
                    {
                        float hh = h;
                        float l0 = Time.realtimeSinceStartup;
                        // let the turret settle on the new point (the FCS slews the gun like the gunner's controls)
                        while (Time.realtimeSinceStartup - l0 < 2f)
                        {
                            Vector3 aimAt = CenterOf(tgt) + Vector3.up * hh;
                            Fcs.SetAimWorldPosition(aimAt);
                            yield return null;
                            if (Time.realtimeSinceStartup - l0 > 0.35f && Vector3.Angle(Fcs.AimWorldVector, aimAt - eye) < 0.02f) break;
                        }
                        for (int i = 0; i < 4; i++) { ArcadeHud.ProbeNow(); yield return null; }
                        var ck = ArcadeHud.Cross;
                        bool onIt = ck == ArcadeHud.CrossKind.Armour && ArcadeHud.Arc.Probe.Unit == tgt;
                        bool shortOf = !onIt && ArcadeHud.Arc.Hit && Vector3.Distance(eye, ArcadeHud.Arc.Point) < Vector3.Distance(eye, CenterOf(tgt));
                        Log($"  aim {hh:0.00} m above: {ck}{(onIt ? " on it" : shortOf ? " (short)" : " (over)")}, arc {ArcadeHud.Arc.Path:0} m");
                        if (onIt) { hit = hh; break; }
                        if (shortOf) lo = hh; else hi = hh;
                        h = hi < 0f ? hh * 2f : (lo + hi) * 0.5f;
                    }
                    if (hit > 0f)
                    {
                        // the window's edges: bisect towards the short side and the over side, then take its middle
                        float loIn = hit, loOut = lo, hiIn = hit, hiOut = hi > 0f ? hi : hit * 2f;
                        for (int side2 = 0; side2 < 2; side2++)
                            for (int step = 0; step < 3; step++)
                            {
                                float hh = side2 == 0 ? (loIn + loOut) * 0.5f : (hiIn + hiOut) * 0.5f;
                                float l0 = Time.realtimeSinceStartup;
                                while (Time.realtimeSinceStartup - l0 < 2f)
                                {
                                    Vector3 aimAt = CenterOf(tgt) + Vector3.up * hh;
                                    Fcs.SetAimWorldPosition(aimAt);
                                    yield return null;
                                    if (Time.realtimeSinceStartup - l0 > 0.35f && Vector3.Angle(Fcs.AimWorldVector, aimAt - eye) < 0.02f) break;
                                }
                                for (int i = 0; i < 4; i++) { ArcadeHud.ProbeNow(); yield return null; }
                                bool onIt = ArcadeHud.Cross == ArcadeHud.CrossKind.Armour && ArcadeHud.Arc.Probe.Unit == tgt;
                                if (side2 == 0) { if (onIt) loIn = hh; else loOut = hh; }
                                else { if (onIt) hiIn = hh; else hiOut = hh; }
                            }
                        hit = (loIn + hiIn) * 0.5f;
                        Log($"the arc lands on it from {loIn:0.00} to {hiIn:0.00} m above: aiming at the middle, {hit:0.00} m");
                        float m0 = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - m0 < 2f)
                        {
                            Vector3 aimAt = CenterOf(tgt) + Vector3.up * hit;
                            Fcs.SetAimWorldPosition(aimAt);
                            yield return null;
                            if (Time.realtimeSinceStartup - m0 > 0.35f && Vector3.Angle(Fcs.AimWorldVector, aimAt - eye) < 0.02f) break;
                        }
                        for (int i = 0; i < 4; i++) { ArcadeHud.ProbeNow(); yield return null; }
                    }
                    Log(hit > 0f ? $"aiming {hit:0.00} m above it drops the round onto it" : "never got the cross onto it");
                    yield return Shot("hud_10_far_aimed_high");
                    if (slowE != null)
                    {
                        // the game's own round from the same aim: does it land where the cross said?
                        if (hit > 0f) yield return FireAndWait(slowE, "hud_10b_fired_on_it", tgt);
                        float over = (hit > 0f ? hit : 10f) * 2.5f;
                        for (int i = 0; i < 60; i++) { Fcs.SetAimWorldPosition(CenterOf(tgt) + Vector3.up * over); yield return null; }
                        for (int i = 0; i < 4; i++) { ArcadeHud.ProbeNow(); yield return null; }
                        yield return FireAndWait(slowE, "hud_10c_fired_over", tgt);
                    }
                    ArcadeHud.TestAmmo = null;
                    // c. zeroed much too long: over the top
                    Fcs.TargetRange = Mathf.Min(real * 2.2f, 3900f);
                    for (int i = 0; i < 90; i++) { Fcs.SetAimWorldPosition(CenterOf(tgt)); yield return null; }
                    for (int i = 0; i < 4; i++) { ArcadeHud.ProbeNow(); yield return null; }
                    yield return Shot("hud_11_far_zeroed_long");
                    // and fire the loaded round over it for real: "Overshoot"
                    if (ArcadeHud.Ammo != null) yield return FireAndWait(new Arsenal.Entry { Ammo = ArcadeHud.Ammo, Label = ArcadeHud.Ammo.Name }, "hud_11b_fired_over", tgt);
                    Fcs.TargetRange = real;
                    for (int i = 0; i < 40; i++) { Fcs.SetAimWorldPosition(CenterOf(tgt)); yield return null; }
                }
                else Log("no far spot with a clear line: zeroing test skipped");

                // 5. a helicopter: brackets with name + km, the lead circle for the main gun, then for the coax (another round,
                // another lead), the sight laid on the lead until the cross comes up on it, and the main gun fired from there
                // a moving one first (the proving ground's own two hover)
                var heli = helis.Where(h => h != null && !h.Destroyed).OrderBy(h => SpeedOf(h) > 5f ? 0 : 1).ThenBy(h => Vector3.Distance(h.transform.position, ArcadeHud.Player.transform.position)).FirstOrDefault();
                if (heli != null)
                {
                    string hn = Recorder.SafeName(heli);
                    string LeadOf(Unit u)
                    {
                        int i = Array.FindIndex(ArcadeHud.Leads, 0, ArcadeHud.LeadCount, l => l.C != null && l.C.Unit == u);
                        if (i < 0) return "none";
                        var l = ArcadeHud.Leads[i];
                        return $"{l.Offset.magnitude:0.00} m from its middle ({l.Offset.x:0.0}, {l.Offset.y:0.0}, {l.Offset.z:0.0}), flight {l.Tof:0.000} s, drop {l.Drop:0.00} m, its speed {SpeedOf(u):0.0} m/s at {Vector3.Distance(ArcadeHud.Gun.MuzzleIdentity.position, l.C.Center):0} m";
                    }
                    float h0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - h0 < 6f && heli != null && !heli.Destroyed)
                    {
                        Fcs.SetAimWorldPosition(CenterOf(heli));
                        if (Time.realtimeSinceStartup - h0 > 1.5f && ArcadeHud.LeadCount > 0) break;
                        yield return null;
                    }
                    Log($"lead on {hn} with {ArcadeHud.Gun?.name} / {ArcadeHud.Ammo?.Name} ({ArcadeHud.Ammo?.MuzzleVelocity:0} m/s): {LeadOf(heli)}");
                    yield return Shot("hud_5_lead");
                    // the coax: its own round, its own lead (on a helicopter inside its reach; they fly loops, so wait for one)
                    if (SelectWeapon(w => w.Role == WeaponSystemRole.Coaxial, out var mainW))
                    {
                        for (int i = 0; i < 3; i++) { ArcadeHud.ProbeNow(); yield return null; }   // the HUD picks up the new weapon
                        var mg = ArcadeHud.Ammo;
                        Log($"weapon switched to {ArcadeHud.Gun?.name} / {mg?.Name} ({mg?.MuzzleVelocity:0} m/s, max range {mg?.MaximumRange:0} m)");
                        // a flying helicopter inside the coax's reach (it loops in and out of it), else after 20 s any one
                        Unit near = null;
                        for (float w0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - w0 < 20f; )
                        {
                            ArcadeHud.ProbeNow();
                            yield return null;
                            near = helis.Where(h => h != null && !h.Destroyed && Array.FindIndex(ArcadeHud.Leads, 0, ArcadeHud.LeadCount, l => l.C != null && l.C.Unit == h) >= 0)
                                .OrderBy(h => SpeedOf(h) > 5f ? 0 : 1).ThenBy(h => Vector3.Distance(h.transform.position, ArcadeHud.Player.transform.position)).FirstOrDefault();
                            if (near != null && SpeedOf(near) > 5f) break;
                        }
                        if (near != null)
                        {
                            string nn = Recorder.SafeName(near);
                            yield return OnLead(near, 5f);
                            string coaxLead = LeadOf(near);
                            bool coaxOn = ArcadeHud.Cross != ArcadeHud.CrossKind.None && ArcadeHud.Arc.Hit && ArcadeHud.Arc.Probe.Unit == near;
                            Log($"coax laid on the lead of {nn}: cross {ArcadeHud.Cross} {(coaxOn ? "on it" : "not on it")}{(ArcadeHud.Arc.Moving ? " (moving frame)" : "")}; coax lead {coaxLead}");
                            yield return Shot("hud_5b_lead_coax");
                            if (coaxOn && mg != null)
                            {
                                // the hit cam skips MG calibres (MinCaliberMm): record this one so the log can tell a hit
                                float minCal = Settings.MinCaliberMm.Value;
                                Settings.MinCaliberMm.Value = 5f;
                                yield return FireAndWait(new Arsenal.Entry { Ammo = mg, Label = mg.Name }, "hud_5b2_coax_fired", near);
                                Settings.MinCaliberMm.Value = minCal;
                            }
                            SelectWeapon(w => w == mainW, out _);
                            for (int i = 0; i < 6; i++) { ArcadeHud.ProbeNow(); yield return null; }
                            Log($"weapon back to {ArcadeHud.Gun?.name} / {ArcadeHud.Ammo?.Name}: main gun lead on {nn} {LeadOf(near)} (coax: {coaxLead})");
                        }
                        else
                        {
                            Log("no helicopter came inside the coax's reach");
                            SelectWeapon(w => w == mainW, out _);
                            for (int i = 0; i < 6; i++) { ArcadeHud.ProbeNow(); yield return null; }
                        }
                    }
                    else Log("no coax to switch to");
                    // main gun on the lead: the cross on the helicopter, then the game's own round from there
                    if (heli != null && !heli.Destroyed)
                    {
                        yield return OnLead(heli, 8f);
                        bool crossOnHeli = ArcadeHud.Cross != ArcadeHud.CrossKind.None && ArcadeHud.Arc.Hit && ArcadeHud.Arc.Probe.Unit == heli;
                        Log($"main gun laid on the lead: cross {ArcadeHud.Cross} {(crossOnHeli ? "on " + hn : "not on it")}{(ArcadeHud.Arc.Moving ? " (moving frame)" : "")}; lead {LeadOf(heli)}");
                        yield return Shot("hud_5c_on_lead");
                        if (crossOnHeli && ArcadeHud.Ammo != null)
                            yield return FireAndWait(new Arsenal.Entry { Ammo = ArcadeHud.Ammo, Label = ArcadeHud.Ammo.Name }, "hud_5d_fired_on_lead", heli);
                    }
                }
                else Log("no helicopter: lead test skipped");

                // 5b. callouts: two shots of the loaded round (side of one, front of the other), fired like the other self-test shots
                int n = 0;
                foreach (var (tgt, aspect) in new[] { (side, "side"), (front, "front") })
                {
                    if (tgt == null || tgt.Destroyed || ArcadeHud.Ammo == null) continue;
                    yield return Aim(() => CenterOf(tgt), 6f);
                    var pbBefore = HitCamPlayer.Current;
                    float firedAt = Time.time;
                    AutoTest.Fire(ArcadeHud.Player, tgt, ArcadeHud.Ammo, aspect);
                    MelonCoroutines.Start(SkinWatch(100 + n, pbBefore, true, firedAt));
                    float f0 = Time.realtimeSinceStartup, u0 = Time.unscaledTime;
                    while (Time.realtimeSinceStartup - f0 < 4f && (ArcadeHud.Current == null || ArcadeHud.Current.Last < u0)) yield return null;
                    yield return new WaitForSecondsRealtime(0.4f);
                    yield return Shot($"hud_callout_live{n}_a");
                    yield return new WaitForSecondsRealtime(1.2f);
                    yield return Shot($"hud_callout_live{n}_b");
                    n++;
                    yield return new WaitForSecondsRealtime(2.5f);
                }

                // 5b2. a 152 mm HE round into the side-on target's crew compartment: crew killed -> ragdolls go limp (skin stretch check)
                var he = Arsenal.Find("HE 152 mm");
                var heTgt = new[] { side, front }.FirstOrDefault(u => u != null && !u.Destroyed);
                if (he != null && heTgt != null)
                {
                    Arsenal.PrepareForFire(he);
                    var pbBefore = HitCamPlayer.Current;
                    float firedAt = Time.time;
                    AutoTest.Fire(ArcadeHud.Player, heTgt, he.Ammo, "side");
                    var watch = SkinWatch(200, pbBefore, true, firedAt);
                    while (watch.MoveNext()) yield return watch.Current;   // wait for the whole replay
                }

                // 5c. the sounds play: our source's own samples (an audio filter on it), and the game's master volume they go through
                foreach (var (snd, play) in new (string, Action)[] { ("hit", HudSfx.Hit), ("pen", HudSfx.Pen), ("kill", HudSfx.Kill) })
                {
                    yield return new WaitForSecondsRealtime(0.3f);
                    HudSfx.ResetMeter();
                    float quiet = 0f;
                    for (int i = 0; i < 4; i++) { yield return null; quiet = Mathf.Max(quiet, HudSfx.MeterPeak); }
                    HudSfx.ResetMeter();
                    play();
                    float mix = 0f;
                    for (int i = 0; i < 12; i++) { yield return null; mix = Mathf.Max(mix, HudSfx.OutputPeak()); }
                    Log($"sound '{snd}': our source peak {quiet:0.000} before -> {HudSfx.MeterPeak:0.000} playing (ArcadeSfxVolume {Settings.ArcadeSfxVolume.Value:0.00}); game master volume {AudioListener.volume:0.00}, whole Unity mix peak {mix:0.000}");
                    yield return new WaitForSecondsRealtime(0.8f);
                }

                // 6. turret turned away: off-screen markers on the edge, player-up minimap
                Vector3 away = Quaternion.Euler(0f, 110f, 0f) * (side.transform.position - ArcadeHud.Player.transform.position);
                Vector3 awayPt = ArcadeHud.Player.transform.position + away.normalized * 400f + Vector3.up * 2f;
                yield return Aim(() => awayPt, 9f, lase: false);
                Settings.MinimapPlayerUp.Value = true;
                yield return new WaitForSecondsRealtime(0.3f);
                yield return Shot("hud_6_offscreen_playerup");
                Settings.MinimapPlayerUp.Value = playerUp;

                // 7. back outside, wide
                if (cm != null && !cm.ExteriorMode) cm.ToggleCameraSlotSet();
                yield return Aim(() => CenterOf(front ?? side), 8f);
                for (int i = 0; i < 4; i++) { FaceCamera(cm, CenterOf(front ?? side)); yield return null; }
                yield return Shot("hud_7_exterior_final");

                // 7b. minimap class icons: what each unit is filed as, then every enemy (MinimapEnemies=Always) zoomed out, and Off
                string Mass(Unit u0) { try { var rb = u0.GetComponent<Rigidbody>() ?? u0.GetComponentInChildren<Rigidbody>(); return rb != null ? $" {rb.mass / 1000f:0.#} t" : ""; } catch { return ""; } }
                Log("map classes: " + string.Join(", ", Contacts.All.Concat(Contacts.Friends).Where(c => c.Unit != null).Select(c => $"{c.Name} ({c.Unit.ShortNameUs}{Mass(c.Unit)}) = {MapIcons.Name(c.Class)}"))
                    + $"; infantry units {Contacts.Troops.Count} ({Contacts.Troops.Count(c => c.Class == MapClass.AntiTankTeam)} anti-tank), shown {Contacts.Troops.Count(c => c.Shown)}");
                try { File.WriteAllBytes(Path.Combine(Dir, "hud_icons_atlas.png"), MapIcons.Atlas.EncodeToPNG()); } catch (Exception e) { Log("icon atlas: " + e.Message); }
                string enemies0 = Settings.MinimapEnemies.Value;
                int zoom0 = Minimap.Zoom;
                Settings.MinimapEnemies.Value = "Spotted";
                yield return new WaitForSecondsRealtime(0.3f);
                yield return Shot("hud_17_minimap_spotted");
                Settings.MinimapEnemies.Value = "Always";
                Minimap.Zoom = zoom0 + 1;
                yield return new WaitForSecondsRealtime(0.3f);
                yield return Shot("hud_18_minimap_always");
                Settings.MinimapEnemies.Value = "Off";
                yield return new WaitForSecondsRealtime(0.3f);
                yield return Shot("hud_19_minimap_off");
                Settings.MinimapEnemies.Value = enemies0;
                Minimap.Zoom = zoom0;

                // 8. the settings window (opens paused, HUD previews behind it), a bigger callout / kill feed scale, the keys tab, the gear
                float sc = Settings.HudScaleCallouts.Value, sk = Settings.HudScaleKillFeed.Value;
                ConfigMenu.Open();
                ConfigMenu.Tab = ConfigMenu.TabIndex("HUD");
                Settings.HudScaleCallouts.Value = 1.5f;
                Settings.HudScaleKillFeed.Value = 1.3f;
                yield return new WaitForSecondsRealtime(0.3f);
                yield return Shot("hud_12_config_hud");
                Settings.HudScaleCallouts.Value = sc;
                Settings.HudScaleKillFeed.Value = sk;
                ConfigMenu.Tab = ConfigMenu.TabIndex("Keys");
                yield return new WaitForSecondsRealtime(0.2f);
                yield return Shot("hud_13_config_keys");
                ConfigMenu.Close();
                bool cv = Cursor.visible;
                Cursor.visible = true;
                yield return new WaitForSecondsRealtime(0.2f);
                yield return Shot("hud_14_gear");
                Cursor.visible = cv;

                // 9. the game's own menu -> Options: the HitCam tab is there, and it opens the settings window
                var mc = UnityEngine.Object.FindObjectOfType<GHPC.UI.MenuController>();
                if (mc != null)
                {
                    mc.ToggleRootMenu();
                    yield return new WaitForSecondsRealtime(0.6f);
                    var mmp = UnityEngine.Object.FindObjectOfType<GHPC.UI.Menu.MainMenuPanel>();
                    var opt = mmp != null ? HarmonyLib.AccessTools.Field(typeof(GHPC.UI.Menu.MainMenuPanel), "buttonOptions")?.GetValue(mmp) as UnityEngine.UI.Button : null;
                    Log($"game menu open: {mc.HasActiveMenu}, options button {(opt != null ? opt.name : "not found")}");
                    if (opt != null) opt.onClick.Invoke();
                    yield return new WaitForSecondsRealtime(1.5f);   // the tab is added within a second
                    var tab = UnityEngine.Object.FindObjectOfType<OptionsTab>();
                    Log($"options open, HitCam tabs added so far {OptionsHook.Added}, visible tab {(tab != null ? tab.name : "none")}");
                    yield return Shot("hud_15_game_options");
                    if (tab != null)
                    {
                        tab.OnPointerEnter(null);
                        tab.OnPointerClick(null);
                        yield return new WaitForSecondsRealtime(0.4f);
                        Log($"HitCam tab clicked: settings window open {ConfigMenu.IsOpen}, game UI input {(UnityEngine.EventSystems.EventSystem.current != null ? UnityEngine.EventSystems.EventSystem.current.enabled.ToString() : "none")}");
                        ConfigMenu.Tab = ConfigMenu.TabIndex("Quick setup");
                        yield return Shot("hud_16_options_hitcam_window");
                        ConfigMenu.Close();
                        tab.OnPointerExit(null);
                    }
                    mc.CloseAllMenus();
                    yield return new WaitForSecondsRealtime(0.4f);
                    Log($"menus closed: {!mc.HasActiveMenu}, paused {GHPC.State.TimeController.Paused}");
                }
                else Log("no game menu controller: options test skipped");
            }
            finally
            {
                ArcadeHud.TestAmmo = null;
                Settings.MinimapPlayerUp.Value = playerUp;
                if (ConfigMenu.IsOpen) ConfigMenu.Close();
            }
        }
    }
}
