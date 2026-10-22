using System;
using System.Reflection;
using System.Text;
using GHPC.UI.Menu;
using GHPC.UI.Menu.Fields;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GHPCHitCam
{
    /// mod settings window from GHPC's own Options: an "Overhaul" tab added to every options tab bar the game builds
    /// (main menu options and the in-mission menu), a copy of the game's own last tab so it looks like one of them.
    /// clicking it opens the settings window (ConfigMenu), nothing else in the game's menus changes
    internal static class OptionsHook
    {
        const string TabName = "HitCam settings tab";
        static float _next;
        public static int Added { get; private set; }
        static readonly AccessTools.FieldRef<TabGroup, GameObject> SwapRoot = AccessTools.FieldRefAccess<TabGroup, GameObject>("_swapRoot");

        static readonly System.Collections.Generic.HashSet<int> _looked = new System.Collections.Generic.HashSet<int>();

        /// once a second while a menu can be up (cursor free): any options tab bar (they come with each scene's menus)
        /// without a HitCam tab yet gets one. each bar only gets looked at once: telling an options bar from the others
        /// searches the whole menu under it, and doing that every second for every bar was a 25 ms hitch every second
        public static void Update()
        {
            if (Time.unscaledTime < _next || !Cursor.visible) return;
            _next = Time.unscaledTime + 1f;
            try
            {
                foreach (var tg in Resources.FindObjectsOfTypeAll<TabGroup>())
                {
                    if (tg == null || !tg.gameObject.scene.IsValid() || !_looked.Add(tg.GetInstanceID())) continue;   // prefabs, seen already
                    if (IsOptions(tg)) Add(tg);
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("Options tab: " + e.Message); _next = Time.unscaledTime + 30f; }
        }

        static bool IsOptions(TabGroup tg)
        {
            GameObject root = null;
            try { root = SwapRoot(tg); } catch { }
            if (root == null) return false;
            return root.GetComponentInChildren<VideoSettingsMenuPanel>(true) != null || root.GetComponentInChildren<GamePrefsMenuPanel>(true) != null
                || root.GetComponentInChildren<AudioSettingsMenuPanel>(true) != null || root.GetComponentInChildren<InputSettingsMenuPanel>(true) != null;
        }

        static void Add(TabGroup tg)
        {
            var tabs = tg.GetComponentsInChildren<TabButton>(true);
            if (tabs.Length == 0) return;
            // copy the last tab that's shown (Cheats sits last but hidden unless cheats are on)
            var src = tabs[tabs.Length - 1];
            for (int i = tabs.Length - 1; i >= 0; i--) if (tabs[i].gameObject.activeSelf) { src = tabs[i]; break; }
            var bar = src.transform.parent;
            if (bar == null || bar.Find(TabName) != null) return;
            float idle = 1f;
            foreach (var t in tabs) { try { idle = Mathf.Min(idle, t.BackgroundSpriteAlpha); } catch { } }

            var go = UnityEngine.Object.Instantiate(src.gameObject, bar, false);
            go.name = TabName;
            go.transform.SetAsLastSibling();
            go.SetActive(true);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<TabButton>());
            // game's localisation would put the copied tab's own name back
            foreach (var c in go.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0) UnityEngine.Object.DestroyImmediate(c);
            SetText(go, "Overhaul");
            go.AddComponent<OptionsTab>().Init(go.GetComponent<Image>(), idle);
            // bar without a layout group: step the copy along like the tabs before it
            int si = Array.IndexOf(tabs, src);
            if (bar.GetComponent<LayoutGroup>() == null && si >= 1 && tabs[si - 1].transform is RectTransform a
                && src.transform is RectTransform b && go.transform is RectTransform rt)
                rt.anchoredPosition = b.anchoredPosition + (b.anchoredPosition - a.anchoredPosition);
            Added++;
            HitCamMod.Log.Msg($"Options: HitCam tab added after '{src.name}' in '{PathOf(bar)}' (scene {go.scene.name})");
            if (AutoTest.Active) HitCamMod.Log.Msg("Options tab bar: " + Dump(bar));
        }

        static void SetText(GameObject go, string s)
        {
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var p = c.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (p == null || p.PropertyType != typeof(string) || !p.CanWrite) continue;
                string old = p.GetValue(c, null) as string ?? "";
                p.SetValue(c, old.Length > 0 && old == old.ToUpperInvariant() ? s.ToUpperInvariant() : s, null);
            }
        }

        static string PathOf(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        static string Dump(Transform bar)
        {
            var sb = new StringBuilder();
            sb.Append(bar.GetComponent<LayoutGroup>() != null ? bar.GetComponent<LayoutGroup>().GetType().Name : "no layout group");
            foreach (Transform ch in bar)
            {
                var rt = ch as RectTransform;
                string txt = "";
                foreach (var c in ch.GetComponentsInChildren<Component>(true))
                {
                    var p = c != null ? c.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance) : null;
                    if (p != null && p.PropertyType == typeof(string)) { txt = p.GetValue(c, null) as string; break; }
                }
                sb.Append($" | '{ch.name}' \"{txt}\" active={ch.gameObject.activeSelf}" + (rt != null ? $" at {rt.anchoredPosition} size {rt.rect.size}" : ""));
            }
            return sb.ToString();
        }
    }

    /// HitCam tab in the game's options: opens the settings window, lights up on hover like the game's tabs
    internal class OptionsTab : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        Image _bg;
        float _idle = 0.5f;

        public void Init(Image bg, float idle)
        {
            _bg = bg;
            _idle = Mathf.Clamp(idle, 0.2f, 0.9f);
            Alpha(_idle);
        }

        void Alpha(float a)
        {
            if (_bg == null) return;
            var c = _bg.color;
            c.a = a;
            _bg.color = c;
        }

        public void OnPointerClick(PointerEventData e) => ConfigMenu.Open();
        public void OnPointerEnter(PointerEventData e) => Alpha(0.98f);
        public void OnPointerExit(PointerEventData e) => Alpha(_idle);
        void OnDisable() => Alpha(_idle);
    }
}
