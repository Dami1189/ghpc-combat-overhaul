using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GHPC;
using GHPC.Player;
using GHPC.Weaponry;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace GHPCHitCam
{
    /// Test gun: every projectile the game has loaded, plus generated shells of any calibre (APFSDS / AP / HEAT / HE)
    /// and bombs. While the arsenal is on, the player's main gun fires the selected projectile; bombs are dropped
    /// onto whatever the gun points at. U on/off, 9 / 0 pick, Shift+9 / 0 jump group, J fire instantly (no reload).
    internal static class Arsenal
    {
        internal class Entry
        {
            public string Group, Label;
            public AmmoType Ammo;
            public bool Bomb;
        }

        static readonly List<Entry> _entries = new List<Entry>();
        static int _sel;
        static bool _built;
        public static bool On;
        static float _msgUntil;
        static string _msg;

        static AccessTools.FieldRef<AmmoType, float> _radiusRef;
        static readonly Dictionary<AmmoType, AmmoType> _templateOf = new Dictionary<AmmoType, AmmoType>();
        static readonly MethodInfo _clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Install(HarmonyLib.Harmony h)
        {
            try { _radiusRef = AccessTools.FieldRefAccess<AmmoType, float>("_radius"); } catch { }
            var init = AccessTools.Method(typeof(LiveRound), "Init");
            if (init != null)
                h.Patch(init, prefix: new HarmonyMethod(typeof(Arsenal).GetMethod(nameof(InitPrefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
        }

        public static void Reset() { _built = false; _entries.Clear(); }

        public static void PrepareForFire(Entry e)
        {
            if (_templateOf.TryGetValue(e.Ammo, out var tpl) && tpl.CachedIndex >= 0) e.Ammo.CachedIndex = tpl.CachedIndex;
        }

        /// For the self-test: an arsenal entry by (part of) its name.
        public static Entry Find(string name)
        {
            if (!_built) Build();
            return _entries.FirstOrDefault(e => e.Ammo.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? _entries.FirstOrDefault(e => e.Ammo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static Entry Selected => _entries.Count > 0 ? _entries[Mathf.Clamp(_sel, 0, _entries.Count - 1)] : null;

        /// The whole catalogue (the Armor Sim's projectile picker).
        public static IReadOnlyList<Entry> Entries { get { if (!_built) Build(); return _entries; } }

        // ------------------------------------------------------------------ catalogue

        static void Build()
        {
            _built = true;
            _entries.Clear();
            var real = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>().Where(a => a != null && a.AmmoType != null).Select(a => a.AmmoType)
                .Concat(UnityEngine.Object.FindObjectsOfType<WeaponSystem>().Select(w => w.CurrentAmmoType).Where(a => a != null))
                .GroupBy(a => a.Name).Select(g => g.First()).ToList();
            foreach (var a in real.OrderBy(a => a.Category).ThenBy(a => a.GetFinalCaliber()))
            {
                if (a.GetFinalCaliber() < 10f && a.Category == AmmoType.AmmoCategory.Penetrator) continue;   // rifle bullets
                bool bomb = a.Category == AmmoType.AmmoCategory.Explosive && a.MuzzleVelocity < 150f;
                _entries.Add(new Entry { Group = "Game: " + Cat(a, bomb), Label = $"{a.Name}  ({a.GetFinalCaliber():0} mm, pen {a.RhaPenetration:0}, TNT {a.TntEquivalentKg:0.##} kg)", Ammo = a, Bomb = bomb });
            }

            // templates for generated rounds: the game's own examples of each kind, so fuzes / spall settings stay sane
            AmmoType rod = real.Where(a => a.Category == AmmoType.AmmoCategory.Penetrator && a.Radius * 2000f < a.GetFinalCaliber() * 0.6f && a.GetFinalCaliber() >= 90).OrderByDescending(a => a.RhaPenetration).FirstOrDefault();
            AmmoType ap = real.Where(a => a.Category == AmmoType.AmmoCategory.Penetrator && a.Radius * 2000f >= a.GetFinalCaliber() * 0.6f && a.GetFinalCaliber() >= 20).OrderByDescending(a => a.GetFinalCaliber()).FirstOrDefault()
                         ?? rod;
            AmmoType heat = real.Where(a => a.Category == AmmoType.AmmoCategory.ShapedCharge && a.Guidance == 0 && a.GetFinalCaliber() >= 60).OrderByDescending(a => a.RhaPenetration).FirstOrDefault();
            AmmoType he = real.Where(a => a.Category == AmmoType.AmmoCategory.Explosive && a.MuzzleVelocity >= 150f).OrderByDescending(a => a.TntEquivalentKg).FirstOrDefault();
            AmmoType bombT = real.Where(a => a.Category == AmmoType.AmmoCategory.Explosive && a.MuzzleVelocity < 150f).OrderByDescending(a => a.TntEquivalentKg).FirstOrDefault() ?? he;

            int[] cals = { 20, 25, 30, 35, 40, 57, 76, 90, 100, 105, 115, 120, 125, 130, 152, 203 };
            foreach (int c in cals)
            {
                if (rod != null && c >= 25) Add("Generated APFSDS", Gen(rod, $"APFSDS {c} mm", c, kind: 'S'));
                if (ap != null) Add("Generated AP", Gen(ap, $"AP {c} mm", c, kind: 'A'));
                if (heat != null && c >= 40) Add("Generated HEAT", Gen(heat, $"HEAT {c} mm", c, kind: 'H'));
                if (he != null) Add("Generated HE", Gen(he, $"HE {c} mm", c, kind: 'E'));
            }
            if (bombT != null)
                foreach (int kg in new[] { 50, 100, 250, 500, 1000, 2000 })
                    Add("Bombs", GenBomb(bombT, kg), bomb: true);
            HitCamMod.Log.Msg($"Arsenal: {_entries.Count} projectiles ({real.Count} from the game, templates rod={rod?.Name} ap={ap?.Name} heat={heat?.Name} he={he?.Name} bomb={bombT?.Name})");
        }

        static string Cat(AmmoType a, bool bomb) => bomb ? "bombs" : a.Guidance != 0 ? "missiles" : a.Category == AmmoType.AmmoCategory.Penetrator ? "kinetic" : a.Category == AmmoType.AmmoCategory.ShapedCharge ? "HEAT" : "HE";

        static void Add(string group, AmmoType a, bool bomb = false)
        {
            if (a == null) return;
            _entries.Add(new Entry { Group = group, Label = $"{a.Name}  (pen {a.RhaPenetration:0} mm, TNT {a.TntEquivalentKg:0.##} kg, {a.MuzzleVelocity:0} m/s)", Ammo = a, Bomb = bomb });
        }

        static AmmoType Clone(AmmoType t)
        {
            // keep the template's cache index: GHPC's impact effect / decal tables are keyed by it
            var a = (AmmoType)_clone.Invoke(t, null);
            _templateOf[a] = t;
            return a;
        }

        /// Scaled from public rules of thumb: modern APFSDS ~4.4x calibre RHA, full-calibre AP ~1.9x,
        /// HEAT ~5x, HE filler ~1.7e-6*cal^3 kg TNT (152 mm -> 6 kg), shell mass ~1.25e-5*cal^3 kg.
        static AmmoType Gen(AmmoType t, string name, float cal, char kind)
        {
            var a = Clone(t);
            a.Name = name;
            a.Caliber = cal;
            float r;
            switch (kind)
            {
                case 'S':
                    a.RhaPenetration = 4.4f * cal; a.MuzzleVelocity = 1650f + cal * 0.6f; a.Mass = 2.6e-6f * cal * cal * cal; a.TntEquivalentKg = 0f;
                    r = cal * 0.11f / 1000f; break;                       // rod ~22 % of calibre
                case 'A':
                    a.RhaPenetration = 1.9f * cal; a.MuzzleVelocity = 850f; a.Mass = 1.25e-5f * cal * cal * cal; a.TntEquivalentKg = 0f;
                    r = cal * 0.5f / 1000f; break;
                case 'H':
                    a.RhaPenetration = 5f * cal; a.MuzzleVelocity = 950f; a.Mass = 0.8e-5f * cal * cal * cal; a.TntEquivalentKg = 1.0e-6f * cal * cal * cal;
                    r = cal * 0.5f / 1000f; break;
                default:
                    a.RhaPenetration = Mathf.Max(0.65f * cal, 5f); a.MuzzleVelocity = 800f; a.Mass = 1.25e-5f * cal * cal * cal; a.TntEquivalentKg = 1.7e-6f * cal * cal * cal;
                    r = cal * 0.5f / 1000f; break;
            }
            a.SectionalArea = Mathf.PI * r * r;
            if (_radiusRef != null) _radiusRef(a) = r;
            a.MaxSpallRha = Mathf.Max(t.MaxSpallRha * cal / Mathf.Max(t.GetFinalCaliber(), 1f), 2f);
            a.Guidance = 0;
            return a;
        }

        /// General-purpose bomb: ~45 % of its mass is explosive; diameter grows with the cube root of mass.
        static AmmoType GenBomb(AmmoType t, int kg)
        {
            var a = Clone(t);
            a.Name = $"Bomb {kg} kg";
            a.Caliber = 210f * Mathf.Pow(kg / 100f, 1f / 3f);
            a.Mass = kg;
            a.TntEquivalentKg = 0.45f * kg;
            a.RhaPenetration = Mathf.Max(t.RhaPenetration, 30f + kg * 0.05f);
            a.MuzzleVelocity = 280f;
            float r = a.Caliber * 0.5f / 1000f;
            a.SectionalArea = Mathf.PI * r * r;
            if (_radiusRef != null) _radiusRef(a) = r;
            a.Guidance = 0;
            return a;
        }

        // ------------------------------------------------------------------ firing

        static Unit Player => SceneUtil.Player != null ? SceneUtil.Player.CurrentPlayerUnit : null;

        internal static WeaponSystem MainGun(Unit u)
        {
            var wm = u != null ? u.WeaponsManager : null;
            if (wm == null || wm.Weapons == null) return null;
            var info = wm.GetWeaponInfoByRole(WeaponSystemRole.MainGun) ?? wm.Weapons.FirstOrDefault(w => w != null && w.Weapon != null);
            return info?.Weapon;
        }

        /// The player's main gun firing while the arsenal is on: swap in the selected projectile.
        static void InitPrefix(LiveRound __instance, LiveRound parentRound)
        {
            try
            {
                if (!On || parentRound != null || __instance.Info == null || _overriding) return;
                var u = Player;
                if (u == null || __instance.Shooter != u) return;
                if (__instance.Info.GetFinalCaliber() < 20f) return;   // machine guns stay what they are
                var e = Selected;
                if (e == null) return;
                Apply(__instance, e, __instance.transform.position, __instance.transform.forward);
            }
            catch (Exception ex) { HitCamMod.Log.Warning("Arsenal swap failed: " + ex.Message); }
        }

        static bool _overriding;

        static void Apply(LiveRound round, Entry e, Vector3 muzzle, Vector3 dir)
        {
            if (_templateOf.TryGetValue(e.Ammo, out var tpl) && tpl.CachedIndex >= 0) e.Ammo.CachedIndex = tpl.CachedIndex;
            round.Info = e.Ammo;
            round.Guided = false;
            round.UseGravity = true;
            round.CurrentSpeed = e.Ammo.MuzzleVelocity;
            round.MaxSpeed = e.Ammo.MuzzleVelocity;
            if (e.Bomb)
            {
                // a bomb falls from 300 m onto whatever the sight is on
                Vector3 aim = AimPoint(muzzle, dir, e.Ammo, out string what);
                round.transform.position = aim + Vector3.up * 300f;
                round.transform.rotation = Quaternion.LookRotation(Vector3.down);
                round.CurrentSpeed = round.MaxSpeed = DropSpeed(e.Ammo);
                Say($"{e.Ammo.Name} dropped from 300 m onto {what}");
            }
        }

        internal static Vector3? TestSightDir;   // self-test: the sight held on this direction

        /// Self-test: the picked bomb fired with the sight held on the target, as if you had aimed at it.
        public static void TestSightDrop(Entry e, Unit target)
        {
            int i = _entries.IndexOf(e);
            if (i >= 0) _sel = i;
            var u = Player;
            var gun = MainGun(u);
            Vector3 o = gun != null && gun.MuzzleIdentity != null ? gun.MuzzleIdentity.position : u.transform.position + Vector3.up * 2f;
            try { if (gun?.FCS?.AimTransform != null) o = gun.FCS.AimTransform.position; } catch { }
            Vector3 c = target.transform.position + Vector3.up * 1.2f;
            try { if (target.Center != null) c = target.Center.position; } catch { }
            TestSightDir = (c - o).normalized;
            try { FireNow(); }
            finally { TestSightDir = null; }
        }

        /// How fast a bomb is going when it reaches the ground from 300 m.
        internal static float DropSpeed(AmmoType a) => Mathf.Sqrt(Mathf.Pow(Mathf.Max(a.MuzzleVelocity, 0f), 2f) + 2f * 9.81f * 300f);

        /// Where a bomb lands: what the sight is on, by the game's own ray (it sees the vehicles' armour, which a plain
        /// raycast goes through to the ground behind them); a vehicle's roof when it is one.
        static Vector3 AimPoint(Vector3 from, Vector3 dir, AmmoType ammo, out string what)
        {
            what = "the ground";
            var u = Player;
            Vector3 o = from, d = dir;
            var fcs = u != null && MainGun(u) != null ? MainGun(u).FCS : null;
            if (fcs != null)
            {
                try
                {
                    var at = fcs.AimTransform;
                    if (at != null) o = at.position;
                    Vector3 v = fcs.AimWorldVector;
                    d = v.sqrMagnitude > 0.5f ? v.normalized : at != null ? at.forward : dir;
                }
                catch { }
            }
            if (TestSightDir.HasValue) d = TestSightDir.Value;
            if (PenProbe.Probe(o, d, 6000f, ammo, u, u != null ? u.Allegiance : Faction.Neutral, out var r) && r.Hit)
            {
                if (r.Unit != null && r.Unit != u)
                {
                    what = "the " + Recorder.SafeName(r.Unit);
                    Vector3 c = r.Unit.transform.position;
                    try { if (r.Unit.Center != null) c = r.Unit.Center.position; } catch { }
                    return new Vector3(c.x, r.Point.y, c.z);
                }
                return r.Point;
            }
            what = "a point 1500 m out (the sight is on nothing)";
            return o + d * 1500f;
        }

        /// Fire the selected projectile right now from the main gun (no reload, for quick tests).
        public static void FireNow()
        {
            var u = Player;
            var gun = MainGun(u);
            var e = Selected;
            if (u == null || e == null) { Say("No player vehicle"); return; }
            Transform muzzle = gun != null ? gun.MuzzleIdentity : null;
            Vector3 pos = muzzle != null ? muzzle.position : u.transform.position + Vector3.up * 2.2f + u.transform.forward * 3f;
            Vector3 dir = muzzle != null ? muzzle.forward : u.transform.forward;
            var round = LiveRoundUtility.GetNewLiveRound(e.Ammo);
            round.Info = e.Ammo;
            round.Shooter = u;
            round.transform.position = pos + dir * 0.5f;
            round.transform.rotation = Quaternion.LookRotation(dir);
            Apply(round, e, pos, dir);
            _overriding = true;
            try { round.Init(null, null); }
            finally { _overriding = false; }
            Say("Fired " + e.Ammo.Name);
        }

        // ------------------------------------------------------------------ input + UI

        public static void Update()
        {
            if (Hotkeys.Down(Settings.ArsenalKey))
            {
                if (!_built) Build();
                On = !On;
                Say(On ? "Arsenal ON: main gun fires the selected projectile" : "Arsenal off");
            }
            if (!On) return;
            if (!_built) Build();
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            // (their own Shift: a group at a time)
            if (Input.GetKeyDown(Hotkeys.Parse(Settings.ArsenalNextKey.Value).Key) && Hotkeys.Parse(Settings.ArsenalNextKey.Value).Key != KeyCode.None) Move(+1, shift);
            if (Input.GetKeyDown(Hotkeys.Parse(Settings.ArsenalPrevKey.Value).Key) && Hotkeys.Parse(Settings.ArsenalPrevKey.Value).Key != KeyCode.None) Move(-1, shift);
            if (Hotkeys.Down(Settings.FireNowKey)) FireNow();
        }

        static void Move(int d, bool group)
        {
            if (_entries.Count == 0) return;
            if (!group) { _sel = (_sel + d + _entries.Count) % _entries.Count; return; }
            string g = Selected.Group;
            int i = _sel;
            for (int n = 0; n < _entries.Count; n++)
            {
                i = (i + d + _entries.Count) % _entries.Count;
                if (_entries[i].Group != g) { _sel = i; break; }
            }
            // land on the first entry of that group
            string ng = _entries[_sel].Group;
            while (_sel > 0 && _entries[_sel - 1].Group == ng) _sel--;
        }

        static void Say(string m) { _msg = m; _msgUntil = Time.unscaledTime + 3f; HitCamMod.Log.Msg(m); }

        static GUIStyle _st, _hd;

        public static void Draw()
        {
            if (!On && Time.unscaledTime > _msgUntil) return;
            if (_st == null)
            {
                _st = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false };
                _hd = new GUIStyle(_st) { fontStyle = FontStyle.Bold };
            }
            float x = 14, y = Screen.height * 0.32f, w = 520;
            if (On && _entries.Count > 0)
            {
                GUI.color = new Color(0, 0, 0, 0.62f);
                GUI.DrawTexture(new Rect(x - 6, y - 6, w + 12, 13 * 19 + 48), Texture2D.whiteTexture);
                GUI.color = Color.white;
                var e = Selected;
                _hd.normal.textColor = new Color(1f, 0.8f, 0.3f);
                GUI.Label(new Rect(x, y, w, 20), $"ARSENAL  [{_sel + 1}/{_entries.Count}]  {e.Group}", _hd);
                y += 22;
                int from = Mathf.Clamp(_sel - 6, 0, Mathf.Max(0, _entries.Count - 13));
                for (int i = from; i < Mathf.Min(_entries.Count, from + 13); i++)
                {
                    bool s = i == _sel;
                    _st.normal.textColor = s ? new Color(1f, 0.95f, 0.6f) : _entries[i].Group == e.Group ? new Color(0.85f, 0.88f, 0.92f) : new Color(0.55f, 0.58f, 0.62f);
                    GUI.Label(new Rect(x, y, w, 19), (s ? "▶ " : "   ") + _entries[i].Label, _st);
                    y += 19;
                }
                _st.normal.textColor = new Color(0.7f, 0.75f, 0.8f);
                GUI.Label(new Rect(x, y + 4, w, 19), $"{Hotkeys.Label(Settings.ArsenalPrevKey)} / {Hotkeys.Label(Settings.ArsenalNextKey)} select   Shift: group   {Hotkeys.Label(Settings.FireNowKey)} fire now   {Hotkeys.Label(Settings.ArsenalKey)} off   (main gun fires it too)", _st);
            }
            if (Time.unscaledTime < _msgUntil)
            {
                _hd.normal.textColor = Color.white;
                GUI.Label(new Rect(x, Screen.height * 0.32f - 26, 700, 22), _msg, _hd);
            }
        }
    }
}
