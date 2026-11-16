// VLC Commentary - Phone remote
// A tiny web server on your home Wi-Fi, so your phone becomes the remote:
// play/pause, jump back or forward, and add timestamped notes without touching
// the keyboard. Open the link (or scan the QR code) from "Connect a phone..."
// in the tray menu; no app to install.
//
// Safety:
//  - every address includes a random secret (the "token"), so nobody else on the
//    network can use it without the link;
//  - only devices on a private network (home Wi-Fi) are answered;
//  - the pairing page with the QR code is only shown on this PC itself.
//
// Uses a plain TcpListener rather than HttpListener, because HttpListener needs
// admin rights to accept connections from other devices.
//
// C# 5 only (see WatchMode.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VlcCommentary
{
    class PhoneRemote : IDisposable
    {
        readonly Config cfg;
        readonly VlcClient vlc;
        // The note in progress lives in the tray app, shared with the keyboard,
        // so a note can move between phone and keyboard and survives a crash.
        readonly TrayApp host;
        TcpListener listener;
        volatile bool running;

        public int Port { get; private set; }

        public PhoneRemote(Config c, TrayApp app)
        {
            cfg = c;
            vlc = new VlcClient(c);
            host = app;
        }

        // ------------------------------------------------------------ start / stop

        public void Start()
        {
            bool changed = false;
            if (string.IsNullOrEmpty(cfg.PhoneToken)) { cfg.PhoneToken = NewToken(); changed = true; }

            int first = cfg.PhonePort > 0 ? cfg.PhonePort : 8780;
            for (int p = first; p < first + 20 && listener == null; p++)
            {
                if (p == cfg.Port) continue; // VLC's own web interface
                try
                {
                    var l = new TcpListener(IPAddress.Any, p);
                    l.Start();
                    listener = l;
                    Port = p;
                }
                catch (SocketException) { }
            }
            if (listener == null) throw new Exception("No free network port from " + first + " to " + (first + 19) + ".");
            // Keep the port the phone has saved, unless it had to move.
            if (Port != cfg.PhonePort) { cfg.PhonePort = Port; changed = true; }
            if (changed) { try { cfg.Save(); } catch { } }

            running = true;
            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "phone-remote" };
            t.Start();
            Program.Log("phone remote listening on port " + Port + " (" + string.Join(", ", LanAddresses().ToArray()) + ")");
        }

        public void Dispose()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        public void OpenPairingPage()
        {
            try { Process.Start("http://127.0.0.1:" + Port + "/" + cfg.PhoneToken + "/pair"); }
            catch (Exception ex) { Program.Log("couldn't open pairing page: " + ex.Message); }
        }

        static string NewToken()
        {
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (!running) return; Thread.Sleep(100); continue; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
            }
        }

        // ------------------------------------------------------------ HTTP plumbing

        class Response
        {
            public int Status = 200;
            public string Type = "application/json; charset=utf-8";
            public byte[] Body = new byte[0];
            public string Location;
        }

        void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = 10000;
                    c.SendTimeout = 10000;
                    var remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
                    if (!IsPrivate(remote)) return;
                    var stream = c.GetStream();

                    string method, target;
                    byte[] body;
                    if (!ReadRequest(stream, out method, out target, out body)) return;

                    Response r;
                    try { r = Route(method, target, body, IPAddress.IsLoopback(remote)); }
                    catch (Exception ex)
                    {
                        Program.Log("phone request " + method + " " + Redact(target) + " failed: " + ex.Message);
                        r = Json(500, Err(ex.Message));
                    }

                    var head = new StringBuilder();
                    head.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
                    head.Append("Content-Type: ").Append(r.Type).Append("\r\n");
                    head.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
                    if (r.Location != null) head.Append("Location: ").Append(r.Location).Append("\r\n");
                    // The secret is in the address: never let it leak to other sites.
                    head.Append("Referrer-Policy: no-referrer\r\n");
                    head.Append("X-Content-Type-Options: nosniff\r\n");
                    head.Append("Cache-Control: no-store\r\n");
                    head.Append("Connection: close\r\n\r\n");
                    byte[] h = Encoding.ASCII.GetBytes(head.ToString());
                    stream.Write(h, 0, h.Length);
                    stream.Write(r.Body, 0, r.Body.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex) { Program.Log("phone connection error: " + ex.Message); }
        }

        static bool ReadRequest(Stream s, out string method, out string target, out byte[] body)
        {
            method = target = null;
            body = new byte[0];
            var buf = new MemoryStream();
            var chunk = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = s.Read(chunk, 0, chunk.Length);
                if (n <= 0) return false;
                buf.Write(chunk, 0, n);
                if (buf.Length > 32 * 1024) return false;
                headerEnd = IndexOfBlankLine(buf.GetBuffer(), (int)buf.Length);
            }
            byte[] all = buf.ToArray();
            string[] lines = Encoding.ASCII.GetString(all, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return false;
            method = first[0].ToUpperInvariant();
            target = first[1];

            int length = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0 && lines[i].Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(lines[i].Substring(colon + 1).Trim(), out length);
            }
            if (length < 0 || length > 256 * 1024) return false;

            int bodyStart = headerEnd + 4;
            var b = new MemoryStream();
            b.Write(all, bodyStart, all.Length - bodyStart);
            while (b.Length < length)
            {
                int n = s.Read(chunk, 0, chunk.Length);
                if (n <= 0) return false;
                b.Write(chunk, 0, n);
            }
            body = b.ToArray();
            return true;
        }

        static int IndexOfBlankLine(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            return -1;
        }

        static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 302: return "Found";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 502: return "Bad Gateway";
                default: return "Error";
            }
        }

        string Redact(string target)
        {
            return string.IsNullOrEmpty(cfg.PhoneToken) ? target : target.Replace(cfg.PhoneToken, "<link>");
        }

        // Home networks only: 10.x, 172.16-31.x, 192.168.x, link-local, and this PC.
        static bool IsPrivate(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return a.IsIPv6LinkLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
            byte[] b = a.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254);
        }

        // This PC's addresses on the home network, most likely first
        // (real Wi-Fi/Ethernet with a router before virtual adapters).
        public static List<string> LanAddresses()
        {
            var good = new List<string>();
            var other = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string d = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    bool virt = d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") ||
                                d.Contains("vethernet") || d.Contains("wsl") || d.Contains("vpn") ||
                                d.Contains("bluetooth") || d.Contains("tailscale") || d.Contains("zerotier");
                    var props = ni.GetIPProperties();
                    bool hasGateway = false;
                    foreach (var g in props.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                            hasGateway = true;
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (!IsPrivate(ua.Address) || IPAddress.IsLoopback(ua.Address)) continue;
                        byte[] b = ua.Address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue; // no router gave it an address
                        (hasGateway && !virt ? good : other).Add(ua.Address.ToString());
                    }
                }
            }
            catch (Exception ex) { Program.Log("listing network addresses failed: " + ex.Message); }
            good.AddRange(other);
            return good;
        }

        // ------------------------------------------------------------ routes

        Response Route(string method, string target, byte[] body, bool fromThisPc)
        {
            string path = target, query = "";
            int q = target.IndexOf('?');
            if (q >= 0) { path = target.Substring(0, q); query = target.Substring(q + 1); }

            string prefix = "/" + cfg.PhoneToken;
            if (cfg.PhoneToken.Length < 16 || !path.StartsWith(prefix, StringComparison.Ordinal))
                return Text(404, "Not found. Open the link from VLC Commentary's tray menu: Connect a phone.");
            string rest = path.Substring(prefix.Length);
            if (rest.Length > 0 && rest[0] != '/')
                return Text(404, "Not found.");

            if (rest == "") return new Response { Status = 302, Location = prefix + "/", Type = "text/plain" };

            if (rest == "/" || rest == "/index.html")
                return Page("phone.html", null);

            if (rest == "/pair")
            {
                if (!fromThisPc) return Text(403, "The pairing page only opens on the PC.");
                return Pairing();
            }

            if (!rest.StartsWith("/api/")) return Text(404, "Not found.");
            string api = rest.Substring(5);
            var input = ParseBody(body);

            if (api == "status") return Status();
            if (api == "notes") return Notes();
            if (method != "POST") return Json(405, Err("Use POST."));

            switch (api)
            {
                case "toggle": return Vlc(delegate { vlc.TogglePause(); });
                case "seek":
                    {
                        int by;
                        if (!int.TryParse(Field(input, "by") ?? QueryValue(query, "by"), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out by) || by == 0 || Math.Abs(by) > 600)
                            return Json(400, Err("Bad seek amount."));
                        return Vlc(delegate { vlc.SeekBy(by); });
                    }
                case "mark": return MarkNow(input);
                case "draft": return UpdateDraft(input);
                case "note": return SaveNote(input);
                case "cancel": return CancelMark();
                default: return Json(404, Err("Unknown command."));
            }
        }

        delegate void VlcAction();

        Response Vlc(VlcAction a)
        {
            try { a(); }
            catch (Exception ex) { return Json(502, VlcError(ex)); }
            return Status();
        }

        Response Status()
        {
            var o = new Dictionary<string, object>();
            VlcStatus st;
            try { st = vlc.GetStatus(); }
            catch (Exception ex)
            {
                // Still report an unsaved note: after a restart VLC may not be open yet.
                var e = VlcError(ex);
                e["note"] = host.RunOnUi(delegate { return host.PhoneNoteState(); });
                return Json(200, e);
            }

            o["vlc"] = "ok";
            o["state"] = st.State;
            o["playing"] = st.HasMedia;
            o["seconds"] = Math.Round(st.Seconds, 2);
            o["length"] = Math.Round(st.Length, 2);
            o["timestamp"] = NoteStore.Fmt(st.Seconds);
            o["movie"] = st.HasMedia ? NoteStore.MovieName(st) : null;
            o["note"] = host.RunOnUi(delegate { return host.PhoneNoteState(); });
            return Json(200, o);
        }

        Dictionary<string, object> VlcError(Exception ex)
        {
            var o = new Dictionary<string, object>();
            var we = ex as WebException;
            var r = we != null ? we.Response as HttpWebResponse : null;
            if (r != null && r.StatusCode == HttpStatusCode.Unauthorized)
            {
                o["vlc"] = "auth";
                o["error"] = "VLC rejected the password. Run the installer again, then restart VLC.";
            }
            else
            {
                o["vlc"] = "offline";
                o["error"] = "Can't reach VLC. Open VLC on the PC (close and reopen it once if it's already open).";
            }
            return o;
        }

        // The phone tapped a note type: start a note at this exact moment, or carry
        // on with the one in progress (from the keyboard, or left by a crash).
        Response MarkNow(Dictionary<string, object> input)
        {
            VlcStatus st = null;
            MovieRef movie = null;
            try
            {
                st = vlc.GetStatus();
                if (st.HasMedia) movie = MovieRef.From(st);
            }
            catch (Exception ex) { Program.Log("phone: VLC unreachable when starting a note: " + ex.Message); }

            int typeIndex = TypeIndex(input);
            string error = null;
            var note = host.RunOnUi(delegate { return host.PhoneStart(st, movie, typeIndex, out error); });
            if (note == null) return Json(st == null ? 502 : 409, Err(error ?? "Couldn't start a note."));
            var o = new Dictionary<string, object>(note);
            o["ok"] = true;
            return Json(200, o);
        }

        // Live text while typing on the phone (shown on the PC and kept safe on disk).
        Response UpdateDraft(Dictionary<string, object> input)
        {
            string text = Field(input, "text") ?? "";
            if (text.Length > 20000) return Json(400, Err("That note is too long."));
            int typeIndex = TypeIndex(input);
            int owner = host.RunOnUi(delegate { return host.PhoneUpdate(text, typeIndex); });
            if (owner == TrayApp.PhoneNoNote && Restore(input))
                owner = host.RunOnUi(delegate { return host.PhoneUpdate(text, typeIndex); });
            var o = new Dictionary<string, object>();
            o["ok"] = owner == TrayApp.PhoneOk;
            if (owner == TrayApp.PhoneMoved) o["moved"] = true;
            if (owner == TrayApp.PhoneNoNote) o["error"] = "The PC lost this note and the film has changed.";
            return Json(200, o);
        }

        // The phone has a note the PC doesn't know about (the PC restarted before it
        // reached the disk). Recreate it at the moment the phone remembers, if that
        // film is still the one playing.
        bool Restore(Dictionary<string, object> input)
        {
            double seconds;
            if (!double.TryParse(Field(input, "seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                seconds < 0)
                return false;
            string movieName = Field(input, "movie");
            VlcStatus st;
            try { st = vlc.GetStatus(); }
            catch { return false; }
            if (!st.HasMedia) return false;
            if (!string.IsNullOrEmpty(movieName) && movieName != NoteStore.MovieName(st)) return false;
            var d = new Draft
            {
                Movie = MovieRef.From(st),
                Seconds = seconds,
                TypeIndex = Math.Max(0, TypeIndex(input)),
                Text = Field(input, "text") ?? "",
                Phone = true
            };
            return host.RunOnUi(delegate { return host.PhoneAdopt(d); });
        }

        Response SaveNote(Dictionary<string, object> input)
        {
            string text = Field(input, "text") ?? "";
            if (text.Length > 20000) return Json(400, Err("That note is too long."));
            int typeIndex = TypeIndex(input);
            int owner = 0;
            string timestamp = null;
            int count = 0;
            string error = host.RunOnUi(delegate { return host.PhoneSave(text, typeIndex, out owner, out timestamp, out count); });
            if (error != null && owner == TrayApp.PhoneNoNote && Restore(input))
                error = host.RunOnUi(delegate { return host.PhoneSave(text, typeIndex, out owner, out timestamp, out count); });
            if (error != null)
            {
                bool moved = owner == TrayApp.PhoneMoved;
                var e = Err(moved ? error :
                            owner == TrayApp.PhoneNoNote ? "Not saved: the PC lost this note and a different film is playing." :
                            "Not saved: " + error);
                if (moved) e["moved"] = true;
                return Json(moved ? 409 : 500, e);
            }
            var o = new Dictionary<string, object>();
            o["ok"] = true;
            o["timestamp"] = timestamp;
            o["count"] = count;
            return Json(200, o);
        }

        Response CancelMark()
        {
            host.RunOnUi(delegate { host.PhoneCancel(); return true; });
            var o = new Dictionary<string, object>();
            o["ok"] = true;
            return Json(200, o);
        }

        static int TypeIndex(Dictionary<string, object> input)
        {
            return Array.IndexOf(NoteStore.Types, Field(input, "type")); // -1 when missing or unknown
        }

        Response Notes()
        {
            VlcStatus st;
            try { st = vlc.GetStatus(); }
            catch (Exception ex) { return Json(200, VlcError(ex)); }
            var list = new List<object>();
            if (st.HasMedia)
                foreach (var n in NoteStore.Load(cfg, MovieRef.From(st)))
                {
                    var d = new Dictionary<string, object>();
                    object v;
                    d["timestamp"] = n.TryGetValue("timestamp", out v) ? v : "";
                    d["seconds"] = n.TryGetValue("seconds", out v) ? v : 0;
                    d["type"] = n.TryGetValue("type", out v) ? v : "";
                    d["content"] = n.TryGetValue("content", out v) ? v : "";
                    d["created"] = n.TryGetValue("created", out v) ? v : "";
                    list.Add(d);
                }
            var o = new Dictionary<string, object>();
            o["movie"] = st.HasMedia ? NoteStore.MovieName(st) : null;
            o["notes"] = list;
            return Json(200, o);
        }

        Response Pairing()
        {
            var urls = new List<string>();
            foreach (var ip in LanAddresses())
                urls.Add("http://" + ip + ":" + Port + "/" + cfg.PhoneToken + "/");
            var vars = new Dictionary<string, string>();
            vars["URLS_JSON"] = new JavaScriptSerializer().Serialize(urls);
            return Page("pair.html", vars);
        }

        // ------------------------------------------------------------ helpers

        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        static Dictionary<string, object> ParseBody(byte[] body)
        {
            if (body == null || body.Length == 0) return new Dictionary<string, object>();
            try
            {
                lock (Ser)
                    return Ser.DeserializeObject(Encoding.UTF8.GetString(body)) as Dictionary<string, object>
                           ?? new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); }
        }

        static string Field(Dictionary<string, object> d, string k)
        {
            return VlcClient.Str(d, k);
        }

        static string QueryValue(string query, string key)
        {
            foreach (var part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && Uri.UnescapeDataString(part.Substring(0, eq)) == key)
                    return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        static Dictionary<string, object> Err(string msg)
        {
            var o = new Dictionary<string, object>();
            o["ok"] = false;
            o["error"] = msg;
            return o;
        }

        static Response Json(int status, object o)
        {
            string s;
            lock (Ser) s = Ser.Serialize(o);
            return new Response { Status = status, Body = Encoding.UTF8.GetBytes(s) };
        }

        static Response Text(int status, string s)
        {
            return new Response { Status = status, Type = "text/plain; charset=utf-8", Body = Encoding.UTF8.GetBytes(s) };
        }

        static readonly Dictionary<string, string> PageCache = new Dictionary<string, string>();

        static Response Page(string name, Dictionary<string, string> vars)
        {
            string html;
            lock (PageCache)
            {
                if (!PageCache.TryGetValue(name, out html))
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VlcCommentary." + name))
                    {
                        if (s == null) return Text(500, "Missing page " + name + " (rebuild with build.ps1).");
                        using (var r = new StreamReader(s, Encoding.UTF8)) html = r.ReadToEnd();
                    }
                    PageCache[name] = html;
                }
            }
            if (vars != null)
                foreach (var kv in vars) html = html.Replace("{{" + kv.Key + "}}", kv.Value);
            return new Response { Type = "text/html; charset=utf-8", Body = Encoding.UTF8.GetBytes(html) };
        }
    }
}
