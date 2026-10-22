using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using GHPC.AI;
using GHPC.AI.Interfaces;
using GHPC.Mission;
using GHPC.Mission.Data;
using GHPC.Player;
using GHPC.Utility;
using GHPC.Vehicle;
using GHPC.World;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace GHPCHitCam
{
    /// Test range around the player: passive target vehicles in front, and helicopters flying loops overhead.
    /// F7 spawns a wave (again = another wave). Uses the game's own UnitSpawner, so targets are real units
    /// with real armor, modules and crew.
    internal static class TestRange
    {
        static readonly string[] Ground = { "T72M1", "T80B", "BMP2", "BTR70", "M113", "M1IP", "LEO2A3", "BRDM2" };
        static readonly string[] Helis = { "Mi24", "Mi8T", "AH1", "MI2" };
        static readonly List<Unit> _spawned = new List<Unit>();
        static readonly List<(Unit u, Vector3 center, float r, float alt, float speed)> _flyers = new List<(Unit, Vector3, float, float, float)>();
        static string _msg;
        static float _msgUntil;
        static bool _busy;
        static int _wave;

        static AccessTools.FieldRef<HelicopterController, float> _rpm;

        public static void Reset()
        {
            _spawned.Clear();
            _flyers.Clear();
            _wave = 0;
            _busy = false;
        }

        public static void Update()
        {
            if (Hotkeys.Down(Settings.RangeKey) && !_busy) MelonCoroutines.Start(SpawnWave());
            // helicopters only get their engine forced on for the first seconds; keep them on station afterwards
            for (int i = _flyers.Count - 1; i >= 0; i--)
                if (_flyers[i].u == null || _flyers[i].u.Destroyed) _flyers.RemoveAt(i);
        }

        public static IEnumerator SpawnWave()
        {
            _busy = true;
            try { } finally { }
            var player = SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            if (player == null || sp == null)
            {
                Say(player == null ? "Test range: no player vehicle" : "Test range: this mission has no unit spawner");
                _busy = false;
                yield break;
            }
            _wave++;
            Faction enemy = player.Allegiance == Faction.Blue ? Faction.Red : Faction.Blue;
            Vector3 p0 = player.transform.position;
            Vector3 fwd = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.1f) fwd = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float lane = (_wave - 1) * 60f;   // every wave a bit further out

            var newGround = new List<Unit>();
            for (int i = 0; i < Ground.Length; i++)
            {
                // two rows; front row side-on (side shots), back row facing you (frontal armor)
                bool back = i % 2 == 1;
                Vector3 pos = p0 + fwd * (350f + lane + (back ? 150f : 0f)) + right * ((i / 2 - 1.5f) * 45f);
                Quaternion rot = Quaternion.LookRotation(back ? -fwd : right);
                var u = Spawn(sp, Ground[i], pos, rot, enemy, true);
                if (u != null) newGround.Add(u);
                yield return null;
            }

            var newHelis = new List<(Unit, Vector3, float, float, float)>();
            Vector3 center = p0 + fwd * (550f + lane);
            for (int j = 0; j < Helis.Length; j++)
            {
                float r = 300f + j * 140f;
                float alt = 60f + j * 25f;
                float a0 = j * Mathf.PI * 0.5f;
                Vector3 pos = center + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * r;
                pos.y = GroundY(pos) + alt;
                Vector3 tangent = new Vector3(-Mathf.Sin(a0), 0f, Mathf.Cos(a0));
                var u = Spawn(sp, Helis[j], pos, Quaternion.LookRotation(tangent), enemy, false);
                if (u != null) newHelis.Add((u, center, r, alt, 28f + j * 7f));
                yield return null;
            }

            // let Start()/AI startup run before giving orders
            yield return null;
            yield return null;
            foreach (var u in newGround) MakePassive(u);
            foreach (var h in newHelis)
            {
                try { Fly(h.Item1, h.Item2, h.Item3, h.Item4, h.Item5); _flyers.Add(h); }
                catch (Exception e) { HitCamMod.Log.Warning("Helicopter setup failed: " + e.Message); }
            }
            // keep the rotors at full speed for a few seconds while they settle into the loop
            float t0 = Time.time;
            while (Time.time - t0 < 4f)
            {
                foreach (var h in newHelis) KeepRotor(h.Item1);
                yield return null;
            }
            Say($"Test range wave {_wave}: {newGround.Count} targets, {newHelis.Count} helicopters ({string.Join(", ", newHelis.Select(h => Recorder.SafeName(h.Item1)))})");
            _busy = false;
        }

        static readonly HashSet<string> _spawnErrs = new HashSet<string>();

        internal static Unit Spawn(UnitSpawner sp, string key, Vector3 pos, Quaternion rot, Faction f, bool ground)
        {
            try
            {
                if (ground)
                {
                    pos = CodeUtils.GetPositionOnTerrain(pos + Vector3.up * 50f, out _, 4000f);
                    pos = CodeUtils.GetTerrainAwarePosition(pos);
                    rot = CodeUtils.GetTerrainAwareRotation(pos, rot * Vector3.forward);
                }
                var md = new UnitMetaData
                {
                    Name = key + " (range)",
                    Position = pos,
                    Rotation = rot,
                    Allegiance = f,
                    UnitType = ground ? UnitType.GroundVehicle : UnitType.AirVehicle,
                };
                var iu = ModdedVehicles.Spawn(sp, key, md);
                var u = iu as Unit;
                if (u == null) { HitCamMod.Log.Warning("Test range: could not spawn " + key); return null; }
                u.Targetable = true;
                try { SceneUnitsManager.Instance.Meta?.UnimportantUnits?.Add(u); } catch { }   // never decides the mission
                _spawned.Add(u);
                return u;
            }
            catch (Exception e)
            {
                HitCamMod.Log.Warning($"Test range: spawn {key} failed: {e.Message}");
                if (_spawnErrs.Add(key)) HitCamMod.Log.Warning(e.ToString());   // the whole error once a vehicle
                return null;
            }
        }

        static float GroundY(Vector3 p)
        {
            var g = CodeUtils.GetPositionOnTerrain(new Vector3(p.x, 2000f, p.z), out bool ok, 4000f);
            return ok ? g.y : p.y;
        }

        /// Range targets stand still and don't fight back.
        internal static void MakePassive(Unit u)
        {
            if (u == null) return;
            try
            {
                var ai = u.InfoBroker != null ? u.InfoBroker.AI : null;
                if (ai != null) { ai.Stop(); ai.enabled = false; }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Could not make " + Recorder.SafeName(u) + " passive: " + e.Message); }
        }

        internal static void Fly(Unit u, Vector3 center, float r, float alt, float speed)
        {
            var ai = u.InfoBroker != null ? u.InfoBroker.HelicopterAI : null;
            if (ai == null) { HitCamMod.Log.Warning(Recorder.SafeName(u) + " has no helicopter AI"); return; }
            ai.canAttack = false;
            ai.idleBehavior = HelicopterAIController.IdleBehavior.Hover;
            var pts = new List<IWaypoint>();
            const int n = 16;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                Vector3 p = center + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                p.y = GroundY(p);
                pts.Add(new VectorWaypoint(p, -1f, r * 0.25f));
            }
            // start at the waypoint ahead of where it spawned
            Vector3 rel = u.transform.position - center;
            int start = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(rel.z, rel.x) / (Mathf.PI * 2f) * n + 1f, n)) % n;
            ai.SetCurrentRoute(pts, start, false);
            ai.WaypointTracker.LoopWaypoints = true;
            ai.SetAbsoluteAltitude(GroundY(center) + alt);
            ai.SetOverrideSpeed(speed);
            KeepRotor(u);
        }

        internal static void KeepRotor(Unit u)
        {
            if (u == null) return;
            var hc = u.GetComponent<HelicopterController>();
            if (hc == null) return;
            try
            {
                _rpm ??= AccessTools.FieldRefAccess<HelicopterController, float>("_rpm");
                if (_rpm(hc) < hc.FullRpm) _rpm(hc) = hc.FullRpm;
                AccessTools.PropertySetter(typeof(HelicopterController), "ThrottlePercent")?.Invoke(hc, new object[] { 100f });
            }
            catch { }
        }

        static void Say(string m) { _msg = m; _msgUntil = Time.unscaledTime + 4f; HitCamMod.Log.Msg(m); }

        static GUIStyle _st;

        public static void Draw()
        {
            if (Time.unscaledTime > _msgUntil || string.IsNullOrEmpty(_msg)) return;
            _st ??= new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _st.normal.textColor = new Color(0.7f, 1f, 0.7f);
            GUI.Label(new Rect(14, Screen.height * 0.32f - 52, 900, 24), _msg, _st);
        }

        /// For the self-test: spawn a wave and wait until it is done.
        public static IEnumerator SpawnWaveAndWait()
        {
            yield return SpawnWave();
        }

        public static IReadOnlyList<Unit> Spawned => _spawned;
    }
}
