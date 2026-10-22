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
    /// update check: once per game start (in the main menu), latest release on the mod's GitHub page. a newer one shows
    /// up in the main menu's Combat Overhaul box; one click downloads that release's GHPCCombatOverhaul.dll, checks it
    /// really is this mod at that version, keeps the old file in UserData/HitCam/update and puts the new one in Mods
    /// (game loads it next start). UpdateCheck: Ask (default), Auto (installs by itself), Off
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

        /// self-test: update_feed=<url> stands in for the GitHub API (local file:// JSON with its own DLL)
        static string Feed => AutoTest.Active && AutoTest.Opt("update_feed", "").Length > 0 ? AutoTest.Opt("update_feed", "") : Api;

        public static void OnMenu()
        {
            if (_started) return;
            // self-test only asks GitHub when told to (updatecheck=true or its own update_feed)
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
            string text = null, err = null, left = null, reset = null;
            long code = 0;
            using (var req = UnityWebRequest.Get(Feed))
            {
                req.SetRequestHeader("User-Agent", "GHPC-Combat-Overhaul/" + CurrentText);
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) text = req.downloadHandler.text;
                else err = req.error;
                code = req.responseCode;
                left = req.GetResponseHeader("X-RateLimit-Remaining");
                reset = req.GetResponseHeader("X-RateLimit-Reset");
            }
            if (text == null)
            {
                // GitHub API gives 60 requests an hour per connection and GHPC Mod Manager burns them all on start (one per mod
                // in its catalog). release page itself doesn't count: version comes from where it redirects, the DLL from the
                // release's own download link (notes stay on the page)
                bool limited = (code == 403 || code == 429) && (left == "0" || code == 429);
                string tag = null;
                if (limited && Feed == Api && !AutoTest.Opt("update_nopage", false)) yield return TagFromPage(t => tag = t);
                if (tag != null)
                {
                    Latest = tag.TrimStart('v', 'V');
                    Notes = "The notes are on the release page: " + Page;
                    _assetUrl = $"https://github.com/{Repo}/releases/download/{tag}/{DllName}";
                    HitCamMod.Log.Msg($"Update check: GitHub's API limit is used up for now (HTTP {code}); its release page says {tag}");
                }
                else if (limited)
                {
                    var at = ResetTime(reset);
                    Fail(at != null ? $"GitHub's limit is used up for now (GHPC Mod Manager uses it too), trying again at {at.Value:HH:mm}"
                                    : "GitHub's limit is used up for now (GHPC Mod Manager uses it too), trying again next start",
                         $"HTTP {code}, requests left {left}, reset {reset}");
                    if (at != null && (at.Value - DateTime.Now).TotalMinutes < 70) MelonCoroutines.Start(RetryAt(at.Value, auto));
                    yield break;
                }
                else { Fail("can't reach GitHub right now, trying again next start", err); yield break; }
            }
            else if (!Read(text)) yield break;
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

        /// latest release tag from where the release page redirects (github.com/.../releases/latest ->
        /// .../releases/tag/v1.1.1), null if that failed
        static IEnumerator TagFromPage(Action<string> got)
        {
            string loc = null;
            using (var req = UnityWebRequest.Head(Page))
            {
                req.SetRequestHeader("User-Agent", "GHPC-Combat-Overhaul/" + CurrentText);
                req.redirectLimit = 0;
                req.timeout = 15;
                yield return req.SendWebRequest();
                loc = req.GetResponseHeader("Location");
            }
            int i = loc != null ? loc.LastIndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase) : -1;
            string tag = i >= 0 ? Uri.UnescapeDataString(loc.Substring(i + "/releases/tag/".Length)).Trim('/') : null;
            got(!string.IsNullOrEmpty(tag) && Version.TryParse(tag.TrimStart('v', 'V'), out _) ? tag : null);
        }

        /// when GitHub's API takes requests again (its X-RateLimit-Reset, seconds since 1970)
        static DateTime? ResetTime(string header)
        {
            if (!long.TryParse(header, out long s) || s <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime; } catch { return null; }
        }

        /// one more check once the limit is over (if the game is still running then)
        static IEnumerator RetryAt(DateTime at, bool auto)
        {
            float wait = (float)Math.Max(5.0, (at - DateTime.Now).TotalSeconds + 10.0);
            yield return new WaitForSecondsRealtime(wait);
            if (Now == State.Failed) yield return Check(auto);
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

        /// checks the download and swaps it in. null when done, else what was wrong (nothing changed then)
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
                // running DLL is mapped by the game and can't be overwritten, but it can be moved (same disk): it goes to the
                // update folder as backup and the new one takes its name
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

        /// quits the game and starts it again once closed: hidden PowerShell waits for this process to end, then starts
        /// the game through Steam (app id from steam_appid.txt), or GHPC.exe itself if there's none
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

        /// what an earlier update left in its folder (failed download), backups stay
        public static void Tidy()
        {
            try
            {
                string tmp = Path.Combine(HitCamMod.DataDir, "update", DllName + ".download");
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch { }
        }

        /// the mod's own file in Mods (where MelonLoader loaded it from)
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

        /// what the menu box says; details (HTTP error etc) go to the log only
        static void Fail(string why, string detail = null)
        {
            Now = State.Failed;
            Error = why;
            HitCamMod.Log.Warning("Update: " + why + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
        }

        static Version Norm(Version v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        /// GitHub release notes (Markdown) as plain lines for the IMGUI box
        static string Plain(string md)
        {
            var lines = md.Replace("\r", "").Split('\n').Select(l =>
            {
                string s = l.TrimEnd();
                while (s.StartsWith("#")) s = s.Substring(1);
                s = s.Replace("**", "").Replace("`", "");
                s = System.Text.RegularExpressions.Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!~>|])", "$1");   // Markdown escapes: \[ \~ \*
                if (s.TrimStart().StartsWith("- ") || s.TrimStart().StartsWith("* ") || s.TrimStart().StartsWith("+ ")) s = "  •" + s.TrimStart().Substring(1);
                return s.Trim().Length == 0 ? "" : s;
            });
            return string.Join("\n", lines).Trim();
        }
    }
}
