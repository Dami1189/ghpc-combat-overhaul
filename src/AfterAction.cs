using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GHPC;
using GHPC.Humans;
using GHPC.Player;
using GHPC.State;
using GHPC.Weapons;
using GHPC.World;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GHPCHitCam
{
    /// one hit in the After Action Report
    internal class AarEntry
    {
        public HitRecord Rec;
        public GhostSnapshot Full;          // full x-ray model (live hits; null once released, or for archived hits)
        public AarSchematic Schem;          // same vehicle as boxes (fallback, and what the JSON keeps)
        public GhostSnapshot SchemSnap;     // built from Schem on first use
        public float MissionTime = -1f;     // s since mission start, -1 = unknown
        public float Distance = -1f;
        public bool OnPlayer, Friendly, Archived;
        public bool CreditKill, CreditKo;   // target died / got knocked out later and this was your last damaging hit on it
        public float Added;
        public string StampText;
        string _damage;
        int _damageEvents = -1;

        public float Range => Rec.RootShot != null && Rec.RootShot.Distance > 0f ? Rec.RootShot.Distance : Distance;
        public bool Penetrated => Rec.ArmorHits.Any(a => a.Penetrated && !a.Era);
        public bool Killed => Rec.TargetKilled || (Rec.RootShot != null && Rec.RootShot.IsKillShot) || CreditKill;
        public bool KnockedOut => (Rec.TargetKnockedOut || CreditKo) && !Killed;

        public string Damage
        {
            get
            {
                if (_damageEvents != Rec.Events.Count) { _damage = AfterAction.DamageSummary(Rec); _damageEvents = Rec.Events.Count; }
                return _damage;
            }
        }
    }

    /// everything recorded in one mission (or loaded back from its folder)
    internal class AarMission
    {
        public string Name = "Mission", Folder, Result, PlayerName;
        public bool Archived;
        public AarMissionDto Info;
        public DateTime Started = DateTime.Now;
        public float StartTime, EndTime = -1f, ArchivedDuration;
        public Faction Faction = Faction.Blue;
        public readonly List<AarEntry> Entries = new List<AarEntry>();
        public int Shots, Incoming, Unplayable;   // Unplayable: old hit files saved without paths
        public readonly Dictionary<string, int> ShotsByAmmo = new Dictionary<string, int>();
        public int CrewLostFriendly = -1, CrewLostEnemy = -1, UnitsLostFriendly = -1, UnitsLostEnemy = -1;

        public float Duration => Archived ? ArchivedDuration : (EndTime >= 0f ? EndTime : Time.time) - StartTime;
    }

    internal class AarStats
    {
        public int Shots, Incoming, Hits, Pens, Kills, KnockOuts, FriendlyHits, HitsTaken, PensTaken;
        public float Accuracy = -1f, AvgRange, MaxRange;
        public int CrewLostFriendly = -1, CrewLostEnemy = -1, UnitsLostFriendly = -1, UnitsLostEnemy = -1;
        public readonly List<string> ModulesLost = new List<string>(), ModulesDamaged = new List<string>(), CrewKilled = new List<string>(), CrewWounded = new List<string>();
        public readonly List<(string name, int hits, int pens, string result, Color color)> Targets = new List<(string, int, int, string, Color)>();
        public readonly List<(string ammo, int fired, int hits, int pens)> ByAmmo = new List<(string, int, int, int)>();
    }

    /// After Action Report: every recorded hit of the mission with its 3D replay, mission stats, past missions
    /// core stuff here (recording, mission bookkeeping, stats, pause, model lifetime), the panel is in AarView.cs
    internal static partial class AfterAction
    {
        public static MelonPreferences_Entry<bool> Enabled;
        public static MelonPreferences_Entry<string> Key;
        public static MelonPreferences_Entry<bool> PauseGame;
        public static MelonPreferences_Entry<int> FullModels;

        static AarMission _mission;     // mission being played
        static AarMission _last;        // the one before (still browsable from main menu)
        static object _sumRef;          // mission's SceneUnitsManager: new one = new mission
        static readonly Dictionary<HitRecord, AarEntry> _byRec = new Dictionary<HitRecord, AarEntry>();
        static float _hintUntil = -1f, _factionCheck, _missionCheck;
        static string _hint;
        static bool _wePaused, _cursorVisible;
        static CursorLockMode _cursorLock;

        public static bool Recording => Enabled != null && Enabled.Value;

        public static void Init()
        {
            var cat = Settings.Cat;
            Enabled = cat.CreateEntry("AarEnabled", true, description: "After Action Report: keeps every hit of the mission (yours and the ones on you) for 3D replays and stats.");
            Key = cat.CreateEntry("AarKey", "F2", description: "Opens and closes the After Action Report.");
            PauseGame = cat.CreateEntry("AarPauseGame", true, description: "Pause the game while the report is open.");
            FullModels = cat.CreateEntry("AarFullModels", 30, description: "How many of the newest hits keep the full x-ray model for the replays; older ones replay on a box model.");

            Application.quitting += () => { try { if (_mission != null) SaveAll(_mission); } catch { } };
        }

        public static void Install(HarmonyLib.Harmony h)
        {
            Patch(h, AccessTools.Method(typeof(LiveRound), "Init"), nameof(RoundInitPostfix));
            Patch(h, AccessTools.Method(typeof(ScenarioResultManager), "ResolveWin"), nameof(ResolveWinPostfix));
            Patch(h, AccessTools.Method(typeof(AarController), "StartAar"), nameof(GameAarPostfix));
        }

        static void Patch(HarmonyLib.Harmony h, MethodBase target, string postfix)
        {
            if (target == null) { HitCamMod.Log.Warning("AAR: patch target for " + postfix + " not found"); return; }
            try { h.Patch(target, null, new HarmonyMethod(typeof(AfterAction).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic))); }
            catch (Exception e) { HitCamMod.Log.Error("AAR: failed to patch " + target.Name + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ recording

        /// Recorder asks: keep this hit? own hits always; with PlayerShotsOnly the others only if they hit you
        public static bool Accept(HitRecord rec, Unit target)
        {
            if (rec.PlayerShot || !Settings.PlayerShotsOnly.Value) return true;
            return Recording && IsPlayerUnit(target);
        }

        static bool IsPlayerUnit(Unit u)
        {
            try { var pi = SceneUtil.Player; return u != null && pi != null && pi.CurrentPlayerUnit == u; }
            catch { return false; }
        }

        /// counts every round fired (hits, misses): your shots and the ones aimed at you
        static void RoundInitPostfix(LiveRound __instance)
        {
            try
            {
                if (!Recording || __instance.ParentRound != null) return;
                var shot = __instance.ShotInfo;
                var ammo = __instance.Info;
                if (shot == null || ammo == null) return;
                if (Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) < Settings.RecordMinCaliberMm) return;
                var m = Current();
                if (m == null) return;
                if (shot.IsPlayerShot)
                {
                    m.Shots++;
                    string n = ammo.Name ?? "?";
                    m.ShotsByAmmo[n] = (m.ShotsByAmmo.TryGetValue(n, out int c) ? c : 0) + 1;
                }
                else if (shot.PlayerTargeted) m.Incoming++;
            }
            catch (Exception e) { HitCamMod.Log.Error("AAR shot count: " + e.Message); }
        }

        /// hit is done (Timeline built): keep it for the report
        public static void OnRecordReady(HitRecord rec)
        {
            if (!Recording || rec == null || rec.Snapshot == null) return;
            try
            {
                var m = Current();
                if (m == null || _byRec.ContainsKey(rec)) return;
                var e = new AarEntry
                {
                    Rec = rec, Full = rec.Snapshot, Added = Time.unscaledTime,
                    MissionTime = rec.FirstImpactReal - m.StartTime, OnPlayer = IsPlayerUnit(rec.Target),
                    Schem = Schematic.Capture(rec.Snapshot),
                };
                try { var pu = SceneUtil.Player?.CurrentPlayerUnit; e.Friendly = rec.PlayerShot && rec.Target != null && pu != null && rec.Target != pu && rec.Target.Allegiance == pu.Allegiance; } catch { }
                m.Entries.Add(e);
                _byRec[rec] = e;
                ReleaseOld(m);
            }
            catch (Exception ex) { HitCamMod.Log.Error("AAR: could not keep hit: " + ex); }
        }

        public static AarEntry EntryOf(HitRecord rec) => rec != null && _byRec.TryGetValue(rec, out var e) ? e : null;

        /// for the JSON: hit's vehicle as boxes
        public static AarSchematic SchematicOf(HitRecord rec)
        {
            var e = EntryOf(rec);
            if (e?.Schem != null) return e.Schem;
            try { return Schematic.Capture(rec.Snapshot); } catch { return null; }
        }

        static bool _releaseLogged;

        /// full models own baked meshes (crew poses, skinned parts): keep newest N, older hits replay on boxes
        static void ReleaseOld(AarMission m)
        {
            int cap = Mathf.Max(4, FullModels.Value);
            var full = m.Entries.Where(x => x.Full != null).ToList();
            if (full.Count <= cap) return;
            var playing = HitCamPlayer.Current?.Records.ToList() ?? new List<HitRecord>();
            var old = full.Take(full.Count - cap).ToList();
            // autocannon burst hits share one copy of the target: freed only when none that keeps it still uses it
            var kept = new HashSet<GhostSnapshot>(full.Skip(full.Count - cap).Select(x => x.Full));
            foreach (var e in old)
                if (Time.unscaledTime - e.Added < 60f || playing.Contains(e.Rec) || (_replay != null && _replay.Entry == e)) kept.Add(e.Full);
            foreach (var e in old)
            {
                if (e.Full == null || kept.Contains(e.Full)) continue;   // (already let go with another hit of its burst)
                var snap = e.Full;
                foreach (var o in old) if (o.Full == snap) o.Full = null;
                int v = Schematic.Release(snap);
                if (!_releaseLogged) { _releaseLogged = true; HitCamMod.Log.Msg($"AAR: hit #{e.Rec.Id} now replays on its box model (freed {v} baked vertices); AarFullModels = {cap}"); }
            }
        }

        /// record a replay plays: the original with its full model if still intact, else a copy on boxes
        internal static HitRecord ReplayRecord(AarEntry e, out bool schematic)
        {
            schematic = e.Archived;
            if (e.Archived) return e.Rec;
            if (e.Full != null && !Schematic.Broken(e.Full)) return e.Rec;
            schematic = true;
            if (e.SchemSnap == null)
            {
                Bounds b = e.Rec.Snapshot != null ? e.Rec.Snapshot.LocalBounds : new Bounds();
                e.SchemSnap = Schematic.ToSnapshot(e.Schem, b);
                HitCamMod.Log.Msg($"AAR: hit #{e.Rec.Id} ({e.Rec.TargetName}): " + (e.Full == null ? "full model released" : "game meshes gone") + ", replaying on the box model");
            }
            var r = new HitRecord
            {
                Id = e.Rec.Id, RootShot = e.Rec.RootShot, Target = e.Rec.Target, TargetName = e.Rec.TargetName, ShooterName = e.Rec.ShooterName,
                PlayerShot = e.Rec.PlayerShot, Ammo = e.Rec.Ammo, FirstImpactReal = e.Rec.FirstImpactReal, CreatedReal = e.Rec.CreatedReal,
                LastActivityReal = e.Rec.LastActivityReal, W2LAtImpact = e.Rec.W2LAtImpact, L2WAtImpact = e.Rec.L2WAtImpact, Main = e.Rec.Main,
                Snapshot = e.SchemSnap, Ready = true, Saved = true, InteriorEnd = e.Rec.InteriorEnd, CoreEnd = e.Rec.CoreEnd,
                ApproachStart = e.Rec.ApproachStart, EntryL = e.Rec.EntryL, EntryDirL = e.Rec.EntryDirL, HasEntry = e.Rec.HasEntry,
                Title = e.Rec.Title, TitleColor = e.Rec.TitleColor, TargetKilled = e.Rec.TargetKilled, TargetKnockedOut = e.Rec.TargetKnockedOut,
            };
            r.Fragments.AddRange(e.Rec.Fragments);
            r.Events.AddRange(e.Rec.Events);
            r.ArmorHits.AddRange(e.Rec.ArmorHits);
            return r;
        }

        // ------------------------------------------------------------------ mission bookkeeping

        static AarMission Current()
        {
            if (SceneUtil.IsGameplayGone()) return null;
            var sum = SceneUnitsManager.Instance;
            if (_mission == null || !ReferenceEquals(sum, _sumRef))
            {
                if (_mission != null) EndMission();
                StartMission(sum);
            }
            return _mission;
        }

        static void StartMission(object sum)
        {
            _sumRef = sum;
            _byRec.Clear();
            _mission = new AarMission { StartTime = Time.time, Name = MissionName(), Started = DateTime.Now };
            _hintUntil = -1f;
            Archive.NewSession();
            HitCamMod.Log.Msg("AAR: new mission \"" + _mission.Name + "\"");
        }

        static void EndMission()
        {
            var m = _mission;
            if (m == null) return;
            if (m.EndTime < 0f) m.EndTime = Time.time;
            RefreshCasualties(m);
            SaveAll(m);
            _last = m;
            _mission = null;
            HitCamMod.Log.Msg($"AAR: mission \"{m.Name}\" closed: {m.Entries.Count} hits, {m.Shots} shots fired");
        }

        /// HitCamMod: mission scene went away (main menu, restart), its objects are gone: stop the replay, keep the data
        public static void OnGameplayGone()
        {
            try
            {
                StopReplay();
                if (_open) Close();
                EndMission();
            }
            catch (Exception e) { HitCamMod.Log.Error("AAR: OnGameplayGone: " + e); }
        }

        static string MissionName()
        {
            try
            {
                var mds = UnityEngine.Object.FindObjectOfType<MissionDataState>();
                string n = null;
                try { n = mds?.MetaData?.MissionName; } catch { }
                if (string.IsNullOrEmpty(n)) try { n = mds?.InfoData?.Title; } catch { }
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return SceneManager.GetActiveScene().name;
        }

        static void RefreshCasualties(AarMission m)
        {
            try
            {
                var sum = SceneUnitsManager.Instance;
                if (m == null || m.Archived || sum == null) return;
                sum.UpdateCasualtyCountsNow();
                bool blue = m.Faction != Faction.Red;
                m.CrewLostFriendly = blue ? sum.BluePeopleLost : sum.RedPeopleLost;
                m.CrewLostEnemy = blue ? sum.RedPeopleLost : sum.BluePeopleLost;
                m.UnitsLostFriendly = blue ? sum.BlueUnitsLost : sum.RedUnitsLost;
                m.UnitsLostEnemy = blue ? sum.RedUnitsLost : sum.BlueUnitsLost;
            }
            catch (Exception e) { HitCamMod.Log.Warning("AAR: casualty count failed: " + e.Message); }
        }

        /// writes every unsaved hit + mission.json (name, result, shots, losses) into the mission's folder
        static void SaveAll(AarMission m)
        {
            if (m == null || m.Archived || !Archive.Keeping) return;
            try
            {
                foreach (var e in m.Entries) if (!e.Rec.Saved) Archive.Save(e.Rec);
                string dir = Archive.SessionDir(m.Entries.Count > 0 || m.Shots > 0);
                if (dir == null) return;
                m.Folder = dir;
                var d = new AarMissionDto
                {
                    name = m.Name, started = m.Started.ToString("s"), ended = DateTime.Now.ToString("s"), result = m.Result, duration = m.Duration,
                    shots = m.Shots, incoming = m.Incoming, crewLostFriendly = m.CrewLostFriendly, crewLostEnemy = m.CrewLostEnemy,
                    unitsLostFriendly = m.UnitsLostFriendly, unitsLostEnemy = m.UnitsLostEnemy,
                };
                foreach (var kv in m.ShotsByAmmo) { d.ammoNames.Add(kv.Key); d.ammoShots.Add(kv.Value); }
                ResolveKills(m);
                foreach (var e in m.Entries) { if (e.Killed) d.killIds.Add(e.Rec.Id); else if (e.KnockedOut) d.koIds.Add(e.Rec.Id); if (e.Friendly) d.friendlyIds.Add(e.Rec.Id); }
                File.WriteAllText(Path.Combine(dir, "mission.json"), AarJson.Write(d));
            }
            catch (Exception e) { HitCamMod.Log.Warning("AAR: could not save the mission: " + e.Message); }
        }

        // ------------------------------------------------------------------ mission end

        static void ResolveWinPostfix(Faction winningFaction)
        {
            try
            {
                var m = Current();
                if (m == null || m.Result != null) return;
                var pu = SceneUtil.Player?.CurrentPlayerUnit;
                if (pu != null) m.Faction = pu.Allegiance;
                m.Result = winningFaction == m.Faction ? "VICTORY" : "DEFEAT";
                m.EndTime = Time.time;
                RefreshCasualties(m);
                SaveAll(m);
                Hint($"MISSION OVER ({m.Result})  ·  press {Hotkeys.Label(Key)} for the After Action Report: {m.Entries.Count} hit{(m.Entries.Count == 1 ? "" : "s")}, each replayable in 3D", 25f);
                HitCamMod.Log.Msg("AAR: mission over, " + m.Result);
            }
            catch (Exception e) { HitCamMod.Log.Error("AAR: mission end: " + e.Message); }
        }

        static void GameAarPostfix()
        {
            var m = _mission;
            if (m != null && m.Entries.Count > 0) Hint($"Press {Hotkeys.Label(Key)} for the After Action Report ({m.Entries.Count} hit{(m.Entries.Count == 1 ? "" : "s")} in 3D)", 12f);
        }

        static void Hint(string text, float seconds)
        {
            _hint = text;
            _hintUntil = Time.unscaledTime + seconds;
        }

        // ------------------------------------------------------------------ open / close

        public static bool IsOpen => _open;
        static bool _open;

        public static void Open()
        {
            if (_open) return;
            _open = true;
            HitCamPlayer.Suspend();
            _shown = _mission ?? _last;
            if (_shown != null && _shown == _mission) RefreshCasualties(_mission);
            _view = _shown != null ? View.Hits : View.Past;
            _scroll = 0f;
            _statsFor = null;
            _cursorVisible = Cursor.visible;
            _cursorLock = Cursor.lockState;
            _hintUntil = -1f;
            _wePaused = false;
            if (PauseGame.Value) TryPause();
            HitCamMod.Log.Msg($"AAR opened: {(_shown != null ? _shown.Entries.Count + " hits of \"" + _shown.Name + "\"" : "no mission")}, game paused by us: {_wePaused}");
        }

        /// game's own pause (TimeController: time scale 0, audio muted, gunner and camera input off), only in a running
        /// mission and not inside the game's AAR (that one freezes the world itself)
        static void TryPause()
        {
            try
            {
                if (SceneUtil.IsGameplayGone() || AarController.InAar) return;
                if (TimeController.Instance == null || TimeController.Paused) return;
                TimeController.Pause();
                _wePaused = TimeController.Paused;
            }
            catch (Exception e) { HitCamMod.Log.Warning("AAR: could not pause: " + e.Message); }
        }

        public static void Close()
        {
            if (!_open) return;
            StopReplay();
            _open = false;
            // hits of finished missions it showed get dropped (Past missions kept), a running mission keeps its own
            bool running = false;
            try { var m = Current(); running = m != null && m.Result == null && SceneUtil.Player != null && SceneUtil.Player.CurrentPlayerUnit != null; } catch { }
            Archive.Prune(running);
            if (_wePaused)
            {
                try { if (TimeController.Instance != null && TimeController.Paused) TimeController.Unpause(); } catch { }
                _wePaused = false;
            }
            Cursor.visible = _cursorVisible;
            Cursor.lockState = _cursorLock;
        }

        // ------------------------------------------------------------------ per frame

        public static void Update()
        {
            if (Enabled == null) return;
            try
            {
                // outside a mission SceneUnitsManager.Instance does FindObjectOfType every call: only look twice a second
                if (_mission != null || Time.unscaledTime >= _missionCheck)
                {
                    if (_mission != null && SceneUtil.IsGameplayGone()) EndMission();
                    if (Recording) Current();
                    if (_mission == null) _missionCheck = Time.unscaledTime + 0.5f;
                }
                var m = _mission;
                if (m != null && Time.unscaledTime > _factionCheck)
                {
                    _factionCheck = Time.unscaledTime + 1f;
                    var pu = SceneUtil.Player?.CurrentPlayerUnit;
                    if (pu != null) { m.Faction = pu.Allegiance; m.PlayerName = Recorder.SafeName(pu); }
                }
                // works in the game's own AAR too: the hint there says to press it (game's AAR keys are F5-F8, no clash)
                if (Hotkeys.Down(Key, evenInGameAar: true)) { if (_open) Close(); else Open(); }
                if (!_open) return;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                ViewUpdate();
            }
            catch (Exception e) { HitCamMod.Log.Error("AAR update: " + e); }
        }

        public static void LateUpdate()
        {
            if (!_open) return;
            try { ViewLateUpdate(); }
            catch (Exception e) { HitCamMod.Log.Error("AAR camera: " + e); StopReplay(); }
        }

        // ------------------------------------------------------------------ stats

        public static AarStats Stats(AarMission m)
        {
            var s = new AarStats
            {
                Shots = m.Shots, Incoming = m.Incoming, CrewLostFriendly = m.CrewLostFriendly, CrewLostEnemy = m.CrewLostEnemy,
                UnitsLostFriendly = m.UnitsLostFriendly, UnitsLostEnemy = m.UnitsLostEnemy,
            };
            if (!m.Archived) ResolveKills(m);
            var mine = m.Entries.Where(e => e.Rec.PlayerShot).ToList();
            s.Hits = mine.Count;
            s.Pens = mine.Count(e => e.Penetrated);
            s.Kills = mine.Count(e => e.Killed && !e.Friendly);
            s.KnockOuts = mine.Count(e => e.KnockedOut && !e.Friendly);
            s.FriendlyHits = mine.Count(e => e.Friendly);
            if (s.Shots > 0) s.Accuracy = Mathf.Clamp01(s.Hits / (float)s.Shots);
            var ranges = mine.Select(e => e.Range).Where(r => r > 0f).ToList();
            if (ranges.Count > 0) { s.AvgRange = ranges.Average(); s.MaxRange = ranges.Max(); }

            foreach (var g in mine.GroupBy(e => e.Archived || ReferenceEquals(e.Rec.Target, null) ? (object)e.Rec.TargetName : e.Rec.Target))
            {
                var list = g.ToList();
                var worst = list.OrderByDescending(e => Timeline.Rank(e.Rec.Title)).First().Rec;
                s.Targets.Add((list[0].Rec.TargetName, list.Count, list.Count(e => e.Penetrated), worst.Title, worst.TitleColor));
            }
            var ammoNames = m.ShotsByAmmo.Keys.Union(mine.Select(e => e.Rec.Ammo?.Name ?? "?")).Distinct();
            foreach (var a in ammoNames)
            {
                var hits = mine.Where(e => (e.Rec.Ammo?.Name ?? "?") == a).ToList();
                s.ByAmmo.Add((a, m.ShotsByAmmo.TryGetValue(a, out int n) ? n : 0, hits.Count, hits.Count(e => e.Penetrated)));
            }

            // damage taken: everything that happened to the player's vehicle
            var onMe = m.Entries.Where(e => e.OnPlayer).ToList();
            s.HitsTaken = onMe.Count;
            s.PensTaken = onMe.Count(e => e.Penetrated);
            var modMin = new Dictionary<string, float>();
            var crewMax = new Dictionary<string, float>();
            foreach (var ev in onMe.SelectMany(e => e.Rec.Events))
            {
                if (ev.Kind == EvKind.Module && Matters(ev.Subject)) modMin[ev.Subject] = Mathf.Min(modMin.TryGetValue(ev.Subject, out var v) ? v : 100f, ev.After);
                if (ev.Kind == EvKind.Crew && ev.Subject != null) crewMax[ev.Subject] = Mathf.Max(crewMax.TryGetValue(ev.Subject, out var c) ? c : 0f, ev.After);
            }
            var mine2 = onMe.SelectMany(e => e.Rec.Events).ToList();
            if (onMe.Any(h => h.Rec.Events.Any(ev => ev.Kind == EvKind.Explosion && !Timeline.OwnWarhead(h.Rec, ev)))) s.ModulesLost.Add("Ammunition exploded");
            else if (mine2.Any(ev => ev.Kind == EvKind.AmmoFire)) s.ModulesLost.Add("Ammunition fire");
            if (mine2.Any(ev => ev.Kind == EvKind.Fire)) s.ModulesDamaged.Add("Fire");
            foreach (var kv in modMin.OrderBy(k => k.Value))
                if (kv.Value <= 0f) s.ModulesLost.Add(kv.Key); else if (kv.Value < 60f) s.ModulesDamaged.Add($"{kv.Key} ({kv.Value:0}%)");
            foreach (var kv in crewMax.OrderByDescending(k => k.Value))
                if (kv.Value >= (float)HealthStatus.Critical) s.CrewKilled.Add(kv.Key + " " + Recorder.StatusText((HealthStatus)Mathf.RoundToInt(kv.Value)));
                else if (kv.Value >= (float)HealthStatus.Wounded) s.CrewWounded.Add(kv.Key + " " + Recorder.StatusText((HealthStatus)Mathf.RoundToInt(kv.Value)));
            return s;
        }

        /// game marks a kill on the shot only when the round itself kills, an ammo fire or crew bailing comes later (often
        /// after the recorder's 25 s window). so a target that's destroyed / neutralized now credits your last hit on it
        /// that did real damage (HIT or worse)
        static void ResolveKills(AarMission m)
        {
            var groups = m.Entries.Where(e => e.Rec.PlayerShot && !ReferenceEquals(e.Rec.Target, null)).GroupBy(e => e.Rec.Target);
            foreach (var g in groups)
            {
                bool dead, outOfAction;
                try { dead = g.Key.Destroyed; outOfAction = g.Key.Neutralized; } catch { continue; }
                var list = g.OrderBy(e => e.Rec.FirstImpactReal).ToList();
                foreach (var e in list) e.CreditKill = e.CreditKo = false;
                if (!dead && !outOfAction) continue;
                var last = list.LastOrDefault(e => Timeline.Rank(e.Rec.Title) >= Timeline.Rank("HIT"));
                if (last == null) continue;
                if (dead) last.CreditKill = true; else last.CreditKo = true;
            }
        }

        static bool Matters(string subject)
        {
            var c = GhostSnapshot.Classify(subject);
            return c != ModCat.Other && c != ModCat.Radio;
        }

        /// "Gunner killed · Engine destroyed · Fire", worst first
        public static string DamageSummary(HitRecord rec)
        {
            var crew = new Dictionary<string, float>();
            var mods = new Dictionary<string, (float before, float after)>();
            bool fire = false, ammoFire = false, boom = false, ammoHit = false;
            foreach (var e in rec.Events)
            {
                switch (e.Kind)
                {
                    case EvKind.Crew: if (e.Subject != null) crew[e.Subject] = Mathf.Max(crew.TryGetValue(e.Subject, out var c) ? c : 0f, e.After); break;
                    case EvKind.Module:
                        if (e.Subject == null || !Matters(e.Subject)) break;
                        if (mods.TryGetValue(e.Subject, out var m)) mods[e.Subject] = (m.before, Mathf.Min(m.after, e.After));
                        else mods[e.Subject] = (e.Before, e.After);
                        break;
                    case EvKind.Fire: fire = true; break;
                    case EvKind.AmmoFire: ammoFire = true; break;
                    case EvKind.Explosion: if (!Timeline.OwnWarhead(rec, e)) boom = true; break;
                    case EvKind.AmmoHit: ammoHit = true; break;
                }
            }
            var parts = new List<string>();
            if (boom) parts.Add("Ammo exploded");
            else if (ammoFire) parts.Add("Ammo fire");
            foreach (var kv in crew.OrderByDescending(k => k.Value))
                if (kv.Value >= (float)HealthStatus.Wounded) parts.Add(kv.Key + " " + Recorder.StatusText((HealthStatus)Mathf.RoundToInt(kv.Value)));
            foreach (var kv in mods.Where(k => k.Value.after <= 0f)) parts.Add(kv.Key + " destroyed");
            if (fire) parts.Add("Fire");
            if (ammoHit && !boom && !ammoFire) parts.Add("Ammo hit");
            foreach (var kv in mods.Where(k => k.Value.after > 0f && (k.Value.after < 60f || (k.Value.before >= 0f && k.Value.before - k.Value.after >= 15f))).OrderBy(k => k.Value.after))
                parts.Add($"{kv.Key} {kv.Value.after:0}%");
            return parts.Count == 0 ? "-" : string.Join("  ·  ", parts);
        }

        // ------------------------------------------------------------------ self-test (AutoTest)

        /// opens the report after the self-test shots, replays the hits and screenshots the list, stats and replay
        /// (mid-pen, interior, free camera, after a backwards seek); then from main menu the same mission again (models
        /// still there after the scene unloaded?) and a replay loaded from the JSON
        public static IEnumerator SelfTest(string outDir, bool menu)
        {
            yield return new WaitForSecondsRealtime(1.5f);
            var m = _mission;
            if (m == null) { HitCamMod.Log.Warning("AAR TEST: no mission data"); yield break; }
            HitCamMod.Log.Msg($"AAR TEST: {m.Entries.Count} hits, shots {m.Shots}, incoming {m.Incoming}; full-model vertices: " + string.Join(", ", m.Entries.Select(e => e.Rec.Id + ":" + Schematic.OwnedVertices(e.Full))));
            // per-instance meshes (renamed "... Instance") die with their object, shared asset meshes live as long as the scene's assets
            foreach (var e in m.Entries.Where(x => x.Full != null))
                HitCamMod.Log.Msg($"AAR TEST: hit #{e.Rec.Id} {e.Rec.TargetName}: {e.Full.Exterior.Count} hull meshes ({e.Full.Exterior.Count(p => p.Mesh != null && p.Mesh.name.Contains("Instance"))} per-instance, {e.Full.Exterior.Count(p => p.OwnsMesh)} baked), target destroyed: {e.Rec.Target == null || e.Rec.Target.Destroyed}, broken now: {Schematic.Broken(e.Full)}");
            // a round at the player from the nearest enemy (damage taken; must not pop up in the hit cam with PlayerShotsOnly)
            var player = SceneUtil.Player?.CurrentPlayerUnit;
            var enemy = SceneUnitsManager.Instance?.AllUnitsInScene.Where(u => u != null && player != null && u != player && !u.Destroyed && u.Allegiance != player.Allegiance && u.Type != UnitType.Infantry)
                .OrderBy(u => (u.transform.position - player.transform.position).sqrMagnitude).FirstOrDefault();
            var incoming = Arsenal.Find("HEAT 100 mm");
            if (player != null && enemy != null && incoming != null)
            {
                int before = m.Entries.Count;
                Arsenal.PrepareForFire(incoming);
                AutoTest.Fire(enemy, player, incoming.Ammo, "top");   // from above: a platoon mate next to you could take a side shot
                HitCamMod.Log.Msg($"AAR TEST: {incoming.Ammo.Name} fired by {Recorder.SafeName(enemy)} at the player's {Recorder.SafeName(player)}");
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 5f && m.Entries.Count == before) yield return null;
                yield return new WaitForSecondsRealtime(1f);
                var hitOnMe = m.Entries.Skip(before).FirstOrDefault();
                HitCamMod.Log.Msg("AAR TEST: incoming hit " + (hitOnMe == null ? "NOT recorded" : $"#{hitOnMe.Rec.Id} {hitOnMe.Rec.Title} on {hitOnMe.Rec.TargetName}, on player: {hitOnMe.OnPlayer}, damage: {hitOnMe.Damage}") + $"; rounds aimed at you {m.Incoming}; hit cam showing it: {HitCamPlayer.Current != null && HitCamPlayer.Current.Records.Any(r => !r.PlayerShot)}");
            }
            else HitCamMod.Log.Warning($"AAR TEST: no incoming shot (player {player != null}, enemy {enemy != null}, ammo {incoming != null})");

            // mission ends: report gets offered on screen
            var srm = ScenarioResultManager.Instance;
            if (srm != null && player != null)
            {
                srm.ForceMissionResult(player.Allegiance, true);
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shot(outDir, "aar_mission_end_hint");
                HitCamMod.Log.Msg($"AAR TEST: mission result forced; recorded result '{m.Result}', hint shown: {_hintUntil > Time.unscaledTime}");
            }

            float ts0 = Time.timeScale;
            Open();
            HitCamMod.Log.Msg($"AAR TEST: opened; timeScale {ts0} -> {Time.timeScale}, TimeController.Paused={SafePaused()}");
            yield return Frames(4);
            yield return Shot(outDir, "aar_list");
            _view = View.Stats;
            yield return Frames(3);
            yield return Shot(outDir, "aar_stats");

            for (int i = 0; i < m.Entries.Count; i++)
            {
                var e = m.Entries[i];
                Play(e);
                if (_replay?.P == null) { HitCamMod.Log.Warning($"AAR TEST: hit #{e.Rec.Id} did not start: {_replay?.Error}"); continue; }
                var p = _replay.P;
                HitCamMod.Log.Msg($"AAR TEST: replay #{e.Rec.Id} {e.Rec.Title} on {e.Rec.TargetName}: duration {p.Duration:0.00}s, impact {p.ImpactWall:0.00}s, dwell {p.Dwell:0.00}s, schematic={_replay.Schematic}");
                yield return SeekAndShoot(outDir, $"aar_replay{i}_1_penetration", p.ImpactWall + Mathf.Max(p.Dwell, 0.3f) * 0.45f);
                if (i > 0) continue;
                yield return SeekAndShoot(outDir, "aar_replay0_0_approach", p.A * 0.5f);
                yield return SeekAndShoot(outDir, "aar_replay0_2_interior", p.A + p.B * 0.62f);
                yield return SeekAndShoot(outDir, "aar_replay0_3_aftermath", p.A + p.B + p.C * 0.7f);
                // backwards: rebuilds the replay and fast-forwards
                yield return SeekAndShoot(outDir, "aar_replay0_4_back", p.ImpactWall + Mathf.Max(p.Dwell, 0.3f) * 0.7f);
                // free camera: orbit 110° around, closer, from above
                _replay.StartFreeCam();
                _replay.Orbit(110f, -18f);
                _replay.Zoom(0.75f);
                yield return Frames(3);
                yield return Shot(outDir, "aar_replay0_5_freecam");
                // play at 0.25x for a second
                _replay.Free = false;
                _replay.Speed = 0.25f;
                _replay.Paused = false;
                float t0 = _replay.P.T;
                yield return new WaitForSecondsRealtime(1f);
                HitCamMod.Log.Msg($"AAR TEST: 1 s at 0.25x advanced {(_replay.P.T - t0):0.000}s");
                _replay.Speed = 1f;
                yield return Shot(outDir, "aar_replay0_6_playing");
            }
            Close();
            yield return Frames(2);
            HitCamMod.Log.Msg($"AAR TEST: closed; timeScale {Time.timeScale}, TimeController.Paused={SafePaused()}");
            if (!menu) yield break;

            // ---- after the mission: main menu
            var sc = UnityEngine.Object.FindObjectOfType<SceneController>();
            if (sc == null) { HitCamMod.Log.Warning("AAR TEST: no SceneController"); yield break; }
            HitCamMod.Log.Msg("AAR TEST: back to the main menu");
            sc.LoadMainMenu();
            float w0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - w0 < 90f && !(SceneUtil.IsGameplayGone() && SceneUtil.IsMenuScene(SceneManager.GetActiveScene().name))) yield return null;
            yield return new WaitForSecondsRealtime(6f);
            HitCamMod.Log.Msg($"AAR TEST: menu scene '{SceneManager.GetActiveScene().name}', last mission {_last?.Entries.Count ?? -1} hits; meshes broken: " + string.Join(", ", (_last?.Entries ?? new List<AarEntry>()).Select(e => e.Rec.Id + ":" + (e.Full == null ? "released" : Schematic.Broken(e.Full) ? "BROKEN" : "ok"))));
            Open();
            yield return Frames(4);
            yield return Shot(outDir, "aar_menu_list");
            if (_last != null && _last.Entries.Count > 0)
            {
                var e = _last.Entries.OrderByDescending(x => x.Penetrated).First();
                Play(e);
                if (_replay?.P != null)
                {
                    yield return SeekAndShoot(outDir, "aar_menu_replay", _replay.P.ImpactWall + Mathf.Max(_replay.P.Dwell, 0.3f) * 0.45f);
                    yield return SeekAndShoot(outDir, "aar_menu_replay_interior", _replay.P.A + _replay.P.B * 0.62f);
                }
                else HitCamMod.Log.Warning("AAR TEST: menu replay failed: " + _replay?.Error);
            }
            // past mission from its JSON (box model)
            ShowPast();
            yield return Frames(3);
            yield return Shot(outDir, "aar_past");
            var sessions = AarArchive.Sessions();
            if (sessions.Count > 0)
            {
                // only from the files (OpenSession would pick the copy still in memory)
                var s0 = sessions.FirstOrDefault(x => _last?.Folder != null && x.Dir.TrimEnd('\\', '/') == _last.Folder.TrimEnd('\\', '/')) ?? sessions[0];
                _shown = AarArchive.Load(s0);
                _view = View.Hits;
                HitCamMod.Log.Msg($"AAR TEST: loaded {s0.Dir} from disk: {_shown.Entries.Count} hits, shots {_shown.Shots}, result '{_shown.Result}'; per hit (fragments/armor/events/hull boxes/modules/crew): "
                    + string.Join(", ", _shown.Entries.Select(e => $"#{e.Rec.Id} {e.Rec.Fragments.Count}/{e.Rec.ArmorHits.Count}/{e.Rec.Events.Count}/{e.Schem?.hull.Count ?? 0}/{e.Schem?.modules.Count ?? 0}/{e.Schem?.crew.Count ?? 0}")));
                var st = Stats(_shown);
                HitCamMod.Log.Msg($"AAR TEST: archived stats: hits {st.Hits}, pens {st.Pens}, kills {st.Kills}, KOs {st.KnockOuts}, accuracy {st.Accuracy:0.00}");
                yield return Frames(3);
                yield return Shot(outDir, "aar_past_list");
                var e = _shown?.Entries.OrderByDescending(x => x.Penetrated).FirstOrDefault();
                if (e != null)
                {
                    Play(e);
                    if (_replay?.P != null)
                    {
                        yield return SeekAndShoot(outDir, "aar_past_replay", _replay.P.ImpactWall + Mathf.Max(_replay.P.Dwell, 0.3f) * 0.45f);
                        yield return SeekAndShoot(outDir, "aar_past_replay_interior", _replay.P.A + _replay.P.B * 0.62f);
                    }
                    else HitCamMod.Log.Warning("AAR TEST: archived replay failed: " + _replay?.Error);
                }
            }
            Close();
        }

        static string SafePaused() { try { return TimeController.Paused.ToString(); } catch { return "?"; } }

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        static IEnumerator SeekAndShoot(string dir, string name, float t)
        {
            if (_replay?.P == null) yield break;
            _replay.Paused = true;
            _replay.Free = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _replay.SeekTo(t);
            int frames = 0;
            while (_replay != null && _replay.Seeking && frames++ < 600) yield return null;
            if (_replay?.P == null) yield break;
            HitCamMod.Log.Msg($"AAR TEST: seek to {t:0.00}s took {watch.ElapsedMilliseconds} ms / {frames} frames (now {_replay.P.T:0.00}s)");
            yield return Frames(3);
            yield return Shot(dir, name);
        }

        static IEnumerator Shot(string dir, string name)
        {
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
            yield return Frames(3);
        }
    }
}
