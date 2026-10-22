using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using GHPC;
using GHPC.World;
using GHPC.State;
using GHPC.Vehicle;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GHPCHitCam
{
    /// vehicle mods (Fulda Expansion Pack, Pact Increased Lethality and others built the same way) convert the mission's
    /// vehicles once at mission start: each registers a static Convert(GameState) for PlayerReady that walks the vehicles
    /// the mod listed then (its own static Vehicle[]). anything spawned after that (proving ground, Armor Sim) stayed stock:
    /// a Leopard 2K without its variants, a T-80B where the player set the mod up for T-80Us. so we note those handlers as
    /// the game takes them and rerun them for each vehicle we spawn with the list holding just that vehicle, and nothing
    /// gets read off the mission's vehicles (Warmup) until the mods are done with them
    internal static class ModdedVehicles
    {
        class Handler
        {
            public GameStateEventHandler Run;
            public GameState State;
            public int Stage, Order;
            public GameStatePriority Priority;
            public FieldInfo List;
            public StateController Owner;
            public string Name, Mod;
        }

        static readonly List<Handler> _handlers = new List<Handler>();
        static readonly HashSet<string> _told = new HashSet<string>();
        static int _order;
        static bool _busy;
        static StateController _convertedFor;
        static float _waitSince = -1f;

        public static void Install(HarmonyLib.Harmony h)
        {
            try
            {
                var run = AccessTools.Method(typeof(StateController), nameof(StateController.RunOrDefer));
                var wait = AccessTools.Method(typeof(StateController), nameof(StateController.WaitForComplete));
                if (run != null) h.Patch(run, postfix: new HarmonyMethod(typeof(ModdedVehicles), nameof(RunPostfix)));
                // (WaitForComplete passes it on to RunOrDefer, but a call that small may get inlined)
                if (wait != null) h.Patch(wait, prefix: new HarmonyMethod(typeof(ModdedVehicles), nameof(WaitPrefix)));
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: can't follow their conversions (vehicles we spawn stay stock): " + e.Message); }
            try
            {
                var vau = AccessTools.TypeByName("VanillaAssetUtil.AssetUtil.AssetPrefabReferenceDatabase");
                var release = vau != null ? AccessTools.Method(vau, "ReleaseTempVanillaAssets") : null;
                if (release != null) h.Patch(release, prefix: new HarmonyMethod(typeof(ModdedVehicles), nameof(KeepBorrowedPrefabs)));
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: VanillaAssetUtil: " + e.Message); }
            try
            {
                var en = AccessTools.Method(typeof(GHPC.Audio.VehicleAudioController), "EnableAudio");
                if (en != null) h.Patch(en, finalizer: new HarmonyMethod(typeof(ModdedVehicles), nameof(SoundFinalizer)));
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: vehicle sound hook: " + e.Message); }
        }

        /// above 0 while we spawn a vehicle or wake its sound
        internal static int Ours;
        static readonly HashSet<string> _soundSaid = new HashSet<string>();

        /// the game's vehicle sound throws as a vehicle gets made mid-mission (its own code, wheels not set up yet). on our
        /// spawns that's no error that looks like ours: WakeAudio starts it again once the vehicle is set up
        static Exception SoundFinalizer(Exception __exception, GHPC.Audio.VehicleAudioController __instance)
        {
            if (__exception == null || Ours <= 0) return __exception;
            try
            {
                string n = __instance != null ? __instance.name.Replace("(Clone)", "").Trim() : "?";
                if (Settings.Verbose && _soundSaid.Add(n)) HitCamMod.Log.Msg($"Spawned {n}: the game's vehicle sound failed as it was made ({__exception.GetType().Name} in the game's code), started again once it's set up");
            }
            catch { }
            return null;
        }

        /// VanillaAssetUtil (lib FEP and PIL load stock vehicles through) lends the mods the prefab of a vehicle the mission
        /// doesn't have, and once the mission is ready destroys it outright (DestroyImmediate on the asset) when letting go:
        /// can't be loaded again for the rest of the mission, and a Ctrl + F4 proving ground came up missing half its targets.
        /// the ones our ranges spawn get taken off its list first and handed to the game's spawner, which keeps them and lets
        /// them go with the mission like its own
        static void KeepBorrowedPrefabs(object __instance)
        {
            try
            {
                var temp = AccessTools.Field(__instance.GetType(), "TempAssetReferences")?.GetValue(__instance) as List<AssetReference>;
                if (temp == null || temp.Count == 0) return;
                var sp = GHPC.Mission.UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<GHPC.Mission.UnitSpawner>();
                if (sp == null || sp.PrefabLookup == null) return;
                _loadedUnits = _loadedUnits ?? AccessTools.Field(typeof(GHPC.Mission.UnitSpawner), "_loadedUnits");
                var loaded = _loadedUnits?.GetValue(sp) as List<AssetReference>;
                if (loaded == null) return;
                // all of them, not only the ones we spawn: a mod's conversion of a later spawned vehicle needs its donors too (FEP's
                // Leopard 2K takes its Rh202 gun from a borrowed Marder: "[RH202] Marder donor was not loaded" in the Armor Sim)
                // same as VanillaAssetUtil's own legacy mode, costs the memory of those few vehicles until the mission ends
                bool all = !(AutoTest.Active && AutoTest.Opt("vau_keep", "all") == "ours");
                var want = new HashSet<string>(ProvingGround.PlannedKeys().Concat(ArmorSimKeys()), StringComparer.OrdinalIgnoreCase);
                var kept = new List<string>();
                int lent = temp.Count;
                foreach (var m in sp.PrefabLookup.AllUnits)
                {
                    var r = m?.PrefabReference;
                    if (r == null || (!all && !want.Contains(m.Name)) || !temp.Contains(r) || !(r.Asset is GameObject g) || g == null) continue;
                    temp.Remove(r);
                    if (!loaded.Contains(r)) loaded.Add(r);
                    kept.Add(m.Name);
                }
                if (kept.Count > 0) HitCamMod.Log.Msg($"Vehicle mods: kept {kept.Count} of the {lent} stock prefab(s) VanillaAssetUtil lent out and was about to destroy (vehicles we spawn later and their conversions' donors need them): {string.Join(", ", kept)}");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: keeping VanillaAssetUtil's prefabs: " + e.Message); }
        }

        static void RunPostfix(GameState state, GameStateEventHandler handler, GameStatePriority priority, GameStateInvocationStatus __result)
        {
            if (__result != GameStateInvocationStatus.Fail) Note(state, (int)state * 2, handler, priority);
        }

        static void WaitPrefix(GameState state, GameStateEventHandler handler, GameStatePriority priority) => Note(state, (int)state * 2 + 1, handler, priority);

        static void Note(GameState state, int stage, GameStateEventHandler handler, GameStatePriority priority)
        {
            try
            {
                var m = handler?.Method;
                if (m == null || !m.IsStatic || m.DeclaringType == null) return;
                var asm = m.DeclaringType.Assembly;
                if (asm == typeof(ModdedVehicles).Assembly || asm == typeof(StateController).Assembly) return;
                if (typeof(MelonBase).IsAssignableFrom(m.DeclaringType)) return;   // mod's own setup (it fills the list)
                var list = VehicleList(m);
                if (list == null) return;
                string mod = asm.GetCustomAttribute<MelonInfoAttribute>()?.Name ?? asm.GetName().Name;
                string name = $"{mod}: {m.DeclaringType.Name}.{m.Name}";
                _handlers.RemoveAll(x => x.Run.Method == m);
                _handlers.Add(new Handler { Run = handler, State = state, Stage = stage, Order = _order++, Priority = priority, List = list, Owner = StateController.Instance, Name = name, Mod = Short(mod) });
                if (_told.Add(name)) HitCamMod.Log.Msg($"Vehicle mods: {name} converts the mission's vehicles as it starts; the proving ground's and the Armor Sim's get it too");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: " + e.Message); }
        }

        /// static Vehicle[] the handler walks (read only, the thing that fills it isn't a conversion)
        static FieldInfo VehicleList(MethodInfo m)
        {
            var sm = m.GetCustomAttribute<IteratorStateMachineAttribute>();
            MethodBase body = sm != null ? AccessTools.Method(sm.StateMachineType, "MoveNext") : m;
            if (body == null) return null;
            FieldInfo found = null;
            foreach (var ins in PatchProcessor.ReadMethodBody(body))
            {
                if (!(ins.Value is FieldInfo f) || !f.IsStatic || f.FieldType != typeof(Vehicle[])) continue;
                if (ins.Key == OpCodes.Stsfld) return null;
                if (ins.Key == OpCodes.Ldsfld) found = f;
            }
            return found;
        }

        /// noted in this mission (its state controller), not a past one
        static bool Live(Handler h) => h.Owner != null && h.Owner == SceneUtil.States;

        public static bool Any => _handlers.Any(Live);

        /// mods are done with the mission's vehicles (game's last startup state is through), or there are none in this
        /// mission. half a minute max in case the game never gets there
        public static bool MissionConverted
        {
            get
            {
                try
                {
                    var sc = SceneUtil.States;
                    if (sc == null || _convertedFor == sc) return true;
                    if (!_handlers.Any(Live)) return true;
                    if (StateController.IsGameStateComplete(GameState.GameReady)) return true;
                    if (_waitSince < 0f) _waitSince = Time.realtimeSinceStartup;
                    return Time.realtimeSinceStartup - _waitSince > 30f;
                }
                catch { return true; }
            }
        }

        /// once a mission, when the mods are through: anything read off a vehicle before that gets dropped
        public static void Update()
        {
            WatchMission();
            var sc = SceneUtil.States;
            if (sc == null || _convertedFor == sc || !_handlers.Any(Live) || !MissionConverted) return;
            _convertedFor = sc;
            _waitSince = -1f;
            int n = 0;
            try
            {
                foreach (var x in SceneUnitsManager.Instance.AllUnitsInScene)
                    if (x is Unit u && u != null) { Warmup.Forget(u); n++; }
            }
            catch { }
            HitCamMod.Log.Msg($"Vehicle mods: done with the mission's vehicles ({n} units looked at again)");
            MissionConversions();
        }

        /// a vehicle we spawned (after its Start): noted conversions in the game's order, each mod's list holding just this
        /// vehicle. one vehicle at a time (lists are shared)
        static readonly Dictionary<string, Material> _eraShared = new Dictionary<string, Material>();
        static StateController _eraSharedFor;

        /// ERA from vehicle mods is one object per brick and every brick ends up with its own material copy ("K1 (Instance)":
        /// 1938 bricks on 836 materials on a modded proving ground). unity can't batch/instance stuff that doesn't share a
        /// material, so each brick was its own draw call: 127 fps vs ~170 in the plain game. bricks with identical materials
        /// now share one (instanced): ~172 fps, same picture. a brick a mod changes later via .material gets its own copy back
        internal static void ShareEraMaterials(Unit u)
        {
            if (u == null || (AutoTest.Active && AutoTest.Opt("era_own_materials", false))) return;
            try
            {
                var sc = SceneUtil.States;
                if (!ReferenceEquals(sc, _eraSharedFor)) { _eraSharedFor = sc; _eraShared.Clear(); }
                var bricks = EraBricks(u, out int pieces);
                int n = 0;
                foreach (var r in bricks)
                {
                    var ms = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < ms.Length; i++)
                    {
                        var m = ms[i];
                        if (m == null || m.shader == null) continue;
                        string key = m.shader.name + "|" + m.ComputeCRC() + "|" + m.renderQueue;
                        if (!_eraShared.TryGetValue(key, out var s) || s == null) { s = new Material(m) { name = m.name.Replace(" (Instance)", "") + " (shared)", enableInstancing = true }; _eraShared[key] = s; }
                        if (s != m) { ms[i] = s; changed = true; }
                    }
                    if (changed) { r.sharedMaterials = ms; n++; }
                }
                if (bricks.Count > 0 && Settings.Verbose) HitCamMod.Log.Msg($"Vehicle mods: {n} of {Recorder.SafeName(u)}'s {bricks.Count} ERA bricks ({pieces} ERA armour pieces) now share materials ({_eraShared.Count} shared on the range so far)");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: ERA materials: " + e.Message); }
        }

        /// drawn ERA bricks of a vehicle: its small mesh renderers sitting where one of its ERA armour colliders is
        internal static List<MeshRenderer> EraBricks(Unit u, out int pieces)
        {
            var bricks = new List<MeshRenderer>();
            pieces = 0;
            if (u == null) return bricks;
            var roots = GhostSnapshot.Roots(u);
            var era = new List<Vector3>();
            foreach (var root in roots)
                if (root != null)
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                        if (c != null && c.TryGetComponent(out GHPC.IArmor a) && a.IsEra) era.Add(c.bounds.center);
            pieces = era.Count;
            if (era.Count == 0) return bricks;
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r == null) continue;
                    var b = r.bounds;
                    if (b.size.sqrMagnitude > 1.5f * 1.5f) continue;
                    foreach (var p in era)
                        if ((b.center - p).sqrMagnitude < 0.03f * 0.03f) { bricks.Add(r); break; }
                }
            }
            return bricks;
        }

        static StateController _ammoFor;
        static readonly List<UnityEngine.Object> _keptAmmo = new List<UnityEngine.Object>();

        /// mods find some of what their conversions use by scanning what's loaded (FEP's Leopard 2K looks for its 20 mm ammo
        /// with Resources.FindObjectsOfTypeAll) and the game drops that once nothing holds it: a vehicle spawned later got
        /// "[RH202] Required 20mm ammo codex entries are missing". so every ammo entry loaded when the mods are done
        /// (PlayerReady, last) is held for the mission
        static void WatchMission()
        {
            var sc = SceneUtil.States;
            if (sc == null || ReferenceEquals(_ammoFor, sc)) return;
            _ammoFor = sc;
            _keptAmmo.Clear();
            try { StateController.RunOrDefer(GameState.PlayerReady, KeepAmmo, GameStatePriority.Lowest); }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: ammo entries: " + e.Message); }
        }

        static string AmmoProbe()
        {
            var have = new HashSet<string>(Resources.FindObjectsOfTypeAll<GHPC.Weaponry.AmmoCodexScriptable>().Where(c => c != null && c.AmmoType != null).Select(c => c.name));
            return "; 20 mm: " + string.Join(", ", new[] { "ammo_20mm_DM43", "ammo_20mm_DM51A2", "ammo_20mm_DM63", "ammo_20mm_DM81" }.Select(n => n.Substring(10) + (have.Contains(n) ? " yes" : " no")));
        }

        static IEnumerator KeepAmmo(GameState _)
        {
            if (_handlers.Any(Live))
            {
                _keptAmmo.AddRange(Resources.FindObjectsOfTypeAll<GHPC.Weaponry.AmmoCodexScriptable>());
                HitCamMod.Log.Msg($"Vehicle mods: holding the {_keptAmmo.Count} ammo entries loaded now, for conversions of vehicles spawned later" + (AutoTest.Active ? AmmoProbe() : ""));
            }
            yield break;
        }

        public static IEnumerator Convert(Unit u)
        {
            var v = u as Vehicle;
            if (v == null) yield break;
            var run = _handlers.Where(Live).OrderBy(h => h.Stage).ThenBy(h => h.Priority).ThenBy(h => h.Order).ToList();
            if (run.Count == 0) yield break;
            if (AutoTest.Active) HitCamMod.Log.Msg("Vehicle mods: before converting " + Recorder.SafeName(u) + AmmoProbe());
            float t0 = Time.realtimeSinceStartup;
            while (_busy && Time.realtimeSinceStartup - t0 < 10f) yield return null;
            if (v == null) yield break;
            _busy = true;
            LastFailures.Clear();
            string before = Friendly(v);
            string key = "";
            try { key = v.UniqueName ?? ""; } catch { }
            string changer = null;
            foreach (var h in run)
            {
                if (v == null) break;
                string was = Friendly(v);
                Vehicle[] old = null;
                IEnumerator e = null;
                try
                {
                    old = h.List.GetValue(null) as Vehicle[];
                    h.List.SetValue(null, new[] { v });
                    e = h.Run(h.State);
                }
                catch (Exception ex) { Failed(h, before, ex); }
                // their coroutine, stepped here: whatever it waits for we wait for
                while (e != null)
                {
                    object cur;
                    try
                    {
                        if (!e.MoveNext()) break;
                        cur = e.Current;
                    }
                    catch (Exception ex) { Failed(h, before, ex); break; }
                    yield return cur;
                }
                try { h.List.SetValue(null, old); } catch { }
                if (v != null && Friendly(v) != was && changer == null) changer = h.Mod;
            }
            _busy = false;
            // what this key becomes with the mods (tools' vehicle lists show it, kept for the next game start too)
            if (v != null && key.Length > 0) Remember(key, Friendly(v) != before ? Friendly(v) : null, changer);
            if (u == null) yield break;
            Warmup.Forget(u);
            string after = Friendly(v);
            HitCamMod.Log.Msg(after != before ? $"Vehicle mods: the {before} we spawned is now a {after}" : $"Vehicle mods: the {before} we spawned went through {run.Count} conversion(s), unchanged");
        }

        static void Failed(Handler h, string vehicle, Exception ex)
        {
            LastFailures.Add(h.Name);
            HitCamMod.Log.Warning($"Vehicle mods: {h.Name} failed on the {vehicle} ({ex.GetType().Name}{(string.IsNullOrEmpty(ex.Message) ? "" : ": " + ex.Message)})"
                + "; a mod that gets its parts ready only for the vehicles in the mission at its start can't convert one spawned later");
        }

        /// for a spawn that can't wait: conversions two frames later (after the vehicle's Start), then `then`
        public static void After(Unit u, Action then)
        {
            if (u == null) return;
            if (!_handlers.Any(Live)) { then?.Invoke(); return; }
            MelonCoroutines.Start(AfterFlow(u, then));
        }

        static IEnumerator AfterFlow(Unit u, Action then)
        {
            yield return null;
            yield return null;
            if (u == null) yield break;
            yield return Convert(u);
            if (u == null) yield break;
            try { then?.Invoke(); }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: after converting " + Recorder.SafeName(u) + ": " + e.Message); }
        }

        /// conversions that failed on the last vehicle we spawned (mod: class.method)
        public static readonly List<string> LastFailures = new List<string>();

        /// some mods prep their parts (sights, rounds, donor pieces) only for vehicle types in the mission at start (PIL asks
        /// the game's spawner which prefabs it has loaded). a range that will be built here gets those prefabs loaded into
        /// the spawner early, after the mission's own units and before the mods look, so its vehicles count as the mission's
        public static void PreloadForMission(Func<IEnumerable<string>> keys, string what)
        {
            try
            {
                var sc = SceneUtil.States;
                if (sc == null || _preloadFor == sc) return;
                var status = StateController.RunOrDefer(GameState.MissionUnitsLoaded, _ => PreloadNow(keys, what), GameStatePriority.Lowest);
                if (status != GameStateInvocationStatus.Fail) _preloadFor = sc;
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: preload: " + e.Message); }
        }

        static StateController _preloadFor;

        static IEnumerator PreloadNow(Func<IEnumerable<string>> keys, string what)
        {
            var sp = GHPC.Mission.UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<GHPC.Mission.UnitSpawner>();
            if (sp == null) yield break;
            var done = new List<string>();
            float t0 = Time.realtimeSinceStartup;
            foreach (var key in keys().Where(k => !string.IsNullOrEmpty(k)).Distinct())
            {
                try
                {
                    _prefabRef = _prefabRef ?? AccessTools.Method(typeof(GHPC.Mission.UnitSpawner), "GetPrefabByUniqueName");
                    _loadedUnits = _loadedUnits ?? AccessTools.Field(typeof(GHPC.Mission.UnitSpawner), "_loadedUnits");
                    var r = _prefabRef?.Invoke(sp, new object[] { key });
                    var loaded = _loadedUnits?.GetValue(sp) as IList;
                    if (r == null || loaded == null || loaded.Contains(r)) continue;
                    // what the spawner does itself on a first spawn of that key
                    var load = r.GetType().GetMethods().FirstOrDefault(m => m.Name == "LoadAssetAsync" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    var handle = load?.MakeGenericMethod(typeof(GameObject)).Invoke(r, null);
                    handle?.GetType().GetMethod("WaitForCompletion")?.Invoke(handle, null);
                    loaded.Add(r);
                    done.Add(key);
                }
                catch (Exception e) { HitCamMod.Log.Warning($"Vehicle mods: preload {key}: {e.InnerException?.Message ?? e.Message}"); }
            }
            if (done.Count > 0) HitCamMod.Log.Msg($"Vehicle mods: {done.Count} vehicle type(s) of the {what} loaded with the mission, so the mods get them ready too ({Time.realtimeSinceStartup - t0:0.0} s): {string.Join(", ", done)}");
        }

        static MethodInfo _prefabRef;
        static FieldInfo _loadedUnits;

        /// before we spawn `key`: a mod may have let go of that prefab while the game's spawner still counts it loaded
        /// (VanillaAssetUtil drops the prefabs it loaded "temporarily" once the mission is up, the same ones the spawner
        /// holds), then the spawner hands on nothing and every later spawn of it failed. so it gets loaded again
        public static void BeforeSpawn(GHPC.Mission.UnitSpawner sp, string key)
        {
            try
            {
                if (sp == null || string.IsNullOrEmpty(key)) return;
                _prefabRef = _prefabRef ?? AccessTools.Method(typeof(GHPC.Mission.UnitSpawner), "GetPrefabByUniqueName");
                _loadedUnits = _loadedUnits ?? AccessTools.Field(typeof(GHPC.Mission.UnitSpawner), "_loadedUnits");
                var r = _prefabRef?.Invoke(sp, new object[] { key });
                var loaded = _loadedUnits?.GetValue(sp) as IList;
                if (r == null || loaded == null || !loaded.Contains(r)) return;
                var asset = AccessTools.Property(r.GetType(), "Asset")?.GetValue(r) as UnityEngine.Object;
                if (asset != null) return;
                // released (or destroyed then released): off the spawner's list so it loads it as new
                if (AccessTools.Method(r.GetType(), "IsValid")?.Invoke(r, null) is bool valid && valid) AccessTools.Method(r.GetType(), "ReleaseAsset")?.Invoke(r, null);
                loaded.Remove(r);
                HitCamMod.Log.Msg($"Vehicle mods: the {key} prefab was let go by another mod while the game still counted it as loaded: loading it again");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: prefab check for " + key + ": " + e.Message); }
        }

        static readonly HashSet<string> _refetched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static FieldInfo _refOp;

        /// spawns a vehicle by its unit-table key like the game does, once its prefab is really there. with vehicle mods a
        /// prefab can be missing while the game counts it loaded (another mod borrowed it and let go, BeforeSpawn), or come
        /// back empty from the game's own load when the mission never had that vehicle: FEP's spawn hook then failed on it
        /// and half the proving ground (Ctrl + F4 in a mission) never came. such a prefab gets loaded again on its own handle,
        /// given to the game's reference (game lets it go with the mission)
        public static GHPC.IUnit Spawn(GHPC.Mission.UnitSpawner sp, string key, GHPC.Mission.Data.UnitMetaData md, bool sound = true)
        {
            BeforeSpawn(sp, key);
            EnsurePrefab(sp, key);
            HashSet<Unit> before = null;
            if (!string.IsNullOrEmpty(md?.Name))
                try { before = new HashSet<Unit>(UnityEngine.Object.FindObjectsOfType<Unit>().Where(u => u != null && u.gameObject.name == md.Name)); } catch { }
            Ours++;
            try
            {
                var iu = sp.SpawnUnit(key, md, (GHPC.AI.WaypointHolder)null, (Transform)null);
                AfterSpawn(iu as Unit, sound);
                return iu;
            }
            catch (Exception e)
            {
                // another mod's spawn hook failing after the vehicle was made (seen with several mods: test range lost half its
                // targets, which stood there anyway, untracked and fighting): vehicle is there, only the call threw. so it counts
                // as spawned, error goes to the log once per vehicle
                Unit made = null;
                if (before != null)
                    try { made = UnityEngine.Object.FindObjectsOfType<Unit>().FirstOrDefault(u => u != null && u.gameObject.name == md.Name && !before.Contains(u)); } catch { }
                if (_spawnHookErrs.Add(key))
                    HitCamMod.Log.Warning($"Spawning {key}: {(made != null ? "the vehicle came, but a hook on the game's spawn failed" : "the spawn failed")} " +
                        $"(the code it failed in is at the top of this, often another mod's): {e}");
                if (made != null) { AfterSpawn(made, sound); return made; }
                throw;
            }
            finally { Ours--; }
        }

        /// game's sound on a vehicle made mid-mission starts before its wheels are set up and would throw every frame:
        /// parked right away, started again two frames later (sound: false = stays off, the Armor Sim's test vehicle)
        static void AfterSpawn(Unit u, bool sound)
        {
            if (u == null) return;
            ProvingGround.ParkBrokenSound(u);
            if (sound) MelonCoroutines.Start(WakeLater(u));
        }

        static IEnumerator WakeLater(Unit u)
        {
            yield return null;
            yield return null;
            if (u != null) ProvingGround.WakeAudio(u);
        }

        static readonly HashSet<string> _spawnHookErrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void EnsurePrefab(GHPC.Mission.UnitSpawner sp, string key)
        {
            try
            {
                var r = sp.GetPrefabByUniqueName(key);
                _loadedUnits = _loadedUnits ?? AccessTools.Field(typeof(GHPC.Mission.UnitSpawner), "_loadedUnits");
                var loaded = _loadedUnits?.GetValue(sp) as List<AssetReference>;
                if (r == null || loaded == null) return;
                if (!loaded.Contains(r))
                {
                    // what the game's SpawnUnit(key) does first
                    loaded.Add(r);
                    if (!r.IsValid()) r.LoadAssetAsync<GameObject>().WaitForCompletion();
                }
                if (r.Asset is GameObject g && g != null) return;
                var op = r.IsValid() ? r.OperationHandle : default;
                string was = !op.IsValid() ? "no handle" : $"handle {op.Status}{(op.OperationException != null ? " (" + op.OperationException.Message + ")" : "")}, asset {((object)r.Asset == null ? "none" : r.Asset is GameObject ? "destroyed" : r.Asset.GetType().Name)}";
                var h = Addressables.LoadAssetAsync<GameObject>(r.RuntimeKey);
                var go = h.WaitForCompletion();
                if (go == null)
                {
                    HitCamMod.Log.Warning($"Vehicle mods: the {key} prefab is missing ({was}) and didn't load again either ({h.Status})");
                    if (h.IsValid()) Addressables.Release(h);
                    return;
                }
                _refOp = _refOp ?? AccessTools.Field(typeof(AssetReference), "m_Operation");
                if (_refOp == null) { Addressables.Release(h); return; }
                _refOp.SetValue(r, (AsyncOperationHandle)h);
                if (_refetched.Add(key)) HitCamMod.Log.Msg($"Vehicle mods: the {key} prefab was missing ({was}): loaded it again");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: prefab load for " + key + ": " + e.Message); }
        }

        static readonly Dictionary<string, (string name, string mod)> _converted = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        static HashSet<string> _modEntries;
        static int _modEntriesOf = -1;

        /// short mod name for a vehicle list: FEP, PIL, else its initials
        static string Short(string mod)
        {
            if (string.IsNullOrEmpty(mod)) return "mod";
            if (mod.IndexOf("Fulda", StringComparison.OrdinalIgnoreCase) >= 0) return "FEP";
            if (mod.IndexOf("Pact Increased", StringComparison.OrdinalIgnoreCase) >= 0) return "PIL";
            var words = mod.Split(new[] { ' ', '|', '[', ']', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).ToList();
            string ini = string.Concat(words.Take(4).Select(w => char.ToUpperInvariant(w[0])));
            return ini.Length >= 2 ? ini : mod.Length > 12 ? mod.Substring(0, 12) : mod;
        }

        /// unit table entry that's a real vehicle to spawn. mods add lookup-only ones too: FEP's binocular fix adds entries
        /// like "FULDA1989_BINOCULAR_T-64AV obr.1984" with an empty prefab ref so the game finds an army by vehicle name,
        /// they're not vehicles so the lists skip them
        public static bool Spawnable(GHPC.Mission.UnitPrefabLookupScriptable.UnitPrefabMetadata m)
        {
            try
            {
                if (m == null || string.IsNullOrEmpty(m.Name)) return false;
                if (m.Name.StartsWith("FULDA1989_BINOCULAR_", StringComparison.OrdinalIgnoreCase)) return false;
                object r = Traverse.Create(m).Field("PrefabReference").GetValue() ?? Traverse.Create(m).Property("PrefabReference").GetValue();
                if (r == null) return false;
                string guid = Traverse.Create(r).Property("AssetGUID").GetValue() as string;
                if (string.IsNullOrEmpty(guid))
                {
                    // in-memory ref with no GUID: only if it already holds its prefab
                    var asset = Traverse.Create(r).Property("Asset").GetValue() as UnityEngine.Object;
                    return asset != null;
                }
                return true;
            }
            catch { return true; }
        }

        /// vehicle lists offer the same units as the game's own mission customizer (entries marked AllowInCustomizer)
        public static bool Released(GHPC.Mission.UnitPrefabLookupScriptable.UnitPrefabMetadata m)
        {
            try { return m != null && m.AllowInCustomizer; }
            catch { return false; }
        }

        /// unit table entries a vehicle mod added. FEP puts its Leopard 2K in as a second entry on the Leopard 1A4's own
        /// prefab, so an entry on a prefab an earlier entry already has is a mod's
        public static bool IsModEntry(string key)
        {
            try
            {
                var sp = GHPC.Mission.UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<GHPC.Mission.UnitSpawner>();
                var all = sp?.PrefabLookup?.AllUnits;
                if (all == null) return false;
                if (_modEntries == null || _modEntriesOf != all.Length)
                {
                    _modEntriesOf = all.Length;
                    _modEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var seen = new HashSet<string>();
                    foreach (var m in all)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Name)) continue;
                        object r = Traverse.Create(m).Field("PrefabReference").GetValue() ?? Traverse.Create(m).Property("PrefabReference").GetValue();
                        if (r == null) continue;
                        string id = Traverse.Create(r).Property("RuntimeKey").GetValue()?.ToString();
                        if (string.IsNullOrEmpty(id)) id = "ref" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r);
                        if (!seen.Add(id)) _modEntries.Add(m.Name);
                    }
                }
                return _modEntries.Contains(key);
            }
            catch { return false; }
        }

        /// what a vehicle list shows after a vehicle's name: " [mod]" for one a mod added, " [PIL > T-80U]" for a stock one
        /// a mod turns into something else (seen on a spawn, this game start or an earlier one)
        public static string Tag(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            Load();
            bool added = IsModEntry(key);
            bool conv = _converted.TryGetValue(key, out var c) && ModLoaded(c.mod);
            if (conv) return $"  [{c.mod} > {c.name}]";
            return added ? "  [mod]" : "";
        }

        /// a mod's vehicle, or a stock one a loaded mod changes: drawn in this colour in the lists
        public static bool IsModded(string key) => !string.IsNullOrEmpty(key) && (IsModEntry(key) || (_converted.TryGetValue(key, out var c) && ModLoaded(c.mod)));
        public static readonly Color TagColor = new Color(1f, 0.72f, 0.4f, 1f);

        /// for the Armor Sim's mission: vehicle types the vehicle mods are known to change, and the usual ones
        public static IEnumerable<string> ArmorSimKeys()
        {
            Load();
            var keys = new List<string> { "M1IP" };
            keys.AddRange(ProvingGround.TargetKeys);
            keys.AddRange(_converted.Where(kv => ModLoaded(kv.Value.mod)).Select(kv => kv.Key));
            return keys.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        static string SavePath => System.IO.Path.Combine(HitCamMod.DataDir, "vehicle_mods.txt");
        static bool _loaded;

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!System.IO.File.Exists(SavePath)) return;
                foreach (var line in System.IO.File.ReadAllLines(SavePath))
                {
                    var f = line.Split('\t');
                    if (f.Length >= 3 && f[0].Length > 0 && !_converted.ContainsKey(f[0])) _converted[f[0]] = (f[1], f[2]);
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: " + SavePath + ": " + e.Message); }
        }

        /// a key's conversion as seen now (null: stayed itself, so whatever was known goes), kept in the file
        static void Remember(string key, string name, string mod)
        {
            Load();
            bool had = _converted.TryGetValue(key, out var old);
            if (name == null) { if (!had) return; _converted.Remove(key); }
            else
            {
                string m = mod ?? (had ? old.mod : "mod");
                if (had && old.name == name && old.mod == m) return;
                _converted[key] = (name, m);
            }
            try { System.IO.File.WriteAllLines(SavePath, _converted.Select(kv => $"{kv.Key}\t{kv.Value.name}\t{kv.Value.mod}")); }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: " + e.Message); }
        }

        /// mission's own vehicles once the mods are through: one whose name isn't its prefab's anymore got changed by one
        /// of them (so the lists can say so before we ever spawn that type)
        static void MissionConversions()
        {
            try
            {
                var sp = GHPC.Mission.UnitSpawner.Instance ?? UnityEngine.Object.FindObjectOfType<GHPC.Mission.UnitSpawner>();
                if (sp == null) return;
                _prefabRef = _prefabRef ?? AccessTools.Method(typeof(GHPC.Mission.UnitSpawner), "GetPrefabByUniqueName");
                var mods = _handlers.Where(Live).Select(h => h.Mod).Distinct().ToList();
                string guess = mods.Count == 1 ? mods[0] : "mod";
                int n = 0;
                foreach (var x in SceneUnitsManager.Instance.AllUnitsInScene)
                {
                    if (!(x is Vehicle v) || v == null) continue;
                    string key = v.UniqueName;
                    if (string.IsNullOrEmpty(key)) continue;
                    var r = _prefabRef?.Invoke(sp, new object[] { key });
                    var prefab = r != null ? AccessTools.Property(r.GetType(), "Asset")?.GetValue(r) as GameObject : null;
                    var pv = prefab != null ? prefab.GetComponent<Vehicle>() : null;
                    if (pv == null) continue;
                    string was = pv.FriendlyName, now = Friendly(v);
                    if (string.IsNullOrEmpty(was) || was == now) continue;
                    // a mod we already know did it keeps its name
                    Remember(key, now, _converted.TryGetValue(key, out var c) && c.mod != "mod" ? c.mod : guess);
                    n++;
                }
                if (n > 0) HitCamMod.Log.Msg($"Vehicle mods: {n} of the mission's vehicles came out changed by a mod");
            }
            catch (Exception e) { HitCamMod.Log.Warning("Vehicle mods: mission scan: " + e.Message); }
        }

        static HashSet<string> _loadedMods;

        /// a mod (by short name) is running in this game
        static bool ModLoaded(string shortName)
        {
            if (shortName == "mod") return _handlers.Count > 0;
            if (_loadedMods == null)
            {
                _loadedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var m in MelonBase.RegisteredMelons) if (m?.Info != null) _loadedMods.Add(Short(m.Info.Name)); } catch { }
            }
            return _loadedMods.Contains(shortName);
        }

        static string Friendly(Vehicle v)
        {
            try { return v != null ? v.FriendlyName : "?"; } catch { return "?"; }
        }
    }
}
