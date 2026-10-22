using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC;
using GHPC.Crew;
using GHPC.Effects;
using GHPC.Humans;
using GHPC.Player;
using GHPC.Utility;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace GHPCHitCam
{
    /// War Thunder style kill feed. WT damage tiers (official wiki, "destroy rules"): destroyed = explodes or not enough
    /// crew left; "active fire" goes to whoever set it alight; "critical damage" = crew member, gun, engine or
    /// transmission knocked out (red); "serious damage" = engine, transmission, barrel or breech damaged (grey); anything
    /// else that hurts is a "hit". vehicle dying later (fire, cook-off, bail-out) goes to the earlier hit, here for
    /// KillCreditSeconds
    /// messages stack on the right, kills also get a short banner top centre, HudSfx plays the hit / pen / kill sounds
    /// own Harmony postfixes on the game's notifications (Unit.NotifyStruck / NotifyDestroyed / NotifyIncapacitated /
    /// NotifyAbandoned, FlammableItem.Ignite, FlammablesManager.NotifyDidBurst, Compartment.NotifyExplosion), UI only
    internal static class KillFeed
    {
        class Credit
        {
            public Unit Unit;
            public string Name = "?";
            public float LastHit = -999f;      // Time.time of your last round on it
            public float LastOther = -999f;    // someone else's
            public float FireAt = -1f, BoomAt = -1f;
            public bool Burning, Dead, Out, BoomShown;
        }

        internal class Msg
        {
            public string Text;
            public Color Color;
            public float Born;
        }

        static readonly Dictionary<Unit, Credit> _credit = new Dictionary<Unit, Credit>();
        public static readonly List<Msg> Feed = new List<Msg>();
        static float _nextClean;
        const float FeedSeconds = 9f;

        static readonly Color Red = new Color(1f, 0.28f, 0.22f), Orange = new Color(1f, 0.6f, 0.18f), Grey = new Color(0.8f, 0.82f, 0.85f),
            White = new Color(0.95f, 0.96f, 1f), Assist = new Color(0.75f, 0.85f, 1f);

        public static void Reset()
        {
            _credit.Clear();
            _aimed.Clear();
            _strikes.Clear();
            Feed.Clear();
        }

        // ------------------------------------------------------------------ hooks

        public static void Install(HarmonyLib.Harmony h)
        {
            Patch(h, AccessTools.Method(typeof(Unit), "NotifyStruck"), nameof(StruckPostfix));
            Patch(h, AccessTools.Method(typeof(Unit), "NotifyDestroyed"), nameof(DestroyedPostfix));
            Patch(h, AccessTools.Method(typeof(Unit), "NotifyIncapacitated"), nameof(IncapacitatedPostfix));
            Patch(h, AccessTools.Method(typeof(Unit), "NotifyAbandoned"), nameof(AbandonedPostfix));
            Patch(h, AccessTools.Method(typeof(FlammableItem), "Ignite"), nameof(IgnitePostfix), nameof(IgnitePrefix));
            Patch(h, AccessTools.Method(typeof(FlammablesManager), "NotifyDidBurst"), nameof(BurstPostfix));
            Patch(h, AccessTools.Method(typeof(Compartment), "NotifyExplosion"), nameof(CompartmentPostfix));
            Patch(h, AccessTools.Method(typeof(LiveRound), "Init"), nameof(FiredPostfix));
            Patch(h, AccessTools.Method(typeof(GHPC.Weapons.ShotInfo), "NotifyShotTerminated"), nameof(ShotEndedPostfix));
        }

        static void Patch(HarmonyLib.Harmony h, MethodBase target, string postfix, string prefix = null)
        {
            if (target == null) { HitCamMod.Log.Warning("Kill feed: patch target missing for " + postfix + " (game update?)"); return; }
            try
            {
                h.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(KillFeed).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)),
                    new HarmonyMethod(typeof(KillFeed).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { HitCamMod.Log.Error("Kill feed: failed to patch " + target.Name + ": " + e.Message); }
        }

        static bool Active => Settings.ArcadeHud.Value && (Settings.ArcadeKillFeed.Value || Settings.ArcadeSfx.Value);

        static Unit Player => SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;

        static Credit Get(Unit u)
        {
            if (!_credit.TryGetValue(u, out var c))
            {
                c = new Credit { Unit = u };
                try { c.Name = string.IsNullOrEmpty(u.FriendlyName) ? Recorder.Pretty(u.name) : u.FriendlyName; } catch { }
                _credit[u] = c;
            }
            return c;
        }

        /// credit for a vehicle you hit recently enough, else null
        static Credit Recent(Unit u)
        {
            if (u == null || !_credit.TryGetValue(u, out var c)) return null;
            return Time.time - c.LastHit <= Mathf.Max(Settings.KillCreditSeconds.Value, 1f) ? c : null;
        }

        static void StruckPostfix(Unit __instance, IUnit shooter, AmmoType ammoType, bool isSpall)
        {
            try
            {
                // your rounds' hits, for the miss messages (a non-pen hit never shows in the shot's HitUnits)
                if (!isSpall && __instance != null && shooter != null && (object)shooter == Player && __instance.Type != UnitType.Infantry)
                {
                    _strikes.Add((__instance, Time.time));
                    if (_strikes.Count > 16) _strikes.RemoveAt(0);
                }
                if (!Active || isSpall || __instance == null) return;
                var me = Player;
                if (me == null || __instance == me) return;
                if (shooter != null && (object)shooter == me)
                {
                    var c = Get(__instance);
                    bool newRound = Time.time - c.LastHit > 0.15f;   // one round hits several plates
                    c.LastHit = Time.time;
                    float cal = ammoType != null ? Mathf.Max(ammoType.Caliber, ammoType.Radius * 2000f) : 0f;
                    if (newRound && cal >= Settings.RecordMinCaliberMm && !c.Dead && !c.Out) HudSfx.Hit();
                }
                else if (shooter != null && _credit.TryGetValue(__instance, out var c2))
                    c2.LastOther = Time.time;
            }
            catch (Exception e) { HitCamMod.Log.Warning("Kill feed (struck): " + e.Message); }
        }

        /// player's hit is fully recorded (ArcadeHud.OnRecordReady): what tier of damage it did
        public static void OnRecordReady(HitRecord rec)
        {
            try
            {
                if (!Active || rec == null || !rec.PlayerShot || rec.Target == null) return;
                var c = Get(rec.Target);
                c.LastHit = Mathf.Max(c.LastHit, rec.FirstImpactReal);
                if (c.Dead || c.Out) return;

                // critical: crew knocked out, or gun / engine / transmission destroyed; serious: one of those damaged
                string crit = null, sev = null;
                foreach (var e in rec.Events)
                {
                    if (e.Kind == EvKind.Crew && e.After >= (float)HealthStatus.Critical)
                        crit = Join(crit, e.Subject?.ToLowerInvariant());
                    else if (e.Kind == EvKind.Module && Timeline.Real(e))
                    {
                        var cat = GhostSnapshot.Classify(e.Subject);
                        bool vital = cat == ModCat.Engine || cat == ModCat.Transmission || cat == ModCat.Gun;
                        if (vital && e.After <= 0f) crit = Join(crit, e.Subject);
                        else if (vital) sev = Join(sev, e.Subject);
                    }
                }
                string t = rec.Title ?? "";
                bool pen = t != "NON-PENETRATION" && t != "RICOCHET" && t != "SHELL SHATTERED" && t != "FUZE NOT ARMED";
                if (t == "TARGET DESTROYED" || t == "AMMUNITION EXPLODED" || t == "CREW KNOCKED OUT") return;   // kill hooks report those (and play the kill sound)
                if (crit != null) { Add($"{c.Name}  ·  CRITICALLY DAMAGED  ({Trim(crit)})", Red); HudSfx.Pen(); }
                else if (sev != null) { Add($"{c.Name}  ·  SEVERELY DAMAGED  ({Trim(sev)})", Grey); HudSfx.Pen(); }
                else if (pen && (t == "PENETRATION" || t == "HIT" || t == "CRITICAL HIT" || t == "FIRE" || t == "AMMUNITION FIRE")) { Add($"{c.Name}  ·  HIT", White); HudSfx.Pen(); }
                else if (pen && t == "PENETRATED, NO DAMAGE") Add($"{c.Name}  ·  HIT", White);
            }
            catch (Exception e) { HitCamMod.Log.Warning("Kill feed (record): " + e.Message); }
        }

        static string Join(string a, string b) => string.IsNullOrEmpty(b) ? a : a == null ? b : a.Contains(b) ? a : a + ", " + b;
        static string Trim(string s) => s.Length > 48 ? s.Substring(0, 46) + "…" : s;

        static void DestroyedPostfix(Unit __instance) => Killed(__instance, "destroyed");
        static void IncapacitatedPostfix(Unit __instance) => Killed(__instance, "crew knocked out");
        static void AbandonedPostfix(Unit __instance) => Killed(__instance, "crew bailed out");

        static void Killed(Unit u, string how)
        {
            try
            {
                if (!Active) return;
                var c = Recent(u);
                if (c == null) return;
                bool destroyed = how == "destroyed";
                if (destroyed ? c.Dead : (c.Out || c.Dead)) return;
                float now = Time.time, since = now - c.LastHit;
                bool firstKill = !c.Dead && !c.Out;
                if (destroyed) c.Dead = true; else c.Out = true;

                // someone else's round landed after yours, right before the end: their kill, your assist
                if (c.LastOther > c.LastHit + 0.05f && now - c.LastOther < 3f)
                {
                    Add($"{c.Name}  ·  ASSIST  ({how})", Assist);
                    if (firstKill) { ArcadeHud.PushToast("Assist", Assist, c.Name + "  ·  " + how); HudSfx.Pen(); }
                    return;
                }
                string why = destroyed ? (c.BoomAt >= 0f && now - c.BoomAt < 6f ? "ammunition exploded" : c.Burning ? "fire" : "") : how;
                string verb = destroyed ? "DESTROYED" : how == "crew bailed out" ? "CREW BAILED OUT" : "CREW KNOCKED OUT";
                string after = since >= 2f ? $"your hit {since:0} s ago" : "";
                string detail = why.Length > 0 && verb != why.ToUpperInvariant() ? why : "";
                Add($"{c.Name}  ·  {verb}" + (detail.Length > 0 || after.Length > 0 ? "  (" + detail + (detail.Length > 0 && after.Length > 0 ? ", " : "") + after + ")" : ""), Red);
                if (!firstKill) return;          // knocked out first, burnt out later: one message
                string sub = c.Name + (detail.Length > 0 ? "  ·  " + detail : "") + (after.Length > 0 ? "  ·  " + after : "");
                ArcadeHud.PushToast(destroyed ? "Target destroyed" : how == "crew bailed out" ? "Crew bailed out" : "Crew knocked out", Red, sub);
                HudSfx.Kill();
            }
            catch (Exception e) { HitCamMod.Log.Warning("Kill feed (kill): " + e.Message); }
        }

        static void IgnitePrefix(FlammableItem __instance, out bool __state) { __state = __instance != null && __instance.IsBurning; }

        static void IgnitePostfix(FlammableItem __instance, bool __state)
        {
            try
            {
                if (!Active || __state || __instance == null || !__instance.IsBurning) return;
                var c = Recent(CodeUtils.GetParentUnitFollowAware(__instance.transform));
                if (c == null || c.Burning || c.Dead) return;
                c.Burning = true;
                c.FireAt = Time.time;
                Add($"{c.Name}  ·  SET AFIRE" + (__instance.Explosive ? "  (ammunition)" : ""), Orange);
                ArcadeHud.PushToast("Set afire", Orange, c.Name);
                HudSfx.Pen();
            }
            catch (Exception e) { HitCamMod.Log.Warning("Kill feed (fire): " + e.Message); }
        }

        // ------------------------------------------------------------------ misses (undershoot / overshoot), named after the shot

        class Aimed { public Unit Unit; public float Range; public string Name; public LiveRound Round; public float At; }
        static readonly List<GHPC.Weapons.ShotInfo> _gone = new List<GHPC.Weapons.ShotInfo>();
        static readonly List<(Unit u, float t)> _strikes = new List<(Unit, float)>();

        /// did this round touch a vehicle or a helicopter? game only lists its "vehicles" in HitUnits, a helicopter (or a
        /// round that didn't get in) only shows up as the armour a shot frame hit
        static bool StruckVehicle(GHPC.Weapons.ShotInfo si)
        {
            foreach (var u in si.HitUnits) if (u is Unit hu && hu.Type != UnitType.Infantry) return true;
            var fr = si.AllShotFrames;
            if (fr == null) return false;
            for (int i = 0; i < fr.Count; i++)
            {
                var f = fr[i];
                if (f.HitVehicle is Unit hv && hv.Type != UnitType.Infantry) return true;
                Transform t = null;
                try { if (f.HitArmor != null) t = f.HitArmor.Transform; } catch { }
                if (t == null) continue;
                var u = CodeUtils.GetParentUnitFollowAware(t);
                if (u != null && u.Type != UnitType.Infantry) return true;
            }
            return false;
        }

        /// did one of your rounds hit a vehicle near p since t (this round, as far as we can tell)?
        static bool StruckNear(Vector3 p, float since)
        {
            foreach (var (u, t) in _strikes)
                if (t >= since && u != null && (u.transform.position - p).sqrMagnitude < 60f * 60f) return true;
            return false;
        }
        static readonly Dictionary<GHPC.Weapons.ShotInfo, Aimed> _aimed = new Dictionary<GHPC.Weapons.ShotInfo, Aimed>();

        /// one of your main-gun rounds leaves: remember what was under the crosshair
        static void FiredPostfix(LiveRound __instance, LiveRound parentRound)
        {
            try
            {
                if (parentRound != null || !Settings.ArcadeHud.Value || !Settings.ArcadeCallouts.Value) return;
                var me = Player;
                if (me == null || __instance == null || (object)__instance.Shooter != me || __instance.ShotInfo == null) return;
                var a = __instance.Info;
                if (a == null || Mathf.Max(a.Caliber, a.Radius * 2000f) < Settings.RecordMinCaliberMm) return;
                // what you're shooting at: under the crosshair, under the impact cross, or the marker nearest the crosshair (lob)
                var u = ArcadeHud.AimedEnemy(out Vector3 at);
                if (u == null) { if (AutoTest.Active) HitCamMod.Log.Msg("Miss tracking: no enemy under the crosshair / cross / within 3 deg of the sight"); return; }
                _aimed[__instance.ShotInfo] = new Aimed { Unit = u, Range = Vector3.Distance(__instance.transform.position, at), Name = Recorder.SafeName(u), Round = __instance, At = Time.time };
                if (AutoTest.Active) HitCamMod.Log.Msg($"Miss tracking: shot at {Recorder.SafeName(u)}, {Vector3.Distance(__instance.transform.position, at):0} m");
                if (_aimed.Count > 64) _aimed.Clear();
            }
            catch { }
        }

        /// it's done: if it never touched a vehicle, say if it fell short or flew over what you aimed at
        static void ShotEndedPostfix(GHPC.Weapons.ShotInfo __instance)
        {
            try
            {
                if (__instance == null || __instance.ParentShot != null || !_aimed.TryGetValue(__instance, out var aim)) return;
                _aimed.Remove(__instance);
                if (StruckVehicle(__instance))
                {
                    if (AutoTest.Active) HitCamMod.Log.Msg("Miss tracking: it hit a vehicle");
                    return;      // a hit: the record's callout says how it went
                }
                var frames = __instance.AllShotFrames;
                Vector3 end = frames != null && frames.Count > 0 ? frames[frames.Count - 1].WorldPosition : __instance.DetonationPosition;
                if (StruckNear(end, aim.At))
                {
                    if (AutoTest.Active) HitCamMod.Log.Msg("Miss tracking: it struck a vehicle (no penetration)");
                    return;
                }
                float d = Vector3.Distance(__instance.StartPosition, end);
                string what = d < aim.Range - 8f ? "UNDERSHOOT" : d > aim.Range + 8f ? "OVERSHOOT" : "MISS";
                if (AutoTest.Active) HitCamMod.Log.Msg($"Miss tracking: round ended {d:0} m out vs {aim.Range:0} m -> {what}");
                ArcadeHud.ShowMiss(what, aim.Name);
            }
            catch { }
        }

        /// round already well past what you aimed at without touching a vehicle went over: say so now, not when it comes
        /// down km further on (long zero, or the proving ground's deck edge with the map 30 m below)
        static void CheckOvershoots()
        {
            if (_aimed.Count == 0) return;
            try
            {
                _gone.Clear();
                foreach (var kv in _aimed)
                {
                    var si = kv.Key;
                    var aim = kv.Value;
                    var r = aim.Round;
                    if (si == null || r == null || r.ShotInfo != si) continue;
                    Vector3 rp = r.transform.position, run = rp - si.StartPosition;
                    float d = run.magnitude;
                    if (d < 1f) continue;
                    if (StruckVehicle(si) || StruckNear(rp, aim.At)) { _gone.Add(si); continue; }   // it hit something on the way
                    // past the target where it is now, along the round's own path (moving target = wherever it got to)
                    float past = d - aim.Range;
                    if (aim.Unit != null && !aim.Unit.Destroyed)
                    {
                        Vector3 tp = aim.Unit.transform.position;
                        try { var ct = aim.Unit.Center; if (ct != null) tp = ct.position; } catch { }
                        past = Vector3.Dot(rp - tp, run / d);
                    }
                    if (past < 40f) continue;
                    bool hit = false;
                    foreach (var u in si.HitUnits) if (u is Unit hu && hu.Type != UnitType.Infantry) { hit = true; break; }
                    _gone.Add(si);
                    if (hit) continue;
                    if (AutoTest.Active) HitCamMod.Log.Msg($"Miss tracking: round {d:0} m out, {past:0} m past {aim.Name} without a hit -> OVERSHOOT");
                    ArcadeHud.ShowMiss("OVERSHOOT", aim.Name);
                }
                foreach (var si in _gone) _aimed.Remove(si);
            }
            catch { }
        }

        static void BurstPostfix(FlammablesManager __instance) => Boom(__instance != null ? __instance.transform : null);
        static void CompartmentPostfix(Compartment __instance) => Boom(__instance != null ? __instance.transform : null);

        static void Boom(Transform t)
        {
            try
            {
                if (!Active || t == null) return;
                var c = Recent(CodeUtils.GetParentUnitFollowAware(t));
                if (c != null && c.BoomAt < 0f) c.BoomAt = Time.time;
            }
            catch { }
        }

        // ------------------------------------------------------------------ per frame

        public static void Update()
        {
            CheckOvershoots();
            if (!Active) return;
            float now = Time.time;
            // ammo explosion that didn't (yet) finish it off
            foreach (var kv in _credit)
            {
                var c = kv.Value;
                if (c.BoomAt >= 0f && !c.BoomShown && !c.Dead && now - c.BoomAt > 2f)
                {
                    c.BoomShown = true;
                    Add($"{c.Name}  ·  AMMUNITION EXPLODED", Red);
                    ArcadeHud.PushToast("Ammunition exploded", Red, c.Name);
                }
            }
            if (Time.unscaledTime >= _nextClean)
            {
                _nextClean = Time.unscaledTime + 5f;
                List<Unit> drop = null;
                float keep = Mathf.Max(Settings.KillCreditSeconds.Value, 1f) + 30f;
                foreach (var kv in _credit)
                    if (kv.Key == null || now - kv.Value.LastHit > keep) (drop ??= new List<Unit>()).Add(kv.Key);
                if (drop != null) foreach (var u in drop) _credit.Remove(u);
            }
            float t = Time.unscaledTime;
            for (int i = Feed.Count - 1; i >= 0; i--) if (t - Feed[i].Born > FeedSeconds) Feed.RemoveAt(i);
        }

        static void Add(string text, Color c)
        {
            if (!Settings.ArcadeKillFeed.Value) return;
            Feed.Add(new Msg { Text = text, Color = c, Born = Time.unscaledTime });
            while (Feed.Count > 4) Feed.RemoveAt(0);
            if (AutoTest.Active) HitCamMod.Log.Msg("Kill feed: " + text);
        }

        /// config menu preview: example lines when nothing is happening
        static readonly Msg[] _sample =
        {
            new Msg { Text = "T-72M1  ·  CRITICALLY DAMAGED  (engine)", Color = Red },
            new Msg { Text = "T-72M1  ·  SET AFIRE", Color = Orange },
            new Msg { Text = "T-72M1  ·  DESTROYED  (fire, your hit 9 s ago)", Color = Red },
        };

        // ------------------------------------------------------------------ drawing

        public static void Draw(float k)
        {
            bool preview = ConfigMenu.IsOpen && Feed.Count == 0;
            float t = Time.unscaledTime;
            int n = preview ? _sample.Length : Feed.Count;
            if (n == 0) return;
            bool right = !string.Equals(Settings.KillFeedSide.Value, "Left", StringComparison.OrdinalIgnoreCase);
            float h = 15f * k, w = 420f * k;
            // right: just above the minimap (bottom right by default) like WT's battle log; left: above GHPC's weapon panel
            float mm = Settings.ArcadeMinimap.Value && (Settings.MinimapCorner.Value ?? "").Contains("Bottom") && !(Settings.MinimapCorner.Value ?? "").Contains("Left") == right
                ? Mathf.Clamp(Settings.MinimapSize.Value, 0.1f, 0.6f) * Screen.height + 14f : 0f;
            float bottom = right ? Screen.height - 14f - mm - 6f * k : Screen.height * 0.75f;
            float x = right ? Screen.width - w - 14f : 16f;
            var font = HudGfx.Font(11f * k, right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, false);
            for (int i = 0; i < n; i++)
            {
                var m = preview ? _sample[i] : Feed[i];
                float age = preview ? 1f : t - m.Born;
                float a = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01((FeedSeconds - age) / 1.2f);
                if (a <= 0.01f) continue;
                float y = bottom - (n - i) * h;
                var c = m.Color; c.a = a;
                HudGfx.Text(new Rect(x, y, w, h), m.Text, font, c);
            }
        }
    }
}
