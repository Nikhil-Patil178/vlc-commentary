// VLC Commentary - installer / uninstaller
//
// One exe with the app embedded as a resource. Installs per-user (no admin):
//   %LOCALAPPDATA%\Programs\VLC Commentary\VlcCommentary.exe
// and sets up everything the app needs: VLC (via winget if missing), VLC's
// local web interface, FFmpeg (via winget) for subtitles, shortcuts, and an
// entry in Settings > Apps for uninstalling.
//
// Built together with src\*.cs (for Config and Subtitles.FindFfmpeg):
// see build.ps1. C# 5 syntax only.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VlcCommentary.Setup
{
    static class SetupProgram
    {
        public const string Version = "1.0.0";
        public const int DefaultPort = 8765;

        [STAThread]
        static int Main(string[] args)
        {
            bool uninstall = Array.IndexOf(args, "--uninstall") >= 0;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(uninstall));
            return 0;
        }
    }

    static class Paths
    {
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VLC Commentary"); }
        }
        public static string AppExe { get { return Path.Combine(InstallDir, "VlcCommentary.exe"); } }
        public static string Uninstaller { get { return Path.Combine(InstallDir, "Uninstall VLC Commentary.exe"); } }
        public static string Vlcrc
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc"); }
        }
        public static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "VLC Commentary.lnk"); }
        }
        public static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "VLC Commentary.lnk"); }
        }
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VlcCommentary";
    }

    // Each step is a plain static method so it can be exercised on its own.
    static class Steps
    {
        public static string FindVlc()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var k = hklm.OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
                    {
                        if (k == null) continue;
                        var exe = k.GetValue(null) as string;
                        if (exe != null && File.Exists(exe)) return exe;
                        var dir = k.GetValue("InstallDir") as string;
                        if (dir != null && File.Exists(Path.Combine(dir, "vlc.exe"))) return Path.Combine(dir, "vlc.exe");
                    }
                }
                catch { }
            }
            foreach (var v in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string pf = Environment.GetEnvironmentVariable(v);
                if (pf == null) continue;
                string p = Path.Combine(pf, "VideoLAN", "VLC", "vlc.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // Runs winget and forwards its readable output lines (not progress bars).
        // Throws Win32Exception if winget isn't available.
        public static int Winget(string args, Action<string> log)
        {
            var psi = new ProcessStartInfo("winget", args + " --accept-source-agreements --accept-package-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                p.StandardError.ReadToEndAsync();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    foreach (var part in line.Split('\r'))
                    {
                        string s = part.Trim();
                        if (s.Length > 3 && Regex.IsMatch(s, "[A-Za-z]{3}") && s.IndexOf('█') < 0 && s.IndexOf('▒') < 0)
                            log("    " + s);
                    }
                }
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static string RandomPassword()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[20];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        // First port from `preferred` that nothing is listening on (VLC is closed by now,
        // so anything listening belongs to another program, e.g. Steam on 8080).
        public static int PickPort(int preferred)
        {
            var used = new HashSet<int>();
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) used.Add(ep.Port);
            for (int p = preferred; p < preferred + 50; p++) if (!used.Contains(p)) return p;
            return preferred;
        }

        // Turns on VLC's web interface, localhost only, password-protected.
        // Backs up an existing vlcrc and returns the backup path (null if there was none).
        public static string ConfigureVlc(string vlcrc, int port, string password)
        {
            var utf8 = new UTF8Encoding(false);
            var lines = new List<string>();
            string backup = null;
            if (File.Exists(vlcrc))
            {
                backup = vlcrc + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(vlcrc, backup, true);
                lines.AddRange(File.ReadAllLines(vlcrc, utf8));
            }
            else Directory.CreateDirectory(Path.GetDirectoryName(vlcrc));

            SetOption(lines, "extraintf", "http");
            SetOption(lines, "http-host", "127.0.0.1");
            SetOption(lines, "http-port", port.ToString());
            SetOption(lines, "http-password", password);
            File.WriteAllLines(vlcrc, lines.ToArray(), utf8);
            return backup;
        }

        // Removes "http" from VLC's extra interfaces (used by uninstall).
        public static bool DisableVlcWeb(string vlcrc)
        {
            if (!File.Exists(vlcrc)) return false;
            var utf8 = new UTF8Encoding(false);
            var lines = new List<string>(File.ReadAllLines(vlcrc, utf8));
            bool changed = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var m = Regex.Match(lines[i], "^extraintf=(.*)$");
                if (!m.Success) continue;
                var keep = new List<string>();
                foreach (var part in m.Groups[1].Value.Split(':'))
                    if (part.Trim().Length > 0 && part.Trim() != "http") keep.Add(part.Trim());
                lines[i] = keep.Count == 0 ? "#extraintf=" : "extraintf=" + string.Join(":", keep);
                changed = true;
            }
            if (changed) File.WriteAllLines(vlcrc, lines.ToArray(), utf8);
            return changed;
        }

        static void SetOption(List<string> lines, string key, string value)
        {
            var rx = new Regex("^#?" + Regex.Escape(key) + "=(.*)$");
            for (int i = 0; i < lines.Count; i++)
            {
                var m = rx.Match(lines[i]);
                if (!m.Success) continue;
                if (key == "extraintf" && !lines[i].StartsWith("#"))
                {
                    // Keep any other extra interfaces the user already has.
                    var parts = new List<string>();
                    foreach (var p in m.Groups[1].Value.Split(':')) if (p.Trim().Length > 0) parts.Add(p.Trim());
                    if (!parts.Contains(value)) parts.Add(value);
                    value = string.Join(":", parts);
                }
                lines[i] = key + "=" + value;
                return;
            }
            // Fresh install without a vlcrc: VLC matches options by name, so appending works.
            lines.Add(key + "=" + value);
        }

        public static void StopApp()
        {
            foreach (var p in Process.GetProcessesByName("VlcCommentary"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }

        public static void ExtractApp(string dest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            using (var s = typeof(Steps).Assembly.GetManifestResourceStream("VlcCommentary.exe"))
            {
                if (s == null) throw new InvalidOperationException("This installer doesn't contain the app (build problem).");
                string tmp = dest + ".new";
                using (var f = File.Create(tmp)) s.CopyTo(f);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
        }

        public static void Shortcut(string link, string target, string args)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic s = shell.CreateShortcut(link);
            s.TargetPath = target;
            s.Arguments = args ?? "";
            s.WorkingDirectory = Path.GetDirectoryName(target);
            s.IconLocation = target + ",0";
            s.Save();
        }

        public static void RegisterUninstaller()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Paths.UninstallKey))
            {
                k.SetValue("DisplayName", "VLC Commentary");
                k.SetValue("DisplayVersion", SetupProgram.Version);
                k.SetValue("Publisher", "VLC Commentary");
                k.SetValue("DisplayIcon", Paths.AppExe);
                k.SetValue("InstallLocation", Paths.InstallDir);
                k.SetValue("UninstallString", "\"" + Paths.Uninstaller + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
    }

    class SetupForm : Form
    {
        readonly bool uninstall;
        readonly TextBox log;
        readonly Button action;
        readonly CheckBox startup;
        readonly CheckBox ffmpeg;
        bool finished;

        public SetupForm(bool uninstall)
        {
            this.uninstall = uninstall;
            Text = uninstall ? "Uninstall VLC Commentary" : "VLC Commentary Setup " + SetupProgram.Version;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(580, 460);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var top = new Panel { Dock = DockStyle.Top, Height = uninstall ? 110 : 190, Padding = new Padding(18, 14, 18, 0) };
            var title = new Label
            {
                Text = "VLC Commentary",
                Font = new Font("Segoe UI Semibold", 15f),
                ForeColor = Color.FromArgb(220, 110, 0),
                AutoSize = true,
                Location = new Point(18, 12)
            };
            var intro = new Label
            {
                Location = new Point(20, 50),
                Size = new Size(540, 80),
                Text = uninstall
                    ? "Removes the app, its shortcuts, and VLC's web interface setting.\n" +
                      "Your notes in Documents\\VLC Commentary are kept. VLC and FFmpeg stay installed."
                    : "Write timestamped notes while a movie plays: press Ctrl+Alt+N, type, press Enter.\n\n" +
                      "This sets up everything for your Windows account: the app, VLC (if missing),\n" +
                      "VLC's web interface (on this PC only, password-protected), and FFmpeg for subtitles."
            };
            top.Controls.Add(title);
            top.Controls.Add(intro);
            if (!uninstall)
            {
                startup = new CheckBox { Text = "Start VLC Commentary when I sign in", Checked = true, AutoSize = true, Location = new Point(20, 134) };
                ffmpeg = new CheckBox { Text = "Install FFmpeg so subtitles are saved with notes (about 100 MB)", Checked = true, AutoSize = true, Location = new Point(20, 158) };
                top.Controls.Add(startup);
                top.Controls.Add(ffmpeg);
            }

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(18, 10, 18, 12) };
            action = new Button { Text = uninstall ? "Uninstall" : "Install", Dock = DockStyle.Right, Width = 120 };
            action.Click += OnAction;
            bottom.Controls.Add(action);

            var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 6, 18, 0) };
            log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 9f)
            };
            logPanel.Controls.Add(log);

            Controls.Add(logPanel);
            Controls.Add(bottom);
            Controls.Add(top);
            AcceptButton = action;
        }

        void OnAction(object sender, EventArgs e)
        {
            if (finished) { Close(); return; }
            action.Enabled = false;
            if (startup != null) { startup.Enabled = false; ffmpeg.Enabled = false; }
            bool wantStartup = startup != null && startup.Checked;
            bool wantFfmpeg = ffmpeg != null && ffmpeg.Checked;
            var t = new Thread(delegate()
            {
                bool ok;
                try { ok = uninstall ? Uninstall() : Install(wantStartup, wantFfmpeg); }
                catch (Exception ex) { Log(""); Log("Something went wrong: " + ex.Message); ok = false; }
                BeginInvoke(new Action(delegate
                {
                    finished = true;
                    action.Text = "Close";
                    action.Enabled = true;
                    if (!ok && startup != null) { finished = false; action.Text = "Try again"; startup.Enabled = true; ffmpeg.Enabled = true; }
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            log.AppendText(line + Environment.NewLine);
        }

        bool Ask(string question, MessageBoxButtons buttons)
        {
            var r = (DialogResult)Invoke(new Func<DialogResult>(delegate
            {
                return MessageBox.Show(this, question, Text, buttons, MessageBoxIcon.Information);
            }));
            return r == DialogResult.Yes || r == DialogResult.OK || r == DialogResult.Retry;
        }

        bool WaitForVlcClosed(string why)
        {
            while (Process.GetProcessesByName("vlc").Length > 0)
                if (!Ask("Please close VLC " + why + ", then click Retry.", MessageBoxButtons.RetryCancel)) return false;
            return true;
        }

        bool Install(bool wantStartup, bool wantFfmpeg)
        {
            Log("Installing VLC Commentary " + SetupProgram.Version);
            Log("");

            // 1. VLC itself
            string vlc = Steps.FindVlc();
            if (vlc == null)
            {
                if (!Ask("VLC media player isn't installed. Install it now?\n\nWindows will ask for permission.", MessageBoxButtons.YesNo))
                { Log("Stopped: VLC is required."); return false; }
                Log("Installing VLC (this can take a minute)...");
                try { Steps.Winget("install --id VideoLAN.VLC -e --silent", Log); }
                catch (Win32Exception) { Log("  winget isn't available on this PC."); }
                vlc = Steps.FindVlc();
                if (vlc == null)
                {
                    Log("Couldn't install VLC automatically. Get it from https://www.videolan.org, then run this again.");
                    try { Process.Start("https://www.videolan.org/vlc/"); } catch { }
                    return false;
                }
            }
            Log("[ok] VLC: " + vlc);

            // 2. VLC must be closed while its settings change
            if (!WaitForVlcClosed("so its settings can be updated")) { Log("Stopped: VLC is still open."); return false; }

            // 3. The app
            Steps.StopApp();
            Steps.ExtractApp(Paths.AppExe);
            if (!string.Equals(Application.ExecutablePath, Paths.Uninstaller, StringComparison.OrdinalIgnoreCase))
                File.Copy(Application.ExecutablePath, Paths.Uninstaller, true);
            Log("[ok] App installed to " + Paths.InstallDir);

            // 4. App settings (keeps an existing password, hotkey and notes folder)
            var cfg = Config.Load();
            if (string.IsNullOrEmpty(cfg.Password)) cfg.Password = Steps.RandomPassword();
            cfg.Port = Steps.PickPort(cfg.Port > 0 ? cfg.Port : SetupProgram.DefaultPort);

            // 5. VLC's web interface
            string backup = Steps.ConfigureVlc(Paths.Vlcrc, cfg.Port, cfg.Password);
            Log("[ok] VLC web interface on 127.0.0.1:" + cfg.Port + (backup != null ? " (old VLC settings backed up)" : ""));

            // 6. FFmpeg for subtitles
            string ff = Subtitles.FindFfmpeg(cfg);
            if (ff == null && wantFfmpeg)
            {
                Log("Installing FFmpeg (this can take a minute)...");
                try { Steps.Winget("install --id Gyan.FFmpeg -e --silent", Log); }
                catch (Win32Exception) { Log("  winget isn't available on this PC."); }
                ff = Subtitles.FindFfmpeg(cfg);
            }
            if (ff != null) { cfg.Ffmpeg = ff; Log("[ok] FFmpeg: " + ff); }
            else Log("[--] No FFmpeg: subtitles inside videos won't be saved (separate .srt files still are).");
            cfg.Save();
            Log("[ok] Settings: " + Config.FilePath);

            // 7. Shortcuts and Settings > Apps entry
            Steps.Shortcut(Paths.StartMenuLink, Paths.AppExe, null);
            if (wantStartup) Steps.Shortcut(Paths.StartupLink, Paths.AppExe, null);
            else if (File.Exists(Paths.StartupLink)) File.Delete(Paths.StartupLink);
            Steps.RegisterUninstaller();
            Log("[ok] Start menu shortcut" + (wantStartup ? " + starts when you sign in" : ""));

            // 8. Go
            Process.Start(Paths.AppExe);
            Log("");
            Log("Done. VLC Commentary is running (orange icon in the system tray).");
            Log("Open a movie in VLC and press Ctrl+Alt+N to add a note.");
            Log("Notes are saved in: " + cfg.NotesDir);
            return true;
        }

        bool Uninstall()
        {
            Log("Uninstalling VLC Commentary");
            Steps.StopApp();
            foreach (var link in new[] { Paths.StartMenuLink, Paths.StartupLink })
                if (File.Exists(link)) File.Delete(link);
            Log("[ok] App stopped, shortcuts removed");

            if (WaitForVlcClosed("so its web interface can be turned off"))
            {
                if (Steps.DisableVlcWeb(Paths.Vlcrc)) Log("[ok] VLC web interface turned off");
            }
            else Log("[--] VLC was open, so its web interface was left on (Tools > Preferences > All > Interface > Main interfaces > Web).");

            try { Registry.CurrentUser.DeleteSubKeyTree(Paths.UninstallKey, false); } catch { }
            try { Directory.Delete(Config.Dir, true); } catch { }

            // This exe lives in the install folder, so delete the folder after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });

            Log("");
            Log("Done. Your notes are still in Documents\\VLC Commentary.");
            Log("VLC and FFmpeg are still installed (remove FFmpeg with: winget uninstall Gyan.FFmpeg).");
            return true;
        }
    }
}
