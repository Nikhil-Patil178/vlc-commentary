// VLC Commentary - Watch mode
// A tiny tray app: press a hotkey while a movie plays in VLC, type a thought,
// press Enter. The note is saved with the exact movie timestamp.
//
// Compiled with the C# 5 compiler that ships with Windows (.NET Framework 4),
// so keep to C# 5 syntax: no $"" strings, no ?. operator, no nameof.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VlcCommentary
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                File.AppendAllText(Path.Combine(Config.Dir, "log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // One-shot mode, no UI:  VlcCommentary.exe --note "text" [Type]
            // Saves a note at VLC's current position. Handy for scripts and testing.
            if (args.Length >= 2 && args[0] == "--note")
            {
                try
                {
                    var cfg = Config.Load();
                    var st = new VlcClient(cfg).GetStatus();
                    if (st.State == "stopped" || (st.FileName == null && st.Title == null))
                    { Log("--note: nothing playing"); return 2; }
                    string type = args.Length >= 3 ? args[2] : NoteStore.Types[0];
                    NoteStore.Save(cfg, st, st.Seconds, type, args[1]);
                    Log("--note: saved at " + NoteStore.Fmt(st.Seconds) + " in " + NoteStore.MovieName(st));
                    return 0;
                }
                catch (Exception ex) { Log("--note failed: " + ex.Message); return 1; }
            }
            return RunTray();
        }

        static int RunTray()
        {
            bool created;
            using (var mutex = new Mutex(true, "VlcCommentary.WatchMode", out created))
            {
                if (!created)
                {
                    MessageBox.Show("VLC Commentary is already running.\nLook for its icon in the system tray.", "VLC Commentary");
                    return 0;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(Config.Load()));
                return 0;
            }
        }
    }

    // ------------------------------------------------------------------ settings

    class Config
    {
        public string Password = "";
        public int Port = 8765;
        public string Hotkey = "Ctrl+Alt+N";
        public bool AutoPause = false;
        public string NotesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VLC Commentary");

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VlcCommentary"); }
        }
        public static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

        public static Config Load()
        {
            var c = new Config();
            if (!File.Exists(FilePath)) return c;
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                int n;
                switch (k)
                {
                    case "password": c.Password = v; break;
                    case "port": if (int.TryParse(v, out n)) c.Port = n; break;
                    case "hotkey": if (v.Length > 0) c.Hotkey = v; break;
                    case "autopause": c.AutoPause = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "notes_dir": if (v.Length > 0) c.NotesDir = Environment.ExpandEnvironmentVariables(v); break;
                }
            }
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[] {
                "# VLC Commentary settings. Restart the app after editing.",
                "password=" + Password,
                "port=" + Port,
                "hotkey=" + Hotkey,
                "autopause=" + (AutoPause ? "true" : "false"),
                "notes_dir=" + NotesDir
            });
        }
    }

    // ------------------------------------------------------------------ VLC web interface

    class VlcStatus
    {
        public string State;     // playing | paused | stopped
        public double Seconds;   // current playback position
        public string FileName;
        public string Title;
    }

    class VlcClient
    {
        readonly Config cfg;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public VlcClient(Config c) { cfg = c; }

        Dictionary<string, object> Request(string query)
        {
            var req = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:" + cfg.Port + "/requests/status.json" + query);
            req.Timeout = 1500;
            req.Proxy = null;
            req.Headers["Authorization"] = "Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + cfg.Password));
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return json.DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
        }

        public VlcStatus GetStatus()
        {
            var d = Request("");
            var s = new VlcStatus();
            s.State = Str(d, "state");
            double time = Num(d, "time"), length = Num(d, "length"), pos = Num(d, "position");
            // "time" is whole seconds; position * length gives sub-second precision.
            s.Seconds = (length > 0 && pos > 0) ? pos * length : time;
            var meta = Dig(d, "information", "category", "meta");
            if (meta != null)
            {
                s.FileName = Str(meta, "filename");
                s.Title = Str(meta, "title");
            }
            return s;
        }

        public void Pause() { Request("?command=pl_forcepause"); }
        public void Resume() { Request("?command=pl_forceresume"); }

        static Dictionary<string, object> Dig(Dictionary<string, object> d, params string[] keys)
        {
            foreach (var k in keys)
            {
                object o;
                if (d == null || !d.TryGetValue(k, out o)) return null;
                d = o as Dictionary<string, object>;
            }
            return d;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            return d != null && d.TryGetValue(k, out o) && o != null
                ? Convert.ToString(o, CultureInfo.InvariantCulture) : null;
        }

        static double Num(Dictionary<string, object> d, string k)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return 0;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }

    // ------------------------------------------------------------------ note storage

    static class NoteStore
    {
        public static readonly string[] Types = { "Observation", "Interpretation", "Emotion", "Question" };

        public static string MovieName(VlcStatus st)
        {
            string n = st.FileName ?? st.Title ?? "Untitled";
            string noExt = Path.GetFileNameWithoutExtension(n);
            return string.IsNullOrEmpty(noExt) ? n : noExt;
        }

        // Appends a note to "<notes dir>\<movie>.json" and regenerates "<movie>.md".
        public static void Save(Config cfg, VlcStatus st, double seconds, string type, string content)
        {
            Directory.CreateDirectory(cfg.NotesDir);
            string baseName = SafeName(MovieName(st));
            string jsonPath = Path.Combine(cfg.NotesDir, baseName + ".json");
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            Dictionary<string, object> doc = null;
            if (File.Exists(jsonPath))
            {
                try { doc = ser.DeserializeObject(File.ReadAllText(jsonPath, Encoding.UTF8)) as Dictionary<string, object>; }
                catch
                {
                    // Never silently overwrite a file we can't read: set it aside.
                    File.Move(jsonPath, jsonPath + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                }
            }
            if (doc == null) doc = new Dictionary<string, object>();

            var notes = new List<object>();
            object existing;
            if (doc.TryGetValue("notes", out existing) && existing is object[])
                notes.AddRange((object[])existing);

            var note = new Dictionary<string, object>();
            note["id"] = Guid.NewGuid().ToString("N").Substring(0, 12);
            note["seconds"] = Math.Round(seconds, 2);
            note["timestamp"] = Fmt(seconds);
            note["type"] = type;
            note["status"] = "draft";
            note["duration"] = 4;
            note["content"] = content;
            note["created"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            notes.Add(note);
            notes.Sort((a, b) => Sec(a).CompareTo(Sec(b)));

            var output = new Dictionary<string, object>();
            output["version"] = 1;
            output["movie"] = baseName;
            output["file"] = st.FileName;
            output["notes"] = notes;

            WriteAtomic(jsonPath, ser.Serialize(output));
            WriteAtomic(Path.Combine(cfg.NotesDir, baseName + ".md"), ToMarkdown(baseName, notes));
        }

        static string ToMarkdown(string movie, List<object> notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + movie);
            sb.AppendLine();
            sb.AppendLine("_" + notes.Count + " note" + (notes.Count == 1 ? "" : "s") +
                          " - generated by VLC Commentary; edit the .json file, not this one_");
            foreach (Dictionary<string, object> n in notes)
            {
                sb.AppendLine();
                sb.AppendLine("## [" + n["timestamp"] + "] " + n["type"] + " - " + n["status"]);
                sb.AppendLine();
                sb.AppendLine(Convert.ToString(n["content"]));
            }
            return sb.ToString();
        }

        static double Sec(object o)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue("seconds", out v)) return 0;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static string Fmt(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            return s.Length == 0 ? "Untitled" : s;
        }

        static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    // ------------------------------------------------------------------ global hotkey

    class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        const int WM_HOTKEY = 0x0312;
        public event Action Pressed;

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public bool Register(string spec)
        {
            uint mods = 0x4000; // MOD_NOREPEAT
            Keys key = Keys.None;
            foreach (var part in spec.Split('+'))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= 0x2;
                else if (p == "alt") mods |= 0x1;
                else if (p == "shift") mods |= 0x4;
                else if (p == "win") mods |= 0x8;
                else
                {
                    try { key = (Keys)Enum.Parse(typeof(Keys), part.Trim(), true); }
                    catch { return false; }
                }
            }
            return key != Keys.None && RegisterHotKey(Handle, 1, mods, (uint)key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && Pressed != null) Pressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, 1);
            DestroyHandle();
        }
    }

    // ------------------------------------------------------------------ tray app

    class TrayApp : ApplicationContext
    {
        readonly Config cfg;
        readonly VlcClient vlc;
        readonly NotifyIcon tray;
        readonly HotkeyWindow hotkey;
        NoteBox openBox;

        public TrayApp(Config c)
        {
            cfg = c;
            vlc = new VlcClient(c);

            var menu = new ContextMenuStrip();
            var autoPause = new ToolStripMenuItem("Pause the movie while I type")
            {
                Checked = cfg.AutoPause,
                CheckOnClick = true
            };
            autoPause.CheckedChanged += delegate
            {
                cfg.AutoPause = autoPause.Checked;
                try { cfg.Save(); } catch { }
            };
            menu.Items.Add(autoPause);
            menu.Items.Add("Open notes folder", null, delegate
            {
                Directory.CreateDirectory(cfg.NotesDir);
                Process.Start("explorer.exe", "\"" + cfg.NotesDir + "\"");
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "VLC Commentary - " + cfg.Hotkey + " adds a note",
                ContextMenuStrip = menu,
                Visible = true
            };

            hotkey = new HotkeyWindow();
            hotkey.Pressed += OnHotkey;
            if (hotkey.Register(cfg.Hotkey))
                Tip("VLC Commentary is running", "Press " + cfg.Hotkey + " while a movie plays to add a note.");
            else
                Tip("Hotkey " + cfg.Hotkey + " is unavailable",
                    "Another app uses it. Change 'hotkey=' in " + Config.FilePath + " and restart.");
        }

        void OnHotkey()
        {
            Program.Log("hotkey pressed");
            if (openBox != null) { openBox.ShowAndFocus(); return; }

            VlcStatus st;
            try { st = vlc.GetStatus(); }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && r.StatusCode == HttpStatusCode.Unauthorized)
                    Tip("VLC rejected the password", "Run install.ps1 again, then restart VLC.");
                else
                    Tip("Can't reach VLC", "Open VLC first. If it's already open, close and reopen it once.");
                return;
            }
            catch (Exception ex) { Tip("Couldn't read VLC", ex.Message); return; }

            if (st.State == "stopped" || (st.FileName == null && st.Title == null))
            {
                Tip("Nothing is playing in VLC", "Start a movie, then press " + cfg.Hotkey + ".");
                return;
            }

            bool pausedByUs = false;
            if (cfg.AutoPause && st.State == "playing")
            {
                try { vlc.Pause(); pausedByUs = true; } catch { }
            }

            double seconds = st.Seconds;
            openBox = new NoteBox(NoteStore.Fmt(seconds), NoteStore.MovieName(st));
            openBox.Finished += delegate(string content, string type)
            {
                openBox = null;
                Program.Log(content == null ? "note cancelled" : "note saved at " + NoteStore.Fmt(seconds));
                if (content != null)
                {
                    try { NoteStore.Save(cfg, st, seconds, type, content); }
                    catch (Exception ex)
                    {
                        try { Clipboard.SetText(content); } catch { }
                        Tip("Note NOT saved (copied to clipboard)", ex.Message);
                    }
                }
                if (pausedByUs) { try { vlc.Resume(); } catch { } }
            };
            openBox.ShowAndFocus();
        }

        void Tip(string title, string text)
        {
            Program.Log(title + ": " + text);
            tray.ShowBalloonTip(4000, title, text, ToolTipIcon.None);
        }

        static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(255, 136, 0)))
                    g.FillEllipse(b, 1, 1, 30, 30);
                using (var p = new Pen(Color.White, 3.5f))
                {
                    g.DrawLine(p, 9, 11, 23, 11);
                    g.DrawLine(p, 9, 16, 23, 16);
                    g.DrawLine(p, 9, 21, 18, 21);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        protected override void ExitThreadCore()
        {
            hotkey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    // ------------------------------------------------------------------ the note popup

    class NoteBox : Form
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        public event Action<string, string> Finished; // content is null when cancelled

        readonly TextBox text;
        readonly Label footer;
        int typeIndex;
        bool done;

        public NoteBox(string timestamp, string movie)
        {
            float k;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 27);
            Opacity = 0.94;
            Size = new Size((int)(470 * k), (int)(170 * k));
            Padding = new Padding((int)(14 * k), (int)(10 * k), (int)(14 * k), (int)(8 * k));

            var editorPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(39, 39, 42),
                Padding = new Padding((int)(8 * k), (int)(6 * k), (int)(8 * k), (int)(6 * k))
            };
            text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                WordWrap = true,
                ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(39, 39, 42),
                ForeColor = Color.FromArgb(244, 244, 245),
                Font = new Font("Segoe UI", 11f)
            };
            editorPanel.Controls.Add(text);

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = (int)(28 * k),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(255, 160, 60),
                Font = new Font("Segoe UI Semibold", 10f),
                Text = "● " + timestamp + "    " + movie
            };
            footer = new Label
            {
                Dock = DockStyle.Bottom,
                Height = (int)(24 * k),
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = Color.FromArgb(161, 161, 170),
                Font = new Font("Segoe UI", 8.5f)
            };

            // Docking is laid out from the last-added control backwards: Fill goes first.
            Controls.Add(editorPanel);
            Controls.Add(header);
            Controls.Add(footer);
            UpdateFooter();

            // Top-right of the screen the mouse is on: clear of subtitles.
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            Location = new Point(screen.Right - Width - (int)(32 * k), screen.Top + (int)(32 * k));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Windows 11 rounded corners
        }

        public void ShowAndFocus()
        {
            Show();
            SetForegroundWindow(Handle);
            Activate();
            text.Focus();
        }

        void UpdateFooter()
        {
            footer.Text = "Type: " + NoteStore.Types[typeIndex] + " (Tab)    Enter save    Shift+Enter new line    Esc cancel";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Finish(null); return true; }
            if (keyData == Keys.Enter) { Finish(text.Text); return true; }
            if (keyData == Keys.Tab)
            {
                typeIndex = (typeIndex + 1) % NoteStore.Types.Length;
                UpdateFooter();
                return true;
            }
            if (keyData == (Keys.Shift | Keys.Tab))
            {
                typeIndex = (typeIndex + NoteStore.Types.Length - 1) % NoteStore.Types.Length;
                UpdateFooter();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // If focus moves away (you click the movie, or VLC grabs focus when a file
        // starts) an empty box just closes; a box with text stays put so nothing
        // you typed is lost or cut short. Press the hotkey again to jump back in.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (text.Text.Trim().Length == 0) Finish(null);
        }

        void Finish(string content)
        {
            if (done) return;
            done = true;
            if (content != null)
            {
                content = content.Trim();
                if (content.Length == 0) content = null;
            }
            if (Finished != null) Finished(content, NoteStore.Types[typeIndex]);
            BeginInvoke(new Action(Close));
        }
    }
}
