using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KosherLink
{
    /// <summary>Keeps the official WhatsApp for Android up to date inside the virtual phone.</summary>
    static class WhatsAppAndroid
    {
        // Official direct download published by WhatsApp on whatsapp.com/android
        const string ApkUrl = "https://www.whatsapp.com/android/current/WhatsApp.apk";
        public const string Package = "com.whatsapp";

        public static bool IsInstalled
        {
            get { return Proc.Shell("pm path " + Package).Contains("package:"); }
        }

        public static string InstalledVersion
        {
            get
            {
                var m = Regex.Match(Proc.Shell("dumpsys package " + Package), @"versionName=(\S+)");
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        /// <summary>Checks whatsapp.com for a newer build and installs it. Returns true if something was installed.</summary>
        public static bool Update(bool force, Action<int> percent)
        {
            string remote = Net.Fingerprint(ApkUrl);
            Config.Set("WaLastCheck", DateTime.UtcNow.ToString("o"));
            bool installed = IsInstalled;
            if (!force && installed && remote == Config.Get("WaFingerprint", ""))
            {
                Log.Write("וואטסאפ מעודכן (" + InstalledVersion + ")");
                return false;
            }

            string apk = Path.Combine(Paths.Downloads, "WhatsApp.apk");
            Log.Write(installed ? "נמצאה גרסת וואטסאפ חדשה, מורידה..." : "מוריד את וואטסאפ הרשמי...");
            Net.Download(ApkUrl, apk, null, (done, total) =>
            {
                if (total > 0 && percent != null) percent((int)(done * 100 / total));
            });

            Log.Write("מתקין את וואטסאפ בטלפון הווירטואלי...");
            string o;
            Proc.Run(Paths.Adb, "-s " + Proc.Serial + " install -r -g \"" + apk + "\"", out o, 10 * 60 * 1000);
            if (!o.Contains("Success"))
                throw new Exception("התקנת וואטסאפ נכשלה: " + o.Trim());

            Config.Set("WaFingerprint", remote);
            File.Delete(apk);
            Log.Write("וואטסאפ הותקן: גרסה " + InstalledVersion);
            Proc.Shell("cmd deviceidle whitelist +" + Package);
            Proc.Shell("cmd appops set " + Package + " RUN_ANY_IN_BACKGROUND allow");
            return true;
        }

        public static void Launch()
        {
            Proc.Shell("monkey -p " + Package + " -c android.intent.category.LAUNCHER 1");
        }

        public static bool DueForCheck(TimeSpan every)
        {
            DateTime last;
            if (!DateTime.TryParse(Config.Get("WaLastCheck", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out last)) return true;
            return DateTime.UtcNow - last > every;
        }
    }

    /// <summary>The official WhatsApp app for Windows (from Microsoft Store, which updates it by itself).</summary>
    static class WhatsAppDesktop
    {
        const string StoreId = "9NKSQGP7F2NH";

        /// <summary>Store apps keep a per-user folder named after their package family.</summary>
        static string FamilyName()
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (var d in Directory.GetDirectories(packages, "*.WhatsAppDesktop_*"))
                return Path.GetFileName(d);
            return null;
        }

        public static bool IsInstalled { get { return FamilyName() != null; } }

        public static void OpenStorePage()
        {
            Process.Start("ms-windows-store://pdp/?productid=" + StoreId);
        }

        public static bool Launch()
        {
            string fn = FamilyName();
            if (fn == null) return false;
            Process.Start("explorer.exe", "shell:AppsFolder\\" + fn + "!App");
            return true;
        }
    }

    /// <summary>Shows the virtual phone's screen on demand (scrcpy, open source, very light).</summary>
    static class PhoneViewer
    {
        static Process _proc;

        public static bool IsInstalled { get { return File.Exists(Paths.Scrcpy); } }

        public static void Install(Action<int> percent)
        {
            string json = Net.GetString("https://api.github.com/repos/Genymobile/scrcpy/releases/latest");
            var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*scrcpy-win64-[^\"]*\\.zip)\"");
            if (!m.Success) throw new Exception("לא נמצא קובץ הצגת המסך להורדה");
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string dir = Path.Combine(Paths.Tools, "scrcpy");
            if (IsInstalled && AndroidSdk.InstalledRev(dir) == tag) return;

            string zip = Path.Combine(Paths.Downloads, "scrcpy.zip");
            Log.Write("מוריד את רכיב הצגת המסך (" + tag + ")...");
            Net.Download(m.Groups[1].Value, zip, null, (d, t) => { if (t > 0 && percent != null) percent((int)(d * 100 / t)); });
            string tmp = Path.Combine(Paths.Root, "tmp-extract");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp);
            var tops = Directory.GetDirectories(tmp);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Paths.Tools);
            Directory.Move(tops.Length == 1 ? tops[0] : tmp, dir);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            File.WriteAllText(Path.Combine(dir, ".kosherlink-rev"), tag);
            File.Delete(zip);
        }

        public static bool IsOpen { get { return _proc != null && !_proc.HasExited; } }

        public static void Show()
        {
            if (IsOpen) { BringToFront(); return; }
            Emulator.ScreenOn();
            WhatsAppAndroid.Launch();
            var psi = Proc.Psi(Paths.Scrcpy,
                "-s " + Proc.Serial + " --no-audio --max-fps 30 --video-bit-rate 4M --stay-awake"
                + " --window-title \"KosherLink - הטלפון הווירטואלי\"");
            _proc = Process.Start(psi);
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (s, e) => { try { Emulator.ScreenOff(); } catch { } };
            BringToFront();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        /// <summary>Makes sure the phone window is not hidden behind other windows.</summary>
        public static void BringToFront()
        {
            if (!IsOpen) return;
            for (int i = 0; i < 40; i++) // the window appears a few seconds after start
            {
                _proc.Refresh();
                IntPtr h = _proc.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9); // restore if minimized
                    SetForegroundWindow(h);
                    return;
                }
                System.Threading.Thread.Sleep(250);
            }
        }
    }

    /// <summary>
    /// Self-update from the latest GitHub Release (config UpdateRepo, default KatzChaim/KosherLink):
    /// the asset KosherLink.exe is downloaded and must match the SHA-256 digest GitHub publishes for it.
    /// UpdateUrl may instead point to a JSON like {"version":"1.0.1","url":"...exe","sha256":"..."}.
    /// </summary>
    static class AppUpdater
    {
        public const string DefaultRepo = "KatzChaim/KosherLink";
        const string AssetName = "KosherLink.exe";

        public class Info { public string Version, Url, Sha256; }

        /// <summary>Reads what the update source currently offers (null if nothing usable).</summary>
        public static Info Latest()
        {
            string custom = Config.Get("UpdateUrl", "");
            if (custom.Length > 0)
            {
                string j = Net.GetString(custom);
                return new Info
                {
                    Version = Field(j, "version"),
                    Url = Field(j, "url"),
                    Sha256 = Field(j, "sha256")
                };
            }

            string repo = Config.Get("UpdateRepo", DefaultRepo);
            if (repo.Length == 0) return null;
            string json = Net.GetString("https://api.github.com/repos/" + repo + "/releases/latest");
            // the asset object: "name":"KosherLink.exe", ..., "digest":"sha256:...", ..., "browser_download_url":"..."
            var start = Regex.Match(json, "\"name\"\\s*:\\s*\"" + Regex.Escape(AssetName) + "\"");
            if (!start.Success) return null;
            int end = json.IndexOf("\"browser_download_url\"", start.Index, StringComparison.Ordinal);
            if (end < 0) return null;
            // from the asset's name up to (and including) its download URL
            string asset = json.Substring(start.Index, Math.Min(json.Length, end + 1000) - start.Index);
            return new Info
            {
                Version = Field(json, "tag_name").TrimStart('v', 'V'),
                Url = Field(asset, "browser_download_url"),
                Sha256 = Regex.Match(asset, "\"digest\"\\s*:\\s*\"sha256:([0-9a-fA-F]{64})\"").Groups[1].Value
            };
        }

        static string Field(string json, string name)
        {
            return Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        }

        public static bool IsNewer(Info i)
        {
            Version remote;
            return i != null && i.Url.Length > 0 && Version.TryParse(i.Version, out remote) && remote > new Version(Program.Version);
        }

        /// <summary>Downloads the update next to <paramref name="exe"/> as .new and verifies it. Returns the .new path.</summary>
        public static string Download(Info i, string exe)
        {
            if (i.Sha256.Length != 64) throw new Exception("לעדכון אין חתימת SHA-256 – לא מתקין");
            string newExe = exe + ".new";
            if (File.Exists(newExe)) File.Delete(newExe);
            Net.Download(i.Url, newExe, null, null);
            if (!Net.Sha256(newExe).Equals(i.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(newExe);
                throw new Exception("קובץ העדכון לא עבר בדיקת תקינות");
            }
            return newExe;
        }

        public static bool CheckAndApply()
        {
            var i = Latest();
            if (!IsNewer(i)) return false;

            Log.Write("נמצאה גרסה חדשה של התוכנה: " + i.Version + " – מוריד...");
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string newExe = Download(i, exe);
            // Windows allows renaming a running program: keep the old one aside, put the new one in place.
            string oldExe = exe + ".old";
            if (File.Exists(oldExe)) File.Delete(oldExe);
            File.Move(exe, oldExe);
            File.Move(newExe, exe);
            Log.Write("התוכנה עודכנה לגרסה " + i.Version + " – מפעיל מחדש");
            Process.Start(exe, "--tray --after-update");
            return true; // caller exits the app; the new version deletes the .old file
        }
    }

    static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "KosherLink";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k != null && k.GetValue(Name) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value) k.SetValue(Name, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray");
                    else k.DeleteValue(Name, false);
                }
            }
        }
    }
}
