using System;
using System.Collections.Generic;
using GHPC;
using GHPC.AI;
using GHPC.AI.Sensors;
using GHPC.World;
using HarmonyLib;
using UnityEngine;

namespace GHPCHitCam
{
    /// Which vehicles the arcade HUD marks. GHPC has real spotting: every unit's TargetSensor keeps the enemies it has
    /// spotted (protected HashSet _activeTargets, filled after a spot delay of ~1-8 s that grows with range), the player's
    /// own vehicle included (PlayerInput.FixedUpdate keeps updating it while you drive). We mark what any friendly
    /// (or friendly helicopter, HelicopterAIController._targetsInSight) has spotted, and keep it for MarkerMemorySeconds after
    /// it drops out. MarkerSource=LineOfSight instead marks enemies the game's own LOS test (VisibilityManager.IsPositionVisible,
    /// terrain / trees / smoke) can see from your sight within MarkerRange.
    internal static class Contacts
    {
        internal class Contact
        {
            public Unit Unit;
            public bool Friendly;
            public bool Destroyed;
            public bool SeenNow;          // spotted / visible on the last check
            public bool EverSeen;
            public bool Shown;            // marker drawn this frame
            public float LastSeen = -999f;
            public float DestroyedAt = -1f;
            public Vector3 Center;
            public float HalfHeight = 1.2f;
            public float Distance;
            public string Label = "", DistLabel = "", NameDist = "", KmLabel = "";
            internal Vector3 LocalCenter;
            internal bool HasLocal;
            internal int LabelKey = -1;
            public string Name = "?";
            internal string Kind = "";
            public MapClass Class = MapClass.Unknown;
        }

        public static readonly List<Contact> All = new List<Contact>();         // enemies
        public static readonly List<Contact> Friends = new List<Contact>();     // for the minimap
        public static readonly List<Contact> Troops = new List<Contact>();      // infantry of both sides: the minimap only
        static readonly Dictionary<Unit, Contact> _by = new Dictionary<Unit, Contact>();
        static float _nextSync, _nextSpot;
        static int _losCursor;
        static Faction _side = Faction.Neutral;

        static AccessTools.FieldRef<TargetSensor, HashSet<Unit>> _active;
        static bool _reflected, _reflectFailed;
        static int _lastSpottedCount = -1;
        public static int SpottedBySensors { get; private set; }
        public static bool UsingGameSpotting { get; private set; }

        public static void Reset()
        {
            All.Clear();
            Friends.Clear();
            Troops.Clear();
            _by.Clear();
            _nextSync = _nextSpot = 0f;
            _side = Faction.Neutral;
            _lastSpottedCount = -1;
        }

        public static void Update(Unit player)
        {
            float now = Time.time;
            if (player.Allegiance != _side) { Reset(); _side = player.Allegiance; }
            if (Time.unscaledTime >= _nextSync) { _nextSync = Time.unscaledTime + 1f; Sync(player); }

            bool los = string.Equals(Settings.MarkerSource.Value, "LineOfSight", StringComparison.OrdinalIgnoreCase);
            if (!los && Time.unscaledTime >= _nextSpot) { _nextSpot = Time.unscaledTime + 0.25f; SpotFromSensors(player); }
            if (los || !UsingGameSpotting) CheckLineOfSight(player, now);

            Vector3 pp = player.transform.position;
            float memory = Mathf.Max(Settings.MarkerMemorySeconds.Value, 0f);
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var c = All[i];
                if (c.Unit == null) { All.RemoveAt(i); continue; }
                Refresh(c, pp, now);
                c.Shown = c.Destroyed ? c.EverSeen && now - c.DestroyedAt < 30f : (c.SeenNow || (c.EverSeen && now - c.LastSeen < memory));
            }
            for (int i = Friends.Count - 1; i >= 0; i--)
            {
                var c = Friends[i];
                if (c.Unit == null) { Friends.RemoveAt(i); continue; }
                Refresh(c, pp, now);
                c.Shown = !c.Destroyed && c.Unit != player;
            }
            for (int i = Troops.Count - 1; i >= 0; i--)
            {
                var c = Troops[i];
                if (c.Unit == null) { Troops.RemoveAt(i); continue; }
                Refresh(c, pp, now);
                c.Shown = !c.Destroyed && (c.Friendly || c.SeenNow || (c.EverSeen && now - c.LastSeen < memory));
            }
        }

        /// New units (spawns, reinforcements) join; the mission's own list includes dead ones.
        static void Sync(Unit player)
        {
            var sum = SceneUnitsManager.Instance;
            if (sum == null) return;
            var units = sum.AllUnitsInScene;
            if (units == null) return;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || _by.ContainsKey(u)) continue;
                if (u.Allegiance == Faction.Neutral) continue;
                var c = new Contact { Unit = u, Friendly = u.Allegiance == player.Allegiance, Class = MapIcons.Classify(u) };
                try { c.Name = string.IsNullOrEmpty(u.FriendlyName) ? Recorder.Pretty(u.name) : u.FriendlyName; } catch { }
                try { c.Kind = u.ShortNameUs.ToString().ToUpperInvariant(); } catch { }
                Locate(c);
                c.Destroyed = Neutralized(u);
                if (c.Destroyed) c.DestroyedAt = -999f;   // dead before we started: no X
                _by[u] = c;
                if (u.Type == UnitType.Infantry) Troops.Add(c);
                else (c.Friendly ? Friends : All).Add(c);
            }
        }

        static bool Neutralized(Unit u)
        {
            try { return u.Destroyed || u.Neutralized; } catch { return u.Destroyed; }
        }

        /// The vehicle's middle (the game's own aim / visibility point, Unit.Center: 1.5 m up its root) and how far its top is
        /// above that, once. Renderer bounds alone are not trusted for the middle: the T-72M1's skinned hull reports ~75 m.
        static void Locate(Contact c)
        {
            var t = c.Unit.transform;
            Vector3 center = t.position + t.up * 1.4f;
            try { var ct = c.Unit.Center; if (ct != null) center = ct.position; } catch { }
            float top = center.y + 0.9f;
            try
            {
                var rs = c.Unit.GetComponentsInChildren<MeshRenderer>(false);
                bool any = false;
                foreach (var r in rs)
                {
                    var rb = r.bounds;
                    if (rb.size.magnitude > 15f) continue;
                    Vector3 off = rb.center - center;
                    if (off.x * off.x + off.z * off.z > 8f * 8f || Mathf.Abs(off.y) > 5f) continue;
                    top = any ? Mathf.Max(top, rb.max.y) : rb.max.y;
                    any = true;
                }
            }
            catch { }
            c.LocalCenter = t.InverseTransformPoint(center);
            c.HalfHeight = Mathf.Clamp(top - center.y, 0.5f, 4f);
            c.HasLocal = true;
        }

        static void Refresh(Contact c, Vector3 playerPos, float now)
        {
            var t = c.Unit.transform;
            c.Center = t.TransformPoint(c.LocalCenter);
            bool dead = Neutralized(c.Unit);
            if (dead && !c.Destroyed) { c.Destroyed = true; c.DestroyedAt = now; }
            c.Distance = Vector3.Distance(playerPos, c.Center);
            int key = Mathf.RoundToInt(c.Distance / 10f);
            if (key != c.LabelKey)
            {
                c.LabelKey = key;
                string d = c.Distance >= 1000f ? (c.Distance / 1000f).ToString("0.00") + " km" : (key * 10).ToString() + " m";
                c.DistLabel = d;
                c.Label = c.Kind.Length > 0 ? c.Name + "  ·  " + c.Kind + "  ·  " + d : c.Name + "  ·  " + d;
                c.NameDist = c.Name + "  " + d;
                c.KmLabel = (key / 100f).ToString("0.00") + " km";
            }
        }

        // ------------------------------------------------------------------ the game's spotting

        static void Reflect()
        {
            _reflected = true;
            try { _active = AccessTools.FieldRefAccess<TargetSensor, HashSet<Unit>>("_activeTargets"); }
            catch (Exception e) { _reflectFailed = true; HitCamMod.Log.Warning("Arcade HUD: TargetSensor._activeTargets not found (" + e.Message + "), markers use line of sight"); }
        }

        static void SpotFromSensors(Unit player)
        {
            if (!_reflected) Reflect();
            if (_reflectFailed || _active == null) { UsingGameSpotting = false; return; }
            float now = Time.time;
            foreach (var c in All) c.SeenNow = false;
            foreach (var c in Troops) c.SeenNow = false;
            int sensors = 0;
            var lists = SceneUnitsManager.AllLiveUnitsByFaction;
            int f = (int)player.Allegiance;
            if (lists != null && f >= 0 && f < lists.Length && lists[f] != null)
            {
                var friends = lists[f];
                for (int i = 0; i < friends.Count; i++) sensors += Collect(friends[i], now) ? 1 : 0;
            }
            // the player's own vehicle (it leaves the live list when knocked out, the sensor stays)
            if (lists == null || f < 0 || f >= lists.Length || lists[f] == null || !lists[f].Contains(player)) sensors += Collect(player, now) ? 1 : 0;
            UsingGameSpotting = sensors > 0;
            int n = 0;
            foreach (var c in All) if (c.SeenNow) n++;
            SpottedBySensors = n;
            if (n != _lastSpottedCount && AutoTest.Active) HitCamMod.Log.Msg($"Arcade HUD: {n} enemies spotted by {sensors} friendly sensors");
            _lastSpottedCount = n;
        }

        static bool Collect(Unit u, float now)
        {
            if (u == null) return false;
            bool any = false;
            try
            {
                if (u.Neutralized && u != ArcadeHud.Player) return false;
                var broker = u.InfoBroker;
                if (broker == null) return false;
                var ai = broker.AI;
                var sensor = ai != null ? ai.TargetSensor : null;
                if (sensor != null)
                {
                    any = true;
                    var set = _active(sensor);
                    if (set != null) foreach (var t in set) Mark(t, now);
                }
                var heli = broker.HelicopterAI;
                if (heli != null && heli._targetsInSight != null)
                {
                    any = true;
                    var l = heli._targetsInSight;
                    for (int i = 0; i < l.Count; i++) Mark(l[i], now);
                }
            }
            catch { }
            return any;
        }

        static void Mark(Unit t, float now)
        {
            if (t == null || !_by.TryGetValue(t, out var c) || c.Friendly) return;
            c.SeenNow = true;
            c.EverSeen = true;
            c.LastSeen = now;
        }

        // ------------------------------------------------------------------ line of sight (fallback / setting)

        static void CheckLineOfSight(Unit player, float now)
        {
            if (All.Count == 0) return;
            float range = Mathf.Max(Settings.MarkerRange.Value, 100f);
            Vector3 eye = player.transform.position + Vector3.up * 2.4f;
            var fcs = ArcadeHud.Fcs;
            try { if (fcs != null && fcs.AimTransform != null) eye = fcs.AimTransform.position; } catch { }
            // a few vehicles per frame, round robin
            int checks = Mathf.Min(3, All.Count);
            for (int k = 0; k < checks; k++)
            {
                _losCursor = (_losCursor + 1) % All.Count;
                var c = All[_losCursor];
                if (c.Unit == null || c.Destroyed) continue;
                bool vis = false;
                if ((c.Center - eye).sqrMagnitude <= range * range)
                {
                    try
                    {
                        vis = VisibilityManager.IsPositionVisible(player, eye, c.Unit, c.Center, range, range * range, out _, out _, grassBlocks: false);
                        if (!vis)
                        {
                            Vector3 top = c.Center + Vector3.up * c.HalfHeight * 0.8f;
                            vis = VisibilityManager.IsPositionVisible(player, eye, c.Unit, top, range, range * range, out _, out _, grassBlocks: false);
                        }
                    }
                    catch { vis = false; }
                }
                c.SeenNow = vis;
                if (vis) { c.EverSeen = true; c.LastSeen = now; }
            }
        }
    }
}
