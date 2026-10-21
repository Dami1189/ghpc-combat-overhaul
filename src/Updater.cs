using System;
using System.Collections;
using System.IO;
using System.Linq;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace GHPCHitCam
{
    /// The update check: once a game start (in the main menu), the latest release on the mod's GitHub page. A newer one is
    /// offered in the main menu's Combat Overhaul box; one click downloads that release's GHPCCombatOverhaul.dll, checks it
    /// really is this mod at that version, keeps the old file under UserData/HitCam/update and puts the new one in Mods,
    /// where the game picks it up on its next start. UpdateCheck: Ask (the default), Auto (installs it by itself), Off.
    internal static class Updater
    {
        const string Repo = "Dami1189/ghpc-combat-overhaul";
        const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
        public const string Page = "https://github.com/" + Repo + "/releases/latest";
        const string DllName = "GHPCCombatOverhaul.dll";

        public enum State { Idle, Checking, UpToDate, Available, Skipped, Downloading, Installed, Failed, Off }
        public static State Now { get; private set; } = State.Idle;
        public static string Latest { get; private set; } = "";
        public static string Notes { get; private set; } = "";
        public static string Error { get; private set; } = "";
        static string _assetUrl;
        static bool _started;

        public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version);
        public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

        /// Self-test: update_feed=<url> stands in for the GitHub API (a local file:// JSON with its own DLL).
        static string Feed => AutoTest.Active && AutoTest.Opt("update_feed", "").Length > 0 ? AutoTest.Opt("update_feed", "") : Api;

        public static void OnMenu()
        {
            if (_started) return;
            // a self-test asks GitHub only when it's told to (updatecheck=true or its own update_feed)
            if (AutoTest.Active && !AutoTest.Opt("updatecheck", false) && AutoTest.Opt("update_feed", "").Length == 0) return;
            _started = true;
            string mode = Settings.UpdateCheck.Value ?? "Ask";
            if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) { Now = State.Off; return; }
            Tidy();
            MelonCoroutines.Start(Check(mode.Equals("Auto", StringComparison.OrdinalIgnoreCase)));
        }

        static IEnumerator Check(bool auto)
        {
            Now = State.Checking;
            string text = null, err = null;
            using (var req = UnityWebRequest.Get(Feed))
            {
                req.SetRequestHeader("User-Agent", "GHPC-Combat-Overhaul/" + CurrentText);
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) text = req.downloadHandler.text;
                else err = req.error;
            }
            if (text == null) { Fail("couldn't reach GitHub (" + err + ")"); yield break; }
            if (!Read(text)) yield break;
            if (!Version.TryParse(Latest, out var v) || Norm(v) <= Current)
            {
                Now = State.UpToDate;
                HitCamMod.Log.Msg($"Update check: {CurrentText} is the latest (GitHub has {Latest})");
                yield break;
            }
            if (!auto && Settings.SkippedVersion.Value == Latest) { Now = State.Skipped; HitCamMod.Log.Msg($"Update check: {Latest} is out, you skipped it"); yield break; }
            Now = State.Available;
            HitCamMod.Log.Info($"Update check: version {Latest} is out (you have {CurrentText}). Main menu: Combat Overhaul box > Update.");
            if (auto) yield return Install();
        }

        static bool Read(string json)
        {
            try
            {
                var j = JObject.Parse(json);
                string tag = (string)j["tag_name"] ?? "";
                Latest = tag.Trim().TrimStart('v', 'V');
                Notes = Plain((string)j["body"] ?? "");
                _assetUrl = j["assets"]?.Children().Where(a => string.Equals((string)a["name"], DllName, StringComparison.OrdinalIgnoreCase))
                    .Select(a => (string)a["browser_download_url"]).FirstOrDefault();
                return true;
            }
            catch (Exception e) { Fail("GitHub's answer didn't read (" + e.Message + ")"); return false; }
        }

        public static void StartInstall() { if (Now == State.Available || Now == State.Skipped || Now == State.Failed && !string.IsNullOrEmpty(_assetUrl)) MelonCoroutines.Start(Install()); }

        public static void Skip()
        {
            if (Now != State.Available) return;
            Settings.SkippedVersion.Value = Latest;
            try { Settings.Cat.SaveToFile(false); } catch { }
            Now = State.Skipped;
        }

        static IEnumerator Install()
        {
            if (string.IsNullOrEmpty(_assetUrl)) { Fail($"release {Latest} has no {DllName} to download"); yield break; }
            Now = State.Downloading;
            byte[] data = null;
            string err = null;
            using (var req = UnityWebRequest.Get(_assetUrl))
            {
                req.SetRequestHeader("User-Agent", "GHPC-Combat-Overhaul/" + CurrentText);
                req.timeout = 90;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) data = req.downloadHandler.data;
                else err = req.error;
            }
            if (data == null) { Fail("download failed (" + err + ")"); yield break; }
            string problem = Put(data);
            if (problem != null) { Fail(problem); yield break; }
            Now = State.Installed;
            Settings.SkippedVersion.Value = "";
            try { Settings.Cat.SaveToFile(false); } catch { }
            HitCamMod.Log.Info($"Updated to {Latest}: it's used from the next start of the game (the old file is in UserData/HitCam/update)");
        }

        /// Checks the download and swaps it in. Null when done, else what was wrong (nothing is changed then).
        static string Put(byte[] data)
        {
            try
            {
                if (data.Length < 50_000 || data.Length > 50_000_000 || data[0] != (byte)'M' || data[1] != (byte)'Z') return "the download isn't a DLL";
                string dir = Path.Combine(HitCamMod.DataDir, "update");
                Directory.CreateDirectory(dir);
                string tmp = Path.Combine(dir, DllName + ".download");
                File.WriteAllBytes(tmp, data);
                var name = System.Reflection.AssemblyName.GetAssemblyName(tmp);
                if (!string.Equals(name.Name, "GHPCCombatOverhaul", StringComparison.OrdinalIgnoreCase)) { File.Delete(tmp); return "the download is another mod (" + name.Name + ")"; }
                if (!Version.TryParse(Latest, out var want) || Norm(name.Version) != Norm(want)) { File.Delete(tmp); return $"the download is version {name.Version}, not {Latest}"; }
                // the running DLL is mapped by the game and can't be written over, but it can be moved (same disk): it goes
                // to the update folder as the backup, and the new one takes its name
                string target = OwnFile();
                string old = Path.Combine(dir, $"GHPCCombatOverhaul-{CurrentText}.dll.old");
                try { if (File.Exists(old)) File.Delete(old); } catch { old = Path.Combine(dir, $"GHPCCombatOverhaul-{CurrentText}-{DateTime.Now:yyyyMMddHHmmss}.dll.old"); }
                bool moved = false;
                try
                {
                    if (File.Exists(target)) { File.Move(target, old); moved = true; }
                    File.Move(tmp, target);
                }
                catch (Exception e)
                {
                    try { if (moved && !File.Exists(target)) File.Move(old, target); } catch { }
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return "couldn't put it in place (" + e.Message + "); nothing was changed";
                }
                return null;
            }
            catch (Exception e) { return "couldn't put it in place (" + e.Message + ")"; }
        }

        /// Quits the game and starts it again once it has closed: a hidden PowerShell waits for this process to end, then
        /// starts the game through Steam (its app id from steam_appid.txt), or GHPC.exe itself without one.
        public static void Restart()
        {
            try
            {
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                string bin = Path.GetDirectoryName(Application.dataPath);
                string idFile = Path.Combine(bin, "steam_appid.txt");
                string id = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : "";
                string start = id.Length > 0 && id.All(char.IsDigit)
                    ? $"Start-Process 'steam://rungameid/{id}'"
                    : $"Start-Process -FilePath '{Path.Combine(bin, "GHPC.exe").Replace("'", "''")}' -WorkingDirectory '{bin.Replace("'", "''")}'";
                string cmd = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; {start}";
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{cmd}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
                HitCamMod.Log.Info("Restarting the game for the update (" + start + ")");
                Application.Quit();
            }
            catch (Exception e) { HitCamMod.Log.Warning("Restart: " + e.Message + " (close the game and start it again yourself)"); }
        }

        /// What an earlier update left in its folder (a failed download); its backups stay.
        public static void Tidy()
        {
            try
            {
                string tmp = Path.Combine(HitCamMod.DataDir, "update", DllName + ".download");
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }

        /// The mod's own file in Mods (where MelonLoader loaded it from).
        static string OwnFile()
        {
            try
            {
                string loc = HitCamMod.Instance?.MelonAssembly?.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc)) return loc;
            }
            catch { }
            return Path.Combine(MelonEnvironment.ModsDirectory, DllName);
        }

        static void Fail(string why)
        {
            Now = State.Failed;
            Error = why;
            HitCamMod.Log.Warning("Update: " + why);
        }

        static Version Norm(Version v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        /// GitHub's release notes (Markdown) as plain lines for the IMGUI box.
        static string Plain(string md)
        {
            var lines = md.Replace("\r", "").Split('\n').Select(l =>
            {
                string s = l.TrimEnd();
                while (s.StartsWith("#")) s = s.Substring(1);
                s = s.Replace("**", "").Replace("`", "");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!~>|])", "$1");   // Markdown's escapes: \[ \~ \*
                if (s.TrimStart().StartsWith("- ") || s.TrimStart().StartsWith("* ") || s.TrimStart().StartsWith("+ ")) s = "  •" + s.TrimStart().Substring(1);
                return s.Trim().Length == 0 ? "" : s;
            });
            return string.Join("\n", lines).Trim();
        }
    }
}
