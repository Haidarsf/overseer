// Claude Monitor - data layer.
// Reads Claude Code's local files under %USERPROFILE%\.claude (read-only):
//   projects\<project>\<session>.jsonl              session transcripts (model, usage, tool calls)
//   projects\<project>\<session>\subagents\*.jsonl  subagent transcripts
//   projects\<project>\<session>\custom-title.json  session title
//   sessions\<pid>.json                             live sessions (busy / idle)
//   skills\**\SKILL.md, plugins\synced\**\plugin.json, settings.json
// Never touches .credentials.json.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using IOPath = System.IO.Path;

namespace ClaudeMonitor
{
    public class Ev
    {
        public DateTime T;
        public string Kind;   // tool | prompt | reply | error
        public string Name;
        public string Detail;
    }

    public class Usage
    {
        public DateTime T;
        public string Model;
        public string Sid;
        public long In, Out, CR, CC;
        public long Billable { get { return In + Out + CC; } }
    }

    public class SkillInfo { public string Name, Description, Source; }
    public class PluginInfo { public string Name, Description, Version; }

    public class Sess
    {
        public string Id, File, Dir, CustomTitle, FirstPrompt, Cwd, Model, ModelName, Version, Entry, Mode, ParentId;
        public bool IsSub;
        public long Offset;
        public DateTime First = DateTime.MinValue, Last = DateTime.MinValue;
        public long In, Out, CR, CC, LastCtx;
        public int Calls, Prompts, Errors, AgentCalls, ToolCalls;
        public Dictionary<string, int> Tools = new Dictionary<string, int>();
        public Dictionary<string, int> Skills = new Dictionary<string, int>();
        public Dictionary<string, int> Mcp = new Dictionary<string, int>();
        public Dictionary<string, Usage> MsgUsage = new Dictionary<string, Usage>();
        public List<Usage> Usages = new List<Usage>();
        public List<Ev> Events = new List<Ev>();
        public List<string> SkillList = new List<string>();
        // live info from sessions\<pid>.json
        public bool Live;
        public string LiveStatus, LiveName;
        public int Pid;

        public string Title
        {
            get
            {
                if (!string.IsNullOrEmpty(LiveName)) return LiveName;
                if (!string.IsNullOrEmpty(CustomTitle)) return CustomTitle;
                if (!string.IsNullOrEmpty(FirstPrompt)) return FirstPrompt;
                return Id;
            }
        }

        public long ContextWindow
        {
            get { return ((Model ?? "").Contains("[1m]") || LastCtx > 200000) ? 1000000 : 200000; }
        }

        public string State
        {
            get
            {
                if (Live)
                {
                    if (LiveStatus == "busy") return "Working";
                    if (!string.IsNullOrEmpty(LiveStatus)) return char.ToUpper(LiveStatus[0]) + LiveStatus.Substring(1);
                    return "Open";
                }
                if (Last > DateTime.Now.AddMinutes(-2)) return "Active";
                return "Ended";
            }
        }
    }

    public class Store
    {
        public readonly string Root;
        public readonly Dictionary<string, Sess> Sessions = new Dictionary<string, Sess>(StringComparer.OrdinalIgnoreCase);
        public List<SkillInfo> InstalledSkills = new List<SkillInfo>();
        public List<PluginInfo> Plugins = new List<PluginInfo>();
        public List<KeyValuePair<string, string>> Settings = new List<KeyValuePair<string, string>>();
        public bool Changed;
        DateTime lastStatic = DateTime.MinValue;
        public void ForceStatic() { lastStatic = DateTime.MinValue; }
        readonly JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        public Store()
        {
            Root = ConfigDir();
        }

        public static string ConfigDir()
        {
            var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            return IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        // ---------- json helpers ----------
        public static object G(object o, string k)
        {
            var d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(k, out v) ? v : null;
        }
        public static string S(object o, string k)
        {
            var v = G(o, k);
            if (v == null) return null;
            if (v is string) return (string)v;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static long L(object o, string k)
        {
            var v = G(o, k);
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        static DateTime ParseTs(string ts)
        {
            DateTime d;
            if (ts != null && DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d))
                return d.ToLocalTime();
            return DateTime.MinValue;
        }
        static string OneLine(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
        }
        static void Inc(Dictionary<string, int> d, string k)
        {
            int v;
            d.TryGetValue(k, out v);
            d[k] = v + 1;
        }

        public static string McpServer(string tool)
        {
            if (!tool.StartsWith("mcp__")) return null;
            var parts = tool.Split(new[] { "__" }, StringSplitOptions.None);
            if (parts.Length < 2) return null;
            var srv = parts[1];
            Guid g;
            if (Guid.TryParse(srv, out g)) srv = "connector " + srv.Substring(0, 8);
            return srv;
        }
        public static string ToolLabel(string tool)
        {
            var srv = McpServer(tool);
            if (srv == null) return tool;
            var idx = tool.LastIndexOf("__", StringComparison.Ordinal);
            return srv + " · " + tool.Substring(idx + 2);
        }

        // ---------- scanning ----------
        public void Scan()
        {
            var proj = IOPath.Combine(Root, "projects");
            if (Directory.Exists(proj))
            {
                foreach (var pdir in SafeDirs(proj))
                {
                    foreach (var f in SafeFiles(pdir, "*.jsonl"))
                    {
                        var s = Get(f, false, null);
                        var titleFile = IOPath.Combine(pdir, s.Id, "custom-title.json");
                        if (File.Exists(titleFile))
                        {
                            try
                            {
                                var t = S(ser.DeserializeObject(ReadShared(titleFile)), "customTitle");
                                if (t != s.CustomTitle) { s.CustomTitle = t; Changed = true; }
                            }
                            catch { }
                        }
                        ReadNew(s);
                        var subDir = IOPath.Combine(pdir, s.Id, "subagents");
                        if (Directory.Exists(subDir))
                            foreach (var sf in SafeFiles(subDir, "*.jsonl"))
                                ReadNew(Get(sf, true, s.Id));
                    }
                }
            }
            ScanLive();
            if ((DateTime.Now - lastStatic).TotalSeconds > 60) { ScanStatic(); lastStatic = DateTime.Now; Changed = true; }
        }

        Sess Get(string file, bool sub, string parent)
        {
            Sess s;
            if (!Sessions.TryGetValue(file, out s))
            {
                s = new Sess { File = file, Id = IOPath.GetFileNameWithoutExtension(file), IsSub = sub, ParentId = parent, Dir = IOPath.GetDirectoryName(file) };
                Sessions[file] = s;
            }
            return s;
        }

        static IEnumerable<string> SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return new string[0]; } }
        static IEnumerable<string> SafeFiles(string d, string p) { try { return Directory.GetFiles(d, p); } catch { return new string[0]; } }
        static string ReadShared(string f)
        {
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
        }

        void ReadNew(Sess s)
        {
            try
            {
                using (var fs = new FileStream(s.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (len < s.Offset) s.Offset = 0; // file rewritten
                    if (len == s.Offset) return;
                    fs.Seek(s.Offset, SeekOrigin.Begin);
                    var buf = new byte[len - s.Offset];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    int last = Array.LastIndexOf(buf, (byte)'\n', read - 1);
                    if (last < 0) return;
                    var text = Encoding.UTF8.GetString(buf, 0, last + 1);
                    s.Offset += last + 1;
                    foreach (var line in text.Split('\n'))
                    {
                        try { ProcessLine(s, line); } catch { }
                    }
                    if (s.Events.Count > 400) s.Events.RemoveRange(0, s.Events.Count - 400);
                    Changed = true;
                }
            }
            catch { }
        }

        void ProcessLine(Sess s, string line)
        {
            if (line.Length < 10) return;
            bool isA = line.Contains("\"type\":\"assistant\"");
            bool isU = line.Contains("\"type\":\"user\"");
            bool isAtt = line.Contains("\"type\":\"attachment\"");
            if (!isA && !isU && !isAtt) return;
            var o = ser.DeserializeObject(line);
            var type = S(o, "type");
            var t = ParseTs(S(o, "timestamp"));
            if (t != DateTime.MinValue)
            {
                if (s.First == DateTime.MinValue || t < s.First) s.First = t;
                if (t > s.Last) s.Last = t;
            }
            if (s.Cwd == null) s.Cwd = S(o, "cwd");
            var v = S(o, "version"); if (v != null) s.Version = v;
            var ep = S(o, "entrypoint"); if (ep != null) s.Entry = ep;
            var pm = S(o, "permissionMode"); if (pm != null) s.Mode = pm;

            if (type == "assistant") Assistant(s, o, t);
            else if (type == "user") User(s, o, t);
            else if (type == "attachment") Attachment(s, o);
        }

        void Assistant(Sess s, object o, DateTime t)
        {
            var msg = G(o, "message");
            var model = S(msg, "model");
            if (!string.IsNullOrEmpty(model) && model != "<synthetic>") s.Model = model;
            var id = S(msg, "id");
            var usage = G(msg, "usage");
            if (id != null && usage != null)
            {
                long i = L(usage, "input_tokens"), ou = L(usage, "output_tokens"),
                     cr = L(usage, "cache_read_input_tokens"), cc = L(usage, "cache_creation_input_tokens");
                Usage u;
                if (!s.MsgUsage.TryGetValue(id, out u))
                {
                    u = new Usage { T = t, Model = s.Model, Sid = s.Id, In = i, Out = ou, CR = cr, CC = cc };
                    s.MsgUsage[id] = u;
                    s.Usages.Add(u);
                    s.In += i; s.Out += ou; s.CR += cr; s.CC += cc;
                    s.Calls++;
                    s.LastCtx = i + cr + cc;
                }
                else if (ou > u.Out)
                {
                    s.Out += ou - u.Out; u.Out = ou; // later streaming chunk has final count
                }
            }
            var content = G(msg, "content") as object[];
            if (content == null) return;
            foreach (var b in content)
            {
                var bt = S(b, "type");
                if (bt == "tool_use")
                {
                    var name = S(b, "name") ?? "?";
                    var input = G(b, "input");
                    s.ToolCalls++;
                    Inc(s.Tools, name);
                    var srv = McpServer(name);
                    if (srv != null) Inc(s.Mcp, srv);
                    if (name == "Skill")
                    {
                        var sk = S(input, "skill") ?? S(input, "command");
                        if (sk != null) Inc(s.Skills, sk);
                    }
                    if (name == "Agent" || name == "Task") s.AgentCalls++;
                    s.Events.Add(new Ev { T = t, Kind = "tool", Name = ToolLabel(name), Detail = Detail(name, input) });
                }
                else if (bt == "text")
                {
                    var tx = S(b, "text");
                    if (!string.IsNullOrWhiteSpace(tx))
                        s.Events.Add(new Ev { T = t, Kind = "reply", Name = "Reply", Detail = OneLine(tx, 160) });
                }
            }
        }

        static string Detail(string name, object input)
        {
            if (name == "Skill") return S(input, "skill") ?? "";
            foreach (var k in new[] { "description", "command", "file_path", "path", "pattern", "url", "query", "prompt", "skill", "action", "title", "recap" })
            {
                var v = S(input, k);
                if (!string.IsNullOrWhiteSpace(v)) return OneLine(v, 160);
            }
            return "";
        }

        void User(Sess s, object o, DateTime t)
        {
            var isMeta = G(o, "isMeta");
            if (isMeta is bool && (bool)isMeta) return;
            var msg = G(o, "message");
            var c = G(msg, "content");
            var str = c as string;
            if (str != null) { Prompt(s, str, t); return; }
            var arr = c as object[];
            if (arr == null) return;
            foreach (var b in arr)
            {
                var bt = S(b, "type");
                if (bt == "text") Prompt(s, S(b, "text"), t);
                else if (bt == "tool_result")
                {
                    var err = G(b, "is_error");
                    if (err is bool && (bool)err)
                    {
                        s.Errors++;
                        var rc = G(b, "content");
                        string txt = rc as string;
                        var rarr = rc as object[];
                        if (txt == null && rarr != null && rarr.Length > 0) txt = S(rarr[0], "text");
                        s.Events.Add(new Ev { T = t, Kind = "error", Name = "Tool error", Detail = OneLine(txt, 160) });
                    }
                }
            }
        }

        void Prompt(Sess s, string text, DateTime t)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var tt = text.TrimStart();
            if (tt.StartsWith("<") || tt.StartsWith("[Request interrupted")) return; // system / command wrappers
            s.Prompts++;
            if (s.FirstPrompt == null) s.FirstPrompt = OneLine(tt, 80);
            s.Events.Add(new Ev { T = t, Kind = "prompt", Name = "You", Detail = OneLine(tt, 160) });
        }

        void Attachment(Sess s, object o)
        {
            var a = G(o, "attachment");
            var at = S(a, "type");
            if (at == "skill_listing")
            {
                var content = S(a, "content") ?? "";
                var list = new List<string>();
                foreach (var ln in content.Split('\n'))
                {
                    if (!ln.StartsWith("- ")) continue;
                    var rest = ln.Substring(2);
                    // "name: description"  (plugin skills look like "plugin:skill: description")
                    var idx = rest.IndexOf(": ", StringComparison.Ordinal);
                    var nm = idx > 0 ? rest.Substring(0, idx) : rest;
                    if (nm.Length < 80) list.Add(nm.Trim());
                }
                if (list.Count > 0) s.SkillList = list;
            }
            else if (at == "model")
            {
                var mn = S(G(a, "identity"), "marketingName");
                if (mn != null) s.ModelName = mn;
            }
        }

        void ScanLive()
        {
            var dir = IOPath.Combine(Root, "sessions");
            var seen = new HashSet<Sess>();
            foreach (var f in SafeFiles(dir, "*.json"))
            {
                try
                {
                    var o = ser.DeserializeObject(ReadShared(f));
                    var sid = S(o, "sessionId");
                    int pid = (int)L(o, "pid");
                    if (sid == null) continue;
                    bool alive = false;
                    try { var p = Process.GetProcessById(pid); alive = !p.HasExited; } catch { }
                    if (!alive) continue;
                    var s = Sessions.Values.FirstOrDefault(x => !x.IsSub && x.Id == sid);
                    if (s == null) continue;
                    var st = S(o, "status"); var nm = S(o, "name");
                    if (!s.Live || s.LiveStatus != st || s.LiveName != nm) Changed = true;
                    s.Live = true; s.Pid = pid; s.LiveStatus = st; s.LiveName = nm;
                    if (s.Entry == null) s.Entry = S(o, "entrypoint");
                    if (s.Version == null) s.Version = S(o, "version");
                    seen.Add(s);
                }
                catch { }
            }
            foreach (var s in Sessions.Values)
                if (s.Live && !seen.Contains(s)) { s.Live = false; s.LiveStatus = null; Changed = true; }
        }

        void ScanStatic()
        {
            // installed skills
            var skills = new List<SkillInfo>();
            var sroot = IOPath.Combine(Root, "skills");
            if (Directory.Exists(sroot))
            {
                string[] files;
                try { files = Directory.GetFiles(sroot, "SKILL.md", SearchOption.AllDirectories); } catch { files = new string[0]; }
                foreach (var f in files)
                {
                    var si = new SkillInfo { Name = IOPath.GetFileName(IOPath.GetDirectoryName(f)), Source = f.Contains("\\synced\\") ? "synced" : "personal" };
                    try
                    {
                        foreach (var ln in File.ReadLines(f).Take(30))
                        {
                            if (ln.StartsWith("name:")) si.Name = ln.Substring(5).Trim().Trim('"');
                            if (ln.StartsWith("description:")) si.Description = ln.Substring(12).Trim().Trim('"');
                        }
                    }
                    catch { }
                    skills.Add(si);
                }
            }
            InstalledSkills = skills.GroupBy(x => x.Name).Select(g => g.First()).OrderBy(x => x.Name).ToList();

            // plugins
            var plugins = new List<PluginInfo>();
            foreach (var sub in new[] { "synced", "cache" })
            {
                var pr = IOPath.Combine(Root, "plugins", sub);
                if (!Directory.Exists(pr)) continue;
                string[] files;
                try { files = Directory.GetFiles(pr, "plugin.json", SearchOption.AllDirectories); } catch { files = new string[0]; }
                foreach (var f in files)
                {
                    try
                    {
                        var o = ser.DeserializeObject(ReadShared(f));
                        var nm = S(o, "name");
                        if (nm == null) continue;
                        plugins.Add(new PluginInfo { Name = nm, Description = S(o, "description"), Version = S(o, "version") });
                    }
                    catch { }
                }
            }
            Plugins = plugins.GroupBy(x => x.Name).Select(g => g.First()).OrderBy(x => x.Name).ToList();

            // settings (top-level keys)
            var settings = new List<KeyValuePair<string, string>>();
            var sf = IOPath.Combine(Root, "settings.json");
            if (File.Exists(sf))
            {
                try
                {
                    var o = ser.DeserializeObject(ReadShared(sf)) as Dictionary<string, object>;
                    if (o != null)
                        foreach (var kv in o)
                        {
                            string val = kv.Value is string || kv.Value is bool || kv.Value is int || kv.Value is long || kv.Value is decimal
                                ? Convert.ToString(kv.Value, CultureInfo.InvariantCulture)
                                : OneLine(ser.Serialize(kv.Value), 120);
                            settings.Add(new KeyValuePair<string, string>(kv.Key, val));
                        }
                }
                catch { }
            }
            Settings = settings;
        }
    }
}
