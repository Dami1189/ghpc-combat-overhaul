using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using GHPC.AI;
using GHPC.Mission;
using GHPC.Mission.Data;
using GHPC.Utility;
using GHPC.World;
using UnityEngine;

namespace GHPCHitCam
{
    /// Self-test ground (autotest.cfg: testground=true): a flat pad of its own, as far from the mission's units as the map
    /// allows, with only our own targets on it. Every mission unit's AI is switched off (nobody drives over, nobody shoots
    /// at the targets), the ground vehicles stand side-on in a row, and the helicopters circle slowly above the pad.
    /// AutoTest then only picks targets from these.
    internal static class TestGround
    {
        static readonly string[] Ground = { "T72M1", "T80B", "M1IP", "LEO2A3", "BMP2", "BTR70", "M113", "BRDM2" };
        static readonly string[] Helis = { "Mi24", "Mi8T", "MI2", "AH1" };
        static readonly List<Unit> _spawned = new List<Unit>();
        static GameObject _pad;

        public static bool Active { get; private set; }
        public static IReadOnlyList<Unit> Spawned => _spawned;
        public static Vector3 Centre { get; private set; }
        public static float Top { get; private set; }

        public static void Reset()
        {
            _spawned.Clear();
            Active = false;
            if (_pad != null) UnityEngine.Object.Destroy(_pad);
            _pad = null;
        }

        public static IEnumerator Build(Unit player)
        {
            var sp = UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<UnitSpawner>();
            if (sp == null || player == null) { HitCamMod.Log.Warning("Test ground: no unit spawner / player"); yield break; }
            var mission = SceneUnitsManager.Instance.AllUnitsInScene.OfType<Unit>().Where(u => u != null).ToList();

            // the mission holds still: no AI anywhere
            int stopped = 0;
            foreach (var u in mission)
            {
                if (u == player) continue;
                try
                {
                    var ai = u.InfoBroker != null ? u.InfoBroker.AI : null;
                    if (ai != null) { ai.Stop(); ai.enabled = false; stopped++; }
                    var hai = u.InfoBroker != null ? u.InfoBroker.HelicopterAI : null;
                    if (hai != null) hai.canAttack = false;
                }
                catch { }
            }

            // the spot on the map farthest from every mission unit (and 400 m inside the map's edge)
            Bounds map = new Bounds(player.transform.position, Vector3.one * 2000f);
            var terrains = Terrain.activeTerrains;
            if (terrains != null && terrains.Length > 0)
            {
                map = new Bounds(terrains[0].GetPosition() + terrains[0].terrainData.size * 0.5f, terrains[0].terrainData.size);
                foreach (var t in terrains.Skip(1)) map.Encapsulate(new Bounds(t.GetPosition() + t.terrainData.size * 0.5f, t.terrainData.size));
            }
            Vector3 best = player.transform.position + player.transform.forward * 1500f;
            float bestD = -1f;
            for (float x = map.min.x + 400f; x <= map.max.x - 400f; x += 150f)
                for (float z = map.min.z + 400f; z <= map.max.z - 400f; z += 150f)
                {
                    var p = new Vector3(x, 0f, z);
                    float d = float.MaxValue;
                    foreach (var u in mission) { Vector3 q = u.transform.position; q.y = 0f; d = Mathf.Min(d, (q - p).magnitude); }
                    if (d > bestD) { bestD = d; best = p; }
                }
            // testground_at=<m>: the pad that far straight ahead of the player instead (its blasts heard at a known range)
            if (float.TryParse(AutoTest.Opt("testground_at", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float at) && at > 0f)
            {
                Vector3 f = player.transform.forward;
                f.y = 0f;
                f = f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
                best = player.transform.position + f * at;
                best.y = 0f;
                bestD = mission.Where(u => u != player).Select(u => { Vector3 q = u.transform.position; q.y = 0f; return (q - best).magnitude; }).DefaultIfEmpty(0f).Min();
            }
            // a flat pad just above the highest ground under it
            const float Half = 130f;
            float top = float.MinValue;
            for (float x = -Half; x <= Half; x += 10f)
                for (float z = -Half; z <= Half; z += 10f)
                {
                    var g = CodeUtils.GetPositionOnTerrain(new Vector3(best.x + x, 3000f, best.z + z), out bool ok, 6000f);
                    if (ok) top = Mathf.Max(top, g.y);
                }
            if (top == float.MinValue) top = player.transform.position.y;
            top += 0.6f;
            Centre = new Vector3(best.x, top, best.z);
            Top = top;
            _pad = MakePad(Centre, Half);
            HitCamMod.Log.Msg($"Test ground: pad {Half * 2f:0} m square at {Centre} ({bestD:0} m from the nearest mission unit, map {map.size.x:0}x{map.size.z:0} m), AI stopped on {stopped} mission units");

            // our own targets: ground vehicles side-on in a row (shots from +x come at their side), helicopters circling above
            Faction enemy = player.Allegiance == Faction.Blue ? Faction.Red : Faction.Blue;
            var ground = new List<Unit>();
            for (int i = 0; i < Ground.Length; i++)
            {
                // a ring 85 m across the pad's middle, each vehicle facing out: a round from its front, side or rear
                // (30 m away) passes no other vehicle
                float a = i * Mathf.PI * 2f / Ground.Length;
                Vector3 outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 pos = Centre + outward * 85f + Vector3.up * 0.4f;
                var u = Spawn(sp, Ground[i], pos, Quaternion.LookRotation(outward), enemy, true);
                if (u != null) ground.Add(u);
                yield return null;
            }
            var helis = new List<(Unit u, float r, float alt, float speed)>();
            for (int j = 0; j < Helis.Length; j++)
            {
                float r = 25f + j * 12f, alt = 30f + j * 12f;
                float a0 = j * Mathf.PI * 0.5f;
                Vector3 pos = Centre + new Vector3(Mathf.Cos(a0) * r, alt, Mathf.Sin(a0) * r);
                var u = Spawn(sp, Helis[j], pos, Quaternion.LookRotation(new Vector3(-Mathf.Sin(a0), 0f, Mathf.Cos(a0))), enemy, false);
                if (u != null) helis.Add((u, r, alt, 9f + j * 2f));
                yield return null;
            }
            yield return null;
            yield return null;
            foreach (var u in ground) TestRange.MakePassive(u);
            foreach (var h in helis)
            {
                try
                {
                    // hovering in place (rotors turning, held still) until the game kills it or its tail goes
                    var hai = h.u.InfoBroker != null ? h.u.InfoBroker.HelicopterAI : null;
                    if (hai != null) { hai.canAttack = false; hai.enabled = false; }
                    h.u.gameObject.AddComponent<HoverHold>().Init(h.u);
                }
                catch (Exception e) { HitCamMod.Log.Warning("Test ground helicopter: " + e.Message); }
            }
            float t0 = Time.time;
            while (Time.time - t0 < 6f)
            {
                foreach (var h in helis) TestRange.KeepRotor(h.u);
                yield return null;
            }
            Active = true;
            HitCamMod.Log.Msg($"Test ground: {ground.Count} vehicles ({string.Join(", ", ground.Select(Recorder.SafeName))}), {helis.Count} helicopters ({string.Join(", ", helis.Select(h => Recorder.SafeName(h.u) + $" at {h.u.transform.position.y - Top:0} m"))})");
        }

        static Unit Spawn(UnitSpawner sp, string key, Vector3 pos, Quaternion rot, Faction f, bool ground)
        {
            try
            {
                var md = new UnitMetaData
                {
                    Name = key + " (test ground)",
                    Position = pos,
                    Rotation = rot,
                    Allegiance = f,
                    UnitType = ground ? UnitType.GroundVehicle : UnitType.AirVehicle,
                };
                var u = ModdedVehicles.Spawn(sp, key, md) as Unit;
                if (u == null) { HitCamMod.Log.Warning("Test ground: could not spawn " + key); return null; }
                u.Targetable = true;
                try { SceneUnitsManager.Instance.Meta?.UnimportantUnits?.Add(u); } catch { }
                _spawned.Add(u);
                return u;
            }
            catch (Exception e) { HitCamMod.Log.Warning($"Test ground: spawn {key} failed: {e.Message}"); return null; }
        }

        /// The pad: a flat slab on the terrain layer (wheels, rounds and debris treat it as ground), with a 1 m grid on it.
        static GameObject MakePad(Vector3 centre, float half)
        {
            int layer = LayerMask.NameToLayer("Terrain");
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "HitCam test ground";
            if (layer >= 0) go.layer = layer;
            go.transform.position = centre - Vector3.up * 1f;
            go.transform.localScale = new Vector3(half * 2f, 2f, half * 2f);
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    bool line = x < 2 || y < 2;
                    float n = 0.9f + 0.1f * Mathf.PerlinNoise(x * 0.2f, y * 0.2f);
                    tex.SetPixel(x, y, line ? new Color(0.30f, 0.29f, 0.26f) : new Color(0.46f * n, 0.44f * n, 0.40f * n));
                }
            tex.Apply();
            var std = Shader.Find("Standard");
            var mat = new Material(std != null ? std : Shader.Find("Diffuse")) { mainTexture = tex, mainTextureScale = new Vector2(half * 2f, half * 2f) };
            if (std != null) mat.SetFloat("_Glossiness", 0.05f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }
    }

    /// Test ground: holds a helicopter still in the air with its rotors turning, and lets go (the game's own flight and
    /// crash physics take over) once the unit is destroyed or loses its tail rotor.
    internal class HoverHold : MonoBehaviour
    {
        Unit _u;
        Rigidbody _rb;
        Vector3 _pos;
        Quaternion _rot;
        GHPC.Vehicle.HelicopterController _hc;
        bool _free;

        public void Init(Unit u)
        {
            _u = u;
            _rb = u.GetComponent<Rigidbody>();
            _hc = u.GetComponent<GHPC.Vehicle.HelicopterController>();
            _pos = u.transform.position;
            _rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(u.transform.forward, Vector3.up).normalized, Vector3.up);
        }

        bool Hurt()
        {
            if (_u == null || _u.Destroyed) return true;
            if (_hc != null && _hc.TailRotorHitZones != null)
                foreach (var z in _hc.TailRotorHitZones) if (z != null && z.HealthPercent <= 0f) return true;
            if (_hc != null && _hc.EngineHitZones != null)
                foreach (var z in _hc.EngineHitZones) if (z != null && z.HealthPercent <= 0f) return true;
            return false;
        }

        void FixedUpdate()
        {
            if (_free || _rb == null) return;
            if (Hurt())
            {
                _free = true;
                HitCamMod.Log.Msg($"Test ground: {Recorder.SafeName(_u)} let go (hit), the game flies / crashes it now");
                return;
            }
            _rb.velocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.MovePosition(_pos);
            _rb.MoveRotation(_rot);
            TestRange.KeepRotor(_u);
        }
    }
}
