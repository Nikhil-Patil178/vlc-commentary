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
using System.Security.Cryptography;
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
                    if (!st.HasMedia) { Log("--note: nothing playing"); return 2; }
                    var movie = MovieRef.From(st);
                    string type = args.Length >= 3 ? args[2] : NoteStore.Types[0];
                    int count;
                    string file = NoteStore.Save(cfg, movie, st.Seconds, type, args[1], out count);
                    Log("--note: saved at " + NoteStore.Fmt(st.Seconds) + " to " + file);
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

    // ------------------------------------------------------------------ crash-safe file writing

    static class SafeFile
    {
        // Write to a temp file, force it onto the physical disk, then swap it in.
        // A power cut leaves either the old file or the new one, never half of one.
        public static void Write(string path, string text)
        {
            string tmp = path + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                           4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(tmp, path, null); return; }
                catch (IOException)
                {
                    // OneDrive or antivirus can briefly lock the file; retry, then fall back to copy.
                    if (attempt < 3) { Thread.Sleep(150); continue; }
                    File.Copy(tmp, path, true);
                    File.Delete(tmp);
                    return;
                }
            }
        }
    }

    // ------------------------------------------------------------------ VLC web interface

    class VlcStatus
    {
        public string State;     // playing | paused | stopped
        public double Seconds;   // current playback position
        public string FileName;
        public string Title;
        public string Uri;       // full location of the current item, e.g. file:///C:/Movies/x.mkv

        public bool HasMedia { get { return State != "stopped" && (FileName != null || Title != null); } }
    }

    class VlcClient
    {
        readonly Config cfg;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public VlcClient(Config c) { cfg = c; }

        Dictionary<string, object> Request(string path)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + cfg.Port + path);
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
            var d = Request("/requests/status.json");
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
            // status.json has no file path; the playlist marks the current item and has its URI.
            try { s.Uri = FindCurrentUri(Request("/requests/playlist.json")); }
            catch (Exception ex) { Program.Log("playlist lookup failed: " + ex.Message); }
            return s;
        }

        public void Pause() { Request("/requests/status.json?command=pl_forcepause"); }
        public void Resume() { Request("/requests/status.json?command=pl_forceresume"); }

        static string FindCurrentUri(Dictionary<string, object> node)
        {
            if (node == null) return null;
            if (Str(node, "current") == "current" && Str(node, "uri") != null) return Str(node, "uri");
            object kids;
            if (node.TryGetValue("children", out kids) && kids is object[])
                foreach (var k in (object[])kids)
                {
                    var found = FindCurrentUri(k as Dictionary<string, object>);
                    if (found != null) return found;
                }
            return null;
        }

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

        internal static string Str(Dictionary<string, object> d, string k)
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

    // ------------------------------------------------------------------ which movie is this?

    // Identifies a movie by its content, not its name, so two different films that
    // happen to share a filename never share notes, and renaming or moving a film
    // keeps its notes attached.
    class MovieRef
    {
        public string Id;        // file:<size>:<hash>, or uri:/name: for streams
        public string Name;      // display name (filename without extension)
        public string FileName;
        public string Location;  // last known full path

        public static MovieRef From(VlcStatus st)
        {
            var m = new MovieRef();
            m.FileName = st.FileName;
            m.Name = NoteStore.MovieName(st);
            if (st.Uri != null)
            {
                try
                {
                    var u = new Uri(st.Uri);
                    if (u.IsFile && File.Exists(u.LocalPath))
                    {
                        m.Location = u.LocalPath;
                        m.Id = Fingerprint(u.LocalPath);
                    }
                }
                catch (Exception ex) { Program.Log("fingerprint failed: " + ex.Message); }
                if (m.Id == null) m.Id = "uri:" + st.Uri;
            }
            if (m.Id == null) m.Id = "name:" + (st.FileName ?? st.Title ?? "untitled");
            return m;
        }

        // File size + MD5 of the first and last 64 KB. Reads 128 KB, so it's instant
        // even for a 50 GB file.
        static string Fingerprint(string path)
        {
            const int chunk = 65536;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var md5 = MD5.Create())
            {
                long len = fs.Length;
                byte[] head = ReadAt(fs, 0, (int)Math.Min(chunk, len));
                byte[] tail = len > chunk ? ReadAt(fs, Math.Max(chunk, len - chunk), (int)Math.Min(chunk, len - chunk)) : new byte[0];
                md5.TransformBlock(head, 0, head.Length, null, 0);
                md5.TransformFinalBlock(tail, 0, tail.Length);
                return "file:" + len + ":" + BitConverter.ToString(md5.Hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        static byte[] ReadAt(FileStream fs, long offset, int count)
        {
            var buf = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            int got = 0;
            while (got < count)
            {
                int n = fs.Read(buf, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return buf;
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

        // Appends a note to this movie's .json file (found by movie id, not by name)
        // and regenerates the matching .md. Returns the .json path.
        // All notes saved so far for this movie, in time order (empty if none).
        public static List<Dictionary<string, object>> Load(Config cfg, MovieRef movie)
        {
            var result = new List<Dictionary<string, object>>();
            try
            {
                if (!Directory.Exists(cfg.NotesDir)) return result;
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string path = FindExisting(cfg.NotesDir, movie, ser);
                if (path == null) return result;
                var doc = ser.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                object arr;
                if (doc != null && doc.TryGetValue("notes", out arr) && arr is object[])
                    foreach (var o in (object[])arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d != null) result.Add(d);
                    }
            }
            catch (Exception ex) { Program.Log("loading saved notes failed: " + ex.Message); }
            return result;
        }

        public static string Save(Config cfg, MovieRef movie, double seconds, string type, string content, out int count)
        {
            Directory.CreateDirectory(cfg.NotesDir);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string jsonPath = FindExisting(cfg.NotesDir, movie, ser) ?? NewFileName(cfg.NotesDir, movie);

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
            output["version"] = 2;
            output["movie_id"] = movie.Id;
            output["movie"] = movie.Name;
            output["file"] = movie.FileName;
            output["path"] = movie.Location;
            output["notes"] = notes;

            SafeFile.Write(jsonPath, ser.Serialize(output));
            SafeFile.Write(Path.ChangeExtension(jsonPath, ".md"), ToMarkdown(movie.Name, notes));
            count = notes.Count;
            return jsonPath;
        }

        // The movie's existing file if any note file carries its id; otherwise a new
        // file named after the movie, with " (2)", " (3)"... if that name is taken by
        // a different movie.
        static string FindExisting(string dir, MovieRef movie, JavaScriptSerializer ser)
        {
            string legacyMatch = null;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var d = ser.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                    string id = VlcClient.Str(d, "movie_id");
                    if (id == movie.Id) return f;
                    // Files from the first version have no movie_id. Adopt one with the
                    // same filename; saving stamps the id, so it's matched by id from then on.
                    if (id == null && legacyMatch == null && movie.FileName != null &&
                        VlcClient.Str(d, "file") == movie.FileName)
                        legacyMatch = f;
                }
                catch { }
            }
            return legacyMatch;
        }

        static string NewFileName(string dir, MovieRef movie)
        {
            string baseName = SafeName(movie.Name);
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, baseName + (i == 1 ? "" : " (" + i + ")") + ".json");
                if (!File.Exists(p)) return p;
            }
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
    }

    // ------------------------------------------------------------------ unsaved-note recovery

    // While the note box is open, what you've typed is copied to disk every couple
    // of seconds, together with the movie and timestamp it belongs to. After a crash
    // or power cut, the next hotkey press reopens that exact note.
    class Draft
    {
        public MovieRef Movie;
        public double Seconds;
        public int TypeIndex;
        public string Text = "";
    }

    static class Recovery
    {
        static string FilePath { get { return Path.Combine(Config.Dir, "unsaved-note.json"); } }

        public static void Save(Draft d)
        {
            Directory.CreateDirectory(Config.Dir);
            SafeFile.Write(FilePath, new JavaScriptSerializer().Serialize(d));
        }

        public static Draft Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var d = new JavaScriptSerializer().Deserialize<Draft>(File.ReadAllText(FilePath, Encoding.UTF8));
                return (d != null && d.Movie != null && !string.IsNullOrEmpty(d.Text)) ? d : null;
            }
            catch (Exception ex) { Program.Log("recovery file unreadable: " + ex.Message); return null; }
        }

        public static void Clear()
        {
            try { File.Delete(FilePath); } catch { }
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
        readonly System.Windows.Forms.Timer draftTimer;
        NoteBox openBox;
        Draft current;
        bool draftDirty;

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

            draftTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            draftTimer.Tick += delegate { PersistDraft(); };

            hotkey = new HotkeyWindow();
            hotkey.Pressed += OnHotkey;
            bool ok = hotkey.Register(cfg.Hotkey);

            var pending = Recovery.Load();
            if (!ok)
                Tip("Hotkey " + cfg.Hotkey + " is unavailable",
                    "Another app uses it. Change 'hotkey=' in " + Config.FilePath + " and restart.");
            else if (pending != null)
                Tip("You have an unsaved note",
                    pending.Movie.Name + " at " + NoteStore.Fmt(pending.Seconds) + ". Press " + cfg.Hotkey + " to finish it.");
            else
                Tip("VLC Commentary is running", "Press " + cfg.Hotkey + " while a movie plays to add a note.");
        }

        void OnHotkey()
        {
            Program.Log("hotkey pressed");
            if (openBox != null) { openBox.ShowAndFocus(); return; }

            // A note left unfinished by a crash comes back first, still tied to its
            // own movie and timestamp, whatever is playing now.
            var pending = Recovery.Load();
            if (pending != null)
            {
                Program.Log("reopening recovered note for " + pending.Movie.Name);
                OpenBox(pending, true, false);
                return;
            }

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

            if (!st.HasMedia)
            {
                Tip("Nothing is playing in VLC", "Start a movie, then press " + cfg.Hotkey + ".");
                return;
            }

            var draft = new Draft { Movie = MovieRef.From(st), Seconds = st.Seconds };
            Program.Log("note started: " + draft.Movie.Id + " at " + NoteStore.Fmt(draft.Seconds));

            bool pausedByUs = false;
            if (cfg.AutoPause && st.State == "playing")
            {
                try { vlc.Pause(); pausedByUs = true; } catch { }
            }
            OpenBox(draft, false, pausedByUs);
        }

        void OpenBox(Draft draft, bool recovered, bool pausedByUs)
        {
            current = draft;
            draftDirty = false;
            var saved = NoteStore.Load(cfg, draft.Movie);
            openBox = new NoteBox(draft.Seconds, draft.Movie.Name, draft.Text, draft.TypeIndex, recovered, saved);
            openBox.Changed += delegate(string text, int typeIndex)
            {
                current.Text = text;
                current.TypeIndex = typeIndex;
                draftDirty = true;
            };
            openBox.Finished += delegate(string content, string type)
            {
                draftTimer.Stop();
                openBox = null;
                current = null;
                if (content == null)
                {
                    Program.Log("note cancelled");
                    Recovery.Clear();
                }
                else
                {
                    try
                    {
                        int count;
                        string file = NoteStore.Save(cfg, draft.Movie, draft.Seconds, type, content, out count);
                        Recovery.Clear();
                        Program.Log("note saved at " + NoteStore.Fmt(draft.Seconds) + " (" + count + " total) to " + file);
                    }
                    catch (Exception ex)
                    {
                        // Keep the recovery copy so the note can be retried.
                        try { draft.Text = content; Recovery.Save(draft); } catch { }
                        try { Clipboard.SetText(content); } catch { }
                        Program.Log("save failed: " + ex.Message);
                        Toast.Pop("✗ NOT saved", "Copied to clipboard. " + ex.Message, false);
                    }
                }
                if (pausedByUs) ResumeIfSameMovie(draft.Movie);
            };
            draftTimer.Start();
            openBox.ShowAndFocus();
        }

        void PersistDraft()
        {
            if (!draftDirty || current == null) return;
            draftDirty = false;
            try
            {
                if (current.Text.Trim().Length > 0) Recovery.Save(current);
                else Recovery.Clear();
            }
            catch (Exception ex) { Program.Log("draft autosave failed: " + ex.Message); }
        }

        // If you switched movies while the box was open, don't start the new one.
        void ResumeIfSameMovie(MovieRef movie)
        {
            try
            {
                var now = vlc.GetStatus();
                if (now.HasMedia && MovieRef.From(now).Id == movie.Id) vlc.Resume();
            }
            catch { }
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
            PersistDraft();
            hotkey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }

    // ------------------------------------------------------------------ "Saved" confirmation

    // A small notice in the corner that never takes focus, so the movie keeps
    // the keyboard. Disappears on its own.
    class Toast : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        static Toast showing;

        public static void Pop(string title, string detail, bool ok)
        {
            if (showing != null) showing.Close();
            showing = new Toast(title, detail, ok);
            showing.Show();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */ | 0x8 /* TOPMOST */;
                return cp;
            }
        }

        Toast(string title, string detail, bool ok)
        {
            float k;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 27);
            Opacity = 0.94;
            Size = new Size((int)(320 * k), (int)(60 * k));
            Padding = new Padding((int)(14 * k), (int)(8 * k), (int)(14 * k), (int)(6 * k));

            var top = new Label
            {
                Dock = DockStyle.Top,
                Height = (int)(24 * k),
                Font = new Font("Segoe UI Semibold", 10.5f),
                ForeColor = ok ? Color.FromArgb(74, 222, 128) : Color.FromArgb(248, 113, 113),
                Text = title
            };
            var bottom = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(212, 212, 216),
                Text = detail
            };
            Controls.Add(bottom);
            Controls.Add(top);

            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            Location = new Point(screen.Right - Width - (int)(32 * k), screen.Top + (int)(32 * k));

            var timer = new System.Windows.Forms.Timer { Interval = ok ? 1800 : 6000 };
            timer.Tick += delegate { timer.Stop(); timer.Dispose(); Close(); };
            timer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (showing == this) showing = null;
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ the note popup

    class NoteBox : Form
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        public event Action<string, string> Finished; // content is null when cancelled
        public event Action<string, int> Changed;     // text or type edited

        readonly TextBox text;
        readonly Label footer;
        readonly Label history;
        readonly int historyHeight;
        readonly System.Windows.Forms.Timer holdTimer;
        int typeIndex;
        bool done;
        bool tabDown;
        bool historyShown;

        public NoteBox(double seconds, string movie, string initialText, int initialType, bool recovered,
                       List<Dictionary<string, object>> saved)
        {
            float k;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            string timestamp = NoteStore.Fmt(seconds);
            KeyPreview = true;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 27);
            Opacity = 0.94;
            Size = new Size((int)(470 * k), (int)(170 * k));
            Padding = new Padding((int)(14 * k), (int)(10 * k), (int)(14 * k), (int)(8 * k));
            typeIndex = Math.Max(0, Math.Min(NoteStore.Types.Length - 1, initialType));

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
                Font = new Font("Segoe UI", 11f),
                Text = initialText ?? ""
            };
            text.TextChanged += delegate { RaiseChanged(); };
            editorPanel.Controls.Add(text);

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = (int)(28 * k),
                AutoEllipsis = true,
                ForeColor = recovered ? Color.FromArgb(250, 204, 21) : Color.FromArgb(255, 160, 60),
                Font = new Font("Segoe UI Semibold", 10f),
                Text = (recovered ? "RECOVERED  " : "\u25CF ") + timestamp + "    " + movie
            };
            footer = new Label
            {
                Dock = DockStyle.Bottom,
                Height = (int)(24 * k),
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = Color.FromArgb(161, 161, 170),
                Font = new Font("Segoe UI", 8.5f)
            };

            // Past notes for this movie, shown while Tab is held down.
            string historyText = BuildHistory(saved, seconds, timestamp);
            historyHeight = (int)((historyText.Split('\n').Length * 19 + 14) * k);
            history = new Label
            {
                Dock = DockStyle.Bottom,
                Height = historyHeight,
                Visible = false,
                BackColor = Color.FromArgb(32, 32, 36),
                ForeColor = Color.FromArgb(212, 212, 216),
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding((int)(8 * k), (int)(6 * k), (int)(8 * k), (int)(4 * k)),
                Text = historyText
            };
            holdTimer = new System.Windows.Forms.Timer { Interval = 300 };
            holdTimer.Tick += delegate { holdTimer.Stop(); ShowHistory(true); };

            // Docking is laid out from the last-added control backwards: Fill goes first.
            Controls.Add(editorPanel);
            Controls.Add(header);
            Controls.Add(footer);
            Controls.Add(history);
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
            text.SelectionStart = text.Text.Length;
        }

        void RaiseChanged()
        {
            if (Changed != null) Changed(text.Text, typeIndex);
        }

        void UpdateFooter()
        {
            footer.Text = NoteStore.Types[typeIndex] + " (tap Tab)    Hold Tab: past notes    Enter: save    Esc: cancel";
        }

        void CycleType(int step)
        {
            typeIndex = (typeIndex + step + NoteStore.Types.Length) % NoteStore.Types.Length;
            UpdateFooter();
            RaiseChanged();
        }

        void ShowHistory(bool on)
        {
            if (on == historyShown) return;
            historyShown = on;
            history.Visible = on;
            Height += on ? historyHeight : -historyHeight;
        }

        // Saved notes in time order with a marker where this note will land.
        // Shows up to 7 lines around the marker.
        static string BuildHistory(List<Dictionary<string, object>> saved, double now, string nowTs)
        {
            if (saved.Count == 0) return "No saved notes for this movie yet. This will be the first.";
            var lines = new List<string>();
            int marker = -1;
            foreach (var n in saved)
            {
                object v;
                double s = n.TryGetValue("seconds", out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
                if (marker < 0 && s > now) { marker = lines.Count; lines.Add("▶ " + nowTs + "   this note"); }
                string content = n.TryGetValue("content", out v) ? Convert.ToString(v) : "";
                content = content.Replace("\r", " ").Replace("\n", " ");
                if (content.Length > 58) content = content.Substring(0, 57) + "…";
                lines.Add((n.TryGetValue("timestamp", out v) ? Convert.ToString(v) : "") + "   " + content);
            }
            if (marker < 0) { marker = lines.Count; lines.Add("▶ " + nowTs + "   this note"); }

            const int max = 7;
            int start = Math.Max(0, Math.Min(marker - max / 2, lines.Count - max));
            var sb = new StringBuilder();
            sb.AppendLine("Saved for this movie: " + saved.Count);
            for (int i = start; i < Math.Min(lines.Count, start + max); i++) sb.AppendLine(lines[i]);
            return sb.ToString().TrimEnd();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Finish(null); return true; }
            if (keyData == Keys.Enter) { Finish(text.Text); return true; }
            if (keyData == Keys.Tab)
            {
                // Tap = change type (decided on release); hold = show past notes.
                bool autoRepeat = ((long)msg.LParam & 0x40000000) != 0;
                if (!autoRepeat && !tabDown) { tabDown = true; holdTimer.Start(); }
                return true;
            }
            if (keyData == (Keys.Shift | Keys.Tab)) { CycleType(-1); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Tab && tabDown)
            {
                tabDown = false;
                e.Handled = true;
                if (historyShown) ShowHistory(false);
                else { holdTimer.Stop(); CycleType(1); }
                return;
            }
            base.OnKeyUp(e);
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
