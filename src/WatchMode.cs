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
            // VLC starts us with --with-vlc every time it opens (see the Lua hook the
            // installer puts in VLC's lua\intf folder), and we quit when VLC closes.
            bool withVlc = args.Length >= 1 && args[0] == "--with-vlc";
            return RunTray(withVlc);
        }

        static int RunTray(bool withVlc)
        {
            bool created;
            using (var mutex = new Mutex(true, "VlcCommentary.WatchMode", out created))
            {
                if (!created)
                {
                    // Opening a second VLC window shouldn't nag; we're already there.
                    if (!withVlc)
                        MessageBox.Show("VLC Commentary is already running.\nLook for its icon in the system tray.", "VLC Commentary");
                    return 0;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(Config.Load(), withVlc));
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
        public string Ffmpeg = "";   // optional explicit path; found automatically otherwise
        public bool Phone = false;   // phone remote on your home Wi-Fi
        public int PhonePort = 8780;
        public string PhoneToken = ""; // secret part of the phone link, made on first use
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
                    case "ffmpeg": c.Ffmpeg = Environment.ExpandEnvironmentVariables(v); break;
                    case "phone": c.Phone = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "phone_port": if (int.TryParse(v, out n)) c.PhonePort = n; break;
                    case "phone_token": c.PhoneToken = v; break;
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
                "notes_dir=" + NotesDir,
                "ffmpeg=" + Ffmpeg,
                "phone=" + (Phone ? "true" : "false"),
                "phone_port=" + PhonePort,
                "phone_token=" + PhoneToken
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
        public double Length;    // movie length in seconds (0 if unknown)
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
            s.Length = length;
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
        public void TogglePause() { Request("/requests/status.json?command=pl_pause"); }
        // seconds > 0 jumps forward, < 0 jumps back.
        public void SeekBy(int seconds)
        {
            Request("/requests/status.json?command=seek&val=" + (seconds >= 0 ? "%2B" : "-") + Math.Abs(seconds) + "s");
        }

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

        // The phone and the keyboard can save at the same moment; one at a time.
        static readonly object SaveLock = new object();

        public static string Save(Config cfg, MovieRef movie, double seconds, string type, string content, out int count)
        {
            lock (SaveLock) return SaveCore(cfg, movie, seconds, type, content, out count);
        }

        static string SaveCore(Config cfg, MovieRef movie, double seconds, string type, string content, out int count)
        {
            Directory.CreateDirectory(cfg.NotesDir);
            MigrateFlatFiles(cfg);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string jsonPath = FindExisting(cfg.NotesDir, movie, ser) ?? NewFileName(cfg.NotesDir, movie);
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));

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
            foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
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

        // Each movie gets its own folder: <notes dir>\<Movie>\<Movie>.json
        static string NewFileName(string dir, MovieRef movie)
        {
            string baseName = SafeName(movie.Name);
            for (int i = 1; ; i++)
            {
                string name = baseName + (i == 1 ? "" : " (" + i + ")");
                string folder = Path.Combine(dir, name);
                if (!Directory.Exists(folder) && !File.Exists(Path.Combine(dir, name + ".json")))
                    return Path.Combine(folder, name + ".json");
            }
        }

        // Earlier versions kept <Movie>.json/.md loose in the notes folder.
        // Move each pair into its own <Movie>\ folder.
        public static void MigrateFlatFiles(Config cfg)
        {
            if (!Directory.Exists(cfg.NotesDir)) return;
            foreach (var json in Directory.GetFiles(cfg.NotesDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(json);
                    string folder = Path.Combine(cfg.NotesDir, name);
                    string target = Path.Combine(folder, name + ".json");
                    if (File.Exists(target)) { Program.Log("migrate: skipped " + name + " (folder already has notes)"); continue; }
                    Directory.CreateDirectory(folder);
                    File.Move(json, target);
                    string md = Path.ChangeExtension(json, ".md");
                    if (File.Exists(md)) File.Move(md, Path.Combine(folder, name + ".md"));
                    Program.Log("migrate: moved " + name + " into its own folder");
                }
                catch (Exception ex) { Program.Log("migrate failed for " + json + ": " + ex.Message); }
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

    // ------------------------------------------------------------------ subtitles

    // Saves the movie's subtitles into its notes folder, once per movie:
    //  - separate files VLC would load (Movie.srt, Movie.en.srt, Subs\*.srt) are copied
    //  - subtitle tracks inside the video file are extracted with FFmpeg
    // Runs in the background at idle priority. A subtitles.txt in the folder records
    // what was saved; delete it to make the app try again.
    static class Subtitles
    {
        static readonly string[] SubExts = { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi" };
        static readonly string[] VideoExts = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".webm", ".flv", ".mpg", ".mpeg" };
        static readonly HashSet<string> running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void EnsureSaved(Config cfg, MovieRef movie, string folder)
        {
            if (movie.Location == null || !File.Exists(movie.Location)) return;
            if (File.Exists(Path.Combine(folder, "subtitles.txt"))) return;
            if (Directory.GetFiles(folder, "*.partial").Length > 0) return; // an extraction is already in progress
            lock (running) { if (!running.Add(folder)) return; }
            var t = new Thread(delegate()
            {
                try { Save(cfg, movie, folder); }
                catch (Exception ex) { Program.Log("subtitles failed: " + ex.Message); }
                finally { lock (running) running.Remove(folder); }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.Lowest;
            t.Start();
        }

        // Synchronous; public so it can be run directly for testing.
        public static void Save(Config cfg, MovieRef movie, string folder)
        {
            var report = new List<string>();
            report.Add("Subtitles saved by VLC Commentary for: " + movie.Location);
            report.Add("Delete this file to make the app look for subtitles again.");
            report.Add("");

            foreach (var src in FindSeparateFiles(movie.Location))
            {
                string dest = Path.Combine(folder, Path.GetFileName(src));
                if (File.Exists(dest)) continue;
                File.Copy(src, dest);
                report.Add("copied   " + Path.GetFileName(dest) + "   <- " + src);
            }

            string ffmpeg = FindFfmpeg(cfg);
            if (ffmpeg == null)
                report.Add("FFmpeg not found, so subtitles inside the video file were not extracted.");
            else
                ExtractEmbedded(ffmpeg, movie, folder, report);

            if (report.Count == 3) report.Add("No subtitles found for this movie.");
            File.WriteAllLines(Path.Combine(folder, "subtitles.txt"), report.ToArray(), new UTF8Encoding(false));
            Program.Log("subtitles done for " + movie.Name + ": " + (report.Count - 3) + " line(s) in subtitles.txt");
        }

        static List<string> FindSeparateFiles(string moviePath)
        {
            var found = new List<string>();
            string dir = Path.GetDirectoryName(moviePath);
            string stem = Path.GetFileNameWithoutExtension(moviePath);

            foreach (var f in Directory.GetFiles(dir))
                if (IsSub(f) && Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    found.Add(f);

            // Subs\ or Subtitles\ folders belong to the movie only if it's alone in its folder
            // (otherwise e.g. a drive full of downloads would pull in everyone's subtitles).
            int videos = 0;
            foreach (var f in Directory.GetFiles(dir))
                if (Array.IndexOf(VideoExts, Path.GetExtension(f).ToLowerInvariant()) >= 0) videos++;
            if (videos == 1)
                foreach (var sub in new[] { "Subs", "Subtitles", "Sub" })
                {
                    string sd = Path.Combine(dir, sub);
                    if (!Directory.Exists(sd)) continue;
                    foreach (var f in Directory.GetFiles(sd, "*", SearchOption.AllDirectories))
                        if (IsSub(f)) found.Add(f);
                }
            return found;
        }

        static bool IsSub(string f)
        {
            return Array.IndexOf(SubExts, Path.GetExtension(f).ToLowerInvariant()) >= 0;
        }

        // Which output format each FFmpeg subtitle codec can be saved as without re-encoding.
        static bool FormatFor(string codec, out string ext, out string muxer, out string encoder)
        {
            ext = muxer = encoder = null;
            switch (codec)
            {
                case "subrip": case "srt": ext = "srt"; muxer = "srt"; encoder = "copy"; return true;
                case "mov_text": case "text": ext = "srt"; muxer = "srt"; encoder = "srt"; return true;
                case "ass": case "ssa": ext = "ass"; muxer = "ass"; encoder = "copy"; return true;
                case "webvtt": ext = "vtt"; muxer = "webvtt"; encoder = "copy"; return true;
                case "hdmv_pgs_subtitle": ext = "sup"; muxer = "sup"; encoder = "copy"; return true;
                default: return false; // e.g. DVD bitmap subtitles: FFmpeg can't write them to a file on their own
            }
        }

        static void ExtractEmbedded(string ffmpeg, MovieRef movie, string folder, List<string> report)
        {
            // 1. List the subtitle tracks (reads only the file header).
            string probe = Run(ffmpeg, "-hide_banner -nostdin -i " + Q(movie.Location), false);
            var rx = new System.Text.RegularExpressions.Regex(
                @"Stream #0:(\d+)(?:\[0x[0-9a-fA-F]+\])?(?:\(([^)]*)\))?: Subtitle: (\w+)");
            var args = new StringBuilder("-hide_banner -nostdin -v error -y -i " + Q(movie.Location));
            var outputs = new List<KeyValuePair<string, string>>(); // partial path, final path
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string stem = SafeStem(movie.Name);

            foreach (System.Text.RegularExpressions.Match m in rx.Matches(probe))
            {
                string index = m.Groups[1].Value, lang = m.Groups[2].Value, codec = m.Groups[3].Value;
                string ext, muxer, encoder;
                if (!FormatFor(codec, out ext, out muxer, out encoder))
                {
                    report.Add("skipped  track " + index + " (" + codec + "): this subtitle type can't be saved on its own");
                    continue;
                }
                string label = lang.Length > 0 ? lang : "track" + index;
                string name = stem + "." + label + "." + ext;
                for (int n = 2; usedNames.Contains(name) || File.Exists(Path.Combine(folder, name)); n++)
                    name = stem + "." + label + "." + n + "." + ext;
                usedNames.Add(name);
                string final = Path.Combine(folder, name);
                string partial = final + ".partial";
                args.Append(" -map 0:" + index + " -c:s " + encoder + " -f " + muxer + " " + Q(partial));
                outputs.Add(new KeyValuePair<string, string>(partial, final));
            }
            if (outputs.Count == 0) return;

            // 2. Extract every track in one pass (FFmpeg has to read the whole video once).
            Program.Log("subtitles: extracting " + outputs.Count + " track(s) from " + movie.Location);
            var watch = Stopwatch.StartNew();
            string err = Run(ffmpeg, args.ToString(), true);
            bool ok = LastExitCode == 0;
            foreach (var o in outputs)
            {
                if (ok && File.Exists(o.Key) && new FileInfo(o.Key).Length > 0)
                {
                    File.Move(o.Key, o.Value);
                    report.Add("extracted " + Path.GetFileName(o.Value) + "   (from inside the video file)");
                }
                else
                {
                    try { File.Delete(o.Key); } catch { }
                }
            }
            if (!ok) report.Add("FFmpeg failed: " + err.Trim());
            Program.Log("subtitles: FFmpeg finished in " + (int)watch.Elapsed.TotalSeconds + " s, exit " + LastExitCode);
        }

        [ThreadStatic] static int LastExitCode;

        static string Run(string exe, string arguments, bool idle)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                // Idle priority also lowers disk priority, so movie playback isn't disturbed.
                if (idle) { try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { } }
                p.StandardOutput.ReadToEndAsync();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                LastExitCode = p.ExitCode;
                return err;
            }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        static string SafeStem(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().TrimEnd('.');
        }

        public static string FindFfmpeg(Config cfg)
        {
            if (!string.IsNullOrEmpty(cfg.Ffmpeg) && File.Exists(cfg.Ffmpeg)) return cfg.Ffmpeg;
            string path = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" +
                          (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
            foreach (var d in path.Split(';'))
            {
                try
                {
                    if (d.Trim().Length == 0) continue;
                    string p = Path.Combine(Environment.ExpandEnvironmentVariables(d.Trim()), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "Microsoft", "WinGet");
            string link = Path.Combine(winget, "Links", "ffmpeg.exe");
            if (File.Exists(link)) return link;
            string pkgs = Path.Combine(winget, "Packages");
            if (Directory.Exists(pkgs))
                foreach (var d in Directory.GetDirectories(pkgs, "*FFmpeg*"))
                    foreach (var f in Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
                        return f;
            return null;
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
        public bool Phone;   // started on the phone: kept even before any text, so the moment isn't lost
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
                return (d != null && d.Movie != null && (!string.IsNullOrEmpty(d.Text) || d.Phone)) ? d : null;
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
        Draft current;       // the note being written, on the PC or on the phone
        bool draftDirty;
        bool phoneOwns;      // current is being written on the phone
        bool notePaused;     // we paused the movie for the current note
        PhoneOverlay overlay;
        readonly Control ui; // runs phone requests on this (the UI) thread

        // Started by VLC: quit once VLC has closed (checked every few seconds).
        readonly System.Windows.Forms.Timer vlcWatch;
        int vlcGoneChecks;

        public TrayApp(Config c, bool followVlc)
        {
            cfg = c;
            vlc = new VlcClient(c);
            ui = new Control();
            ui.CreateControl();
            var forceHandle = ui.Handle;

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
            menu.Items.Add(new ToolStripSeparator());
            var phoneOn = new ToolStripMenuItem("Phone remote (same Wi-Fi)") { Checked = cfg.Phone };
            var phoneLink = new ToolStripMenuItem("Connect a phone...") { Enabled = false };
            phoneOn.Click += delegate
            {
                if (phone == null)
                {
                    if (!StartPhone(true)) return;
                }
                else
                {
                    phone.Dispose();
                    phone = null;
                    Program.Log("phone remote turned off");
                }
                cfg.Phone = phone != null;
                try { cfg.Save(); } catch { }
                phoneOn.Checked = cfg.Phone;
                phoneLink.Enabled = cfg.Phone;
            };
            phoneLink.Click += delegate { if (phone != null) phone.OpenPairingPage(); };
            menu.Items.Add(phoneOn);
            menu.Items.Add(phoneLink);
            menu.Opening += delegate { phoneLink.Enabled = phone != null; };
            menu.Items.Add(new ToolStripSeparator());
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

            NoteStore.MigrateFlatFiles(cfg);

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
            else if (!followVlc) // started by VLC: it opens every time, so stay quiet
                Tip("VLC Commentary is running", "Press " + cfg.Hotkey + " while a movie plays to add a note.");

            if (cfg.Phone && !StartPhone(false))
                phoneOn.Checked = false;

            if (followVlc)
            {
                Program.Log("started by VLC; will exit when VLC closes");
                vlcWatch = new System.Windows.Forms.Timer { Interval = 3000 };
                vlcWatch.Tick += delegate { CheckVlcStillOpen(); };
                vlcWatch.Start();
            }
        }

        void CheckVlcStillOpen()
        {
            if (VlcRunning()) { vlcGoneChecks = 0; return; }
            // Don't vanish mid-note; the draft would survive, but it's jarring.
            if (openBox != null || current != null) return;
            // Two misses in a row, so a quick VLC restart doesn't bounce us.
            if (++vlcGoneChecks < 2) return;
            Program.Log("VLC closed; exiting");
            ExitThread();
        }

        static bool VlcRunning()
        {
            var ps = Process.GetProcessesByName("vlc");
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        PhoneRemote phone;

        // showPairing: open the "scan this with your phone" page once it's running.
        bool StartPhone(bool showPairing)
        {
            try
            {
                var p = new PhoneRemote(cfg, this);
                p.Start();
                phone = p;
                if (showPairing) p.OpenPairingPage();
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("phone remote failed to start: " + ex.Message);
                Tip("Phone remote couldn't start", ex.Message);
                return false;
            }
        }

        void OnHotkey()
        {
            Program.Log("hotkey pressed");
            if (openBox != null) { openBox.ShowAndFocus(); return; }

            // Writing on the phone: carry on with the same note at the keyboard.
            if (current != null && phoneOwns)
            {
                Program.Log("note moved from phone to keyboard");
                OpenBox(current, false, notePaused);
                return;
            }

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
            notePaused = pausedByUs;
            phoneOwns = false;
            CloseOverlay();
            draftDirty = true; // save straight away: it may have just come from the phone
            var saved = NoteStore.Load(cfg, draft.Movie);
            var box = new NoteBox(draft.Seconds, draft.Movie.Name, draft.Text, draft.TypeIndex, recovered, saved);
            openBox = box;
            box.Changed += delegate(string text, int typeIndex)
            {
                if (openBox != box) return;
                current.Text = text;
                current.TypeIndex = typeIndex;
                draftDirty = true;
            };
            box.Finished += delegate(string content, string type)
            {
                if (openBox != box) return;
                openBox = null;
                int count;
                EndNote(content, type, false, out count);
            };
            draftTimer.Start();
            box.ShowAndFocus();
        }

        // Ends the current note: saves it (content != null) or throws it away.
        // Returns null if that worked, or why saving failed; a failed note is kept,
        // on disk and (from the phone) still open, so it can be retried.
        string EndNote(string content, string type, bool fromPhone, out int count)
        {
            count = 0;
            var draft = current;
            bool resume = notePaused;
            draftTimer.Stop();
            current = null;
            phoneOwns = false;
            notePaused = false;
            CloseOverlay();
            if (draft == null) return null;
            lastEndedSeconds = draft.Seconds;
            lastEndedAt = DateTime.UtcNow;

            if (content == null)
            {
                Program.Log("note cancelled");
                Recovery.Clear();
            }
            else
            {
                try
                {
                    string file = NoteStore.Save(cfg, draft.Movie, draft.Seconds, type, content, out count);
                    Recovery.Clear();
                    Program.Log((fromPhone ? "phone: " : "") + "note saved at " + NoteStore.Fmt(draft.Seconds) +
                                " (" + count + " total) to " + file);
                    Subtitles.EnsureSaved(cfg, draft.Movie, Path.GetDirectoryName(file));
                    if (fromPhone) Toast.Pop("✓ Saved from phone", NoteStore.Fmt(draft.Seconds) + "  ·  " + type, true);
                }
                catch (Exception ex)
                {
                    // Keep the recovery copy so the note can be retried.
                    draft.Text = content;
                    draft.TypeIndex = Math.Max(0, Array.IndexOf(NoteStore.Types, type));
                    try { Recovery.Save(draft); } catch { }
                    Program.Log("save failed: " + ex.Message);
                    if (fromPhone)
                    {
                        current = draft;
                        phoneOwns = true;
                        notePaused = resume;
                        draftTimer.Start();
                        ShowOverlay();
                        return ex.Message;
                    }
                    try { Clipboard.SetText(content); } catch { }
                    Toast.Pop("✗ NOT saved", "Copied to clipboard. " + ex.Message, false);
                    return ex.Message;
                }
            }
            if (resume) ResumeIfSameMovie(draft.Movie);
            return null;
        }

        // ------------------------------------------------ phone side (see PhoneRemote)
        // PhoneRemote calls these from its network threads; RunOnUi moves each call
        // onto this thread, so the phone and the keyboard never edit a note at once.

        public T RunOnUi<T>(Func<T> f)
        {
            return (T)ui.Invoke(f);
        }

        // What the phone should show about the note in progress (null: none).
        public Dictionary<string, object> PhoneNoteState()
        {
            string owner;
            Draft d = current;
            if (d != null) owner = phoneOwns ? "phone" : "pc";
            else
            {
                d = Recovery.Load();
                if (d == null) return null;
                owner = "recovered";
            }
            var o = new Dictionary<string, object>();
            o["owner"] = owner;
            o["seconds"] = Math.Round(d.Seconds, 2);
            o["timestamp"] = NoteStore.Fmt(d.Seconds);
            o["movie"] = d.Movie.Name;
            o["type"] = NoteStore.Types[Math.Max(0, Math.Min(NoteStore.Types.Length - 1, d.TypeIndex))];
            o["text"] = d.Text ?? "";
            return o;
        }

        // The phone tapped a note type. Continues the note in progress (taking it
        // over from the keyboard, or one left unsaved by a crash) or starts one at
        // the movie's current moment. Returns null and sets error if there's nothing to do.
        public Dictionary<string, object> PhoneStart(VlcStatus st, MovieRef movie, int typeIndex, out string error)
        {
            error = null;
            if (current != null && phoneOwns) return PhoneNoteState();

            if (openBox != null)
            {
                Program.Log("note moved from keyboard to phone");
                var box = openBox;
                openBox = null;
                box.Detach();
                phoneOwns = true;
                draftDirty = true;
                ShowOverlay();
                return PhoneNoteState();
            }

            var pending = Recovery.Load();
            if (pending != null)
            {
                Program.Log("phone: continuing recovered note for " + pending.Movie.Name);
                current = pending;
                phoneOwns = true;
                notePaused = false;
                draftDirty = false;
                draftTimer.Start();
                ShowOverlay();
                return PhoneNoteState();
            }

            if (st == null || movie == null || !st.HasMedia)
            {
                error = st == null ? "Can't reach VLC. Open VLC on the PC." : "Nothing is playing in VLC.";
                return null;
            }
            current = new Draft
            {
                Movie = movie,
                Seconds = st.Seconds,
                TypeIndex = Math.Max(0, Math.Min(NoteStore.Types.Length - 1, typeIndex)),
                Phone = true
            };
            phoneOwns = true;
            notePaused = false;
            draftDirty = true; // on disk within a second or two, even before any text
            Program.Log("phone: note started " + movie.Id + " at " + NoteStore.Fmt(st.Seconds));
            if (cfg.AutoPause && st.State == "playing")
            {
                try { vlc.Pause(); notePaused = true; } catch { }
            }
            draftTimer.Start();
            ShowOverlay();
            return PhoneNoteState();
        }

        public const int PhoneOk = 0, PhoneMoved = 1, PhoneNoNote = 2;

        // Where a phone's note stands: PhoneOk (it's on the phone), PhoneMoved (on the
        // keyboard now) or PhoneNoNote (the PC lost it, e.g. it restarted before the
        // note reached the disk; the phone then sends it back with PhoneAdopt).
        int PhoneOwner()
        {
            if (current == null) return PhoneNoNote;
            return phoneOwns ? PhoneOk : PhoneMoved;
        }

        // A note the phone still has (with its original moment) but the PC doesn't.
        // False if it's the note that was just saved or cancelled (a late message
        // from the phone must not bring it back).
        public bool PhoneAdopt(Draft d)
        {
            if (current != null || d == null) return false;
            if (Math.Abs(d.Seconds - lastEndedSeconds) < 0.05 && DateTime.UtcNow - lastEndedAt < TimeSpan.FromMinutes(2))
                return false;
            Program.Log("phone: restored note at " + NoteStore.Fmt(d.Seconds) + " for " + d.Movie.Name);
            current = d;
            phoneOwns = true;
            notePaused = false;
            draftDirty = true;
            draftTimer.Start();
            ShowOverlay();
            return true;
        }

        double lastEndedSeconds = -1;
        DateTime lastEndedAt;

        // Live text from the phone.
        public int PhoneUpdate(string text, int typeIndex)
        {
            int owner = PhoneOwner();
            if (owner != PhoneOk) return owner;
            current.Text = text ?? "";
            if (typeIndex >= 0 && typeIndex < NoteStore.Types.Length) current.TypeIndex = typeIndex;
            draftDirty = true;
            if (overlay != null) overlay.Display(current);
            return PhoneOk;
        }

        // Returns null if saved, otherwise why not (owner says if it moved or is unknown).
        public string PhoneSave(string text, int typeIndex, out int owner, out string timestamp, out int count)
        {
            timestamp = null;
            count = 0;
            owner = PhoneOwner();
            if (owner == PhoneMoved) return "This note was moved to the PC.";
            if (owner == PhoneNoNote) return "The PC has no note in progress.";
            current.Text = text ?? "";
            if (typeIndex >= 0 && typeIndex < NoteStore.Types.Length) current.TypeIndex = typeIndex;
            timestamp = NoteStore.Fmt(current.Seconds);
            string content = current.Text.Trim();
            if (content.Length == 0) content = "(quick mark)";
            return EndNote(content, NoteStore.Types[current.TypeIndex], true, out count);
        }

        public void PhoneCancel()
        {
            if (current == null || !phoneOwns) return;
            int count;
            EndNote(null, null, true, out count);
        }

        void ShowOverlay()
        {
            if (overlay == null) overlay = new PhoneOverlay(cfg.Hotkey);
            overlay.Display(current);
        }

        void CloseOverlay()
        {
            if (overlay == null) return;
            overlay.Close();
            overlay = null;
        }

        void PersistDraft()
        {
            if (!draftDirty || current == null) return;
            draftDirty = false;
            try
            {
                if (current.Text.Trim().Length > 0 || current.Phone) Recovery.Save(current);
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
            if (vlcWatch != null) vlcWatch.Dispose();
            PersistDraft();
            if (phone != null) phone.Dispose();
            CloseOverlay();
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

        // The note moved to the phone: close without saving or cancelling it.
        public void Detach()
        {
            done = true;
            Close();
        }
    }

    // ------------------------------------------------------------------ "writing on your phone" overlay

    // Shows, in the corner of the PC screen, the note you're writing on the phone,
    // as you type it. Never takes focus, so VLC stays fullscreen and in control.
    class PhoneOverlay : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        readonly Label header, body, footer;
        readonly float k;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */ | 0x8 /* TOPMOST */ | 0x20 /* click-through */;
                return cp;
            }
        }

        public PhoneOverlay(string hotkey)
        {
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 27);
            Opacity = 0.92;
            Width = (int)(470 * k);
            Padding = new Padding((int)(14 * k), (int)(10 * k), (int)(14 * k), (int)(8 * k));

            header = new Label
            {
                Dock = DockStyle.Top,
                Height = (int)(28 * k),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(255, 160, 60),
                Font = new Font("Segoe UI Semibold", 10f)
            };
            body = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 11f),
                ForeColor = Color.FromArgb(244, 244, 245)
            };
            footer = new Label
            {
                Dock = DockStyle.Bottom,
                Height = (int)(24 * k),
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = Color.FromArgb(161, 161, 170),
                Font = new Font("Segoe UI", 8.5f),
                Text = "Writing on your phone    " + hotkey + ": continue on the keyboard"
            };
            Controls.Add(body);
            Controls.Add(header);
            Controls.Add(footer);
        }

        public void Display(Draft d)
        {
            if (d == null) return;
            string type = NoteStore.Types[Math.Max(0, Math.Min(NoteStore.Types.Length - 1, d.TypeIndex))];
            header.Text = "● " + NoteStore.Fmt(d.Seconds) + "  " + type + "    " + d.Movie.Name;
            string text = (d.Text ?? "").Trim();
            body.ForeColor = text.Length > 0 ? Color.FromArgb(244, 244, 245) : Color.FromArgb(113, 113, 122);
            if (text.Length == 0) text = "Typing on your phone…";
            if (text.Length > 600) text = "…" + text.Substring(text.Length - 600); // newest words stay visible

            // Grow with the text, up to about eight lines.
            int inner = Width - Padding.Horizontal;
            int h = TextRenderer.MeasureText(text, body.Font, new Size(inner, int.MaxValue),
                                             TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            h = Math.Max((int)(26 * k), Math.Min(h, (int)(170 * k)));
            body.Text = text;
            Height = Padding.Vertical + header.Height + footer.Height + h + (int)(4 * k);

            var screen = Screen.PrimaryScreen.Bounds;
            foreach (var s in Screen.AllScreens)
                if (s.Bounds.Contains(Cursor.Position)) screen = s.Bounds;
            Location = new Point(screen.Right - Width - (int)(32 * k), screen.Top + (int)(32 * k));
            if (!Visible) Show();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }
    }
}
