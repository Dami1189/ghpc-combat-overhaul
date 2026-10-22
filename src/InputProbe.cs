using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GHPCHitCam
{
    /// Self-test steps with real input, the way a player does it: keys and mouse clicks into the game's own window (only
    /// while it is the window in front, so nothing else ever gets them).
    ///   key:<Q|TAB|F12|ESC|ALT+HOME...>[:<held s>]   state:<label>   qclick   unitclick   endplanning
    internal static class InputProbe
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        static readonly Dictionary<string, byte> Vk = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["Q"] = 0x51, ["TAB"] = 0x09, ["ESC"] = 0x1B, ["F12"] = 0x7B, ["F2"] = 0x71, ["M"] = 0x4D, ["P"] = 0x50, ["H"] = 0x48,
            ["K"] = 0x4B, ["U"] = 0x55, ["J"] = 0x4A, ["HOME"] = 0x24, ["INSERT"] = 0x2D, ["ALT"] = 0x12, ["ENTER"] = 0x0D,
            ["SPACE"] = 0x20, ["W"] = 0x57, ["1"] = 0x31, ["2"] = 0x32, ["F9"] = 0x78, ["F10"] = 0x79, ["9"] = 0x39, ["0"] = 0x30,
        };

        public static int Skipped;
        static uint _pid;

        static void Log(string s) => HitCamMod.Log.Msg("INPUT: " + s);

        /// The game's window, when it is the one in front.
        static IntPtr OurWindow()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return IntPtr.Zero;
            if (_pid == 0) _pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            GetWindowThreadProcessId(h, out uint pid);
            return pid == _pid ? h : IntPtr.Zero;
        }

        static void Send(byte vk, bool up)
        {
            bool ext = vk == 0x24 || vk == 0x2D || vk == 0x23 || vk == 0x2E || (vk >= 0x21 && vk <= 0x28);
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), (up ? 2u : 0u) | (ext ? 1u : 0u), UIntPtr.Zero);
        }

        public static IEnumerator Key(string combo, float hold)
        {
            if (OurWindow() == IntPtr.Zero) { Log($"game not in front, {combo} NOT pressed"); Skipped++; yield break; }
            var vks = combo.Split('+').Select(p => Vk.TryGetValue(p.Trim(), out var v) ? v : (byte)0).ToArray();
            if (vks.Any(v => v == 0)) { Log("unknown key " + combo); yield break; }
            foreach (var v in vks) { Send(v, false); yield return new WaitForSecondsRealtime(0.03f); }
            yield return new WaitForSecondsRealtime(hold);
            foreach (var v in vks.Reverse()) { Send(v, true); yield return new WaitForSecondsRealtime(0.03f); }
            yield return new WaitForSecondsRealtime(0.6f);
            Log($"pressed {combo}" + (hold > 0.2f ? $" for {hold:0.0} s" : "") + ": " + Brief());
        }

        /// A real left click at a point of the game's window (pixels from its top left).
        static IEnumerator ClickAt(Vector2 client, string what)
        {
            var h = OurWindow();
            if (h == IntPtr.Zero) { Log($"game not in front, no click on {what}"); Skipped++; yield break; }
            var p = new POINT { X = Mathf.RoundToInt(client.x), Y = Mathf.RoundToInt(client.y) };
            ClientToScreen(h, ref p);
            SetCursorPos(p.X, p.Y);
            yield return new WaitForSecondsRealtime(0.2f);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            yield return new WaitForSecondsRealtime(0.07f);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// A real left click on a UI element's centre. True when its button's onClick ran.
        static IEnumerator Click(Button b, string what, bool[] result)
        {
            result[0] = false;
            var h = OurWindow();
            if (h == IntPtr.Zero) { Log($"game not in front, no click on {what}"); Skipped++; yield break; }
            var rt = b.transform as RectTransform;
            var cv = b.GetComponentInParent<Canvas>();
            Camera cam = cv != null && cv.renderMode != RenderMode.ScreenSpaceOverlay ? cv.worldCamera : null;
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
            var p = new POINT { X = Mathf.RoundToInt(sp.x), Y = Mathf.RoundToInt(Screen.height - sp.y) };
            ClientToScreen(h, ref p);
            bool clicked = false;
            UnityEngine.Events.UnityAction a = () => clicked = true;
            b.onClick.AddListener(a);
            yield return ClickAt(new Vector2(sp.x, Screen.height - sp.y), what);
            try { b.onClick.RemoveListener(a); } catch { }
            var es = EventSystem.current;
            string hit = "";
            if (es != null)
            {
                var res = new List<RaycastResult>();
                es.RaycastAll(new PointerEventData(es) { position = sp }, res);
                hit = res.Count > 0 ? res[0].gameObject.name : "nothing";
            }
            result[0] = clicked;
            Log($"click on {what} at screen {p.X},{p.Y}: {(clicked ? "CLICKED" : "NOT CLICKED")} (top of the raycast there: {hit}; {Brief()})");
        }

        static GHPC.Player.PlayerInput Pi => SceneUtil.Player;

        static bool QOpen() { var r = Pi != null ? Pi.ContextMenuRoot : null; return r != null && r.gameObject.activeInHierarchy; }

        static bool UnitMenuOpen()
        {
            var u = GHPC.UI.UnitMenu.UnitMenuController.Instance;
            return u != null && u.MenuVisible;
        }

        static string Brief()
        {
            var pi = Pi;
            var es = EventSystem.current;
            return $"Q menu {(QOpen() ? "open" : "shut")}, unit menu {(UnitMenuOpen() ? "open" : "shut")}, F12 {(ConfigMenu.IsOpen ? "open" : "shut")}, "
                + $"kb blocked {(pi != null ? pi.BlockKeyboardInput.ToString() : "?")}, UI input {(es != null ? es.name + (es.enabled ? "" : " OFF") : "NONE")}, "
                + $"unit {(pi != null && pi.CurrentPlayerUnit != null ? pi.CurrentPlayerUnit.FriendlyName : "-")}";
        }

        public static string State()
        {
            var pi = Pi;
            var all = UnityEngine.Object.FindObjectsOfType<EventSystem>(true);
            string s = Brief() + $"; event systems {string.Join("/", all.Select(e => e.name + (e.enabled && e.gameObject.activeInHierarchy ? "+" : "-") + (e == EventSystem.current ? "(current)" : "")))}"
                + $"; cursor {Cursor.visible} {Cursor.lockState}; paused {GHPC.State.TimeController.Paused}";
            if (pi != null)
                s += $"; suspend gunner {pi.SuspendGunnerInputs} camera {pi.SuspendCameraInputs}, interface free {pi.SuspendInterfaceInputs}, strict live {pi.AllowPlayerStrictLiveInput}, live {pi.AllowPlayerCancellableLiveInput}, menu {(pi.MenuController != null && pi.MenuController.ActiveMenu != null ? pi.MenuController.ActiveMenu.name : "none")}";
            try { s += $", mission {GHPC.State.MissionStateController.CurrentState}"; } catch { }
            return s;
        }

        /// One step of the shot list. False when it isn't one of these.
        public static bool Step(string[] spec, out IEnumerator run)
        {
            run = null;
            switch (spec[0])
            {
                case "key":
                    run = Key(spec[1], spec.Length > 2 ? float.Parse(spec[2], System.Globalization.CultureInfo.InvariantCulture) : 0.08f);
                    return true;
                case "state":
                    Log($"STATE {(spec.Length > 1 ? spec[1] : "")}: " + State());
                    run = Nothing();
                    return true;
                case "qclick": run = QClick(); return true;
                case "reset": run = Reset(); return true;
                case "uiclick": run = UiClick(spec[1]); return true;
                case "failhook": FailHook(spec[1]); run = Nothing(); return true;
                case "f12click": run = F12Click(spec[1]); return true;
                case "unitclick": run = UnitClick(); return true;
                case "endplanning":
                    run = EndPlanning();
                    return true;
                case "shot":
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(AutoTest.OutDir, "input_" + (spec.Length > 1 ? spec[1] : "x") + ".png"));
                    run = Nothing();
                    return true;
            }
            return false;
        }

        static IEnumerator Nothing() { yield return null; }

        /// The Q menu's first button (Restock, a submenu: harmless), clicked for real.
        static IEnumerator QClick()
        {
            var root = Pi != null ? Pi.ContextMenuRoot : null;
            var b = root != null && root.gameObject.activeInHierarchy ? root.GetComponentsInChildren<Button>(false).FirstOrDefault(x => x.IsInteractable()) : null;
            if (b == null) { Log("qclick: the Q menu isn't open (" + Brief() + ")"); yield break; }
            var r = new bool[1];
            yield return Click(b, "Q menu '" + b.name + "'", r);
        }

        /// The unit menu (Tab): a click on another vehicle of the platoon; the player should be in it afterwards.
        static IEnumerator UnitClick()
        {
            var u = GHPC.UI.UnitMenu.UnitMenuController.Instance;
            if (u == null || !u.MenuVisible) { Log("unitclick: the unit menu isn't open (" + Brief() + ")"); yield break; }
            var me = Pi?.CurrentPlayerUnit;
            var buttons = u.GetComponentsInChildren<Button>(false).Where(x => x.IsInteractable()).ToList();
            var label = new Func<Button, string>(x => { var t = x.GetComponentInChildren<TMPro.TMP_Text>(true); return t != null ? t.text : x.name; });
            Log("unitclick: buttons " + string.Join(" | ", buttons.Select(label)));
            var b = buttons.FirstOrDefault(x => me == null || !label(x).StartsWith(me.FriendlyName)) ?? buttons.FirstOrDefault();
            if (b == null) { Log("unitclick: no button"); yield break; }
            var r = new bool[1];
            yield return Click(b, "unit menu '" + label(b) + "'", r);
            yield return new WaitForSecondsRealtime(1.5f);
            var now = Pi?.CurrentPlayerUnit;
            Log($"unitclick: player unit {(me != null ? me.FriendlyName : "-")} -> {(now != null ? now.FriendlyName : "-")} ({(now != me ? "SWITCHED" : "same unit")})");
        }

        /// A real click in the settings window: f12click:close (its Close button) or f12click:tab<n> (a tab), where
        /// ConfigMenu.Window puts them.
        static IEnumerator F12Click(string what)
        {
            if (!ConfigMenu.IsOpen) { Log("f12click: the settings window isn't open"); yield break; }
            float s = Mathf.Max(Screen.height / 1080f, 0.75f), W = Screen.width / s, w = Mathf.Min(940f, W - 40f);
            Vector2 gui = what == "close" ? new Vector2(24f + w - 14f - 65f, 53f)
                : new Vector2(38f + (w - 28f) / 8f * (int.Parse(what.Substring(3)) + 0.5f), 86f);
            yield return ClickAt(gui * s, "settings window " + what);
            Log($"clicked settings window {what}: " + Brief());
        }

        /// Back to plain driving between test blocks: the settings window, the Q and unit menus and the game's menus shut.
        static IEnumerator Reset()
        {
            if (ConfigMenu.IsOpen) ConfigMenu.Close();
            var pi = Pi;
            if (pi != null)
            {
                try { if (QOpen()) HarmonyLib.AccessTools.Method(typeof(GHPC.Player.PlayerInput), "SetContextMenu").Invoke(pi, new object[] { false }); } catch { }
                try { if (UnitMenuOpen()) HarmonyLib.AccessTools.Method(typeof(GHPC.Player.PlayerInput), "ToggleUnitMenu").Invoke(pi, new object[] { true, false }); } catch { }
                try { if (pi.MenuController != null && pi.MenuController.ActiveMenu != null) pi.MenuController.CloseAllMenus(); } catch (Exception e) { Log("reset: " + e.Message); }
            }
            yield return new WaitForSecondsRealtime(0.8f);
            Log("reset: " + State());
        }

        /// uiclick:<text>: a real click on the game's visible UI element whose name or label has that text (a button, or
        /// any element that takes a click, like the options tabs).
        static IEnumerator UiClick(string text)
        {
            GameObject hit = null;
            foreach (var g in UnityEngine.Object.FindObjectsOfType<Graphic>())
            {
                if (g == null || !g.raycastTarget || !g.gameObject.activeInHierarchy) continue;
                var t = g.GetComponentInChildren<TMPro.TMP_Text>(true);
                string label = (t != null ? t.text : "") + " " + g.name;
                if (label.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (g.GetComponentInParent<Selectable>() == null && g.GetComponent<IPointerClickHandler>() == null && g.GetComponentInParent<IPointerClickHandler>() == null) continue;
                hit = g.gameObject;
                break;
            }
            if (hit == null) { Log($"uiclick: nothing visible says '{text}'"); yield break; }
            var rt = hit.transform as RectTransform;
            var cv = hit.GetComponentInParent<Canvas>();
            Camera cam = cv != null && cv.renderMode != RenderMode.ScreenSpaceOverlay ? cv.worldCamera : null;
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
            yield return ClickAt(new Vector2(sp.x, Screen.height - sp.y), $"'{text}' ({hit.name})");
            yield return new WaitForSecondsRealtime(0.5f);
            Log($"uiclick '{text}' ({hit.name}): " + Brief());
        }

        static string _failName;

        /// failhook:<vehicle key>: stands in for another mod's hook on the game's spawn that throws once the vehicle is
        /// made (what a mission with several mods did to half the test range).
        static void FailHook(string key)
        {
            _failName = key + " (range)";
            var m = HarmonyLib.AccessTools.Method(typeof(GHPC.Mission.UnitSpawner), "SpawnUnit", new[] { typeof(GameObject), typeof(GHPC.Mission.Data.UnitMetaData), typeof(GHPC.AI.WaypointHolder), typeof(Transform) });
            new HarmonyLib.Harmony("ghpccc.selftest.failhook").Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(InputProbe).GetMethod(nameof(FailPostfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            Log($"failhook: spawning '{_failName}' throws after the vehicle is made");
        }

        static void FailPostfix(GHPC.Mission.Data.UnitMetaData metaData)
        {
            if (metaData != null && metaData.Name == _failName) throw new NullReferenceException("self-test: a stand-in for another mod's failing spawn hook");
        }

        static IEnumerator EndPlanning()
        {
            var msc = UnityEngine.Object.FindObjectOfType<GHPC.State.MissionStateController>();
            if (msc == null) { Log("endplanning: no MissionStateController"); yield break; }
            msc.EndPlanningPhase();
            yield return new WaitForSecondsRealtime(1f);
            Log("endplanning: " + State());
        }
    }
}
